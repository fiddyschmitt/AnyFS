# Changelog

## 0.2.0 (unreleased)

The release notes are in [docs/release-notes/v0.2.0.md](docs/release-notes/v0.2.0.md).

### Milestone 6: SharePoint

- **SharePoint** (`sharepoint`, spec 7.3): one source for every kind of SharePoint, told apart by
  the address. A site under `.sharepoint.com` or `.sharepoint.us` is SharePoint Online, a container
  type's id is SharePoint Embedded's containers (each a drive, one pinned by name or by id), and any
  other address is SharePoint Server on the person's own network, by name or by address. Every kind
  shares one file-system half: ranged reads with spooling below a threshold, write-on-close uploads,
  renames, times, quota, SharePoint's name rules checked before any request.
- **Sign in with** (Automatic by default; a sign-in that cannot serve the address, such as a Windows
  password for Microsoft 365, is caught in the settings): an *app sign-in*, browser or device code,
  with the shipped app registration or the organisation's own (MSAL, the cache in the source's
  secret store), then Microsoft Graph (one PUT up to 4 MiB, upload sessions in 10 MiB pieces above,
  Graph's error codes mapped); a *web session*, for an organisation that allows no app
  registration, where the person signs in in Edge or Chrome opened by AnyFS with a profile of its
  own, the session's cookies go to SharePoint's REST API, and a headless browser renews them while
  Microsoft's sign-in lasts (one browser per profile at a time, a browser left open on it closed
  first; a closed window or a Defender for Cloud Apps proxy named, and the source then waits for
  Retry). Where AnyFS cannot reach the browser's DevTools port itself (Edge's or Chrome's
  RemoteDebuggingAllowed or UserDataDir policy, or no port opening), the sign-in goes through
  Selenium WebDriver instead, as Mount3DX's does: the default browser first, then Firefox, Chrome and
  Edge, each with a profile of AnyFS's own; a *Windows account* (NTLM or Kerberos; a user name and password, or the
  current logon on Windows) for SharePoint Server through the REST API. The REST store uploads in
  chunks through `StartUpload`/`ContinueUpload`/`FinishUpload` and sets times through a
  client-object-model request (`UpdateOverwriteVersion`), which SharePoint honours for site
  collection administrators.
- **The US Government cloud** (GCC High): app sign-ins go to `login.microsoftonline.us` and
  `graph.microsoft.us` for a site under `.sharepoint.us` or a tenant under `.onmicrosoft.us`, and a
  Cloud setting overrides; an app sign-in there needs the organisation's own registration, since
  AnyFS's exists only in the global cloud, so Automatic takes the web session until its client ID is
  given. Sign-in refusals say which setting to fix and stop the source instead of retrying.
- **The lab farms** (`provisioning/sharepoint/`, `provisioning/windows/dc-*.ps1`, `sp-*.ps1`): a
  domain controller and two one-server farms on SQL Server 2022 Developer, SharePoint Server
  Subscription Edition on Windows Server 2022 (`anyfs-sp-1`) and SharePoint Server 2019 on Windows
  Server 2019 (`anyfs-sp-2`), built by SharePointDsc from pinned images
  (`scripts/fetch-sharepoint.ps1`), with the restarts the build needs driven by the Vagrantfile; the
  shipped app registration (`register-app.ps1`) and the sandbox tenant's Embedded rig
  (`embedded-rig.ps1`) as scripts.
- **E2E**: a collection pair per lab farm on every host (`sharepoint`, `sharepoint2019`, each with a
  `-ro` twin), the fixture seeded through the source; `ANYFS_E2E_COLLECTIONS` selects collections.
