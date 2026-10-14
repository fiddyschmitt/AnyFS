#!/bin/bash
# AnyFS test lab: the macOS test VM.
#
# Vagrant runs this inside the VM (as root) when it first creates the VM, and again on every
# `vagrant provision`. Each step sets a state, so a re-run changes nothing unless something drifted.
#
# The tests drive the VM over SSH with the lab's key. It runs anyfs (osx-arm64: SMB, WebDAV, FTP,
# HTTP mounts, NFS from milestone 7) and is its own client with what macOS ships: mount_smbfs,
# mount_webdav, curl, shasum.
#
# Inputs (from macos/Vagrantfile): LAB_PUBKEY, the lab's SSH public key; VM_NAME; VM_USER, the
# image's account.
set -euo pipefail

# --- The lab's key opens the VM's account --------------------------------------
home=$(dscl . -read "/Users/$VM_USER" NFSHomeDirectory | awk '{print $2}')
install -d -m 700 -o "$VM_USER" -g staff "$home/.ssh"
keys="$home/.ssh/authorized_keys"
touch "$keys"
grep -qxF "$LAB_PUBKEY" "$keys" || printf '%s\n' "$LAB_PUBKEY" >> "$keys"
chown "$VM_USER:staff" "$keys"
chmod 600 "$keys"

# --- TCP segmentation offload off, now and at every boot -----------------------
# With TSO on, bulk TCP from this VM to the VirtualBox VMs stalls (the rclone lab measured 2 MiB
# taking 121 s; with TSO off, 4 x 200 MiB took 6.5 s). Small packets get through, so SSH and the
# SMB handshake work, and then every large transfer hangs.
plist=/Library/LaunchDaemons/local.anyfs-lab.tso-off.plist
cat > /tmp/tso-off.plist <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>local.anyfs-lab.tso-off</string>
  <key>ProgramArguments</key>
  <array>
    <string>/usr/sbin/sysctl</string>
    <string>-w</string>
    <string>net.inet.tcp.tso=0</string>
  </array>
  <key>RunAtLoad</key>
  <true/>
</dict>
</plist>
EOF
if ! cmp -s /tmp/tso-off.plist "$plist"; then
  install -m 644 -o root -g wheel /tmp/tso-off.plist "$plist"
  launchctl bootout system "$plist" 2>/dev/null || true
  launchctl bootstrap system "$plist"
fi
rm -f /tmp/tso-off.plist
sysctl -w net.inet.tcp.tso=0 >/dev/null

# --- Named after its entry in settings.yml -------------------------------------
for key in ComputerName HostName LocalHostName; do
  [ "$(scutil --get "$key" 2>/dev/null || true)" = "$VM_NAME" ] || scutil --set "$key" "$VM_NAME"
done

# --- The folders anyfs runs from and mounts under -----------------------------
install -d -m 755 -o "$VM_USER" -g staff "$home/anyfs-lab" "$home/anyfs-lab/anyfs" "$home/anyfs-lab/mnt"

# --- Never sleeps ---------------------------------------------------------------
pmset -a sleep 0 displaysleep 0 disksleep 0 >/dev/null 2>&1 || true

# --- Summary -------------------------------------------------------------------
echo "$(sw_vers -productName) $(sw_vers -productVersion) $(uname -m), net.inet.tcp.tso=$(sysctl -n net.inet.tcp.tso), lab key authorized, mount_smbfs $(command -v mount_smbfs >/dev/null && echo yes || echo missing), mount_webdav $(command -v mount_webdav >/dev/null && echo yes || echo missing)"
