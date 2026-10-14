# AnyFS test lab: the test machines, defined as code

The machines the end-to-end tests in `tests/AnyFS.E2E` run anyfs on and reach its mounts from. One
machine per OS runs anyfs with every mount provider that OS has; a second Linux machine is the
upstream anyfs serves from (the SSHFS source's sshd, a Samba share for the Mirror UNC source) and the
remote client.

| Machine | Role | OS | Address | Runs on | Configured by |
|---|---|---|---|---|---|
| `anyfs-debian-2` | upstream: sshd + the fixture tree, Samba share `fixture`; remote client (cifs, davfs2, curl, lftp) | Debian 13 | 192.168.0.96 | VirtualBox, this PC | `ansible/playbook.yml` |
| `anyfs-debian-1` | runs anyfs (linux-x64): FUSE, SMB, WebDAV, FTP, HTTP; local client | Debian 13 | 192.168.0.95 | VirtualBox, this PC | `ansible/playbook.yml` |
| `anyfs-win11-1` | runs anyfs (win-x64) with WinFsp: WinFsp, SMB, WebDAV, FTP, HTTP; local client (Explorer-style UNC paths, `net use /TCPPORT`, WebClient, curl) | Windows 11 25H2 Enterprise (evaluation) | 192.168.0.97 | VirtualBox, this PC | `windows/provision.ps1` |
| `anyfs-macos-1` | runs anyfs (osx-arm64): SMB, WebDAV, FTP, HTTP; local client (`mount_smbfs`, `mount_webdav`, curl) | macOS 15 Sequoia | 192.168.0.98 | tart on the Mac mini (192.168.0.33) | `macos/provision.sh` |
| this PC | runs the tests; publishes the builds and shares them into the VMs | Windows 11 | 192.168.0.31 | | |

**Vagrant** builds and runs the VMs. **Ansible** (Linux), a PowerShell script (Windows) and a shell
script (macOS) configure what's inside them. You edit one file, `settings.yml`, and can rebuild any
machine identically at any time. The tests read the machines, their addresses and logins from that
same file (`tests/AnyFS.E2E/Lab.cs`).

## Quick start

    cd C:\Users\Smith\Desktop\dev\cs\AnyFS\provisioning
    ..\scripts\fetch-go-servers.ps1     # once: the pinned SMB server binaries anyfs embeds
    ..\scripts\fetch-winfsp.ps1         # once: the pinned WinFsp installer (the Windows VM installs it too)
    vagrant up                          # the three VirtualBox VMs
    .\mac.ps1 setup                     # once: lets the lab's key in to the Mac, installs tart and Vagrant there
    .\mac.ps1 up                        # the macOS VM
    .\publish.ps1                       # anyfs for win-x64, linux-x64 and osx-arm64, MakeTree, the sample plugin -> publish\
    .\check.ps1                         # is it all ready?

then, from the repo:

    dotnet test tests/AnyFS.E2E

The first `vagrant up` takes about an hour: the Windows VM's first boot 15 to 20 minutes (the box is
14 GB, already here if the ft or rclone lab is), each Linux VM about 10 (most of it bringing Debian
up to date). After that, `vagrant up` just boots them. The lab takes about 11 GB of memory on this
PC and 6 GB on the Mac (the rclone lab's macOS VM can't run at the same time).

## Everyday commands

| Command | What it does |
|---|---|
| `vagrant up [name]` | Create/boot the VMs (provisions only when first created) |
| `vagrant halt` and `.\mac.ps1 halt` | Stop the lab |
| `vagrant reload <name>` | Restart a VM |
| `vagrant provision [name]` | Re-apply the configuration to a running VM. Safe to repeat |
| `vagrant reload --provision [name]` | Restart with changed `memory_mb`/`cpus`/`ip`, then provision |
| `vagrant destroy -f <name>` then `vagrant up <name>` | Rebuild a VM from scratch |
| `vagrant ssh <name>` | Shell into a VM |
| `.\mac.ps1 up` / `halt` / `status` / `provision` / `destroy -f` | The same for the macOS VM, on the Mac |
| `.\mac.ps1 ssh anyfs-macos-1` | Shell into it |
| `.\publish.ps1` | Rebuild what the tests run (do this after every code change) |
| `.\check.ps1` | Every machine's smoke test, over the LAN, as the tests reach them |
| `ssh -i ~/.ssh/anyfs_lab_ed25519 user@192.168.0.95` | Log in as the tests do (`vagrant@` for Windows, `admin@` for the macOS VM) |

