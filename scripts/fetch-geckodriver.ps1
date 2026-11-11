<#
.SYNOPSIS
  Fetches geckodriver, Firefox's WebDriver driver, which every build of anyfs embeds for its runtime.

.DESCRIPTION
  Reads scripts/geckodriver.json (the release tag, and for each runtime the asset and the SHA-256 GitHub publishes for
  it), downloads each asset into tools/geckodriver/<tag>/, checks its hash, and unpacks the driver to
  tools/geckodriver/<tag>/<rid>/geckodriver[.exe]. An archive already there whose hash matches is kept rather than
  downloaded again (the workflows restore tools/geckodriver from a cache). Other tags' folders are removed, so a build
  embeds the pinned driver or none.
  src/AnyFS.App embeds the one for the runtime it builds (the publish RID, else the SDK's own) as "geckodriver", and a
  Firefox sign-in writes it out once per version and hands it to Selenium, so no driver is downloaded (spec 7.3.2).
  geckodriver is MPL-2.0: it is conveyed unmodified, and its source is at https://github.com/mozilla/geckodriver.

.PARAMETER Rid
  The runtimes to fetch it for: every one in geckodriver.json unless named.

.EXAMPLE
  .\scripts\fetch-geckodriver.ps1
  .\scripts\fetch-geckodriver.ps1 -Rid win-x64
#>
[CmdletBinding()]
param(
    [string[]]$Rid,
    [string]$OutDir = (Join-Path (Join-Path (Join-Path $PSScriptRoot '..') 'tools') 'geckodriver')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$manifest = (Get-Content (Join-Path $PSScriptRoot 'geckodriver.json') -Raw | ConvertFrom-Json).geckodriver
$assets = @{}
foreach ($property in $manifest.assets.PSObject.Properties) { $assets[$property.Name] = $property.Value }
if (-not $Rid) { $Rid = @($assets.Keys | Sort-Object) }

New-Item -ItemType Directory -Force $OutDir | Out-Null
# another tag's drivers go, so a build embeds the pinned one or none
Get-ChildItem $OutDir -Directory | Where-Object { $_.Name -ne $manifest.tag } | Remove-Item -Recurse -Force
$tagDir = Join-Path $OutDir $manifest.tag
New-Item -ItemType Directory -Force $tagDir | Out-Null

# Windows' own tar: a GNU tar first on PATH (Git's) takes "C:" in a path for a remote host
$onWindows = $env:OS -eq 'Windows_NT'
$tar = if ($onWindows) { Join-Path $env:SystemRoot 'System32\tar.exe' } else { 'tar' }

function Get-Sha256([string]$Path) { (Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

foreach ($r in $Rid) {
    $entry = $assets[$r]
    if (-not $entry) { throw "geckodriver.json has no asset for $r" }
    $archive = Join-Path $tagDir $entry.asset
    $ridDir = Join-Path $tagDir $r
    $name = if ($r.StartsWith('win')) { 'geckodriver.exe' } else { 'geckodriver' }
    $driver = Join-Path $ridDir $name
    $expected = $entry.sha256.ToLowerInvariant()

    # one already here (a CI cache restored it, or an earlier run) is kept when it is the pinned one
    $fresh = $false
    if (-not ((Test-Path $archive) -and (Get-Sha256 $archive) -eq $expected)) {
        $url = "https://github.com/$($manifest.repo)/releases/download/$($manifest.tag)/$($entry.asset)"
        Write-Host "  $r  <- $url"
        $temp = "$archive.download"
        Invoke-WebRequest -Uri $url -OutFile $temp -UseBasicParsing
        $hash = Get-Sha256 $temp
        if ($hash -ne $expected) {
            Remove-Item $temp -Force
            throw "$($entry.asset): SHA-256 $hash does not match geckodriver.json ($($entry.sha256))"
        }
        Move-Item $temp $archive -Force
        $fresh = $true
    }

    if ($fresh -or -not (Test-Path $driver)) {
        $unpack = Join-Path $tagDir ".unpack-$r"
        if (Test-Path $unpack) { Remove-Item -Recurse -Force $unpack }
        New-Item -ItemType Directory -Force $unpack | Out-Null
        if ($entry.asset.EndsWith('.zip')) {
            Expand-Archive -Path $archive -DestinationPath $unpack -Force
        }
        else {
            & $tar -xzf $archive -C $unpack
            if ($LASTEXITCODE -ne 0) { throw "tar could not unpack $($entry.asset)" }
        }
        $found = Join-Path $unpack $name
        if (-not (Test-Path $found)) { throw "$($entry.asset) holds no $name" }
        New-Item -ItemType Directory -Force $ridDir | Out-Null
        Move-Item $found $driver -Force
        Remove-Item -Recurse -Force $unpack
    }

    Write-Host ("  {0,-11} geckodriver {1}, {2:N1} MB, sha256 ok" -f $r, $manifest.tag, ((Get-Item $driver).Length / 1MB))
}
