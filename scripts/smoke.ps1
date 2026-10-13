<#
.SYNOPSIS
  Smoke-tests a published anyfs: version, status, a third-party plugin loading in the trimmed build, and a
  headless serve of a Mirror source over HTTP with a ranged read. Runs on Windows, Linux and macOS (pwsh).

.PARAMETER Exe
  The published anyfs executable (publish/<rid>/anyfs or anyfs.exe).

.PARAMETER SamplePluginDll
  tests/fixtures/SamplePlugin's built AnyFS.SamplePlugin.dll; copied into the plugins folder.

.EXAMPLE
  .\scripts\smoke.ps1 -Exe publish\win-x64\anyfs.exe -SamplePluginDll tests\fixtures\SamplePlugin\bin\Release\net10.0\AnyFS.SamplePlugin.dll
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Exe,
    [Parameter(Mandatory)] [string]$SamplePluginDll,
    [int]$Port = 11021
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$Exe = (Resolve-Path $Exe).Path
$SamplePluginDll = (Resolve-Path $SamplePluginDll).Path
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("anyfs-smoke-" + [guid]::NewGuid().ToString('N'))
$settings = Join-Path $work 'settings'
$plugins = Join-Path $work 'plugins'
$tree = Join-Path $work 'tree'
New-Item -ItemType Directory -Force $settings, $plugins, ([System.IO.Path]::Combine($tree, 'sub')) | Out-Null
$failures = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "  ok    $what" -ForegroundColor Green } else { Write-Host "  FAIL  $what" -ForegroundColor Red; $script:failures++ }
}

Write-Host "== anyfs --version"
# a pipeline, because PowerShell does not wait for a GUI-subsystem exe (anyfs.exe on Windows) unless its output is piped
$version = (& $Exe --version 2>&1 | Out-String).Trim()
Check ($LASTEXITCODE -eq 0 -and $version -match '^\d+\.\d+\.\d+') "version printed: $version"

Write-Host "== anyfs status on a fresh settings folder (not running)"
$status = & $Exe status --settings-dir $settings 2>&1 | Out-String
Check ($LASTEXITCODE -eq 2) "exit code 2 when not running"
Check ($status -match 'mirror' -and $status -match 'http') "built-in plugins listed"

Write-Host "== third-party plugin in the trimmed build"
Copy-Item $SamplePluginDll (Join-Path $plugins 'AnyFS.SamplePlugin.dll')
$status = & $Exe status --settings-dir $settings 2>&1 | Out-String
Check ($status -match 'sample') "sample plugin loaded from plugins/"

Write-Host "== serve: Mirror of a generated tree + sample source over HTTP on port $Port"
$bytes = New-Object byte[] 300000
(New-Object System.Random 42).NextBytes($bytes)
[System.IO.File]::WriteAllBytes((Join-Path $tree 'blob.bin'), $bytes)
[System.IO.File]::WriteAllText([System.IO.Path]::Combine($tree, 'sub', 'hello.txt'), 'hello from the tree', (New-Object System.Text.UTF8Encoding $false))
$mirrorId = '11111111-1111-1111-1111-111111111111'
$sampleId = '22222222-2222-2222-2222-222222222222'
$treeJson = $tree -replace '\\', '/'
@"
{ "version": 1, "sources": [
  { "id": "$mirrorId", "name": "tree", "type": "mirror", "enabled": true, "settings": { "paths": "$treeJson" } },
  { "id": "$sampleId", "name": "sample", "type": "sample", "enabled": true, "settings": { "greeting": "smoke says hi", "files": 2 } }
] }
"@ | Set-Content -Path (Join-Path $settings 'sources.json') -Encoding utf8
@"
{ "version": 1, "collections": [ { "id": "33333333-3333-3333-3333-333333333333", "name": "smoke", "enabled": true,
  "sources": [ { "id": "$mirrorId", "path": "", "readOnly": false }, { "id": "$sampleId", "path": "sample", "readOnly": true } ],
  "mounts": [ { "id": "44444444-4444-4444-4444-444444444444", "name": "HTTP", "provider": "http", "enabled": true, "readOnly": false, "settings": { "bindAddress": "127.0.0.1", "port": $Port } },
              { "id": "55555555-5555-5555-5555-555555555555", "name": "WebDAV", "provider": "webdav", "enabled": true, "readOnly": false, "settings": { "bindAddress": "127.0.0.1", "port": $($Port + 1) } },
              { "id": "66666666-6666-6666-6666-666666666666", "name": "FTP", "provider": "ftp", "enabled": true, "readOnly": false, "settings": { "bindAddress": "127.0.0.1", "port": $($Port + 2), "allowAnonymous": true } },
              { "id": "77777777-7777-7777-7777-777777777777", "name": "SMB", "provider": "smb", "enabled": true, "readOnly": false, "settings": { "bindAddress": "127.0.0.1", "port": $($Port + 3), "guest": true } } ] } ] }