- **SDK**: `ListingCache.Update` corrects one cached entry in place after a source's own write;
  `SequentialReadAhead`, the per-handle read-ahead the WinFsp mount had, as a helper for mounts;
  `ISettingVisibility`, for a plugin whose fields depend on what has been entered (the window's
  form asks it after every change and hides what it rules out; Test now waits up to ten minutes,
  for a sign-in in a browser window); `SettingAttribute.Primary` and `ISourcePlugin.SuggestName`:
  the Add source dialog asks the setting that says what the source is first, above the name, and
  fills the name (and so the path) from it until the person types one (SharePoint's Site URL gives
  "contoso Team" for `https://contoso.sharepoint.com/sites/Team`); editing a source whose name is
  still the derived one keeps it in step, and on edit the path follows the name while it is the
  default one for the name.
- **SMB**: the VFS endpoint keeps read handles between `/read` requests (`ReadHandles`); the bundled
  server gets a minute to start, not 15 s (its first launch on a freshly booted Windows machine).
- **FUSE**: per-handle read-ahead through `SequentialReadAhead` (the kernel's 128 KiB reads cost a
  round trip each on a slow source); WinFsp uses the same helper.
- **Sources that upload on close**: a pending file answers stats, listings, reads, times, deletes and
  renames itself until the upload lands (Windows opens files for their attributes alone and posts the
  close without waiting).
- **Publishing**: `System.Net.Security.UseManagedNtlm` on, for NTLM from Linux without the
  gss-ntlmssp plugin (on in macOS builds too, untested there so far). Selenium Manager rides in the
  single-file build as a native file (3.7 MB on Windows), for the web session's WebDriver fallback.

## 0.1.1 (2026-10-17)

The release notes are in [docs/release-notes/v0.1.1.md](docs/release-notes/v0.1.1.md).

- **portable-smb-server 0.4.2** bundled (`scripts/go-servers.json`): a performance release of the
  SMB server (faster message signing, pipelined writes, one gathered write per response, zero-copy
  reads); its SMB behaviour and the VFS contract are unchanged. Retested with the unit suite, the
  trimmed smoke test and the E2E SMB rows on the Windows, Linux and macOS hosts from every client.

## 0.1.0 (2026-10-16)

The first release: everything below, from milestone 1 to the end of milestone 5. The release notes
are in [docs/release-notes/v0.1.0.md](docs/release-notes/v0.1.0.md).

### Milestone 5: the lab, the E2E matrix, release workflow, `plugin check`, the SDK package

