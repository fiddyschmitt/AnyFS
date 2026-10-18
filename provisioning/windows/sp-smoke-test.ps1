<#
    AnyFS test lab: checks the SharePoint Server machine. Vagrant runs it after every `vagrant up`, `reload`
    and `provision`; check.ps1 runs it over the LAN; run it any time with
    `powershell -ExecutionPolicy Bypass -File C:\anyfs-lab\smoke-test.ps1`. Exits 1 on any FAIL.
    The provisioner leaves what the last checks need (the site's URL and the test account) in
    C:\anyfs-lab\sp-smoke.json when the farm is up.
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
Check "the PC's media folder at \\vboxsvr\sharepoint" { Test-Path '\\vboxsvr\sharepoint' }

if (-not $system.PartOfDomain) {
    Warn 'not in the domain yet (the first provisioning round restarts to finish the join)'
    if ($script:failed) { exit 1 }
    exit 0
}
Pass "member of $($system.Domain)"

# right after a boot the services take a while (SQL Server a minute or two on this VM): wait, do not fail at once
function Wait-Running([string]$name, [int]$seconds = 150) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        $service = Get-Service $name -ErrorAction SilentlyContinue
        if (-not $service) { return $null }
        if ($service.Status -eq 'Running') { return $service }
        Start-Sleep -Seconds 5
    } while ((Get-Date) -lt $deadline)
    return $service
}
$sql = Wait-Running MSSQLSERVER
$borrowed = if (Test-Path 'C:\anyfs-lab\sp-smoke.json') { (Get-Content 'C:\anyfs-lab\sp-smoke.json' -Raw | ConvertFrom-Json).sql_server } else { $null }
if ($sql) { Check 'SQL Server running' { $sql.Status -eq 'Running' } }
elseif ($borrowed) { Check "SQL Server on $borrowed answers on 1433" { (Test-NetConnection -ComputerName $borrowed -Port 1433 -WarningAction SilentlyContinue).TcpTestSucceeded } }
else { Warn 'SQL Server not installed yet' }
$timer = Wait-Running SPTimerV4
if ($timer) { Check 'SharePoint timer service running' { $timer.Status -eq 'Running' } } else { Warn 'SharePoint not installed yet' }
$iis = Get-Service W3SVC -ErrorAction SilentlyContinue
if ($iis) { Check 'IIS running' { $iis.Status -eq 'Running' } } else { Warn 'IIS not installed yet' }

$config = 'C:\anyfs-lab\sp-smoke.json'
if (-not (Test-Path $config)) {
    Warn 'the farm is not configured yet (no C:\anyfs-lab\sp-smoke.json)'
} else {
    $cfg = Get-Content $config -Raw | ConvertFrom-Json
    $password = ConvertTo-SecureString $cfg.test_password -AsPlainText -Force
    $credential = New-Object System.Management.Automation.PSCredential($cfg.test_user, $password)
    foreach ($base in @($cfg.web_url, $cfg.alternate_url)) {
        $url = "$base$($cfg.site)/_api/web/title"
        Check "$url answers as $($cfg.test_user) over NTLM" {
            $response = Invoke-WebRequest -Uri $url -Credential $credential -UseBasicParsing -Headers @{ Accept = 'application/json;odata=nometadata' }
            $response.StatusCode -eq 200 -and ($response.Content | ConvertFrom-Json).value
        }
    }
    Check "Central Administration listens on $($cfg.central_admin_port)" {
        # http.sys listens on the IPv6 wildcard only, which Get-NetTCPConnection's count missed once; a connect is the truth
        (Test-NetConnection -ComputerName $env:COMPUTERNAME -Port $cfg.central_admin_port -WarningAction SilentlyContinue).TcpTestSucceeded
    }
}

if ($script:failed) { exit 1 }
exit 0
