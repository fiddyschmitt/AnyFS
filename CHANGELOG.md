# Changelog

## Unreleased (milestone 3: mount providers)

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

## Unreleased (fixes after milestone 2)

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

## Unreleased (milestone 2: the window)

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

## Milestone 1 (headless core)

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
