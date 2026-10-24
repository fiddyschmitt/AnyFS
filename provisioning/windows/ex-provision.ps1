<#
    AnyFS test lab: the Exchange Server machine. Exchange Server Subscription Edition, one Mailbox server in the
    lab's domain (the SharePoint farms'), as the trial edition (no product key), with a few mailboxes, a shared
    mailbox, an archive, an address list and a public folder, EWS and IMAP on: what the Outlook source's
    on-premises store reads from.

    Vagrant runs this inside the VM (over WinRM, elevated) when it first creates the VM, after each restart it
    asks for, and on every `vagrant provision`. It works in rounds, each doing what is still missing and leaving
    C:\anyfs-lab\restart-required when the next step needs a restart: joining the domain; the prerequisites
    (Windows features, .NET Framework 4.8.1, the Visual C++ runtimes, IIS URL Rewrite, the UCMA runtime); Exchange's
    own setup; then the configuration (ex-configure.ps1) in the Exchange Management Shell. Setup and the
    configuration run as the domain's Administrator through a scheduled task, since setup needs a schema, enterprise
    and domain administrator and the WinRM session is the local account's. The first build takes about two hours.

    Inputs (from the Vagrantfile): LAB_PUBKEY, LAB_USER, LAB_PASSWORD; DC_IP; EX_LAN_IP; SP_DOMAIN, SP_NETBIOS (the
    lab's domain); EX_ADMIN_USER, EX_ADMIN_PASSWORD; EX_ORGANIZATION; EX_IMAGE (the ISO's file name in
    \\vboxsvr\exchange); EX_MAILBOXES (user=password;...); EX_TESTER; EX_SHARED.
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

foreach ($name in 'LAB_PUBKEY', 'LAB_USER', 'LAB_PASSWORD', 'DC_IP', 'EX_LAN_IP', 'SP_DOMAIN', 'SP_NETBIOS',
                  'EX_ADMIN_USER', 'EX_ADMIN_PASSWORD', 'EX_ORGANIZATION', 'EX_IMAGE', 'EX_MAILBOXES', 'EX_TESTER', 'EX_SHARED') {
    if (-not [Environment]::GetEnvironmentVariable($name)) { throw "$name is not set (the Vagrantfile passes it)" }
}
$user = $env:LAB_USER
New-Item -ItemType Directory -Force -Path 'C:\anyfs-lab' | Out-Null

# --- Restarts: the marker tells the Vagrantfile a round ended with a restart; it holds the boot time it left ---
$restartMarker = 'C:\anyfs-lab\restart-required'
$bootTime = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToString('o')
if ((Test-Path $restartMarker) -and (Get-Content $restartMarker -Raw).Trim() -ne $bootTime) {
    Remove-Item -Force $restartMarker
}
function Request-Restart([string]$why) {
    Set-Content -Path $restartMarker -Value $bootTime -Encoding ascii
    shutdown.exe /r /t 10 /f /d p:4:1 /c "AnyFS lab: $why"
    Write-Host "$($env:COMPUTERNAME): $why; restarting to continue"
    exit 0
}
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$adminName = "$($env:SP_NETBIOS)\$($env:EX_ADMIN_USER)"
$adminCredential = New-Object System.Management.Automation.PSCredential($adminName, (ConvertTo-SecureString $env:EX_ADMIN_PASSWORD -AsPlainText -Force))

