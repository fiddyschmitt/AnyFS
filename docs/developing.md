# Developing AnyFS

How to build, run, test, publish and release AnyFS from a clone. The [README](../README.md) is for
users; this page is for anyone changing the code.

## Prerequisites

- The .NET SDK named in [global.json](../global.json) (any later feature band of the same major
  also works).
- PowerShell for the scripts: Windows PowerShell 5.1 or pwsh 7 both run them.
- Two downloads the build embeds, fetched once into gitignored folders:

```
.\scripts\fetch-go-servers.ps1        # the pinned portable-smb-server binaries into tools/go-servers/<rid>/
.\scripts\fetch-winfsp.ps1            # the pinned WinFsp installer into tools/winfsp/ (Windows builds embed it)
```

Without them the solution still builds, but the SMB provider reports that its server is missing
and the **Install WinFsp driver** button falls back to winget. Every build embeds the two tools for
the runtime it targets: the publish RID, or the SDK's own RID for a plain `dotnet build`, so a
Debug run has them too. On Linux and macOS the fetch script sets the execute bit on the server.

## Build, test, run

```
dotnet build
dotnet test
dotnet run --project src/AnyFS.App -- --settings-dir ./scratch/settings
```

The last line opens the window against a scratch settings folder. The first start creates
"Collection 1" with the native mount provider for the OS (WinFsp on Windows, FUSE on Linux, SMB on
macOS). **Add ▾ › New source...** under Sources adds a Mirror of a folder; on Windows with WinFsp
installed the collection is at `\\anyfs\Collection 1` at once and **Open containing folder** opens
it in Explorer. **Add** under Mounts adds the others: an HTTP mount, say, serves
<http://localhost:11001/Collection%201/> to a browser.

The build treats warnings as errors, and the App project is trimmed, so a trim warning from a new
dependency fails the publish rather than the build. Test projects turn trimming off.

### Headless

The same settings files drive `anyfs serve`. Their format, with a worked example of a folder
served over HTTP, is in [settings-format.md](settings-format.md).

```
anyfs                                  the window
anyfs serve [--collection NAME]...     run all (or the named) enabled collections until Ctrl+C
anyfs status                           collections, sources, mounts, states, access paths; exit 0 when all green
anyfs plugin check <dll>               whether a third-party plugin loads in the trimmed host
anyfs --settings-dir DIR ...           use another settings folder (window or command)
anyfs --log-level LEVEL ...            trace, debug, info, warn, error
anyfs --version
```

Settings live beside the executable when that folder is writable, else in the user's config folder
(`%APPDATA%\AnyFS`, `~/.config/anyfs`, `~/Library/Application Support/AnyFS`). Secrets never go
into `sources.json`; they are protected in `state/secrets.json`.

On Windows `anyfs.exe` is a GUI program, so a console window never flashes up when the window
starts. With arguments it attaches to the console it was started from and prints there; because the
shell does not wait for GUI programs, `anyfs status` returns the prompt at once unless its output
goes through a pipeline (`anyfs status | more`, `$s = anyfs status | Out-String`; a plain
`$s = anyfs status` does not wait), and `anyfs serve` keeps running in the background of that
console until Ctrl+C. Scripts that need the exit code use a pipeline (`$LASTEXITCODE` is set
afterwards) or `Start-Process -Wait`.

### Unit tests

One test project per assembly under `tests/`, MSTest throughout. The GUI tests run the window and
every dialog on headless Avalonia. A few tests need something from outside and mark themselves
inconclusive or skipped without it: the live SSHFS tests want `ANYFS_TEST_SSH_HOST`, `_PORT`,
`_USER`, `_KEY` and `_ROOT` (plus `_PASSWORD_USER` and `_PASSWORD` for the password paths), the
real-server SMB test wants the fetched binary (or `ANYFS_SMB_SERVER` pointing at one), and the
WinFsp tests want the driver installed.

## Publish

```
.\scripts\publish.ps1                 # every RID into publish/<rid>/
.\scripts\publish.ps1 -Rid win-x64    # one
.\scripts\smoke.ps1 -Exe publish\win-x64\anyfs.exe -SamplePluginDll tests\fixtures\SamplePlugin\bin\Release\net10.0\AnyFS.SamplePlugin.dll
```

