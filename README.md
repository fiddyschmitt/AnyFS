# AnyFS

AnyFS presents anything as a file system. A *source plugin* turns some system (a share, an SSH
server, a document store, an issue tracker...) into a tree of files; a *mount provider plugin*
exposes that tree to the operating system or the network. A *collection* groups sources and
decides how they are mounted, so `\\anyfs\work\files\aus-files-01\shared\report.docx` can be a
file on a share, `\\anyfs\work\jira\aus-jira-01\issues\GB-501\attachments\foo.png` an attachment,
and both open in any program.

One self-contained executable per OS (Windows, Linux, macOS), with a window or headless. Built-in
sources and mount providers are compiled in; the same plugin interfaces are published so anyone
can write their own.

## Status

Milestones 1 to 4 are done:

- `AnyFS.Sdk`: the plugin contract (sources, mount providers, settings attributes, prompts),
  `SourceBase`, and helpers for caching (`ListingCache`, `SpoolCache`, `RandomAccessOverStream`).
- `AnyFS.Core`: collections and the router that synthesises one tree per collection from its
  sources' paths, the three read-only layers, settings files, secrets, status, stats, logging,
  plugin loading; everything can be changed while it runs.
- The window (Avalonia): Collections, All sources and Stats tabs with tile lists, Add/Edit/Delete
  dialogs, a settings form generated from each plugin's `[Setting]` attributes, prompts, and a
  status bar. It follows the OS light/dark theme.
- Built-in **Mirror** source (local folders, drives and, on Windows, UNC shares) and **SSHFS**
  source (a folder on any SSH server over SFTP: keys or passwords, two-factor prompts through the
  window or the console, host keys pinned on first use, ranged reads on pooled connections).
- Sources can be exported to a file and imported elsewhere (All sources › Export... / Import...),
  never with secrets.
- Built-in mounts: **WinFsp** (`\\anyfs\<collection>` on Windows, no drive letter, with an Install
  button when the driver is missing), **SMB** (the bundled portable-smb-server, for Windows, Linux
  and macOS clients), **FUSE** (Linux), **WebDAV** (our own server, with the Windows UNC form),
  **FTP** and **HTTP** (directory pages, JSON, resumable downloads). [docs/mounts.md](docs/mounts.md)
  says how to reach each one without mapping anything.
- `anyfs serve` and `anyfs status` for headless use.
- Unit and GUI tests, a trimmed single-file publish, CI.

Coming next: the end-to-end lab and release workflow (M5), a macOS `.app` (M6), NFS (M7).

## Try it

```
dotnet build
dotnet test
dotnet run --project src/AnyFS.App -- --settings-dir ./scratch/settings
```

The last line opens the window against a scratch settings folder. The first start creates
"Collection 1" with the native mount provider for the OS (WinFsp on Windows, FUSE on Linux, SMB on
macOS). Use **Add ▾ › New source...** under Sources to add a Mirror of a folder; on Windows with
WinFsp installed (the tile offers to install it) the collection is at `\\anyfs\Collection 1` at
once, and **Open containing folder** opens it in Explorer. **Add** under Mounts adds the others:
an HTTP mount, say, serves <http://localhost:11001/Collection%201/> to a browser.

Headless, the same settings files drive `anyfs serve`. The format is in
[docs/settings-format.md](docs/settings-format.md); for example a folder over HTTP:

```json
// settings/sources.json
{ "version": 1, "sources": [
  { "id": "11111111-1111-1111-1111-111111111111", "name": "photos", "type": "mirror",
    "enabled": true, "settings": { "paths": "D:/Photos" } } ] }

// settings/collections.json
{ "version": 1, "collections": [
  { "id": "22222222-2222-2222-2222-222222222222", "name": "work", "enabled": true,
    "sources": [ { "id": "11111111-1111-1111-1111-111111111111", "path": "files", "readOnly": false } ],
    "mounts":  [ { "id": "33333333-3333-3333-3333-333333333333", "name": "HTTP", "provider": "http",
                   "enabled": true, "readOnly": false, "settings": { "bindAddress": "127.0.0.1", "port": 11001 } } ] } ] }
```

Then `anyfs serve`, open <http://localhost:11001/work/>, and `anyfs status` in another terminal.

```
anyfs                                  the window
anyfs serve [--collection NAME]...     run all (or the named) enabled collections until Ctrl+C
anyfs status                           collections, sources, mounts, states, access paths; exit 0 when all green
anyfs --settings-dir DIR ...           use another settings folder (window or command)
anyfs --log-level LEVEL ...            trace, debug, info, warn, error
anyfs --version
```

