<#
    AnyFS test lab: the SharePoint Server machine. SQL Server 2022 Developer and SharePoint Server
    Subscription Edition as a one-server farm in the lab's domain, with a web application on port 80
    (Windows authentication) and the site collection the tests use.

    Vagrant runs this inside the VM (over WinRM, elevated) when it first creates the VM, after each restart
    it asks for, and on every `vagrant provision`. It works in rounds, each doing what is still missing and
    leaving C:\anyfs-lab\restart-required when the next step needs a restart: joining the domain; SQL
    Server when its setup says so; SharePoint's prerequisites (once or twice). The farm itself is built by
    SharePointDsc, Microsoft's Desired State Configuration module for SharePoint, which runs the installer
    and the farm configuration as the setup account; every round re-applies the same configuration and
    only what is not yet in place is done. The first build takes well over an hour.
    Both installation images (SQL Server, SharePoint) come from \\vboxsvr\sharepoint: tools\sharepoint on the PC,
    filled by scripts\fetch-sharepoint.ps1.

    Inputs (from the Vagrantfile): LAB_PUBKEY, LAB_USER, LAB_PASSWORD; DC_IP; SP_LAN_IP; SP_DOMAIN, SP_NETBIOS;
    SP_SETUP_USER/_PASSWORD, SP_FARM_USER/_PASSWORD, SP_TEST_USER/_PASSWORD; SP_PASSPHRASE; SP_PRODUCT_KEY;
    SP_PRODUCT (se or 2019), SP_IMAGE (the installation image's file name), SP_DB_PREFIX (this farm's database
    names), SP_SQL_SERVER (another machine's address to use its SQL Server; empty: install one here); SP_WEB_URL;
    SP_SITE; SP_DSC_VERSION.
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

foreach ($name in 'LAB_PUBKEY', 'LAB_USER', 'LAB_PASSWORD', 'DC_IP', 'SP_LAN_IP', 'SP_DOMAIN', 'SP_NETBIOS',
                  'SP_SETUP_USER', 'SP_SETUP_PASSWORD', 'SP_FARM_USER', 'SP_FARM_PASSWORD', 'SP_TEST_USER', 'SP_TEST_PASSWORD',
                  'SP_PASSPHRASE', 'SP_PRODUCT', 'SP_IMAGE', 'SP_PRODUCT_KEY', 'SP_DB_PREFIX', 'SP_WEB_URL', 'SP_SITE', 'SP_DSC_VERSION') {
    if (-not [Environment]::GetEnvironmentVariable($name)) { throw "$name is not set (the Vagrantfile passes it)" }
}
$user = $env:LAB_USER
New-Item -ItemType Directory -Force -Path 'C:\anyfs-lab' | Out-Null

# --- Restarts: the marker tells the Vagrantfile a round ended with a restart; it holds the boot time it left ---
$restartMarker = 'C:\anyfs-lab\restart-required'
$bootTime = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToString('o')
if ((Test-Path $restartMarker) -and (Get-Content $restartMarker -Raw).Trim() -ne $bootTime) {
    Remove-Item -Force $restartMarker   # left by the round before the restart that just happened
}
function Request-Restart([string]$why) {
    Set-Content -Path $restartMarker -Value $bootTime -Encoding ascii
    # ten seconds: after this script has exited and Vagrant has cleaned up its scheduled task
    shutdown.exe /r /t 10 /f /d p:4:1 /c "AnyFS lab: $why"
    Write-Host "$($env:COMPUTERNAME): $why; restarting to continue"
    exit 0
}
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$setupName = "$($env:SP_NETBIOS)\$($env:SP_SETUP_USER)"
$setupCredential = New-Object System.Management.Automation.PSCredential($setupName, (ConvertTo-SecureString $env:SP_SETUP_PASSWORD -AsPlainText -Force))

# --- OpenSSH Server, running and starting at boot, PowerShell as its shell, the lab's key -----------
# (The same steps as windows/provision.ps1: check.ps1 and the tests reach every Windows machine this way.)
if (-not (Get-Service sshd -ErrorAction SilentlyContinue)) {
    Add-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0' | Out-Null
}
Set-Service sshd -StartupType Automatic
Start-Service sshd
if (-not (Test-Path 'HKLM:\SOFTWARE\OpenSSH')) { New-Item -Path 'HKLM:\SOFTWARE\OpenSSH' | Out-Null }
Set-ItemProperty -Path 'HKLM:\SOFTWARE\OpenSSH' -Name DefaultShell -Type String `
    -Value "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
# sshd.exe is wherever the service says: System32\OpenSSH for the Windows capability, Program Files\OpenSSH-Win64
# on a box that ships Win32-OpenSSH (gusztavvargadr's Windows Server 2019, where the capability is not present).
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

# --- Two adapters: NAT (Vagrant's own connection and the internet) and the LAN ---------------------
# Both ask the domain controller for names (it forwards the rest), and only the LAN address registers
# in the domain's DNS: a 10.0.2.x record would point the other machines at an address they can't reach.
$nat = Get-NetIPAddress -AddressFamily IPv4 -IPAddress '10.0.2.*' | Select-Object -First 1
$lan = Get-NetIPAddress -AddressFamily IPv4 -IPAddress $env:SP_LAN_IP -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $nat -or -not $lan) {
    throw "expected a NAT adapter (10.0.2.x) and the LAN adapter at $($env:SP_LAN_IP), found $((Get-NetIPAddress -AddressFamily IPv4).IPAddress -join ', ')"
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
    Add-Computer -DomainName $env:SP_DOMAIN -Credential $setupCredential -Force -WarningAction SilentlyContinue
    Request-Restart "joined $($env:SP_DOMAIN)"
}

# --- Room for SharePoint's PowerShell: WinRM caps a shell and its children at 1 GB by default --------------
# DSC runs each SharePointDsc resource as the setup account in a WinRM shell of its own, and SPSite creates
# a site collection in a child powershell.exe; loading SharePoint there blew the cap (OutOfMemoryException,
# exit code -532462766, 18-Oct-2026). The new cap takes effect for shells made after a restart.
$shellQuota = 8192
if ((Get-Item WSMan:\localhost\Shell\MaxMemoryPerShellMB).Value -lt $shellQuota -or
    (Get-Item WSMan:\localhost\Plugin\Microsoft.PowerShell\Quotas\MaxMemoryPerShellMB).Value -lt $shellQuota) {
    Set-Item WSMan:\localhost\Shell\MaxMemoryPerShellMB -Value $shellQuota -Force
    Set-Item WSMan:\localhost\Plugin\Microsoft.PowerShell\Quotas\MaxMemoryPerShellMB -Value $shellQuota -Force
    Request-Restart "WinRM's memory cap per shell is now $shellQuota MB"
}

# --- The setup account administers this machine (SharePoint's installer and farm run as it) ---------
if (-not (Get-LocalGroupMember -Group Administrators -Member $setupName -ErrorAction SilentlyContinue)) {
    Add-LocalGroupMember -Group Administrators -Member $setupName
}

# --- Installation images: the ISOs in \\vboxsvr\sharepoint (tools\sharepoint on the PC) are copied here first,
# because Mount-DiskImage wants a local file; in a remote session the volume can take a moment to appear.
function Mount-LabImage([string]$Path) {
    $image = Mount-DiskImage -ImagePath $Path -PassThru
    for ($i = 0; $i -lt 30; $i++) {
        $letter = ($image | Get-Volume -ErrorAction SilentlyContinue).DriveLetter
        if ($letter) { return $letter }
        Start-Sleep -Seconds 2
        $image = Get-DiskImage -ImagePath $Path
    }
    throw "$Path is mounted but has no drive letter"
}

# --- SQL Server 2022 Developer: the engine, TCP on, MAXDOP 1 (SharePoint insists), 6 GB cap ---------
# Installed from the Developer ISO that scripts\fetch-sharepoint.ps1 puts next to SharePoint's. (The
# bootstrapper Microsoft's page offers, which downloads the media itself, refuses to run once it is a
# few months old; in quiet mode it just hangs.)
# A farm that borrows another machine's SQL Server (SP_SQL_SERVER) installs none; it needs that machine up.
$sqlServer = if ($env:SP_SQL_SERVER) { $env:SP_SQL_SERVER } else { $env:COMPUTERNAME }
if ($env:SP_SQL_SERVER) {
    if (-not (Test-NetConnection -ComputerName $env:SP_SQL_SERVER -Port 1433 -WarningAction SilentlyContinue).TcpTestSucceeded) {
        throw "SQL Server at $($env:SP_SQL_SERVER) is not answering on 1433: is that machine up and built? (vagrant up <its name>)"
    }
    Write-Host "Using the SQL Server at $($env:SP_SQL_SERVER)"
} elseif (-not (Get-Service MSSQLSERVER -ErrorAction SilentlyContinue)) {
    $iso = '\\vboxsvr\sharepoint\SQLServer2022-x64-ENU-Dev.iso'
    if (-not (Test-Path $iso)) { throw "No SQL Server image at ${iso}: run scripts\fetch-sharepoint.ps1 on the PC first" }
    $sqlDir = 'C:\anyfs-lab\sql'
    New-Item -ItemType Directory -Force -Path $sqlDir | Out-Null
    $localIso = Join-Path $sqlDir 'SQLServer2022-x64-ENU-Dev.iso'
    Write-Host 'Copying the SQL Server image'
    Copy-Item $iso $localIso -Force
    $letter = Mount-LabImage $localIso
    try {
        Write-Host 'Installing SQL Server'
        $sqlArgs = '/Q /ACTION=Install /FEATURES=SQLEngine /INSTANCENAME=MSSQLSERVER ' +
            "/SQLSYSADMINACCOUNTS=`"$setupName`" `"BUILTIN\Administrators`" " +
            '/TCPENABLED=1 /SQLMAXDOP=1 /SQLMAXMEMORY=6144 /SQLSVCINSTANTFILEINIT=True /UPDATEENABLED=False ' +
            '/IACCEPTSQLSERVERLICENSETERMS /SUPPRESSPRIVACYSTATEMENTNOTICE'
        $p = Start-Process "${letter}:\setup.exe" -ArgumentList $sqlArgs -Wait -PassThru
    } finally {
        Dismount-DiskImage -ImagePath $localIso | Out-Null
    }
    Remove-Item $localIso -Force
    if ($p.ExitCode -eq 3010) { Request-Restart 'SQL Server is installed' }
    if ($p.ExitCode -ne 0) { throw "SQL Server's setup exited with $($p.ExitCode): see C:\Program Files\Microsoft SQL Server\160\Setup Bootstrap\Log\Summary.txt" }
}

