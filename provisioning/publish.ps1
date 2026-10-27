<#
.SYNOPSIS
    Builds what the lab tests: the trimmed anyfs per runtime, the fixture generator, the SMB stress
    tool and the sample plugin, into .\publish, which the VMs see as a shared folder.

.DESCRIPTION
    scripts/publish.ps1 publishes anyfs exactly as a release does (self-contained, single file,
    trimmed), here for the lab's runtimes only, into publish/<rid>/. Beside each anyfs goes MakeTree
    (tests/fixtures/MakeTree), which generates the same fixture tree on every machine from a seed so
    no large files travel over the LAN, and SmbStress (tests/fixtures/SmbStress), the load the SMB
    stress run puts on a host from every client, and under publish/plugins/ the sample plugin DLL the "plugin
    loads in the trimmed build" rows drop into a host's plugins folder. publish/version.txt records
    the commit built. The Vagrantfile shares .\publish into the VirtualBox VMs (\\vboxsvr\publish,
    /media/vboxsf/<pc>/publish); the Mac VM gets its files over SCP from the tests.

.PARAMETER Rid
    The runtimes to build. Default: the lab's three.

.PARAMETER NoTrim
    Publish anyfs untrimmed, to tell a trimming problem apart from a code problem.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -Rid linux-x64
#>
[CmdletBinding()]
param(
    [string[]]$Rid = @('win-x64', 'linux-x64', 'osx-arm64'),
    [switch]$NoTrim,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$out = Join-Path $PSScriptRoot 'publish'
New-Item -ItemType Directory -Force $out | Out-Null

$branch = git -C $repo rev-parse --abbrev-ref HEAD
$sha = git -C $repo rev-parse --short HEAD
if (git -C $repo status --porcelain) { $sha += "+uncommitted" }    # the tests report this; say when it isn't a commit
Write-Host "publishing $($Rid -join ', ') from $branch @ $sha -> $out" -ForegroundColor Cyan

$publishArgs = @{ Rid = $Rid; OutDir = $out }
if ($NoTrim) { $publishArgs.NoTrim = $true }
if ($Clean) { $publishArgs.Clean = $true }
& (Join-Path $repo 'scripts\publish.ps1') @publishArgs

foreach ($r in $Rid) {
    foreach ($tool in 'MakeTree', 'SmbStress') {
        Write-Host "== $tool for $r" -ForegroundColor Cyan
        & dotnet publish (Join-Path $repo "tests\fixtures\$tool\$tool.csproj") -c Release -r $r --self-contained `
            -p:PublishSingleFile=true -p:PublishTrimmed=true -p:DebugType=none -o (Join-Path $out $r) -nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish of $tool failed for $r" }
    }
}

Write-Host "== the sample plugin" -ForegroundColor Cyan
& dotnet build (Join-Path $repo 'tests\fixtures\SamplePlugin\SamplePlugin.csproj') -c Release -nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "dotnet build of SamplePlugin failed" }
New-Item -ItemType Directory -Force (Join-Path $out 'plugins') | Out-Null
Copy-Item (Join-Path $repo 'tests\fixtures\SamplePlugin\bin\Release\net10.0\AnyFS.SamplePlugin.dll') (Join-Path $out 'plugins\') -Force

Set-Content -Path (Join-Path $out 'version.txt') -Value "$branch $sha $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))" -Encoding ascii
Get-ChildItem $out -Recurse -File | Where-Object { $_.Name -match '^(anyfs|MakeTree|SmbStress)(\.exe)?$|\.dll$|version\.txt' } |
    ForEach-Object { "   {0,-28} {1,8:N1} MB" -f $_.FullName.Substring($out.Length + 1), ($_.Length / 1MB) }