The executable is self-contained, single file and trimmed. A documented set of framework
assemblies is kept whole so third-party plugins can rely on it; [plugin-authoring.md](plugin-authoring.md)
lists it, and a test ties the list to the project file. The smoke script starts the published
executable against a generated tree and checks the HTTP, WebDAV, FTP and SMB mounts (and WinFsp
where the driver is installed), the sample plugin in the trimmed host, `status` and the log; run
it after any change to publishing or trimming.

The SMB provider's server is a separate Go program, [portable-smb-server](https://github.com/fiddyschmitt/portable-smb-server).
Its release tag and the SHA-256 of each binary are pinned in `scripts/go-servers.json`; bumping
the server means a new tag there, new hashes, and a fetch. The WinFsp installer pinned in
`scripts/winfsp.json` and the `winfsp.net` package must share a major.minor version, or the .NET
layer refuses the driver it installs; the App project checks this at build time.

## Continuous integration and release

[build.yml](../.github/workflows/build.yml) runs on every push to `main` and on pull requests, on
Windows, Linux and macOS: restore, build with warnings as errors, unit tests, the trimmed publish
for that runner, the smoke test, and the executable as an artifact.

A tag `v<version>` runs [release.yml](../.github/workflows/release.yml): the five runtimes are
published (trimmed; smoke-tested where the runner can run them), packaged as
`anyfs-<version>-<rid>.zip` (Windows) or `.tar.gz` (Linux and macOS, which keeps the executable
bit) with `SHA256SUMS.txt`, the SDK packages are packed (`scripts/pack.ps1`), and a GitHub release
is created with all of it. The packages also go to NuGet.org when the `NUGET_API_KEY` secret is
set. Nothing runs on commit: there are no git hooks.

## Test lab

[provisioning/](../provisioning/README.md) defines the end-to-end lab as code: `vagrant up` for the
Debian upstream, the Debian host and the Windows host, `.\mac.ps1 up` for the macOS VM on a Mac,
`.\publish.ps1` to build what the tests run, `.\check.ps1` to see it is all ready, then
`dotnet test tests/AnyFS.E2E` from the development PC. The E2E suite deploys the published anyfs to
every host and exercises every mount provider from the host itself and from each other machine
against a generated fixture; a machine that is not reachable turns its rows inconclusive rather
than red. `ANYFS_E2E_MOUNTS=Smb` (or `ANYFS_E2E_CLIENTS=local,macos`) before `dotnet test` runs
only those rows. Note that `dotnet test AnyFS.slnx` includes this project: harmless while the lab
is down (its rows go inconclusive in seconds), a two-hour run while it is up.

## Layout

```
src/AnyFS.Sdk            interfaces, DTOs, SourceBase, caching helpers (the NuGet package)
src/AnyFS.Sdk.Testing    contract tests and fakes for plugin authors (and our own tests)
src/AnyFS.Core           model, plugin host, router, read-only layers, status, settings, secrets, stats, logging
src/AnyFS.App            anyfs: the window (Gui/) and the command line (Cli/)
src/Sources/Mirror       the Mirror source
src/Sources/Sshfs        the SSHFS source over SSH.NET
src/Mounts/WinFsp        the WinFsp mount (Windows)
src/Mounts/Smb           the SMB mount: the VFS endpoint and the portable-smb-server process
src/Mounts/Fuse          the FUSE mount (Linux)
src/Mounts/WebDav        the WebDAV server and mount
src/Mounts/Ftp           the FTP mount: AnyFS's own FTP server
src/Mounts/Http          the HTTP mount
tests/                   unit tests per assembly, GUI tests (headless Avalonia), AnyFS.E2E, fixtures/MakeTree, fixtures/SamplePlugin
scripts/                 publish.ps1, smoke.ps1, pack.ps1, fetch-go-servers.ps1 (+ go-servers.json), fetch-winfsp.ps1 (+ winfsp.json)
provisioning/            the test lab: settings.yml, Vagrantfile, ansible/, windows/, macos/, mac.ps1, check.ps1, publish.ps1
tools/go-servers/        the fetched Go server binaries per RID (not in git)
tools/winfsp/            the fetched WinFsp installer (not in git)
docs/                    settings-format.md, plugin-authoring.md, mounts.md, this page
```

Plugin authoring (the contract, the settings attributes, the guaranteed framework set and
`anyfs plugin check`) is in [plugin-authoring.md](plugin-authoring.md); how each mount is reached
from each OS without mapping anything is in [mounts.md](mounts.md).