# --- SharePoint's installation image, copied out of the ISO once ---------------------------------
$spDir = 'C:\anyfs-lab\sharepoint'
$mediaDir = Join-Path $spDir 'media'
if (-not (Test-Path (Join-Path $mediaDir 'setup.exe'))) {
    $iso = "\\vboxsvr\sharepoint\$($env:SP_IMAGE)"
    if (-not (Test-Path $iso)) { throw "No SharePoint image at ${iso}: run scripts\fetch-sharepoint.ps1 on the PC first" }
    New-Item -ItemType Directory -Force -Path $spDir | Out-Null
    $localIso = Join-Path $spDir $env:SP_IMAGE
    Write-Host 'Copying the SharePoint image'
    Copy-Item $iso $localIso -Force
    $letter = Mount-LabImage $localIso
    try {
        robocopy.exe "${letter}:\" $mediaDir /E /R:2 /W:5 /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "copying the SharePoint image failed (robocopy $LASTEXITCODE)" }
    } finally {
        Dismount-DiskImage -ImagePath $localIso | Out-Null
    }
    Remove-Item $localIso -Force
}

# --- SharePointDsc from the PowerShell Gallery, and a Local Configuration Manager that leaves restarts to us
if (-not (Get-PackageProvider -Name NuGet -ListAvailable -ErrorAction SilentlyContinue)) {
    Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force | Out-Null
}
if (-not (Get-Module -ListAvailable SharePointDsc | Where-Object { $_.Version -eq [version]$env:SP_DSC_VERSION })) {
    Install-Module SharePointDsc -RequiredVersion $env:SP_DSC_VERSION -Force -AllowClobber -Scope AllUsers
}