- **The test lab** (`provisioning/`, spec 16.2): `settings.yml`, a Vagrantfile for the VirtualBox
  VMs (`anyfs-debian-2` the upstream at 192.168.0.96, `anyfs-debian-1` the Linux host at .95,
  `anyfs-win11-1` the Windows host at .99), `ansible_local` for the Linux machines (Debian kept
  current, the test account, the lab key, cifs/davfs2/curl/lftp clients; the upstream's sshd with
  passwords and keyboard-interactive, a Samba share of the fixture and a key pair for the SSHFS
  source; fuse3 on the host), a PowerShell provisioner for Windows (OpenSSH, the pinned WinFsp
  installer checked against `scripts/winfsp.json`, the WebClient service with its file-size cap
  lifted, RunRemote, autologon), and the macOS VM on the Mac mini through `mac.ps1`/`macos/` (tart,
  a pinned MAC address, TCP segmentation offload off). Each machine runs a smoke test after every
  up/provision; `check.ps1` runs them all over the LAN; `publish.ps1` builds anyfs per runtime,
  `MakeTree` and the sample plugin into the shared `publish\` folder.
- **The E2E suite** (`tests/AnyFS.E2E`, MSTest over SSH.NET, reading `settings.yml`): per host OS,
  anyfs is deployed once with a collection per source (the local Mirror, the SSHFS source on the
  upstream, on Windows the Mirror of the upstream's Samba share) and its read-only twin, every
  mount provider of the OS on each; a row is (collection × mount × client) and checks the listing
  against the fixture (names, unicode, spaces, a 204-character name, nesting, an empty folder, the
  5,000-file folder), the 1 KiB / 1 MiB / 200 MiB hashes with a throughput floor, three ranged
  reads, the write round trip (refused on the read-only twin), eight concurrent readers and, over
  HTTP, `curl -C -` and `wget -c` resumes. Also: counts reach the fixture, a prompt-needing source
  fails cleanly headless, the sample plugin loads in the trimmed build, an upstream outage degrades
  then recovers, and the log has no errors. An unreachable machine makes its rows Inconclusive.
- Two things the lab's first runs caught: FTP names are UTF-8 for every client (the server's default
  was ASCII until a client sent `OPTS UTF8 ON`, which curl, wget and lftp never do, so a name like
  `ünïcödé ✓ 日本語.txt` listed as question marks); and a folder could not be created at the root of a
  collection whose source sits at an empty path over FUSE or WebDAV (the write check refused the
  root as a layout folder while the create path accepted it, so SMB, which asks nothing first,
  worked and the others got "permission denied").
- **Secrets in the settings files**: a secret property found in `sources.json` or
  `collections.json` at load moves into the secret store and the file is rewritten without it (as
  Import already did), so scripts can hand anyfs a password.
- **`anyfs plugin check <dll>`** (spec 14.1): the SDK version against the host's, every framework
  assembly the DLL references against the guaranteed set the trimmed host keeps (one outside it
  fails: the host doesn't carry it and a plugin can't ship one), other references against the files
  beside the DLL, then the DLL loaded as the host loads it, its plugin types, and each settings
  class described, defaulted, round-tripped through JSON and validated. PASS/WARN/FAIL lines, exit
  1 on a FAIL. The guaranteed set now lives in `GuaranteedFramework` with a test holding it to the
  App project's roots and the docs.
- **SDK packages**: `AnyFS.Sdk` and `AnyFS.Sdk.Testing` pack with a README, tags, SourceLink and
  symbol packages (`scripts/pack.ps1`); `docs/plugin-authoring.md` gained "Getting the SDK" and
  "Checking a plugin".
- **Release workflow** (`.github/workflows/release.yml`): a `v*` tag publishes the five runtimes
  (trimmed, smoke-tested where the runner can run them), packs the SDK, and creates the GitHub
  release with one bare executable per runtime (`anyfs-win-x64.exe`, `anyfs-linux-x64`, ...; no
  archive, so nothing to unpack), the packages and `SHA256SUMS.txt`; the packages go to
  NuGet.org when `NUGET_API_KEY` is set. `build.yml` now fetches the bundled tools before
  publishing, and `scripts/publish.ps1` takes `-Version`. The smoke script runs `plugin check` on
  the sample plugin.
- **WinFsp read-ahead**: the Windows rows of the lab showed a 4 KB sequential reader (Get-FileHash)
  getting 17 MiB/s through a WinFsp mount on the VM, one round trip to the source per read, because
  WinFsp passes application reads through as they are (its full caching mode was tried and rejected:
  it served a file's old contents after the source changed). A handle that reads on from where it
  stopped now gets 1 MiB from the source at a time; nothing outlives the handle. The Windows lab
  machine moved to 192.168.0.99 (`settings.yml`), the Windows provisioner and smoke test lost two
  PowerShell slips (`$msi:` in a string, a DWORD compared with the Int32 `0xFFFFFFFF`), and the E2E
  Windows client escapes its commands for Win32-OpenSSH, writes UTF-8, stores the Samba credential
  through RunRemote and treats a late RunRemote acknowledgement as what it is.
- **The FTP mount is AnyFS's own server now** (`src/Mounts/Ftp/FtpServer.cs`, `FtpSession.cs`); the
  FubarDev.FtpServer library is gone, with its package, its trimming roots and the Newtonsoft.Json
  pin it dragged in. RFC 959 with UTF-8 names, FEAT, REST, SIZE, MDTM, MLSD/MLST, STAT, EPSV/EPRT and
  PORT (to the client's own address only), APPE, ABOR during a transfer, a passive port range
  handed out round robin, 1 MiB reads and 256 KiB writes against the collection's tree, the same
  settings, logins and root of collections as before; listings no longer carry `.` and `..`. New
  unit tests: active mode with MLSD facts and MDTM, eight concurrent readers, APPE and REST before
  STOR, ABOR mid-transfer. The library dropped connections under concurrent downloads on the lab's
  slow Windows VM with a null reference inside its RETR handler, copied files to the data
  connection a few KB at a time, and has had no release since 2021.
- **portable-smb-server 0.4.0** (`scripts/go-servers.json`): a lookup by name is one stat instead
  of a listing of the whole folder, and Apple's SMB2 extensions are answered (the `AAPL` create
  context, ReadDirAttr entries), so macOS, which resolves every entry of a folder by name, lists
  the lab's 5,000-file folder from a remote anyfs in about 8 s with or without attributes where
  it never finished within two minutes before (Samba without `vfs_fruit`: 10 s and 48 s from the
  same Mac). The ten Mac-over-SMB rows that failed by timeout pass.
- **Every lab machine is a client of every host**: the E2E rows reach each host's mounts from the
  host itself, from the Linux upstream, and now from the Windows and macOS machines too (Windows
  `net use /TCPPORT` and WebClient against the Linux and Mac hosts, `mount_smbfs` and
  `mount_webdav` against the Linux and Windows hosts, curl from all); a machine that is down makes
  its rows Inconclusive. 194 rows in all (Linux 68, Windows 78, macOS 48) instead of 122.
- The FTP library's own debug log (each command, connection and data channel event) now reaches
  `anyfs.log` at debug level; it was cut off at warning. The lab's concurrent-reader rows from the
  Windows machine saw sporadic control-connection resets under eight simultaneous downloads, once
  with a `NullReferenceException` inside the library's RETR handler, and those lines are what a
  diagnosis needs.
- **FTP downloads read the source 1 MiB at a time**: the Windows rows' Mirror of the upstream's
  Samba share served 3.5 MiB/s over FTP against 23 over HTTP, because the FTP library copies a file
  to the data connection a few KB at a time and each piece was a round trip to the share. The
  stream handed to it is now buffered.
- **One unpacked SMB server per version**: the bundled portable-smb-server is written to
  `cache/mount-providers/smb/portable-smb-server-<hash>` and shared by every SMB mount (it used to
  be one copy per mount under `state/mounts/<id>/bin/`, and nothing removed old versions); copies
  of other versions, and the temporary file of a write that never finished, are deleted when a
  new one lands, except a copy an older anyfs is still running, which goes next time. The SDK's
  `IMountContext` gained `ProviderCacheDirectory` for files a provider's mounts can share;
  `TestMountContext` shares it between contexts built with `shareWith`. The smoke test checks
  that exactly one copy is there.
- **The same for anyfs's own unpacked files**: the single-file host unpacks the native libraries
  (Skia, HarfBuzz, ANGLE) into `%TEMP%\.net\anyfs\<build>\` (`~/.net/anyfs/` on Linux and macOS)
  and never removes a previous build's folder, so every build a user ran left 18 MB behind. At
  start-up anyfs now deletes the other builds' folders, except one a running anyfs has loaded and
  one written in the last ten minutes (an anyfs still starting); the host puts a deleted folder
  back the next time that build runs. The WinFsp plugin's copy of `winfsp-msil` under
  `%LOCALAPPDATA%\AnyFS\winfsp` likewise loses the copies of other versions when a new one lands.
  (Having the host unpack portable-smb-server beside those libraries was tried and rejected: the
  folder is temp, which Storage Sense empties, carries no execute bit, and is reachable only
  through an undocumented runtime property.)
- **portable-smb-server 0.4.1** (`scripts/go-servers.json`): conformance fixes for the operating
  systems' own clients, found by its new client rig: a signed compound request (the `-o sign`
  Linux mount, Windows and macOS mounts) is verified as received instead of being refused; a
  Windows truncate (`FileAllocationInformation`) is accepted; removing a non-empty folder from
  Windows reports it non-empty instead of succeeding; a case-only rename is allowed; `QUERY_INFO`
  honours the caller's buffer length (Windows' `GetFileInformationByHandle` over the mount); and
  `CLOSE` returns the attributes macOS asks for. Nothing changes in the VFS contract. The lab's 46
  SMB rows and the 15 per-host checks pass on all three hosts, in the same times as with 0.4.0.
- **E2E row selection**: `ANYFS_E2E_MOUNTS` and `ANYFS_E2E_CLIENTS` (comma-separated) narrow the
  matrix to some mounts or clients, since a test-name filter cannot reach rows that are expanded
  at run time; the SMB retest above took 35 minutes instead of two hours.

### Milestone 4: SSHFS, prompts, Import/Export

- **SSHFS** source (type `sshfs`, folder `sshfs`) over SSH.NET 2026.0.0: a remote root (`/`, `~`
  or any absolute path) served through SFTP with up to 16 pooled connections (open files spread
  across them), ranged reads, listings cached for two seconds, writes (create, delete, rename
  with replace through `posix-rename@openssh.com`, truncate, modified time), `statvfs` for free
  space. Symbolic links show as what they point at (dangling ones as empty files), dot names are
  hidden, files nobody may write are read-only. A dropped connection is retried once on a fresh
  one and the source is Degraded until something succeeds.
- **Logins and prompts**: key file (asking for the passphrase when the key is encrypted and none
  is stored), password, then keyboard-interactive, whose prompts are answered with the saved
  password once and otherwise reach the user through the existing prompt dialog or the console
  (two-factor codes, PINs); declined or headless without a console, the source fails with "Needs
  input" and waits for Retry. Host keys are trusted on first use and pinned per algorithm in the
  source's state; a changed key fails the source naming both fingerprints.
- **Import... and Export...** on the All sources tab: Export writes the ticked sources to a
  `sources.json`-shaped file without secrets; Import merges by id (a clash asks replace, keep or
  import as a copy), keeps names unique, moves any secrets a hand-written file carries into the
  secret store, and starts what it added.
- SDK/App: `IDialogs` gained a save-file picker and a multi-option question; `SourceTransfer`
  (Core) reads and writes the shared file; `ImportSourcesAsync`/`ExportSources` on the engine.
- `tests/fixtures/sshfs-fixture.sh` makes the tree the live SSHFS tests expect; they run when
  `ANYFS_TEST_SSH_HOST` (and friends) point at a server and are inconclusive otherwise.
- SSHFS against a Windows OpenSSH server: the drives it lists at `/` as `C:`, `D:` (names no
  Windows mount can show) appear as folders `c`, `d`, and `C:\Users` is accepted as a root.
- SSHFS "Count files on start" is the source's own parallel walk (four folders per connection at
  once, links counted but not followed) instead of the host's one-at-a-time walk; symbolic links in
  one listing are resolved several at a time.
- Counts and stats use tabular figures (Inter's `tnum`), so a changing number no longer shifts the
  text around it.
- "Count files on start" for a source without its own count (the host's folder-by-folder walk) no
  longer pauses between folders. The status while counting is just "Counting files": the number is
  the count shown under it.
- SMB on Windows: "Open containing folder" now drops any cached connection to the server first, so a
  mount whose password you changed opens with the new password instead of failing with "the user
  name or password is incorrect" (Windows had kept the old credentials; a reconnect over them fails
  with error 1219). `MountOpenAction.Command` gained an optional best-effort `Reset` step for this,
  and the opener closes the command's stdin so a `net use /delete` never waits on a prompt.
- WinFsp: `winfsp.net` is pinned to the bundled driver's major.minor and the build fails when they
  drift (NuGet's newest, 2.2.x, is the WinFsp 2026 beta; it refuses the 2.1 driver at load with
  "incorrect dll version"). A driver older than the build needs shows the reason and an **Upgrade
  WinFsp driver** button (the same silent install); a failure to load the .NET layer is reported in
  words instead of a reflection stack. ARM64 Windows is detected (`winfsp-a64.dll`).
- The published single-file `anyfs.exe` could not mount WinFsp at all: the .NET layer reads its own
  file version through `Assembly.Location`, which is empty inside a bundle. The bundle now leaves
  `winfsp-msil` out and the WinFsp plugin hands the runtime its own copy, written once per user
  under `%LOCALAPPDATA%\AnyFS\winfsp`; `scripts/smoke.ps1` mounts WinFsp on Windows so this stays
  covered.

### Fixes after milestone 3

- Add source: a filter box above the source types matches the name, the description and the type
  id, for when there are hundreds of them.
- Add mount: the same filter box plus two pills. **High performance** keeps the native and
  network-tier providers (WinFsp, FUSE, SMB); **No admin** hides providers that need something
  installed as administrator first (WinFsp without the driver, FUSE without libfuse3), through the
  new optional `IMountProviderPlugin.NeedsAdministrator`.
- Counting files is nominal: a source that is enumerating shows green, and the collection's status
  says "counting files".
- Collections tab: the Sources and Mounts box starts level with the first collection tile, so it
  reads as part of the selected collection.
- All sources: the tiles are up to 560 px wide.
- Stats: the per-collection table drops a collection when it is deleted (and, at start, any that
  was deleted while anyfs was not running); a rename keeps its counters under the new name; the
  "A file is served when a handle..." line is gone.
- `scripts/go-servers.json` carries the SHA-256s of the portable-smb-server v0.3.0 release, so
  `scripts/fetch-go-servers.ps1` now verifies every binary it downloads.
- Stats: the per-collection breakdown is a table (bordered, shaded two-row header, a row for every
  collection whether it has served anything or not).
- Status bar: the settings path beside **Open log** is gone.
- SMB: a **Drive letter** box (Windows only) maps the letter to the share while the mount runs
  (`net use X: \\<host>\<collection> /TCPPORT:<port>`), Open opens the letter, and the mapping is
  removed when the mount stops; a letter in use by something else leaves the mount yellow with the
  UNC path still working, and a guest share gets no letter where Windows blocks guest logons (said
  at once). The Add mount dialog no longer talks about where to put
  portable-smb-server or `ANYFS_SMB_SERVER`: every build embeds the server for the runtime it
  targets (a plain build takes the SDK's own RID), so a Debug run has it too.
- WinFsp: Windows builds embed the WinFsp 2.1.25156 installer (`scripts/fetch-winfsp.ps1`,
  `tools/winfsp/`), and **Install WinFsp driver** runs it silently through `msiexec /qn` behind one
  administrator prompt; winget stays the fallback for a build without it. With a drive letter set,
  Open opens the letter.
- SDK: `SettingAttribute.Platforms` shows a field only on the named operating systems (the JSON
  keeps the value everywhere), and `DriveLetter` normalises and validates a letter setting.

### Milestone 3: mount providers

- **WinFsp** (Windows): one WinFsp network file system per collection at `\\anyfs\<collection>`,
  with no drive letter (a letter is an opt-in setting) through a prefix-only mount of winfsp.net
  2.1.25156; case-insensitive and preserving, read-only layers shown as the read-only attribute,
  normalized names, delete-on-close, rename, times. When the WinFsp driver is missing the tile and
  the Add mount dialog offer **Install WinFsp** (`winget install WinFsp.WinFsp`) and retry the mount
  afterwards.
- **SMB** (all OSes) through portable-smb-server 0.3.0: one server process per bind address and
  port serving every collection mounted there, fed by a loopback VFS endpoint that only answers
  requests carrying the per-launch token; per-file read-only reaches the share as
  `FILE_ATTRIBUTE_READONLY`; the process restarts when the set of shares changes. Access paths and
  Open: the device-less `net use \\host\<collection> /TCPPORT:1445` on Windows, `smb://` for Finder,
  the `mount -t cifs` line on Linux. `scripts/fetch-go-servers.ps1` fetches (or copies from a local
  build) the binaries that the publish embeds per RID; `ANYFS_SMB_SERVER` or a copy beside anyfs
  override it.