Settings live beside the executable when that folder is writable, else in the user's config
folder (`%APPDATA%\AnyFS`, `~/.config/anyfs`, `~/Library/Application Support/AnyFS`).

On Windows `anyfs.exe` is a GUI program, so a console window never flashes up when the window
starts. With arguments it attaches to the console it was started from and prints there; because the
shell does not wait for GUI programs, `anyfs status` returns the prompt at once unless its output
goes through a pipeline (`anyfs status | more`, `$s = anyfs status | Out-String`; a plain
`$s = anyfs status` does not wait), and `anyfs serve` keeps running in the background of that
console until Ctrl+C. Scripts that need the exit code use a pipeline (`$LASTEXITCODE` is set
afterwards) or `Start-Process -Wait`.

## Publish

```
.\scripts\fetch-go-servers.ps1        # the pinned portable-smb-server binaries into tools/go-servers/<rid>/
.\scripts\fetch-winfsp.ps1            # the pinned WinFsp installer into tools/winfsp/ (Windows builds embed it)
.\scripts\publish.ps1                 # every RID into publish/<rid>/
.\scripts\publish.ps1 -Rid win-x64    # one
.\scripts\smoke.ps1 -Exe publish\win-x64\anyfs.exe -SamplePluginDll tests\fixtures\SamplePlugin\bin\Release\net10.0\AnyFS.SamplePlugin.dll
```

The executable is self-contained, single file and trimmed; a documented set of framework
assemblies is kept whole so third-party plugins can rely on it (see
[docs/plugin-authoring.md](docs/plugin-authoring.md)). Two tools ride inside it: the SMB provider's
server, a separate Go executable ([portable-smb-server](https://github.com/fiddyschmitt/portable-smb-server)),
and the WinFsp installer behind the **Install WinFsp driver** button. Every build embeds them for
the runtime it targets (the publish RID, or the SDK's own RID for a plain `dotnet build`, so a
Debug run has them too) from `tools/go-servers/` and `tools/winfsp/`; run the two fetch scripts
once before building, or the SMB provider says its server is missing and the Install button falls
back to winget.

## Plugins

Drop a DLL into the `plugins` folder beside `settings`. A plugin references `AnyFS.Sdk` and
implements `ISourcePlugin` or `IMountProviderPlugin`; its settings are a POCO with `[Setting]`
attributes and the host builds the form, the JSON and the secret handling.
[tests/fixtures/SamplePlugin](tests/fixtures/SamplePlugin) is the 60-line example.

## Layout

```
src/AnyFS.Sdk            interfaces, DTOs, SourceBase, caching helpers
src/AnyFS.Sdk.Testing    contract tests and fakes for plugin authors (and our own tests)
src/AnyFS.Core           model, plugin host, router, read-only layers, status, settings, secrets, stats, logging
src/AnyFS.App            anyfs: the window (Gui/) and the command line (Cli/)
src/Sources/Mirror       the Mirror source
src/Sources/Sshfs        the SSHFS source over SSH.NET
src/Mounts/WinFsp        the WinFsp mount (Windows)
src/Mounts/Smb           the SMB mount: the VFS endpoint and the portable-smb-server process
src/Mounts/Fuse          the FUSE mount (Linux)
src/Mounts/WebDav        the WebDAV server and mount
src/Mounts/Ftp           the FTP mount over FubarDev.FtpServer
src/Mounts/Http          the HTTP mount
tests/                   unit tests per assembly, GUI tests (headless Avalonia), fixtures/MakeTree, fixtures/SamplePlugin
scripts/                 publish.ps1, smoke.ps1, fetch-go-servers.ps1 (+ go-servers.json), fetch-winfsp.ps1 (+ winfsp.json)
tools/go-servers/        the fetched Go server binaries per RID (not in git)
tools/winfsp/            the fetched WinFsp installer (not in git)
docs/                    settings-format.md, plugin-authoring.md, mounts.md
```

## Licence and third-party components

AnyFS is MIT licensed. It embeds two unmodified third-party binaries:

- [portable-smb-server](https://github.com/fiddyschmitt/portable-smb-server) (MIT), one per
  runtime, extracted into the state folder on first use.
- The [WinFsp](https://github.com/winfsp/winfsp) installer `winfsp-2.1.25156.msi` (GPLv3 with the
  FLOSS exception), in Windows builds, for the Install button. Its corresponding source is that
  repository at tag `v2.1`. WinFsp's .NET layer (`winfsp-msil`, the `winfsp.net` package, same
  licence) is linked as usual and also carried as a resource of the WinFsp plugin, so the
  single-file exe can hand it to the runtime as a file.
