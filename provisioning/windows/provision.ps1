<#
    AnyFS test lab: the Windows machine that runs anyfs (every mount provider Windows has) and is its
    own client.

    Vagrant runs this inside the VM (over WinRM, elevated) when it first creates the VM, and again on
    every `vagrant provision`. Each step sets a state, so a re-run changes nothing unless something
    drifted. When a step needs a restart (the autologon), it leaves C:\anyfs-lab\restart-required and the
    Vagrantfile restarts the VM.

    The tests drive the machine over SSH (PowerShell, the lab key) and start anyfs through RunRemote,
    inside the logged-on session: WinFsp's \\anyfs\... and the stored credentials for the upstream's
    share live there, and an SSH session can't see either.

    Inputs (from the Vagrantfile): LAB_PUBKEY, the lab's SSH public key; LAB_USER and LAB_PASSWORD, the
    account the tests use (the box's own); UPSTREAM_IP, the Linux machine whose Samba share the Mirror
    UNC rows use; WINFSP_MSI and WINFSP_SHA256, the pinned installer under \\vboxsvr\winfsp.
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

foreach ($name in 'LAB_PUBKEY', 'LAB_USER', 'LAB_PASSWORD', 'UPSTREAM_IP', 'WINFSP_MSI', 'WINFSP_SHA256') {
    if (-not [Environment]::GetEnvironmentVariable($name)) { throw "$name is not set (the Vagrantfile passes it)" }
}
$user = $env:LAB_USER
New-Item -ItemType Directory -Force -Path 'C:\anyfs-lab' | Out-Null

# --- OpenSSH Server, running and starting at boot -------------------------------------------------
if (-not (Get-Service sshd -ErrorAction SilentlyContinue)) {
    Add-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0' | Out-Null
}
Set-Service sshd -StartupType Automatic
Start-Service sshd

# --- SSH sessions get PowerShell, the shell the tests write their commands in ---------------------
if (-not (Test-Path 'HKLM:\SOFTWARE\OpenSSH')) { New-Item -Path 'HKLM:\SOFTWARE\OpenSSH' | Out-Null }
Set-ItemProperty -Path 'HKLM:\SOFTWARE\OpenSSH' -Name DefaultShell -Type String `
    -Value "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"

# --- The lab's key opens the account ---------------------------------------------------------------
# Whichever file sshd reads the account's keys from: Windows' stock sshd_config sends administrators
# to ProgramData\ssh\administrators_authorized_keys, but this box's config doesn't, so there it's the
# account's own .ssh\authorized_keys. (Vagrant runs this script as that account.)
$conf = & "$env:SystemRoot\System32\OpenSSH\sshd.exe" -T -C "user=$user,host=localhost,addr=127.0.0.1"
if ($LASTEXITCODE -ne 0) { throw 'sshd -T failed' }
$keys = (($conf | Where-Object { $_ -like 'authorizedkeysfile *' }) -split ' ')[1]
$keys = $keys -replace '__PROGRAMDATA__', $env:ProgramData -replace '/', '\'
if (-not [IO.Path]::IsPathRooted($keys)) { $keys = Join-Path $env:USERPROFILE $keys }
$have = if (Test-Path $keys) { @(Get-Content $keys) } else { @() }
if ($have -notcontains $env:LAB_PUBKEY) {
    New-Item -ItemType Directory -Force -Path (Split-Path $keys) | Out-Null
    Set-Content -Path $keys -Value ($have + $env:LAB_PUBKEY) -Encoding ascii
}
if ($keys -like "$env:ProgramData\*") {
    # sshd ignores the shared file unless only Administrators and SYSTEM can reach it.
    icacls.exe $keys /inheritance:r /grant '*S-1-5-32-544:F' /grant '*S-1-5-18:F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed on $keys" }
}

# --- No firewall ---------------------------------------------------------------------------------
# The other machines connect to anyfs's mounts on this one (SMB 1445, WebDAV 11000, HTTP 11001, FTP
# 2121 and its passive range) and to SSH and RunRemote's UDP 8888. This is an isolated lab, so the
# firewall is off rather than a rule per port.
Set-NetFirewallProfile -All -Enabled False

# --- WinFsp: the pinned installer from \\vboxsvr\winfsp, silently --------------------------------
# The same MSI anyfs.exe carries for its Install button; its hash is checked against scripts/winfsp.json
# first. WinFsp needs no restart.
$msi = "\\vboxsvr\winfsp\$env:WINFSP_MSI"
if (-not (Test-Path $msi)) { throw "No WinFsp installer at $msi: run scripts\fetch-winfsp.ps1 on the PC first" }
$hash = (Get-FileHash $msi -Algorithm SHA256).Hash
if ($hash -ne $env:WINFSP_SHA256) { throw "$msi has SHA-256 $hash, scripts/winfsp.json says $env:WINFSP_SHA256" }
$wantVersion = if ($env:WINFSP_MSI -match '(\d+\.\d+\.\d+)') { $matches[1] } else { throw "no version in $env:WINFSP_MSI" }
$winfspDll = Join-Path ${env:ProgramFiles(x86)} 'WinFsp\bin\winfsp-x64.dll'
$haveVersion = if (Test-Path $winfspDll) { (Get-Item $winfspDll).VersionInfo.FileVersion -replace '\..*?$', '' } else { '' }
$haveVersion = if (Test-Path $winfspDll) { ((Get-Item $winfspDll).VersionInfo.FileVersion -split '\.')[0..2] -join '.' } else { '' }
if ($haveVersion -ne $wantVersion) {
    $log = 'C:\anyfs-lab\winfsp-install.log'
    $p = Start-Process msiexec.exe -ArgumentList @('/i', $msi, '/qn', '/norestart', '/l*v', $log) -Wait -PassThru
    if ($p.ExitCode -notin 0, 3010) { throw "WinFsp's installer exited with $($p.ExitCode); see $log" }
}

# --- WebDAV client: the WebClient service, with room for the 200 MiB file -------------------------
# Windows' WebDAV redirector is off by default and caps files at 50 MB; the WebDAV rows need both.
Set-Service WebClient -StartupType Automatic
$webclient = 'HKLM:\SYSTEM\CurrentControlSet\Services\WebClient\Parameters'
if ((Get-ItemProperty $webclient -Name FileSizeLimitInBytes -ErrorAction SilentlyContinue).FileSizeLimitInBytes -ne 0xFFFFFFFF) {
    Set-ItemProperty $webclient -Name FileSizeLimitInBytes -Type DWord -Value 0xFFFFFFFF
    Restart-Service WebClient -ErrorAction SilentlyContinue
}
Start-Service WebClient

# --- RunRemote: starts anyfs inside the logged-on session, when the tests ask (UDP 8888) ----------
# Installed from the PC's build (shared in as \\vboxsvr\runremote). It runs at logon, and every two
# minutes after that, so it comes back if it ever dies: a second copy exits at once while the first
# holds the port.
$rrDir = 'C:\anyfs-lab\runremote-server'
$rrExe = Join-Path $rrDir 'server.exe'
$rrSource = '\\vboxsvr\runremote\server.exe'
if (-not (Test-Path $rrSource)) { throw "No RunRemote server at $rrSource (windows.runremote_dir in settings.yml)" }
New-Item -ItemType Directory -Force -Path $rrDir | Out-Null
if (-not (Test-Path $rrExe) -or (Get-FileHash $rrExe).Hash -ne (Get-FileHash $rrSource).Hash) {
    Stop-ScheduledTask -TaskName RunRemote -ErrorAction SilentlyContinue
    Get-Process server -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $rrExe } | Stop-Process -Force
    Start-Sleep -Seconds 1
    Copy-Item -Force $rrSource $rrExe
}
$action = New-ScheduledTaskAction -Execute $rrExe -WorkingDirectory $rrDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$trigger.Repetition = (New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 2)).Repetition
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
$taskSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName RunRemote -Action $action -Trigger $trigger -Principal $principal `
    -Settings $taskSettings -Description 'AnyFS lab: RunRemote server (UDP 8888) in the logged-on session' `
    -Force | Out-Null
Start-ScheduledTask -TaskName RunRemote -ErrorAction SilentlyContinue

# --- The account is logged on at the console after every boot -----------------------------------
# The box logs on by itself only twice (its unattend file's LogonCount is 2: the first boot and the
# rename). At the next boot Windows sees the count run out and switches autologon off, so the count
# goes, and a change here takes a restart to show.
$winlogon = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
$current = Get-ItemProperty -Path $winlogon
if ($current.AutoAdminLogon -ne '1' -or $current.DefaultUserName -ne $user -or
    $current.DefaultPassword -ne $env:LAB_PASSWORD -or $null -ne $current.AutoLogonCount) {
    Set-ItemProperty -Path $winlogon -Name AutoAdminLogon -Type String -Value '1'
    Set-ItemProperty -Path $winlogon -Name DefaultUserName -Type String -Value $user
    Set-ItemProperty -Path $winlogon -Name DefaultPassword -Type String -Value $env:LAB_PASSWORD
    Remove-ItemProperty -Path $winlogon -Name AutoLogonCount -ErrorAction SilentlyContinue
    New-Item -ItemType File -Force -Path 'C:\anyfs-lab\restart-required' | Out-Null
}

# --- Never sleeps (a sleeping test machine looks like a failing test) -----------------------------
powercfg.exe /change standby-timeout-ac 0
powercfg.exe /change hibernate-timeout-ac 0

# --- The folder anyfs runs from ------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path 'C:\anyfs-lab\anyfs' | Out-Null

# --- Summary ------------------------------------------------------------------------------------
$os = Get-CimInstance Win32_OperatingSystem
$winfsp = if (Test-Path $winfspDll) { (Get-Item $winfspDll).VersionInfo.FileVersion } else { 'missing' }
$restart = if (Test-Path 'C:\anyfs-lab\restart-required') { 'restarting to finish' } else { 'ready' }
Write-Host "$($env:COMPUTERNAME): $($os.Caption) build $($os.BuildNumber), WinFsp $winfsp, sshd $((Get-Service sshd).Status), $restart"