"@ | Set-Content -Path (Join-Path $settings 'collections.json') -Encoding utf8

$serveOut = Join-Path $work 'serve.out'
$serveErr = Join-Path $work 'serve.err'
$proc = Start-Process -FilePath $Exe -ArgumentList @('serve', '--settings-dir', $settings, '--log-level', 'debug') -PassThru -RedirectStandardOutput $serveOut -RedirectStandardError $serveErr -NoNewWindow
try {
    $base = "http://127.0.0.1:$Port/smoke/"
    $up = $false
    for ($i = 0; $i -lt 60 -and -not $up; $i++) {
        Start-Sleep -Milliseconds 500
        try { $null = Invoke-WebRequest -UseBasicParsing -Uri $base -TimeoutSec 2; $up = $true } catch { }
        if ($proc.HasExited) { break }
    }
    Check $up "HTTP mount answered at $base"
    if ($up) {
        # the Mirror root is the collection root here (empty source path); its tree appears under the mirrored chain
        $json = Invoke-WebRequest -UseBasicParsing -Uri ($base + '?format=json') | Select-Object -ExpandProperty Content
        Check ($json -match '"sample"') "listing shows the sample source's folder"
        $chain = (Get-Item $tree).FullName -replace '\\', '/'
        if ($chain -match '^([A-Za-z]):/(.*)$') { $chain = $matches[1].ToLowerInvariant() + '/' + $matches[2] }
        $chain = $chain.TrimStart('/')
        $encoded = ($chain -split '/' | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/'
        $blobUrl = "$base$encoded/blob.bin"
        $full = Invoke-WebRequest -UseBasicParsing -Uri $blobUrl
        Check ($full.StatusCode -eq 200 -and $full.Headers['Accept-Ranges'] -eq 'bytes' -and $full.Headers['ETag']) "full download: 200 with Accept-Ranges and ETag"
        Check ($full.Content.Length -eq $bytes.Length -and [System.Linq.Enumerable]::SequenceEqual([byte[]]$full.Content, $bytes)) "full download bytes match"
        # Windows PowerShell's Invoke-WebRequest refuses a raw Range header, so HttpClient does the ranged read
        Add-Type -AssemblyName System.Net.Http
        $http = New-Object System.Net.Http.HttpClient
        $request = New-Object System.Net.Http.HttpRequestMessage ([System.Net.Http.HttpMethod]::Get, $blobUrl)
        $request.Headers.Range = New-Object System.Net.Http.Headers.RangeHeaderValue (100000, 100999)
        $range = $http.SendAsync($request).GetAwaiter().GetResult()
        $rangeBytes = $range.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        Check ([int]$range.StatusCode -eq 206 -and $range.Content.Headers.ContentRange.ToString() -eq "bytes 100000-100999/$($bytes.Length)") "ranged read: 206 with Content-Range"
        Check ([System.Linq.Enumerable]::SequenceEqual([byte[]]$rangeBytes, [byte[]]$bytes[100000..100999])) "ranged read bytes match"
        $http.Dispose()
        $hello = Invoke-WebRequest -UseBasicParsing -Uri "$base$encoded/sub/hello.txt"
        Check ($hello.Content -eq 'hello from the tree') "text file through the mirror"
        $greeting = Invoke-WebRequest -UseBasicParsing -Uri "${base}sample/hello.txt"
        Check ($greeting.Content.Trim() -eq 'smoke says hi') "the sample plugin's file through the trimmed host"
        try { $null = Invoke-WebRequest -UseBasicParsing -Uri "${base}nope.txt"; Check $false "404 for a missing file" } catch { Check ($_.Exception.Response.StatusCode.value__ -eq 404) "404 for a missing file" }

        Write-Host "== WebDAV, FTP and SMB mounts of the same collection"
        Add-Type -AssemblyName System.Net.Http
        $dav = New-Object System.Net.Http.HttpClient
        $propfind = New-Object System.Net.Http.HttpRequestMessage ((New-Object System.Net.Http.HttpMethod 'PROPFIND'), "http://127.0.0.1:$($Port + 1)/smoke/")
        $propfind.Headers.TryAddWithoutValidation('Depth', '1') | Out-Null
        $davResponse = $dav.SendAsync($propfind).GetAwaiter().GetResult()
        # bytes, not ReadAsStringAsync: .NET Framework's HttpClient refuses the quoted charset in text/xml; charset="utf-8"
        $davBody = [System.Text.Encoding]::UTF8.GetString($davResponse.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult())
        Check ([int]$davResponse.StatusCode -eq 207 -and $davBody -match '<D:href>/smoke/sample/</D:href>') "WebDAV PROPFIND depth 1 lists the collection (207)"
        $davGet = $dav.GetStringAsync("http://127.0.0.1:$($Port + 1)/smoke/sample/hello.txt").GetAwaiter().GetResult()
        Check ($davGet.Trim() -eq 'smoke says hi') "WebDAV GET reads a file"
        $dav.Dispose()
        try {
            $ftp = [System.Net.FtpWebRequest]::Create("ftp://127.0.0.1:$($Port + 2)/smoke/")
            $ftp.Method = [System.Net.WebRequestMethods+Ftp]::ListDirectory
            $ftp.Credentials = New-Object System.Net.NetworkCredential('anonymous', 'smoke@anyfs')
            $ftp.UsePassive = $true
            $ftpResponse = $ftp.GetResponse()
            $reader = New-Object System.IO.StreamReader ($ftpResponse.GetResponseStream())
            $ftpList = $reader.ReadToEnd()
            $reader.Dispose(); $ftpResponse.Dispose()
            Check ($ftpList -match 'sample') "FTP anonymous LIST shows the collection"
        }
        catch { Check $false "FTP anonymous LIST shows the collection ($($_.Exception.Message))" }
        $smbUp = $false
        try { $tcp = New-Object System.Net.Sockets.TcpClient; $tcp.Connect('127.0.0.1', $Port + 3); $smbUp = $tcp.Connected; $tcp.Dispose() } catch { }
        Check $smbUp "SMB server (bundled portable-smb-server) listening on port $($Port + 3)"

        # status.json is refreshed every two seconds, so give the counters a moment to land
        $status = ''
        $statusExit = -1
        for ($i = 0; $i -lt 6 -and -not ($status -match 'Files served: [1-9]'); $i++) {
            Start-Sleep -Seconds 1
            $status = & $Exe status --settings-dir $settings 2>&1 | Out-String
            $statusExit = $LASTEXITCODE
        }
        # exit 1 is allowed only for the WebDAV mount's yellow on Windows (the WebClient service is usually stopped); nothing may be Failed
        Check (($statusExit -eq 0) -or ($statusExit -eq 1 -and $status -match 'WebClient' -and $status -notmatch 'Failed')) "anyfs status while serving: all green, or only WebDAV yellow about WebClient (exit $statusExit)"
        Check ($status -match 'Files served: [1-9]') "status reports files served"
    }
}
finally {
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    Start-Sleep -Milliseconds 500
}
$log = Get-Content (Join-Path $work 'anyfs.log') -Raw -ErrorAction SilentlyContinue
Check ($log -match 'is up at') "anyfs.log written with the mount's access path"
Check (-not ($log -match ' ERROR ')) "no ERROR lines in anyfs.log"
if ($log -match ' ERROR ') { Write-Host "--- ERROR lines"; ($log -split "`n") | Where-Object { $_ -match ' ERROR ' } | Select-Object -First 10 }
if ($failures -gt 0) {
    Write-Host "--- serve stdout"; Get-Content $serveOut -ErrorAction SilentlyContinue | Select-Object -Last 40
    Write-Host "--- serve stderr"; Get-Content $serveErr -ErrorAction SilentlyContinue | Select-Object -Last 40
    throw "$failures smoke check(s) failed"
}
Write-Host "smoke: all checks passed" -ForegroundColor Green
