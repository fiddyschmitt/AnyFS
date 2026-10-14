# Connecting to a mount

AnyFS never maps a drive letter or creates a persistent connection unless you opt in; every
mount provider is reached through the OS's own path form. The tile's status line is the access
path, with a Copy button beside it, and **Open containing folder** does whatever the row below
says. NFS arrives in milestone 7.

| Provider | Serves on | Mounted by | Access path | Writes | Default port |
|---|---|---|---|---|---|
| WinFsp | Windows | the same machine | `\\anyfs\<collection>` | yes | none |
| SMB | all | Windows 11 24H2+, Linux, macOS | `\\<host>\<collection>` on TCP 1445 | yes | 1445 |
| FUSE | Linux | the same machine | `~/anyfs/<collection>` (configurable) | yes | none |
| WebDAV | all | all | `http://<host>:11000/<collection>/`; Windows also `\\<host>@11000\DavWWWRoot\<collection>` | yes | 11000 |
| FTP | all | all | `ftp://<host>:2121/<collection>/` | yes | 2121 |
| HTTP | all | any browser or downloader | `http://<host>:11001/<collection>/` | no | 11001 |

Ports under 1024 need administrator rights on every OS and 445 is held by Windows itself, so none
of the defaults are the "well-known" ones. Every network provider has a **bind address**:
`127.0.0.1` serves this machine only, `0.0.0.0` the LAN. Mounts on the same address and port
share one server, each collection at its own name.

## WinFsp (Windows)

One WinFsp *network* file system per collection with the prefix `\anyfs\<collection>`: WinFsp's
network provider resolves `\\anyfs\<collection>\...` in Explorer, cmd and every program, with no
DNS and no drive letter. `\\anyfs` itself is not browsable (error 53); the tile's Open button is
the way in, or type the path. A drive letter is an opt-in setting (Advanced) for programs that
cannot take UNC paths; when one is set, Open opens the letter.

The first **Open containing folder** after a start takes a few seconds (about five on the
development PC, and two to three for the first `\\anyfs\...` from any other new program); after
that the folder is instant for the life of that program. The file system answers in milliseconds.
The time goes on Windows looking the name `anyfs` up on the network (DNS, LLMNR, mDNS and NetBIOS
all time out, since no such machine exists) before the Multiple Provider Router settles on WinFsp,
and on Explorer probing a server it has not met. A `127.0.0.1 anyfs` line in
`C:\Windows\System32\drivers\etc\hosts` cuts `net use \\anyfs\<collection>` from 5 s to 0.2 s, but
Explorer's first open still takes two to four seconds. The drive letter setting avoids it
entirely: the letter opens in about 0.1 s from a fresh program, because a letter resolves without
the network providers.

