<#
    AnyFS test lab: the SharePoint farm's domain controller (Windows Server Core). Active Directory and DNS
    for the lab's domain, and the accounts SharePoint insists on, which have to be domain accounts.

    Vagrant runs this inside the VM (over WinRM, elevated, as Administrator: promotion deletes every other
    local account) when it first creates the VM, after each restart it asks for, and on every
    `vagrant provision`. Two rounds the first time: round one installs the forest and asks for a restart;
    round two, on the running domain controller, points DNS the right way, creates the accounts and the
    SharePoint server's name. Re-runs change nothing unless something drifted.

    Inputs (from the Vagrantfile): LAB_PUBKEY, LAB_USER, LAB_PASSWORD; SP_DOMAIN, SP_NETBIOS,
    SP_SAFE_MODE_PASSWORD; SP_SETUP_USER/_PASSWORD, SP_FARM_USER/_PASSWORD, SP_TEST_USER/_PASSWORD;
    SP_SERVERS, the SharePoint machines' DNS records as name=ip;name=ip.
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

foreach ($name in 'LAB_PUBKEY', 'LAB_USER', 'LAB_PASSWORD', 'SP_DOMAIN', 'SP_NETBIOS', 'SP_SAFE_MODE_PASSWORD',
                  'SP_SETUP_USER', 'SP_SETUP_PASSWORD', 'SP_FARM_USER', 'SP_FARM_PASSWORD', 'SP_TEST_USER', 'SP_TEST_PASSWORD',
                  'SP_SERVERS') {
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
# Only the LAN address may ever appear in the domain's DNS: a 10.0.2.x record would send the SharePoint
# server to an address it can't reach. So the NAT adapter never registers itself, and round two removes
# anything that slipped through.
$nat = Get-NetIPAddress -AddressFamily IPv4 -IPAddress '10.0.2.*' | Select-Object -First 1
$lan = Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object { $_.IPAddress -notlike '10.0.2.*' -and $_.IPAddress -ne '127.0.0.1' } | Select-Object -First 1
if (-not $nat -or -not $lan) {
    throw "expected a NAT adapter (10.0.2.x) and a LAN adapter, found $((Get-NetIPAddress -AddressFamily IPv4).IPAddress -join ', ')"
}
Set-DnsClient -InterfaceIndex $nat.InterfaceIndex -RegisterThisConnectionsAddress $false
Set-DnsClient -InterfaceIndex $lan.InterfaceIndex -RegisterThisConnectionsAddress $true
# What the NAT adapter resolves names through (VirtualBox's proxy): the domain's DNS server forwards there.
$natDns = (Get-DnsClientServerAddress -InterfaceIndex $nat.InterfaceIndex -AddressFamily IPv4).ServerAddresses |
    Where-Object { $_ -ne '127.0.0.1' }
if ($natDns) { Set-Content -Path 'C:\anyfs-lab\nat-dns.txt' -Value $natDns -Encoding ascii }

# --- Round one: the forest ------------------------------------------------------------------------
$role = (Get-CimInstance Win32_ComputerSystem).DomainRole   # 4 and 5 are domain controllers
if ($role -lt 4) {
    Install-WindowsFeature AD-Domain-Services, DNS -IncludeManagementTools | Out-Null
    Import-Module ADDSDeployment
    $safeMode = ConvertTo-SecureString $env:SP_SAFE_MODE_PASSWORD -AsPlainText -Force
    Install-ADDSForest -DomainName $env:SP_DOMAIN -DomainNetbiosName $env:SP_NETBIOS `
        -SafeModeAdministratorPassword $safeMode -InstallDns -NoRebootOnCompletion -Force -Confirm:$false `
        -WarningAction SilentlyContinue | Out-Null
    # From here until the restart, WinRM refuses logons (the local accounts are on their way out), so the
    # restart has to come from here.
    Request-Restart "the forest $($env:SP_DOMAIN) is installed; becoming its domain controller"
}

# --- Round two: on the domain controller ----------------------------------------------------------
# Active Directory Web Services takes a while after the first boot as a domain controller.
$deadline = (Get-Date).AddMinutes(15)
while ($true) {
    try {
        Import-Module ActiveDirectory
        $domain = Get-ADDomain -ErrorAction Stop
        break
    } catch {
        if ((Get-Date) -gt $deadline) { throw "Active Directory did not come up: $_" }
        Start-Sleep -Seconds 10
    }
}

# DNS: this machine asks itself on every adapter, forwards what it doesn't know to VirtualBox's proxy,
# and holds no 10.0.2.x records.
Set-DnsClientServerAddress -InterfaceIndex $lan.InterfaceIndex -ServerAddresses '127.0.0.1'
Set-DnsClientServerAddress -InterfaceIndex $nat.InterfaceIndex -ServerAddresses '127.0.0.1'
$forwarders = if (Test-Path 'C:\anyfs-lab\nat-dns.txt') { @(Get-Content 'C:\anyfs-lab\nat-dns.txt') } else { @('10.0.2.3') }
Set-DnsServerForwarder -IPAddress $forwarders -UseRootHint $false
Get-DnsServerResourceRecord -ZoneName $env:SP_DOMAIN -RRType A |
    Where-Object { $_.RecordData.IPv4Address.IPAddressToString -like '10.0.2.*' } |
    ForEach-Object { Remove-DnsServerResourceRecord -ZoneName $env:SP_DOMAIN -InputObject $_ -Force }

# The SharePoint server's name, so the domain resolves it before it has joined (and if it registers
# late). Its own registration keeps the record fresh after that.
foreach ($pair in $env:SP_SERVERS -split ';') {
    $serverName, $serverIp = $pair -split '=', 2
    $record = Get-DnsServerResourceRecord -ZoneName $env:SP_DOMAIN -Name $serverName -RRType A -ErrorAction SilentlyContinue
    if ($record -and $record.RecordData.IPv4Address.IPAddressToString -ne $serverIp) {
        Remove-DnsServerResourceRecord -ZoneName $env:SP_DOMAIN -InputObject $record -Force
        $record = $null
    }
    if (-not $record) {
        Add-DnsServerResourceRecordA -ZoneName $env:SP_DOMAIN -Name $serverName -IPv4Address $serverIp | Out-Null
        Write-Host "DNS: $serverName -> $serverIp"
    }
}

# --- The accounts, in their own OU ------------------------------------------------------------------
$ouName = 'AnyFS Lab'
$ou = "OU=$ouName,$($domain.DistinguishedName)"
if (-not (Get-ADOrganizationalUnit -Filter "Name -eq '$ouName'" -ErrorAction SilentlyContinue)) {
    New-ADOrganizationalUnit -Name $ouName -Path $domain.DistinguishedName -ProtectedFromAccidentalDeletion $false | Out-Null
}
$accounts = @(
    @{ Name = $env:SP_SETUP_USER; Password = $env:SP_SETUP_PASSWORD; Description = 'SharePoint setup account: installs and configures the farm; SQL Server sysadmin; owns the test site' },
    @{ Name = $env:SP_FARM_USER;  Password = $env:SP_FARM_PASSWORD;  Description = 'SharePoint farm account: the timer service, the databases, the web application pool' },
    @{ Name = $env:SP_TEST_USER;  Password = $env:SP_TEST_PASSWORD;  Description = 'The account the AnyFS tests sign in to SharePoint as: a member of the test site' }
)
# Vagrant, check.ps1 and the tests log in to this machine as Administrator: the box's 42-day password
# expiry must not take that away (promotion made it the domain's Administrator, under the domain's policy).
Set-ADUser -Identity Administrator -PasswordNeverExpires $true
foreach ($a in $accounts) {
    $secure = ConvertTo-SecureString $a.Password -AsPlainText -Force
    $existing = Get-ADUser -Filter "SamAccountName -eq '$($a.Name)'" -ErrorAction SilentlyContinue
    if (-not $existing) {
        New-ADUser -Name $a.Name -SamAccountName $a.Name -UserPrincipalName "$($a.Name)@$($env:SP_DOMAIN)" -Path $ou `
            -AccountPassword $secure -Enabled $true -PasswordNeverExpires $true -CannotChangePassword $true `
            -Description $a.Description | Out-Null
    } else {
        Set-ADAccountPassword -Identity $a.Name -NewPassword $secure -Reset
        Set-ADUser -Identity $a.Name -Enabled $true -PasswordNeverExpires $true -CannotChangePassword $true
    }
}

# --- Summary ------------------------------------------------------------------------------------
$os = Get-CimInstance Win32_OperatingSystem
Write-Host "$($env:COMPUTERNAME): $($os.Caption) build $($os.BuildNumber), domain controller for $($domain.DNSRoot) ($($domain.NetBIOSName)), DNS forwarding to $($forwarders -join ', '), accounts $(($accounts | ForEach-Object Name) -join ', '), sshd $((Get-Service sshd).Status)"