# --- OpenSSH Server, running and starting at boot, PowerShell as its shell, the lab's key -----------
if (-not (Get-Service sshd -ErrorAction SilentlyContinue)) {
    Add-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0' | Out-Null
}
Set-Service sshd -StartupType Automatic
Start-Service sshd
if (-not (Test-Path 'HKLM:\SOFTWARE\OpenSSH')) { New-Item -Path 'HKLM:\SOFTWARE\OpenSSH' | Out-Null }
Set-ItemProperty -Path 'HKLM:\SOFTWARE\OpenSSH' -Name DefaultShell -Type String `
    -Value "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$sshd = (Get-CimInstance Win32_Service -Filter "Name='sshd'").PathName.Trim('"')
$sshd = $sshd.Substring(0, $sshd.IndexOf('.exe', [StringComparison]::OrdinalIgnoreCase) + 4)
$conf = & $sshd -T -C "user=$user,host=localhost,addr=127.0.0.1"
if ($LASTEXITCODE -ne 0) { throw 'sshd -T failed' }
$keys = (($conf | Where-Object { $_ -like 'authorizedkeysfile *' }) -split ' ')[1]
$keys = $keys -replace '__PROGRAMDATA__', $env:ProgramData -replace '/', '\'
if (-not [IO.Path]::IsPathRooted($keys)) { $keys = Join-Path $env:USERPROFILE $keys }
$have = if (Test-Path $keys) { @(Get-Content $keys) } else { @() }
if ($have -notcontains $env:LAB_PUBKEY) {
    New-Item -ItemType Directory -Force -Path (Split-Path $keys) | Out-Null
    Set-Content -Path $keys -Value ($have + $env:LAB_PUBKEY) -Encoding ascii
}
if ($keys -like "$env:ProgramData\*") {
    icacls.exe $keys /inheritance:r /grant '*S-1-5-32-544:F' /grant '*S-1-5-18:F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed on $keys" }
}

# --- No firewall (an isolated lab); never sleeps ---------------------------------------------------
Set-NetFirewallProfile -All -Enabled False
powercfg.exe /change standby-timeout-ac 0
powercfg.exe /change hibernate-timeout-ac 0

# --- Two adapters: NAT (Vagrant's own connection and the internet) and the LAN; names from the domain controller ---
$nat = Get-NetIPAddress -AddressFamily IPv4 -IPAddress '10.0.2.*' | Select-Object -First 1
$lan = Get-NetIPAddress -AddressFamily IPv4 -IPAddress $env:EX_LAN_IP -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $nat -or -not $lan) {
    throw "expected a NAT adapter (10.0.2.x) and the LAN adapter at $($env:EX_LAN_IP), found $((Get-NetIPAddress -AddressFamily IPv4).IPAddress -join ', ')"
}
Set-DnsClient -InterfaceIndex $nat.InterfaceIndex -RegisterThisConnectionsAddress $false
Set-DnsClient -InterfaceIndex $lan.InterfaceIndex -RegisterThisConnectionsAddress $true
Set-DnsClientServerAddress -InterfaceIndex $lan.InterfaceIndex -ServerAddresses $env:DC_IP
Set-DnsClientServerAddress -InterfaceIndex $nat.InterfaceIndex -ServerAddresses $env:DC_IP
$deadline = (Get-Date).AddMinutes(10)
while ($true) {
    try { Resolve-DnsName -Name $env:SP_DOMAIN -Type A -Server $env:DC_IP -ErrorAction Stop | Out-Null; break }
    catch { if ((Get-Date) -gt $deadline) { throw "the domain controller at $($env:DC_IP) does not resolve $($env:SP_DOMAIN): is anyfs-dc-1 up and provisioned?" }; Start-Sleep -Seconds 10 }
}

# --- Round one: join the domain -----------------------------------------------------------------
$system = Get-CimInstance Win32_ComputerSystem
if (-not ($system.PartOfDomain -and $system.Domain -eq $env:SP_DOMAIN)) {
    Add-Computer -DomainName $env:SP_DOMAIN -Credential $adminCredential -Force -WarningAction SilentlyContinue
    Request-Restart "joined $($env:SP_DOMAIN)"
}

# --- The domain's Administrator administers this machine (setup and the configuration run as it) -----
if (-not (Get-LocalGroupMember -Group Administrators -Member $adminName -ErrorAction SilentlyContinue)) {
    Add-LocalGroupMember -Group Administrators -Member $adminName
}

# --- Runs a command as the domain's Administrator, elevated, through a scheduled task, and waits for it ---------
# (A process started with other credentials from a WinRM session is not elevated; a task with the highest run level is.)
function Invoke-AsAdministrator([string]$TaskName, [string]$Command, [string]$Log, [int]$TimeoutMinutes) {
    if (Test-Path $Log) { Remove-Item -Force $Log }
    # /S with the whole line in quotes: without it cmd strips the first and the LAST quote when the command starts with
    # one ("D:\Setup.exe" ... > "log"), which broke the line and left no log (the first run of this lab, 24-Oct-2026)
    $action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/s /c `"$Command > `"$Log`" 2>&1`""
    $taskSettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 4) -MultipleInstances IgnoreNew -StartWhenAvailable
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Register-ScheduledTask -TaskName $TaskName -Action $action -Settings $taskSettings -User $adminName -Password $env:EX_ADMIN_PASSWORD -RunLevel Highest | Out-Null
    Start-ScheduledTask -TaskName $TaskName
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $shown = 0
    Start-Sleep -Seconds 5
    while ((Get-ScheduledTask -TaskName $TaskName).State -ne 'Ready') {
        if ((Get-Date) -gt $deadline) { throw "$TaskName did not finish within $TimeoutMinutes minutes; see $Log" }
        Start-Sleep -Seconds 30
        if (Test-Path $Log) {
            $lines = @(Get-Content $Log -ErrorAction SilentlyContinue)
            if ($lines.Count -gt $shown) { $lines[$shown..($lines.Count - 1)] | ForEach-Object { Write-Host "  | $_" }; $shown = $lines.Count }
        }
    }
    if (Test-Path $Log) {
        $lines = @(Get-Content $Log -ErrorAction SilentlyContinue)
        if ($lines.Count -gt $shown) { $lines[$shown..($lines.Count - 1)] | ForEach-Object { Write-Host "  | $_" } }
    }
    $result = (Get-ScheduledTaskInfo -TaskName $TaskName).LastTaskResult
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    return $result
}