- **FUSE** (Linux) through LTRData.FuseDotNet over libfuse3: `~/anyfs/<collection>` by default,
  `allow_other` when `/etc/fuse.conf` permits it (otherwise a note), `default_permissions` with
  mode bits from the read-only layers, stale mounts from a crash cleaned up, the soname resolved
  for distros without the -dev package.
- **WebDAV** (all OSes): our own RFC 4918 server on Kestrel: OPTIONS, PROPFIND depth 0/1,
  PROPPATCH (dead properties accepted, live ones refused), GET with ranges, HEAD, PUT, MKCOL,
  DELETE, recursive MOVE and COPY, class 2 exclusive write locks in memory with refresh and
  lock-null files, optional Basic authentication, quota properties. On Windows the tile shows
  `\\host@port\DavWWWRoot\<collection>` and Open starts the WebClient service when it is stopped
  (administrator prompt); on macOS Open mounts it through Finder; on Linux `dav://`.
- **FTP** (all OSes) through FubarDev.FtpServer 3.1.2: each collection a folder under the server
  root, resume, passive mode with an optional port range and public address, anonymous and named
  logins that each see their own collections.
- Mount tiles gained a Retry item (failed or degraded mounts) and the Install button; the engine
  gained `RetryMountAsync`; the SDK gained `IInstallableProvider`, `BindAddress` and an `Elevated`
  flag on `MountOpenAction.Command`.