After every `vagrant up`, `reload` and `provision`, each VM runs its smoke test and shows its
PASS/WARN/FAIL lines; a FAIL stops Vagrant there.

## Files

| File | Role |
|---|---|
| `settings.yml` | The only file you normally edit: the machines, their roles, addresses and sizes, the logins, the Mac |
| `Vagrantfile` | The VirtualBox VMs: box, CPU/RAM, bridged network, shared folders, provisioners, smoke tests |
| `ansible/playbook.yml` | The configuration inside each Linux VM; what a VM gets depends on its role |
| `ansible/tasks/` | `base.yml` (every VM), `upstream.yml` (sshd, Samba, the SSHFS key), `host.yml` (fuse3) |
| `ansible/templates/smoke-test.sh.j2` | A Linux VM's smoke test (`/opt/anyfs/smoke-test.sh`) |
| `windows/provision.ps1` | The configuration inside the Windows VM (OpenSSH, WinFsp, WebClient, RunRemote, autologon) |
| `windows/smoke-test.ps1` | The Windows VM's smoke test (`C:\anyfs-lab\smoke-test.ps1`) |
| `mac.ps1` | Runs Vagrant for the macOS VM on the Mac, from this PC, over SSH |
| `macos/Vagrantfile`, `macos/provision.sh` | The macOS VM (tart, pinned MAC address) and what runs inside it |
| `macos/setup-host.sh`, `macos/shims/tart` | The Mac's one-time setup, and a wrapper that keeps the VM alive after Vagrant exits |
| `publish.ps1` | Builds anyfs per runtime, MakeTree and the sample plugin into `publish\` (shared into the VMs; not in git) |
| `check.ps1` | Is the lab ready? |

## How a test runs

`publish.ps1` puts the trimmed `anyfs` for each runtime in `publish\<rid>\`, with `MakeTree`
beside it; the Vagrantfile shares that folder into the VirtualBox VMs (`\\vboxsvr\publish` on
Windows, `/media/vboxsf/192.168.0.31/publish` on Linux) and the tests SCP it to the macOS VM. A
test class deploys to its host machine: it copies the binaries out of the share, generates the
fixture tree there with `MakeTree` (the same tree on every machine, from a seed, so no large files
travel over the LAN), writes a `settings/` folder with one collection per source (the local
Mirror, the SSHFS source on the upstream, and on Windows the Mirror of the upstream's Samba share),
each with every mount provider the OS has, starts `anyfs serve` (Linux and macOS: `nohup` over
SSH; Windows: through RunRemote, inside the logged-on session, where WinFsp's `\\anyfs\...` and
stored SMB credentials live), waits for `anyfs status` to go green, then exercises each mount from
the host itself and from the upstream machine over SSH and compares against the fixture. A machine
that can't be reached marks its rows Inconclusive.

## Design notes

- **`ansible_local`**: Ansible can't run as a control node on Windows, so Vagrant installs it inside
  each Linux VM and runs the playbook there. No host tooling needed.
- **Idempotence**: each step declares a *state* ("package present", "share exported"), not an
  action, so `vagrant provision` changes nothing unless something drifted. Run it whenever you like.
- **Debian kept current, the kernel included**: the kernel's cifs, nfs and fuse clients are among
  what anyfs is tested with, so the play upgrades each Linux VM rather than leaving it on the kernel
  bento's box was built with, and the Vagrantfile reboots into the newest one. The Guest Additions'
  boot script has no modules for a new kernel, so a drop-in makes it load the kernel's own vboxguest
  and vboxsf instead (as the ft, rclone and SimpleKVM labs do).
- **The WinFsp driver on the Windows VM** is the pinned installer from `scripts/winfsp.json`, the
  same MSI anyfs.exe carries; the provisioner checks its SHA-256 before `msiexec /qn`.
- **The SSHFS source logs in with a key** the upstream makes at provisioning (`/srv/anyfs/sshfs_key`):
  headless anyfs can't answer a password prompt, and a password would have to be in the secret store,
  which only anyfs can write. The tests copy the key to each host.
- **The macOS VM** follows the rclone lab exactly: tart on the Mac mini, a MAC address pinned in
  `macos/Vagrantfile` and the address reserved for it on the router, Vagrant run on the Mac by
  `mac.ps1`. TCP segmentation offload is switched off in the VM, or bulk transfers to the VirtualBox
  VMs stall.