# The configurations live in their own file (PowerShell resolves Import-DscResource when it parses a
# file, so they could not be here before the module existed); the Vagrantfile uploads it next to this.
. 'C:\anyfs-lab\sp-farm-configuration.ps1'
AnyFsLcm -OutputPath 'C:\anyfs-lab\dsc-lcm' | Out-Null
Set-DscLocalConfigurationManager -Path 'C:\anyfs-lab\dsc-lcm' -Force

# --- The farm, as SharePointDsc describes it ------------------------------------------------------
$farmCredential = New-Object System.Management.Automation.PSCredential("$($env:SP_NETBIOS)\$($env:SP_FARM_USER)", (ConvertTo-SecureString $env:SP_FARM_PASSWORD -AsPlainText -Force))
$passphrase = New-Object System.Management.Automation.PSCredential('passphrase', (ConvertTo-SecureString $env:SP_PASSPHRASE -AsPlainText -Force))
$configurationData = @{ AllNodes = @(@{ NodeName = 'localhost'; PSDscAllowPlainTextPassword = $true; PSDscAllowDomainUser = $true }) }
$alternateUrl = "http://$($env:SP_LAN_IP)"
AnyFsSharePoint -ConfigurationData $configurationData -OutputPath 'C:\anyfs-lab\dsc' `
    -SetupAccount $setupCredential -FarmAccount $farmCredential -Passphrase $passphrase -MediaDir $mediaDir `
    -ProductKey $env:SP_PRODUCT_KEY -WebUrl $env:SP_WEB_URL -AlternateUrl $alternateUrl -Site $env:SP_SITE `
    -SqlServer $sqlServer -DatabasePrefix $env:SP_DB_PREFIX | Out-Null

# A run still going from before (the LCM resuming after a restart on a VM whose LCM was set up by an older
# version of this script, or SharePoint's setup.exe it left behind) is waited for, not aborted: -Force would
# stop the LCM but not setup.exe, and a second setup refuses to start while one runs.
$deadline = (Get-Date).AddMinutes(60)
while ((Get-Date) -lt $deadline) {
    $busy = (Get-DscLocalConfigurationManager).LCMState -eq 'Busy'
    $setup = Get-Process setup -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$mediaDir\*" }
    if (-not $busy -and -not $setup) { break }
    Write-Host "Waiting for $(if ($busy) { 'the configuration manager' } else { 'SharePoint''s setup' }) to finish a run started before this one"
    Start-Sleep -Seconds 30
}
Write-Host 'Applying the SharePoint configuration (prerequisites, installer, farm, web application, sites)'
Start-DscConfiguration -Path 'C:\anyfs-lab\dsc' -Wait -Force -Verbose
$status = Get-DscConfigurationStatus
if ($status.RebootRequested) { Request-Restart 'the SharePoint configuration needs a restart' }
if ($status.Status -ne 'Success') {
    $failed = ($status.ResourcesNotInDesiredState | ForEach-Object { $_.ResourceId }) -join ', '
    throw "the SharePoint configuration ended with $($status.Status) (not in the desired state: $failed); see Get-DscConfigurationStatus and the Microsoft-Windows-DSC/Operational event log"
}

# --- The test account is a member of the test site: SharePoint's PowerShell as the setup account ----------
# Through a scheduled task, which gives the setup account the real logon SharePoint's cmdlets want (a DSC
# Script resource run as a credential fails with "An item with the same key has already been added").
function Invoke-AsSetupAccount([string]$Name, [string]$Command) {
    $logPath = "C:\anyfs-lab\$Name.log"
    Remove-Item $logPath -Force -ErrorAction SilentlyContinue
    # the try/catch keeps a terminating error inside the log (thrown out of the redirected pipeline it is lost)
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -Command `"& { try { $Command } catch { 'ANYFS-ERROR: ' + `$_.Exception.Message; `$_.ScriptStackTrace } } *> '$logPath'`""
    Register-ScheduledTask -TaskName "anyfs-$Name" -Action $action -User $setupName -Password $env:SP_SETUP_PASSWORD -RunLevel Highest -Force | Out-Null
    try {
        Start-ScheduledTask -TaskName "anyfs-$Name"
        $deadline = (Get-Date).AddMinutes(10)
        do { Start-Sleep -Seconds 5 } while ((Get-ScheduledTask -TaskName "anyfs-$Name").State -eq 'Running' -and (Get-Date) -lt $deadline)
        $result = (Get-ScheduledTaskInfo -TaskName "anyfs-$Name").LastTaskResult
    } finally {
        Unregister-ScheduledTask -TaskName "anyfs-$Name" -Confirm:$false
    }
    $output = if (Test-Path $logPath) { (Get-Content $logPath -Raw).Trim() } else { '(no output)' }
    if ($result -ne 0 -or $output -notmatch 'ANYFS-OK') { throw "$Name as ${setupName} failed (task result $result):`n$output" }
    ($output -split "`r?`n" | Where-Object { $_ -ne 'ANYFS-OK' }) -join "`n"
}
$testName = "$($env:SP_NETBIOS)\$($env:SP_TEST_USER)"
Write-Host (Invoke-AsSetupAccount -Name 'sp-test-user' -Command "& 'C:\anyfs-lab\sp-test-user.ps1' -WebUrl '$($env:SP_WEB_URL)' -Site '$($env:SP_SITE)' -TestUser '$testName'")

