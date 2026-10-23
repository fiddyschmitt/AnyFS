<#
.SYNOPSIS
  Fetches the Go servers AnyFS embeds (portable-smb-server now; portable-nfs-server from M7) per runtime.

.DESCRIPTION
  Reads scripts/go-servers.json (release tag, asset names, SHA-256s) and puts each binary at
  tools/go-servers/<rid>/portable-smb-server[.exe], which src/Mounts/Smb embeds when publishing for that RID
  (spec 8.2, 14). A checksum that is still empty in the JSON is reported as unverified rather than failing,
  so a release can be used before its hashes are filled in. A binary already in place whose SHA-256 matches the
  JSON is kept rather than downloaded again (the workflows restore tools/go-servers from a cache).

  -FromLocal copies the binaries out of a local build of the Go repo (its build.ps1 output folder) instead
  of downloading, for work on both sides at once.

.EXAMPLE
  .\scripts\fetch-go-servers.ps1                                   # every RID from the pinned release
  .\scripts\fetch-go-servers.ps1 -Rid win-x64                      # one RID
  .\scripts\fetch-go-servers.ps1 -FromLocal ..\..\go\portable-smb-server\bin
#>
[CmdletBinding()]
param(
    [string[]]$Rid = @('win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'),
    [string]$FromLocal,
    [string]$OutDir = (Join-Path (Join-Path $PSScriptRoot '..') 'tools\go-servers')
)

$ErrorActionPreference = 'Stop'
$manifest = Get-Content (Join-Path $PSScriptRoot 'go-servers.json') -Raw | ConvertFrom-Json
$servers = @('portable-smb-server')

# A bare download carries no mode bits. On Linux and macOS the server must be executable where it sits in tools/,
# because the unit tests and unpublished builds start it from there (a published anyfs sets the bit on its own copy).
function Set-Executable([string]$path) {
    if ($PSVersionTable.PSVersion.Major -lt 6 -or $IsWindows -or $path.EndsWith('.exe')) { return }
    & chmod +x $path
}

foreach ($server in $servers) {
    $entry = $manifest.$server
    foreach ($r in $Rid) {
        $asset = $entry.assets.$r
        if (-not $asset) { throw "no asset for $server on $r in go-servers.json" }
        $ext = if ($r -like 'win-*') { '.exe' } else { '' }
        $dir = Join-Path $OutDir $r
        New-Item -ItemType Directory -Force $dir | Out-Null
        $dest = Join-Path $dir "$server$ext"

        if ($FromLocal) {
            $source = Join-Path $FromLocal $asset.name
            if (-not (Test-Path $source)) { throw "$source not found (build the Go repo first: .\build.ps1)" }
            Copy-Item $source $dest -Force
            Set-Executable $dest
            Write-Host ("  {0,-12} {1}  <- {2}" -f $r, $server, $source)
            continue
        }

        # one already here (a CI cache restored it, or an earlier run) is kept when it is the pinned one
        if ($asset.sha256 -and (Test-Path $dest) -and (Get-FileHash $dest -Algorithm SHA256).Hash.ToLowerInvariant() -eq $asset.sha256.ToLowerInvariant()) {
            Set-Executable $dest
            Write-Host ("  {0,-12} {1}  already here, sha256 ok" -f $r, $server)
            continue
        }

        $url = "https://github.com/$($entry.repo)/releases/download/$($entry.version)/$($asset.name)"
        Write-Host ("  {0,-12} {1}  <- {2}" -f $r, $server, $url)
        $temp = "$dest.download"
        Invoke-WebRequest -Uri $url -OutFile $temp -UseBasicParsing
        $hash = (Get-FileHash $temp -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($asset.sha256) {
            if ($hash -ne $asset.sha256.ToLowerInvariant()) {
                Remove-Item $temp -Force
                throw "$($asset.name): SHA-256 $hash does not match go-servers.json ($($asset.sha256))"
            }
            Write-Host "               sha256 ok"
        }
        else {
            Write-Warning "$($asset.name): no SHA-256 in go-servers.json yet; downloaded $hash unverified. Fill it in after checking the release."
        }
        Move-Item $temp $dest -Force
        Set-Executable $dest
    }
}
Write-Host "binaries in $OutDir"
