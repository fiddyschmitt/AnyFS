<#
    AnyFS test lab: checks the SharePoint farm's domain controller. Vagrant runs it after every `vagrant up`,
    `reload` and `provision`; check.ps1 runs it over the LAN; run it any time with
    `powershell -ExecutionPolicy Bypass -File C:\anyfs-lab\smoke-test.ps1`. Exits 1 on any FAIL.
#>
$ErrorActionPreference = 'Continue'
$script:failed = $false
function Pass([string]$what) { Write-Output "PASS  $what" }
function Warn([string]$what) { Write-Output "WARN  $what" }
function Fail([string]$what) { Write-Output "FAIL  $what"; $script:failed = $true }
function Check([string]$what, [scriptblock]$test) {
    $ok = $false
    try { $ok = [bool](& $test) } catch { }
    if ($ok) { Pass $what } else { Fail $what }
}

$os = Get-CimInstance Win32_OperatingSystem
$system = Get-CimInstance Win32_ComputerSystem
$addresses = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '10.0.2.*' -and $_.IPAddress -ne '127.0.0.1' }).IPAddress -join ', '
Write-Output "$($env:COMPUTERNAME) ($addresses): $($os.Caption) build $($os.BuildNumber)"

Check 'sshd running' { (Get-Service sshd).Status -eq 'Running' }
Check 'SSH sessions get PowerShell' { (Get-ItemProperty 'HKLM:\SOFTWARE\OpenSSH').DefaultShell -like '*powershell.exe' }
Check 'firewall off' { -not (Get-NetFirewallProfile | Where-Object Enabled) }

if ($system.DomainRole -lt 4) {
    Warn 'not a domain controller yet (the first provisioning round restarts to finish the promotion)'
} else {
    Pass "domain controller for $($system.Domain)"
    Check 'Active Directory Domain Services running' { (Get-Service NTDS).Status -eq 'Running' }
    Check 'DNS Server running' { (Get-Service DNS).Status -eq 'Running' }
    Check 'Netlogon running' { (Get-Service Netlogon).Status -eq 'Running' }
    Check 'Active Directory answers (Get-ADDomain)' { Import-Module ActiveDirectory; (Get-ADDomain).DNSRoot -eq $system.Domain }
    $domainRecords = @(Get-DnsServerResourceRecord -ZoneName $system.Domain -RRType A -ErrorAction SilentlyContinue)
    Check 'no 10.0.2.x addresses in the domain zone' {
        -not ($domainRecords | Where-Object { $_.RecordData.IPv4Address.IPAddressToString -like '10.0.2.*' })
    }
    $forwarders = (Get-DnsServerForwarder -ErrorAction SilentlyContinue).IPAddress.IPAddressToString -join ', '
    Check "DNS forwards outside names ($forwarders)" { $forwarders -and (Resolve-DnsName microsoft.com -Type A -ErrorAction Stop) }
    foreach ($account in 'sp_setup', 'sp_farm', 'sp_user') {
        Check "account $account exists and is enabled" { (Get-ADUser -Identity $account -ErrorAction Stop).Enabled }
    }
    $farms = @($domainRecords | Where-Object { $_.HostName -like 'anyfs-sp-*' })
    if ($farms.Count -eq 0) { Warn 'no DNS record for a SharePoint server yet' }
    foreach ($sp in $farms) { Pass "DNS has $($sp.HostName) at $($sp.RecordData.IPv4Address.IPAddressToString)" }
    foreach ($other in @($domainRecords | Where-Object { $_.HostName -like 'anyfs-ex-*' -or $_.HostName -like 'anyfs-gitlab-*' })) {
        Pass "DNS has $($other.HostName) at $($other.RecordData.IPv4Address.IPAddressToString)"
    }
    # the GitLab server's Kerberos service account, when there is one
    $gitlab = Get-ADUser -Filter "SamAccountName -eq 'svc_gitlab'" -Properties ServicePrincipalNames, KerberosEncryptionType -ErrorAction SilentlyContinue
    if ($gitlab) {
        Check 'svc_gitlab is enabled and marked for AES' { $gitlab.Enabled -and ($gitlab.KerberosEncryptionType -match 'AES256') }
        Check "svc_gitlab holds GitLab's principal ($($gitlab.ServicePrincipalNames -join ', '))" { @($gitlab.ServicePrincipalNames | Where-Object { $_ -like 'HTTP/anyfs-gitlab-*' }).Count -eq 1 }
        Check 'the keytab C:\anyfs-lab\gitlab\http.keytab and its share \\anyfs-dc-1\gitlab' { (Test-Path 'C:\anyfs-lab\gitlab\http.keytab') -and (Get-SmbShare -Name gitlab -ErrorAction Stop) }
    }
}

if ($script:failed) { exit 1 }
exit 0