# --- The installation image: copied here (Mount-DiskImage wants a local file), mounted when needed ----------------
$mediaLocal = 'C:\anyfs-lab\media-local'
New-Item -ItemType Directory -Force -Path $mediaLocal | Out-Null
$iso = Join-Path $mediaLocal $env:EX_IMAGE
function Get-ExchangeMedia {
    if (-not (Test-Path $iso)) {
        $source = Join-Path '\\vboxsvr\exchange' $env:EX_IMAGE
        if (-not (Test-Path $source)) { $source = Join-Path 'C:\anyfs-lab\media' $env:EX_IMAGE }
        if (-not (Test-Path $source)) { throw "$($env:EX_IMAGE) is not in tools\exchange on the PC (D:\VMs\media\exchange)" }
        Write-Host "copying $($env:EX_IMAGE) ($([math]::Round((Get-Item $source).Length / 1GB, 1)) GB) to this machine"
        Copy-Item -Path $source -Destination $iso
    }
    $image = Mount-DiskImage -ImagePath $iso -PassThru
    for ($i = 0; $i -lt 30; $i++) {
        $letter = ($image | Get-Volume -ErrorAction SilentlyContinue).DriveLetter
        if ($letter) { return "${letter}:" }
        Start-Sleep -Seconds 2
        $image = Get-DiskImage -ImagePath $iso
    }
    throw "$iso is mounted but has no drive letter"
}

$installed = Get-Service MSExchangeIS -ErrorAction SilentlyContinue

