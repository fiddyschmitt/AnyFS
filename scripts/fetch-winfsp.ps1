<#
.SYNOPSIS
  Fetches the WinFsp installer that Windows builds of anyfs embed for the Install button.

.DESCRIPTION
  Reads scripts/winfsp.json (release tag, asset name and the SHA-256 the WinFsp release page publishes), downloads the
  MSI into tools/winfsp/ and checks the hash; any other winfsp-*.msi there is removed so the build embeds exactly one.
  An MSI already there whose SHA-256 matches the JSON is kept rather than downloaded again (the workflows restore
  tools/winfsp from a cache).
  src/AnyFS.App embeds it as "winfsp.msi" when building for Windows (spec 8.1, N6), and the WinFsp provider installs it
  with msiexec /qn when the driver is missing. WinFsp is GPLv3 with a FLOSS exception: the installer is conveyed
  unmodified and its source is at https://github.com/winfsp/winfsp.

.EXAMPLE
  .\scripts\fetch-winfsp.ps1
#>
[CmdletBinding()]
param(
    [string]$OutDir = (Join-Path (Join-Path $PSScriptRoot '..') 'tools\winfsp')
)

$ErrorActionPreference = 'Stop'
$manifest = (Get-Content (Join-Path $PSScriptRoot 'winfsp.json') -Raw | ConvertFrom-Json).winfsp
New-Item -ItemType Directory -Force $OutDir | Out-Null
$dest = Join-Path $OutDir $manifest.asset

# one already here (a CI cache restored it, or an earlier run) is kept when it is the pinned one
if ((Test-Path $dest) -and (Get-FileHash $dest -Algorithm SHA256).Hash.ToLowerInvariant() -eq $manifest.sha256.ToLowerInvariant()) {
    Get-ChildItem $OutDir -Filter 'winfsp-*.msi' | Where-Object { $_.Name -ne $manifest.asset } | Remove-Item -Force
    Write-Host "  winfsp  $($manifest.asset) already here, sha256 ok"
    return
}

$url = "https://github.com/$($manifest.repo)/releases/download/$($manifest.tag)/$($manifest.asset)"
Write-Host "  winfsp  <- $url"
$temp = "$dest.download"
Invoke-WebRequest -Uri $url -OutFile $temp -UseBasicParsing
$hash = (Get-FileHash $temp -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $manifest.sha256.ToLowerInvariant()) {
    Remove-Item $temp -Force
    throw "$($manifest.asset): SHA-256 $hash does not match winfsp.json ($($manifest.sha256))"
}
Write-Host "          sha256 ok"
Get-ChildItem $OutDir -Filter 'winfsp-*.msi' | Where-Object { $_.Name -ne $manifest.asset } | Remove-Item -Force
Move-Item $temp $dest -Force
Write-Host "installer in $dest"
