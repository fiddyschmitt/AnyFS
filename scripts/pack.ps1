<#
.SYNOPSIS
  Packs the plugin SDK (AnyFS.Sdk) and its test helpers (AnyFS.Sdk.Testing) as NuGet packages.

.DESCRIPTION
  Output goes to publish/nuget/: AnyFS.Sdk.<version>.nupkg and .snupkg (symbols), and the same for
  AnyFS.Sdk.Testing. The version is Directory.Build.props's unless -Version overrides it (the release
  workflow passes the tag's). Deterministic, with SourceLink to the repository, so a plugin author can
  step into the SDK.

.EXAMPLE
  .\scripts\pack.ps1
  .\scripts\pack.ps1 -Version 0.2.0
  dotnet nuget push publish\nuget\AnyFS.Sdk.0.2.0.nupkg --api-key ... --source https://api.nuget.org/v3/index.json
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = 'Release',
    [string]$OutDir = (Join-Path (Join-Path (Join-Path $PSScriptRoot '..') 'publish') 'nuget')
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
New-Item -ItemType Directory -Force $OutDir | Out-Null
Get-ChildItem $OutDir -Filter 'AnyFS.Sdk*' | Remove-Item -Force

foreach ($project in 'src/AnyFS.Sdk/AnyFS.Sdk.csproj', 'src/AnyFS.Sdk.Testing/AnyFS.Sdk.Testing.csproj') {
    Write-Host "== packing $project" -ForegroundColor Cyan
    $dotnetArgs = @('pack', (Join-Path $repo $project), '-c', $Configuration, '-o', $OutDir, '-nologo', '-v', 'minimal', '-p:ContinuousIntegrationBuild=true')
    if ($Version) { $dotnetArgs += "-p:Version=$Version" }
    & dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed for $project" }
}

Get-ChildItem $OutDir -Filter 'AnyFS.Sdk*' | ForEach-Object { "   {0,-40} {1,8:N0} KB" -f $_.Name, ($_.Length / 1KB) }
