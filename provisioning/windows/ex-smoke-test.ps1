<#
    AnyFS test lab: is the Exchange server what the Outlook source's tests expect? Runs inside the VM after every
    `vagrant up`, `reload` and `provision`: the services, EWS and Outlook on the web answering on the server's own
    certificate, the configuration marker. PASS/WARN/FAIL lines; exit 1 on any FAIL.
#>
$ErrorActionPreference = 'Continue'
$failed = $false
function Pass($s) { Write-Host "PASS  $s" }
function Warn($s) { Write-Host "WARN  $s" }
function Fail($s) { Write-Host "FAIL  $s"; $script:failed = $true }

foreach ($service in 'MSExchangeADTopology', 'MSExchangeIS', 'MSExchangeTransport', 'MSExchangeFrontEndTransport', 'MSExchangeIMAP4', 'W3SVC', 'sshd') {
    $status = (Get-Service $service -ErrorAction SilentlyContinue).Status
    if ($status -eq 'Running') { Pass "$service is running" } else { Fail "$service is $status" }
}

[Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
foreach ($probe in @(
    @{ Url = "https://localhost/EWS/Exchange.asmx"; Name = 'EWS'; Expect = @(200, 401) },
    @{ Url = "https://localhost/owa/"; Name = 'Outlook on the web'; Expect = @(200, 302, 401) },
    @{ Url = "https://localhost/autodiscover/autodiscover.xml"; Name = 'Autodiscover'; Expect = @(200, 401, 405) })) {
    try {
        $response = [Net.HttpWebRequest]::Create($probe.Url)
        $response.Method = 'GET'
        $response.AllowAutoRedirect = $false
        $response.Timeout = 60000
        $code = [int]$response.GetResponse().StatusCode
    }
    catch [Net.WebException] {
        $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
    }
    if ($probe.Expect -contains $code) { Pass "$($probe.Name) answers ($code)" } else { Fail "$($probe.Name) answers $code" }
}

if (Test-Path 'C:\anyfs-lab\exchange-configured') { Pass 'the mailboxes and settings are configured' } else { Fail 'the configuration has not run' }
if (Test-Path 'C:\anyfs-lab\exchange-seeded') { Pass 'the tester mailbox is seeded' } else { Warn 'the tester mailbox is not seeded yet' }

if ($failed) { exit 1 }
exit 0
