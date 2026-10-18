<#
.SYNOPSIS
  Fetches the installation media the on-premises SharePoint lab installs from: SharePoint Server and SQL Server.

.DESCRIPTION
  Reads scripts/sharepoint.json (download URL, size and SHA-256 of each image), downloads what is missing into
  tools/sharepoint/ and checks the hash; an image already there with the right hash is left alone (they are large).
  Microsoft publishes no hash for these downloads: the ones in the manifest were taken from the first downloads
  (17 and 18-Oct-2026) and pin those exact images. The lab's Vagrantfile shares tools/sharepoint into the
  SharePoint VMs (provisioning/sharepoint/README.md). SQL Server Developer is an ISO here, not the bootstrapper
  Microsoft's page offers: that bootstrapper refuses to run once it is a few months old (in quiet mode it hangs).

.EXAMPLE
  .\scripts\fetch-sharepoint.ps1
#>
[CmdletBinding()]
param(
    [string]$OutDir = (Join-Path (Join-Path $PSScriptRoot '..') 'tools\sharepoint')
)

$ErrorActionPreference = 'Stop'
$manifest = Get-Content (Join-Path $PSScriptRoot 'sharepoint.json') -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Force $OutDir | Out-Null

foreach ($name in $manifest.PSObject.Properties.Name) {
    $image = $manifest.$name
    $dest = Join-Path $OutDir $image.file
    $expected = $image.sha256.ToLowerInvariant()
    if (Test-Path $dest) {
        $have = (Get-FileHash $dest -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($have -eq $expected) {
            Write-Host "  $name  already in $dest (sha256 ok)"
            continue
        }
        Write-Host "  $name  $dest has sha256 $have, not the manifest's; downloading again"
    }

    Write-Host "  $name  <- $($image.url)  ($([math]::Round($image.bytes / 1GB, 2)) GB)"
    $temp = "$dest.download"
    # Invoke-WebRequest buffers whole files in memory on Windows PowerShell 5.1; the .NET client streams.
    $client = New-Object System.Net.WebClient
    try { $client.DownloadFile($image.url, $temp) } finally { $client.Dispose() }
    $hash = (Get-FileHash $temp -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $expected) {
        Remove-Item $temp -Force
        throw "$($image.file): SHA-256 $hash does not match sharepoint.json ($expected)"
    }
    Move-Item $temp $dest -Force
    Write-Host "          sha256 ok -> $dest"
}