# --- Round two: the prerequisites ---------------------------------------------------------------------------------
if (-not $installed) {
    $restartWhy = @()

    # Windows features (Microsoft's list for a Mailbox server on Windows Server 2022)
    $features = 'Server-Media-Foundation', 'NET-Framework-45-Core', 'NET-Framework-45-ASPNET', 'NET-WCF-HTTP-Activation45', 'NET-WCF-Pipe-Activation45', 'NET-WCF-TCP-Activation45', 'NET-WCF-TCP-PortSharing45',
        'RPC-over-HTTP-proxy', 'RSAT-Clustering', 'RSAT-Clustering-CmdInterface', 'RSAT-Clustering-Mgmt', 'RSAT-Clustering-PowerShell', 'RSAT-ADDS', 'WAS-Process-Model',
        'Web-Asp-Net45', 'Web-Basic-Auth', 'Web-Client-Auth', 'Web-Digest-Auth', 'Web-Dir-Browsing', 'Web-Dyn-Compression', 'Web-Http-Errors', 'Web-Http-Logging', 'Web-Http-Redirect', 'Web-Http-Tracing',
        'Web-ISAPI-Ext', 'Web-ISAPI-Filter', 'Web-Lgcy-Mgmt-Console', 'Web-Metabase', 'Web-Mgmt-Console', 'Web-Mgmt-Service', 'Web-Net-Ext45', 'Web-Request-Monitor', 'Web-Server', 'Web-Stat-Compression',
        'Web-Static-Content', 'Web-Windows-Auth', 'Web-WMI'
    $missing = @(Get-WindowsFeature $features | Where-Object { -not $_.Installed } | ForEach-Object Name)
    if ($missing.Count -gt 0) {
        Write-Host "installing Windows features: $($missing -join ', ')"
        $result = Install-WindowsFeature -Name $missing
        if ($result.RestartNeeded -eq 'Yes') { $restartWhy += 'Windows features' }
    }

    # .NET Framework 4.8.1 (Exchange SE insists; Windows Server 2022 ships 4.8)
    $release = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue).Release
    if (-not $release -or $release -lt 533320) {
        $exe = Join-Path $mediaLocal 'ndp481-x86-x64-allos-enu.exe'
        if (-not (Test-Path $exe)) { Write-Host 'downloading .NET Framework 4.8.1'; Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/?linkid=2203305' -OutFile $exe -UseBasicParsing }
        Write-Host 'installing .NET Framework 4.8.1'
        $p = Start-Process -FilePath $exe -ArgumentList '/q', '/norestart' -Wait -PassThru
        if ($p.ExitCode -notin 0, 1641, 3010) { throw ".NET Framework 4.8.1 setup exited with $($p.ExitCode)" }
        $restartWhy += '.NET Framework 4.8.1'
    }

    # The Visual C++ 2012 and 2013 runtimes
    foreach ($vc in @(
        @{ Name = 'Visual C++ 2012'; Key = 'HKLM:\SOFTWARE\Microsoft\VisualStudio\11.0\VC\Runtimes\x64'; Url = 'https://download.microsoft.com/download/1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_x64.exe'; File = 'vcredist2012_x64.exe' },
        @{ Name = 'Visual C++ 2013'; Key = 'HKLM:\SOFTWARE\Microsoft\VisualStudio\12.0\VC\Runtimes\x64'; Url = 'https://aka.ms/highdpimfc2013x64enu'; File = 'vcredist2013_x64.exe' })) {
        # the x64 runtimes of those years record themselves under WOW6432Node (seen on the first run: installed twice)
        $keys = @($vc.Key, ($vc.Key -replace '^HKLM:\\SOFTWARE\\', 'HKLM:\SOFTWARE\WOW6432Node\'))
        if (-not ($keys | Where-Object { (Get-ItemProperty $_ -ErrorAction SilentlyContinue).Installed -eq 1 })) {
            $exe = Join-Path $mediaLocal $vc.File
            if (-not (Test-Path $exe)) { Write-Host "downloading the $($vc.Name) runtime"; Invoke-WebRequest -Uri $vc.Url -OutFile $exe -UseBasicParsing }
            Write-Host "installing the $($vc.Name) runtime"
            $p = Start-Process -FilePath $exe -ArgumentList '/quiet', '/norestart' -Wait -PassThru
            if ($p.ExitCode -notin 0, 1638, 3010) { throw "$($vc.Name) setup exited with $($p.ExitCode)" }
        }
    }

    # IIS URL Rewrite 2.1
    if (-not (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\IIS Extensions\URL Rewrite' -ErrorAction SilentlyContinue)) {
        $msi = Join-Path $mediaLocal 'rewrite_amd64_en-US.msi'
        if (-not (Test-Path $msi)) { Write-Host 'downloading IIS URL Rewrite 2.1'; Invoke-WebRequest -Uri 'https://download.microsoft.com/download/1/2/8/128E2E22-C1B9-44A4-BE2A-5859ED1D4592/rewrite_amd64_en-US.msi' -OutFile $msi -UseBasicParsing }
        Write-Host 'installing IIS URL Rewrite 2.1'
        $p = Start-Process -FilePath 'msiexec.exe' -ArgumentList '/i', "`"$msi`"", '/qn', '/norestart' -Wait -PassThru
        if ($p.ExitCode -notin 0, 3010) { throw "URL Rewrite setup exited with $($p.ExitCode)" }
    }

    # The Unified Communications Managed API 4.0 runtime, from the Exchange image
    $ucma = Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
        ForEach-Object { Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue } | Where-Object { $_.DisplayName -like 'Microsoft Unified Communications Managed API 4.0*' }
    if (-not $ucma) {
        $drive = Get-ExchangeMedia
        Write-Host 'installing the UCMA 4.0 runtime'
        $p = Start-Process -FilePath "$drive\UCMARedist\Setup.exe" -ArgumentList '/passive', '/norestart' -Wait -PassThru
        if ($p.ExitCode -notin 0, 3010) { throw "UCMA setup exited with $($p.ExitCode)" }
        $restartWhy += 'the UCMA runtime'
    }

    if ($restartWhy.Count -gt 0) { Request-Restart "installed $($restartWhy -join ', ')" }

    # --- Round three: Exchange's own setup, as the domain's Administrator (schema, organisation, domain, then the server) ---
    $drive = Get-ExchangeMedia
    $setupLog = 'C:\anyfs-lab\exchange-setup.out'
    Write-Host "running Exchange setup from $drive as $adminName (an hour or so)"
    $command = "`"$drive\Setup.exe`" /IAcceptExchangeServerLicenseTerms_DiagnosticDataOFF /Mode:Install /Roles:Mailbox /OrganizationName:$($env:EX_ORGANIZATION) /InstallWindowsComponents /CustomerFeedbackEnabled:False"
    $result = Invoke-AsAdministrator -TaskName 'AnyFS Exchange setup' -Command $command -Log $setupLog -TimeoutMinutes 180
    $output = if (Test-Path $setupLog) { Get-Content $setupLog -Raw } else { '' }
    if ($result -ne 0 -or $output -notmatch 'completed successfully') {
        $log = 'C:\ExchangeSetupLogs\ExchangeSetup.log'
        $tail = if (Test-Path $log) { (Get-Content $log -Tail 60) -join "`n" } else { 'no ExchangeSetup.log' }
        if ($output -match 'restart' -and $output -match 'pending') { Request-Restart 'Exchange setup wants a restart first' }
        throw "Exchange setup exited with $result; the end of its log:`n$tail"
    }
    Request-Restart 'Exchange Server is installed'
}

# --- Round four: the configuration, in the Exchange Management Shell as the domain's Administrator ------------------
$configured = 'C:\anyfs-lab\exchange-configured'
if (-not (Test-Path $configured)) {
    foreach ($service in 'MSExchangeADTopology', 'MSExchangeIS', 'MSExchangeTransport', 'MSExchangeServiceHost') {
        $deadline = (Get-Date).AddMinutes(15)
        while ((Get-Service $service -ErrorAction SilentlyContinue).Status -ne 'Running') {
            if ((Get-Date) -gt $deadline) { throw "$service is not running 15 minutes after boot" }
            Start-Sleep -Seconds 15
        }
    }

    # the PST fixtures the tester's mailbox is seeded from: a local copy, shared for the import (Exchange reads a UNC path)
    $pstLocal = 'C:\anyfs-lab\pst-local'
    New-Item -ItemType Directory -Force -Path $pstLocal | Out-Null
    Get-ChildItem 'C:\anyfs-lab\pst' -Filter *.pst -ErrorAction SilentlyContinue | ForEach-Object { Copy-Item $_.FullName -Destination $pstLocal -Force }
    # the Mailbox Replication Service opens the file as the server's computer account (Exchange Trusted Subsystem) and wants
    # write access: the share alone was "Access to the path is denied" (first run, 25-Oct-2026)
    icacls.exe $pstLocal /grant "$($env:SP_NETBIOS)\Exchange Trusted Subsystem:(OI)(CI)M" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed on $pstLocal" }
    if (-not (Get-SmbShare -Name pst -ErrorAction SilentlyContinue)) {
        New-SmbShare -Name pst -Path $pstLocal -FullAccess "$($env:SP_NETBIOS)\Exchange Trusted Subsystem", $adminName | Out-Null
    }

    # what the configuration needs to know, since a scheduled task starts without this session's environment
    @{ SP_DOMAIN = $env:SP_DOMAIN; SP_NETBIOS = $env:SP_NETBIOS; EX_MAILBOXES = $env:EX_MAILBOXES; EX_TESTER = $env:EX_TESTER; EX_SHARED = $env:EX_SHARED; EX_ADMIN_USER = $env:EX_ADMIN_USER } |
        ConvertTo-Json | Set-Content -Path 'C:\anyfs-lab\ex-environment.json' -Encoding ascii
    $configureLog = 'C:\anyfs-lab\exchange-configure.out'
    $command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\anyfs-lab\ex-configure.ps1"
    $result = Invoke-AsAdministrator -TaskName 'AnyFS Exchange configuration' -Command $command -Log $configureLog -TimeoutMinutes 60
    $output = if (Test-Path $configureLog) { Get-Content $configureLog -Raw } else { '' }
    if ($result -ne 0 -or $output -notmatch 'configured: done') {
        throw "the Exchange configuration failed ($result); its output:`n$output"
    }
    Set-Content -Path $configured -Value (Get-Date).ToString('o') -Encoding ascii
}

$os = Get-CimInstance Win32_OperatingSystem
$version = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\ExchangeServer\v15\Setup' -ErrorAction SilentlyContinue)
Write-Host "$($env:COMPUTERNAME): $($os.Caption) build $($os.BuildNumber), in $($env:SP_DOMAIN), Exchange $($version.MsiProductMajor).$($version.MsiProductMinor) build $($version.MsiBuildMajor).$($version.MsiBuildMinor), organisation $($env:EX_ORGANIZATION), sshd $((Get-Service sshd).Status)"