- The tile status text trims with an ellipsis instead of overflowing.
- Trimming: winfsp.net, FubarDev.FtpServer, Scrutor and NGettext are rooted whole at publish;
  ILLink's analysis of those libraries' own reflection is suppressed with a justification.

### Fixes after milestone 2

- Collections tab: the "Collection: name" header over the Sources and Mounts box is gone (the
  selected tile on the left says it), and so is the guidance line under Mounts.
- Mount tiles: a running mount's status line is its access path (URL, UNC path), so the Copy
  button now sits beside it and the separate box that repeated it is gone; any further access
  paths a provider reports still get their own box.
- Add source: the membership path is labelled "Virtual Path" with the hint "Where the source
  appears in this collection."; "Count files on start" sits under "Enabled" and defaults on for
  source types that are quick to count (`ISourcePlugin.IsQuickToCount`, true for Mirror) unless
  `app.json`'s `countFilesOnStart` forces it (now `null` by default).
- Mirror: the Paths box's ghost text is a local drive and a typical network path for this OS, one
  per line (`C:\` and `\\aus-files-01\shared` on Windows, `/` and `/mnt/aus-files-01` on Linux,
  `/` and `/Volumes/aus-files-01` on macOS); the hint is just "One per line". "Count files on
  start" walks every configured path at the same time, each on its own thread.
- Source dialog: Read-only, Enabled and Count files on start are evenly spaced. Mirror's field is
  labelled "Paths (one per line)" with no hint and no asterisk (the plugin's own validation asks
  for a path).
- Mount tiles: the Copy button follows the end of the status text instead of sitting at the tile's
  right edge; the letter in a tile's badge is centred.
- Mount tiles no longer repeat the provider's name under a mount named after it (the line stays
  for a mount with a name of its own).
- SDK: `ICountable` (a source counts its own tree when "Count files on start" is on),
  `ISourcePlugin.IsQuickToCount` (optional, default false) and `ISettingTextProvider` (per-machine
  hints and placeholders).
- The example collection in the docs is now `work`.

### Milestone 2: the window

- `anyfs` with no arguments opens the window (Avalonia 12, Fluent, follows the OS light/dark
  theme; `theme` in `app.json` overrides it). On Windows the executable is a GUI program that
  attaches to the console it was started from when given arguments, so `anyfs serve`, `anyfs status`
  and `--help` still print to the terminal.
- Collections tab: tiles for collections, the selected collection's sources and its mounts (boxed
  together), status circles and text with the full message as a tooltip, Enabled checkboxes,
  per-collection Read-only for a source (ticked and locked when the plugin can't write), file counts
  with a spinner while a source starts or counts, Open containing folder (one mount opens it, several
  show a flyout, none disables it), access paths in copyable boxes, a retry item on failed sources,
  guidance under Mounts worded from the providers' tiers.
- Add/Edit/Delete for collections, sources (New source... / Existing source ▸, with the path
  following the name until edited) and mounts; delete confirmations name the mounts and offer to
  delete orphaned source definitions.
- The settings form generated from `[Setting]` attributes: text, multiline, password (with reveal),
  number, bool, choice (enums or `Choices`), file and folder pickers, groups, a collapsed Advanced
  section, required marks, hints, inline errors and warnings from `IValidatable`, a Test button for
  `ITestable`.
- All sources tab: every source with the collections using it, Add (no collection yet), Edit, Delete.
- Stats tab: files and bytes served (total and session), active connections, a per-collection
  breakdown, Reset totals.
- Plugin prompts (PIN, code, yes/no, choice) as modal dialogs, one at a time, with the source's name.
- Status bar: overall circle and summary, files served this session, where the settings live, Open log.
- Window placement is remembered in `app.json`.
- `AnyFS.Core`: the engine now accepts changes while running (add, edit, enable, disable and delete
  sources, memberships, collections and mounts), applies them live (a source restarts only when its
  settings changed; editing or renaming restarts the mounts concerned) and persists them; a `Changed`
  event for the UI.
- `AnyFS.Sdk.Testing`: the in-memory source plugin and the recording mount provider moved here from
  the Core tests so GUI tests (and plugin authors) can use them.
- Tests: `AnyFS.App.Tests` drives the view models over the real engine with scripted dialogs, and
  opens the window and every dialog on Avalonia's headless platform.

### Milestone 1: headless core

- `AnyFS.Sdk`: `ISourcePlugin`/`ISource`, `IMountProviderPlugin`/`IMount`, `IVirtualFileSystem`,
  `IVfsFile` (positionless ranged reads), `VfsEntry`, `VfsException`, `[Setting]` attributes,
  prompts, `SourceBase`, `VfsPath`, `VfsFileStream`, `BytesVfsFile`, `ListingCache`,
  `SpoolCache`, `RandomAccessOverStream`.
- `AnyFS.Sdk.Testing`: `SourceContractTests`, `MemorySource`, `TestSourceContext`,
  `TestMountContext`, `SingleSourceVfs`.
- `AnyFS.Core`: collections with per-membership source paths and read-only flags; the router
  (tree synthesis, merging at shared paths with shadowing, case-insensitive synthetic levels,
  unique case-insensitive fallback inside case-sensitive sources, three read-only layers); settings
  files with atomic writes, versions and quarantine of corrupt files; secrets via Data Protection;
  plugin registry and DLL loading with SDK version checks; source runners with retry backoff,
  progress, health and count-on-start; mount runners; shared listeners; stats (files served,
  bytes read/written, connections; session and lifetime); status snapshots and `status.json`;
  ZLogger logging to a rolling `anyfs.log` (10 MB x 5).
- Built-in Mirror source: folders, drives and UNC shares as folder chains; reachability
  tracking (Ready/Degraded/Failed); writes.
- Built-in HTTP mount on Kestrel: directory pages, JSON listings, resumable downloads (ranges,
  ETag, If-Range, 416, HEAD, 304), one listener per port shared by collections, connection
  counting.
- `anyfs serve`, `anyfs status`, `--settings-dir`, `--log-level`, `--version`.
- Unit tests (MSTest) for every assembly; `scripts/publish.ps1` (self-contained, single file,
  trimmed, guaranteed framework set for plugins); `scripts/smoke.ps1`; GitHub Actions on Windows,
  Ubuntu and macOS.
