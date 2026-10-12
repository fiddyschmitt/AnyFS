# AnyFS

AnyFS presents anything as a file system. A *source plugin* turns some system (a share, an SSH
server, a document store, an issue tracker...) into a tree of files; a *mount provider plugin*
exposes that tree to the operating system or the network. A *collection* groups sources and
decides how they are mounted, so `\\anyfs\ben\files\aus-files-01\shared\report.docx` can be a
file on a share, `\\anyfs\ben\jira\aus-jira-01\issues\GB-501\attachments\foo.png` an attachment,
and both open in any program.

One self-contained executable per OS (Windows, Linux, macOS), with a GUI or headless. Built-in
sources and mount providers are compiled in; the same plugin interfaces are published so anyone
can write their own.

## Status

Milestone 1 (headless core) is done:

- `AnyFS.Sdk`: the plugin contract (sources, mount providers, settings attributes, prompts),
  `SourceBase`, and helpers for caching (`ListingCache`, `SpoolCache`, `RandomAccessOverStream`).
- `AnyFS.Core`: collections and the router that synthesises one tree per collection from its
  sources' paths, the three read-only layers, settings files, secrets, status, stats, logging,
  plugin loading.
- Built-in **Mirror** source (local folders, drives and, on Windows, UNC shares) and **HTTP**
  mount (directory pages, JSON, resumable downloads).
- `anyfs serve` and `anyfs status`.
- Unit tests, a trimmed single-file publish, CI.

Coming next: the Avalonia window (M2), WinFsp, SMB, WebDAV, FTP and FUSE mounts (M3), SSHFS (M4),
the end-to-end lab (M5), NFS (M7).

## Try it

```
dotnet build
dotnet test
dotnet run --project src/AnyFS.App -- status --settings-dir ./scratch/settings
```

A first `anyfs serve` creates `settings/collections.json` with "Collection 1". To serve
something, edit `settings/sources.json` and `settings/collections.json` (format in
[docs/settings-format.md](docs/settings-format.md)), for example a folder over HTTP:

```json
// settings/sources.json
{ "version": 1, "sources": [
  { "id": "11111111-1111-1111-1111-111111111111", "name": "photos", "type": "mirror",
    "enabled": true, "settings": { "paths": "D:/Photos" } } ] }

// settings/collections.json
{ "version": 1, "collections": [
  { "id": "22222222-2222-2222-2222-222222222222", "name": "ben", "enabled": true,
    "sources": [ { "id": "11111111-1111-1111-1111-111111111111", "path": "files", "readOnly": false } ],
    "mounts":  [ { "id": "33333333-3333-3333-3333-333333333333", "name": "HTTP", "provider": "http",
                   "enabled": true, "readOnly": false, "settings": { "bindAddress": "127.0.0.1", "port": 11001 } } ] } ] }
```

Then `anyfs serve`, open <http://localhost:11001/ben/>, and `anyfs status` in another terminal.

```
anyfs                                  the window (milestone 2)
anyfs serve [--collection NAME]...     run all (or the named) enabled collections until Ctrl+C
anyfs status                           collections, sources, mounts, states, access paths; exit 0 when all green
anyfs --settings-dir DIR ...           use another settings folder
anyfs --log-level LEVEL ...            trace, debug, info, warn, error
anyfs --version
```

Settings live beside the executable when that folder is writable, else in the user's config
folder (`%APPDATA%\AnyFS`, `~/.config/anyfs`, `~/Library/Application Support/AnyFS`).

## Publish

```
.\scripts\publish.ps1                 # every RID into publish/<rid>/
.\scripts\publish.ps1 -Rid win-x64    # one
.\scripts\smoke.ps1 -Exe publish\win-x64\anyfs.exe -SamplePluginDll tests\fixtures\SamplePlugin\bin\Release\net10.0\AnyFS.SamplePlugin.dll
```

The executable is self-contained, single file and trimmed; a documented set of framework
assemblies is kept whole so third-party plugins can rely on it (see
[docs/plugin-authoring.md](docs/plugin-authoring.md)).

## Plugins

Drop a DLL into the `plugins` folder beside `settings`. A plugin references `AnyFS.Sdk` and
implements `ISourcePlugin` or `IMountProviderPlugin`; its settings are a POCO with `[Setting]`
attributes and the host builds the form, the JSON and the secret handling.
[tests/fixtures/SamplePlugin](tests/fixtures/SamplePlugin) is the 60-line example.

## Layout

```
src/AnyFS.Sdk            interfaces, DTOs, SourceBase, caching helpers
src/AnyFS.Sdk.Testing    contract tests and fakes for plugin authors
src/AnyFS.Core           model, plugin host, router, read-only layers, status, settings, secrets, stats, logging
src/AnyFS.App            anyfs: command line (and, from M2, the window)
src/Sources/Mirror       the Mirror source
src/Mounts/Http          the HTTP mount
tests/                   unit tests per assembly, fixtures/MakeTree (deterministic test tree), fixtures/SamplePlugin
scripts/                 publish.ps1, smoke.ps1
docs/                    settings-format.md, plugin-authoring.md, mounts.md
```

MIT licence.
