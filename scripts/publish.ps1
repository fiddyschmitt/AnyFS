<#
.SYNOPSIS
  Publishes anyfs as a self-contained, single-file, trimmed executable per runtime.

.DESCRIPTION
  Output goes to publish/<rid>/anyfs[.exe]. Every RID in the release matrix is built unless -Rid narrows
  it. Trimming uses the guaranteed framework set rooted in src/AnyFS.App/AnyFS.App.csproj (spec 14.1);
  because MSBuild's incremental ILLink step doesn't notice changes to that list, -Clean wipes obj/ first.

.EXAMPLE
  .\scripts\publish.ps1                      # all RIDs
  .\scripts\publish.ps1 -Rid win-x64 -Clean  # one RID, fresh trim
  .\scripts\publish.ps1 -Rid linux-x64 -NoTrim
#>
[CmdletBinding()]
param(
    [string[]]$Rid = @('win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'),
    [string]$Configuration = 'Release',
    [string]$OutDir = (Join-Path (Join-Path $PSScriptRoot '..') 'publish'),
    [switch]$NoTrim,
    [switch]$Clean,
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = [System.IO.Path]::Combine($repo, 'src', 'AnyFS.App', 'AnyFS.App.csproj')

if ($Clean) {
    Remove-Item -Recurse -Force ([System.IO.Path]::Combine($repo, 'src', 'AnyFS.App', 'obj')) -ErrorAction SilentlyContinue
}

foreach ($r in $Rid) {
    $out = Join-Path $OutDir $r
    Write-Host "== publishing $r -> $out" -ForegroundColor Cyan
    $dotnetArgs = @('publish', $project, '-c', $Configuration, '-r', $r, '-o', $out, '-nologo', '-v', 'minimal')
    if ($NoTrim) { $dotnetArgs += '-p:NoTrim=true' }
    if ($Version) { $dotnetArgs += "-p:Version=$Version" }   # a release stamps the tag's version (the workflow passes it)
    & dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $r" }

    $exe = Get-ChildItem $out -File | Where-Object { $_.Name -in @('anyfs', 'anyfs.exe') } | Select-Object -First 1
    if (-not $exe) { throw "no anyfs executable found in $out" }
    Write-Host ("   {0}  {1:N1} MB" -f $exe.Name, ($exe.Length / 1MB))
}