The WinFsp driver is a separate install with no reboot. Without it the tile is red ("The WinFsp
driver is not installed.") and shows **Install WinFsp driver**: the WinFsp 2.1.25156 installer
that Windows builds of anyfs carry runs silently (`msiexec /i winfsp.msi /qn /norestart`, so the
only thing you see is the administrator prompt), and the mount starts once it is in; the Add mount
dialog offers the same. A build without the installer inside it falls back to
`winget install WinFsp.WinFsp`. WinFsp is GPLv3 with a FLOSS exception; the installer is conveyed
unmodified and its source is at <https://github.com/winfsp/winfsp>.

The driver and the .NET layer anyfs links (the `winfsp.net` package) must agree: WinFsp refuses a
driver of another major version or an older minor ("incorrect dll version (need 2.1, have 2.0)"),
while a newer 2.x driver is fine. An older driver shows that on the tile with **Upgrade WinFsp
driver**, which runs the same silent install; the build itself fails if the package and the bundled
installer drift apart. The published single-file exe keeps the .NET layer out of its bundle (the
layer reads its version from its own file, and a bundled assembly has none) and writes a copy
under `%LOCALAPPDATA%\AnyFS\winfsp` the first time a WinFsp mount starts.

The read-only layers show as the read-only file attribute, and writes they refuse get "access
denied" (or "write protected" when the whole mount is read-only). Names are case-insensitive and
preserving. Reparse points, streams and security descriptors are not exposed; everyone gets full
access as far as Windows is concerned, and the layers decide.

## SMB (all OSes)

Served by the bundled [portable-smb-server](https://github.com/fiddyschmitt/portable-smb-server)
(a single Go executable, SMB 2.0.2 to 3.0.2, NTLMv2 with signing or guest), extracted into the
state folder on first use. One process runs per bind address and port for every collection
mounted there, so every collection on a port shares one login (user and password, or guest); it
talks to AnyFS over a loopback "bring your own VFS" endpoint that refuses anything without the
per-launch token.

- **Windows 11 24H2+**: a port other than 445 needs the device-less
  `net use \\<host>\<collection> /TCPPORT:1445 /USER:<user>` once per session (no drive
  letter: it just registers the port for that server); afterwards `\\<host>\<collection>` is a
  plain UNC path everywhere. Open runs `net use \\<host>\<collection> /delete` first (Windows caches
  the server's credentials, so after you change the password the old ones would be reused and the
  open would fail with "the user name or password is incorrect"), then the connect with the current
  password, then opens the path. The delete omits `/y`, so if you are browsing the share with files
  open it is declined rather than forcing them shut, and the reconnect is then a harmless no-op.
  Give the mount a **Drive letter** instead
  and anyfs maps it while the mount runs (`net use X: \\<host>\<collection> /TCPPORT:1445 ...`),
  Open opens the letter (which Explorer shows at once, unlike a new UNC server name), and the
  mapping is removed when the mount stops. A letter already in use by something else leaves the
  mount yellow with the reason; the UNC path still works. A guest share gets no letter where
  Windows blocks guest logons (the default on Pro and Enterprise), said at once rather than after
  `net use`'s twenty-second refusal: give the share a user and password. The box only appears on
  Windows.
- **Linux**: `sudo mount -t cifs //<host>/<collection> /mnt/<collection> -o port=1445,vers=3.0,user=<user>`
  (Open shows the line with a Copy button).
- **macOS**: Finder → Go → Connect to Server, `smb://<user>@<host>:1445/<collection>`, which Open
  does for you; or `mount -t smbfs //<user>@<host>:1445/<collection> ~/anyfs/<collection>`.

Windows refuses guest shares unless its `EnableInsecureGuestLogons` policy is on, so a user and
password is the usual choice. A read-only membership or source shows on the share as the
read-only attribute, and writes to it are refused with "access denied" before anything reaches a
source.

## FUSE (Linux)

Mounted at the folder you choose (`~/anyfs/<collection>` by default, created if missing) through
libfuse3; install the `fuse3` package. The mount is yours alone unless `/etc/fuse.conf` contains
`user_allow_other`, in which case **Visible to other users** works and the tile says so otherwise.
A mount left behind by a crash is unmounted on the next start; stopping the mount unmounts it.
macOS is not offered FUSE (spec 8.3): use SMB there.

## WebDAV (all OSes)

Our own RFC 4918 server with class 2 locks (held in memory, so a restart forgets them).

- **Windows**: the tile shows `\\<host>@<port>\DavWWWRoot\<collection>`, which Explorer, cmd and
  every program open through the WebClient service (the `@port` and `DavWWWRoot` keywords keep
  Windows off SMB). The service does not start on its own: Open starts it when it is stopped
  (administrator prompt). To avoid the prompt for good: `sc config WebClient start= auto`. Windows
  refuses files over 50 MB (`FileSizeLimitInBytes`) and Basic authentication over plain HTTP
  (`BasicAuthLevel`); for Windows, WinFsp is the real answer and WebDAV the no-install fallback.
- **macOS**: Open mounts it through Finder (`/Volumes/<collection>`); no size cap.
- **Linux**: Open gives `dav://<host>:<port>/<collection>/` to the file manager (gvfs/kio);
  `davfs2` mounts it too.
- Any client: the URL, with Basic authentication when a user is set.

## FTP (all OSes)

Each collection is a folder under the server root. Explorer, Finder (read-only) and file managers
open `ftp://<host>:<port>/<collection>/`; `curl`, `lftp` and friends resume with `REST`. Passive
mode uses any free port unless a range is set (Advanced), with a public address for servers
behind NAT. Anonymous logins see the collections that allow them; a named login sees the
collections configured with that user name.

## HTTP (all OSes)

Serves each collection at `http://<host>:<port>/<collection>/` (default port 11001, loopback
only unless the bind address says otherwise). Read-only by nature.

- A browser shows directory pages (sortable by name, size and modified); `?format=json` on any
  directory returns `[{ "name", "type", "size", "modified", "href" }]` for scripts.
- Downloads are resumable: every file response carries `Accept-Ranges: bytes`, a strong `ETag`
  and `Last-Modified`; a `Range` request returns `206 Partial Content`, `If-Range` is honoured
  against the ETag, an unsatisfiable range returns 416, a multi-range request gets the whole
  file (200), `HEAD` returns the headers alone, `If-None-Match` gives 304. So `curl -C -`,
  `wget -c`, browsers and download managers resume where they left off, at no extra cost: a
  resume at 3 GB is a ranged read of the source.

```
curl -O -C - http://localhost:11001/work/files/aus-files-01/shared/big.iso
wget -c http://localhost:11001/work/files/aus-files-01/shared/big.iso
```