# --- What the smoke test needs to try the site ------------------------------------------------------
@{
    web_url            = $env:SP_WEB_URL
    alternate_url      = $alternateUrl
    site               = $env:SP_SITE
    test_user          = "$($env:SP_NETBIOS)\$($env:SP_TEST_USER)"
    test_password      = $env:SP_TEST_PASSWORD
    central_admin_port = 9999
    sql_server         = $env:SP_SQL_SERVER   # empty: this machine's own
    product            = $env:SP_PRODUCT
} | ConvertTo-Json | Set-Content -Path 'C:\anyfs-lab\sp-smoke.json' -Encoding ascii

# --- Summary ------------------------------------------------------------------------------------
$os = Get-CimInstance Win32_OperatingSystem
$sqlVersion = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\MSSQL16.MSSQLSERVER\Setup' -ErrorAction SilentlyContinue).Version
$spVersion = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Shared Tools\Web Server Extensions\16.0' -ErrorAction SilentlyContinue).Version
Write-Host "$($env:COMPUTERNAME): $($os.Caption) build $($os.BuildNumber), member of $((Get-CimInstance Win32_ComputerSystem).Domain), $(if ($env:SP_SQL_SERVER) { "SQL Server on $($env:SP_SQL_SERVER)" } else { "SQL Server $sqlVersion" }), SharePoint $($env:SP_PRODUCT) $spVersion, $($env:SP_WEB_URL)$($env:SP_SITE) and $alternateUrl$($env:SP_SITE), sshd $((Get-Service sshd).Status)"
