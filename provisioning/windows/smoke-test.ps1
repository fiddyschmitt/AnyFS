<#
    AnyFS test lab: checks what the tests rely on, on this Windows machine. Vagrant runs it after every
    `vagrant up`, `reload` and `provision`; check.ps1 runs it over the LAN; run it any time with
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
$addresses = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '10.0.2.*' -and $_.IPAddress -ne '127.0.0.1' }).IPAddress -join ', '
Write-Output "$($env:COMPUTERNAME) ($addresses): $($os.Caption) build $($os.BuildNumber)"

Check 'sshd running' { (Get-Service sshd).Status -eq 'Running' }
Check 'SSH sessions get PowerShell' { (Get-ItemProperty 'HKLM:\SOFTWARE\OpenSSH').DefaultShell -like '*powershell.exe' }
Check 'firewall off' { -not (Get-NetFirewallProfile | Where-Object Enabled) }
$winfspDll = Join-Path ${env:ProgramFiles(x86)} 'WinFsp\bin\winfsp-x64.dll'
Check "WinFsp installed ($(if (Test-Path $winfspDll) { (Get-Item $winfspDll).VersionInfo.FileVersion } else { 'missing' }))" {
    (Test-Path $winfspDll) -and (Get-Service WinFsp.Launcher -ErrorAction SilentlyContinue).Status -eq 'Running'
}
Check 'WebClient running, file size cap lifted' {
    # the DWORD 0xFFFFFFFF reads back as 4294967295 (or -1), and the literal 0xFFFFFFFF is the Int32 -1
    $cap = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\WebClient\Parameters').FileSizeLimitInBytes
    (Get-Service WebClient).Status -eq 'Running' -and ($cap -eq 4294967295 -or $cap -eq -1)
}
Check 'the SMB client can use a port other than 445 (Windows 11 24H2+, net use /TCPPORT)' { $os.BuildNumber -ge 26100 }
Check "the PC's publish folder at \\vboxsvr\publish" { Test-Path '\\vboxsvr\publish' }
Check 'C:\anyfs-lab\anyfs writable' {
    $probe = "C:\anyfs-lab\anyfs\.smoke.$PID"
    Set-Content $probe 'x'; Remove-Item $probe; $true
}
Check 'curl.exe' { Get-Command curl.exe -ErrorAction SilentlyContinue }
# the lab's domain, when there is one: the domain controller answers for its names (it may be halted: a WARN)
$nrpt = @(Get-DnsClientNrptRule | Where-Object { $_.Comment -like 'AnyFS lab*' })
if ($nrpt.Count -gt 0) {
    Pass "the lab domain ($(@($nrpt[0].Namespace) -join ', ')) resolves through $(@($nrpt[0].NameServers) -join ', ')"
    $domain = @($nrpt[0].Namespace | Where-Object { -not $_.StartsWith('.') })[0]
    if (Resolve-DnsName "_kerberos._tcp.$domain" -Type SRV -DnsOnly -QuickTimeout -ErrorAction SilentlyContinue) {
        Pass "the realm's KDC is found ($domain)"
    } else {
        Warn "the realm's KDC is not found ($domain): is the domain controller up?"
    }
}

# The account logs on at boot, and RunRemote starts in that session: give both a couple of minutes.
$deadline = (Get-Date).AddMinutes(3)
do {
    $session = (query user 2>$null) -match '\bconsole\b.*\bActive\b'
    $udp = Get-NetUDPEndpoint -LocalPort 8888 -ErrorAction SilentlyContinue
    if ($session -and $udp) { break }
    Start-Sleep -Seconds 5
} while ((Get-Date) -lt $deadline)
if ($session) { Pass 'the account is logged on at the console' } else { Fail 'the account is logged on at the console' }
if ($udp) { Pass 'RunRemote listening (UDP 8888)' } else { Fail 'RunRemote listening (UDP 8888)' }

if ($script:failed) { exit 1 }
exit 0
