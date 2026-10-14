<#
.SYNOPSIS
    Runs Vagrant for the macOS test VM, on the Mac mini, from this PC.

.DESCRIPTION
    Only a Mac can run a macOS VM (tart, on Apple's Virtualization framework), so Vagrant runs there
    for it, with the vagrant-tart provider. This script drives that from here, so settings.yml stays
    the one place you edit: each run copies settings.yml, macos\ and the lab's public key to
    ~/<mac.project_dir> on the Mac, then runs `vagrant <your arguments>` there over SSH.

        .\mac.ps1 setup      One-time: lets the lab's key in to the Mac (asks for the Mac's password,
                             once, if none of your keys is accepted there), then installs tart, Vagrant
                             and vagrant-tart there. Safe to repeat.
        .\mac.ps1 <args>     vagrant <args>, on the Mac

.EXAMPLE
    .\mac.ps1 setup
    .\mac.ps1 up
    .\mac.ps1 status
    .\mac.ps1 ssh anyfs-macos-1 -c sw_vers
    .\mac.ps1 destroy -f
#>
# No param() block: every argument, -f included, goes to vagrant as it is.

# The native commands below report failure by exit code, which is checked. 'Stop' would make Windows
# PowerShell treat anything they print on stderr (Homebrew's progress, say) as a terminating error
# whenever this script's output is redirected.
$ErrorActionPreference = 'Continue'
Set-Location $PSScriptRoot

if ($args.Count -eq 0) {
    Get-Help $PSCommandPath -Detailed
    exit 1
}

# settings.yml, read with the Ruby that comes with Vagrant (Windows PowerShell has no YAML reader).
$vagrant = Get-Command vagrant -ErrorAction SilentlyContinue
if (-not $vagrant) { throw "Vagrant isn't installed here (or isn't on the PATH)." }
$ruby = Join-Path (Split-Path (Split-Path $vagrant.Source)) 'embedded\mingw64\bin\ruby.exe'
$settings = (& $ruby -ryaml -rjson -e 'puts YAML.load_file(ARGV[0]).to_json' settings.yml) | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw "couldn't read settings.yml" }
$mac    = $settings.mac
$target = "$($mac.user)@$($mac.host)"
$dir    = $mac.project_dir

# The lab's key. The Vagrantfile makes it on first use too.
$key = $settings.ssh_key -replace '^~', $env:USERPROFILE
if (-not (Test-Path $key)) {
    New-Item -ItemType Directory -Force -Path (Split-Path $key) -ErrorAction Stop | Out-Null
    & ssh-keygen -q -t ed25519 -N '""' -C 'anyfs test lab' -f $key   # '""': an empty passphrase, as Windows PowerShell passes it
    if ($LASTEXITCODE -ne 0) { throw "ssh-keygen couldn't make $key" }
}
$pubkey = (Get-Content "$key.pub" -Raw -ErrorAction Stop).Trim()
$ssh = @('-i', $key, '-o', 'StrictHostKeyChecking=accept-new', '-o', 'ConnectTimeout=10', '-o', 'LogLevel=ERROR')

# A command on the Mac sees no login shell's PATH. Homebrew's tools are under /opt/homebrew, and the
# Vagrant package links its command into /usr/local/bin. First comes macos/shims, whose tart wrapper
# keeps the VM from dying with Vagrant's output pipes (see the wrapper).
$path = "export PATH=`$HOME/$dir/macos/shims:/opt/homebrew/bin:/opt/homebrew/sbin:/usr/local/bin:`$PATH"

if ($args[0] -eq 'setup') {
    & ssh @ssh -o BatchMode=yes -o LogLevel=QUIET $target true
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Letting the lab's key in to $target. Its password, once:" -ForegroundColor Cyan
        & ssh -o StrictHostKeyChecking=accept-new $target "umask 077; mkdir -p ~/.ssh; grep -qxF '$pubkey' ~/.ssh/authorized_keys 2>/dev/null || echo '$pubkey' >> ~/.ssh/authorized_keys"
        if ($LASTEXITCODE -ne 0) { throw "couldn't add the lab's key on $target" }
    }
}

# Every run: the Mac gets this folder's settings.yml and macos\, and the lab's public key.
& ssh @ssh -o BatchMode=yes $target "mkdir -p ~/$dir/macos/shims"
if ($LASTEXITCODE -ne 0) { throw "can't reach $target with the lab's key: run .\mac.ps1 setup first" }
& scp @ssh -q settings.yml "${target}:$dir/settings.yml"
if ($LASTEXITCODE -eq 0) { & scp @ssh -q "$key.pub" "${target}:$dir/lab_key.pub" }
if ($LASTEXITCODE -eq 0) { & scp @ssh -q -r (Get-ChildItem macos).FullName "${target}:$dir/macos/" }
if ($LASTEXITCODE -eq 0) { & ssh @ssh $target "chmod +x ~/$dir/macos/shims/* ~/$dir/macos/*.sh" }
if ($LASTEXITCODE -ne 0) { throw "couldn't copy the lab's files to ${target}:$dir" }

# A terminal for the remote side when there's one here, so `vagrant ssh` is interactive.
$tty = if ([Console]::IsInputRedirected -or [Console]::IsOutputRedirected) { @() } else { @('-t') }

if ($args[0] -eq 'setup') {
    & ssh @ssh @tty $target "$path; bash ~/$dir/macos/setup-host.sh '$($mac.vagrant_tart_version)'"
    exit $LASTEXITCODE
}

# Each argument single-quoted for the Mac's shell.
$quoted = ($args | ForEach-Object { "'" + ($_ -replace "'", "'\''") + "'" }) -join ' '
Write-Host "vagrant $($args -join ' ')   (on $target)" -ForegroundColor Cyan
& ssh @ssh @tty $target "$path; cd ~/$dir/macos && vagrant $quoted"
exit $LASTEXITCODE
