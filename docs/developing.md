# Developing AnyFS

How to build, run, test, publish and release AnyFS from a clone. The [README](../README.md) is for
users; this page is for anyone changing the code.

## Prerequisites

- The .NET SDK named in [global.json](../global.json) (any later feature band of the same major
  also works).
- PowerShell for the scripts: Windows PowerShell 5.1 or pwsh 7 both run them.
- One download the Windows build embeds, fetched once into a gitignored folder:

```
.\scripts\fetch-winfsp.ps1            # the pinned WinFsp installer into tools/winfsp/
```

Without it the solution still builds, but the **Install WinFsp driver** button falls back to
winget. A build embeds the installer when the runtime it targets is Windows: the publish RID, or
the SDK's own RID for a plain `dotnet build`, so a Debug run has it too.

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
SMB mount's test through Windows' own client wants Windows 11 24H2 (for `net use /TCPPORT`), and
the WinFsp tests want the driver installed. LibSmb's tests talk to the server over loopback TCP with
an SMB2 client of their own (`tests/LibSmb.Tests/SmbTestClient.cs`: NTLMv2, signing, compounds),
which the SMB mount's tests share. The 3DX source's tests run against a small fake 3DEXPERIENCE
platform on 127.0.0.1 (`tests/AnyFS.Sources.Tests/Fake3DX.cs`: 3DPassport, 3DSpace, 3DSearch, a file
server and an identity provider), so they need nothing outside; the Windows sign-ins among them run
on Windows only. Four of them open more: `ANYFS_TEST_3DX_BROWSER=1` lets the web session's browser
tests start Edge and Firefox headless (over DevTools, and through WebDriver with each, an identity
provider on a site of its own and one that shows a dialog among them), `ANYFS_TEST_3DX_EXE` pointing at
a published `anyfs.exe` serves a 3DX source of the fake through that build's HTTP mount,
`ANYFS_TEST_3DX_SCALE=1` builds, saves and reads back a tree of 200,000 documents and prints the
times and memory to the test's output, and `ANYFS_TEST_3DX_WINDOW` pointing at a published
`anyfs.exe` opens its window over a 3DX source listing the fake and prints how long the window took
to answer while it listed (`ANYFS_TEST_3DX_WINDOW_DOCS`, 50,000 documents; `_DELAY_MS`, each listing
call's time, 150; `_CRAWL=1` walks the drive after; `_CPU=1` measures the window's CPU instead). The
GUI tests' `ANYFS_TEST_UI_LOAD=1` does the same on headless Avalonia with a listing of its own, and with
sources keeping their sign-ins in the secret store while something holds secrets.json; and
`ANYFS_TEST_UI_SHAREPOINT` (a SharePoint Server site, with `ANYFS_TEST_SPS_USER` and `_PASSWORD`) adds
three sources of it to the window and measures it as they start.

The SharePoint source's suites run against the lab's farms and the Microsoft 365 developer sandbox when
`ANYFS_TEST_SPS_*`, `_SPO_*` and `_SPE_*` name them ([provisioning/sharepoint](../provisioning/sharepoint/README.md)),
and otherwise against fakes made from what those were seen to answer (`tests/AnyFS.Sources.Tests/FakeSharePoint/`):
a farm's REST API on 127.0.0.1 (Basic, or Windows' own sign-in on Windows) and a tenant's REST API, Graph and
SharePoint Embedded in-process, so they need nothing outside either. `FakeSharePointTests` has what only a
fake does on demand (throttling, a lapsed session or token, a server that ignores ranges, an answer that is not
SharePoint's); `ANYFS_TEST_SHAREPOINT_FAKES=0` turns the fakes off, and the suites are then inconclusive without
the lab.

The GitLab source's unit tests run against `tests/AnyFS.Sources.Tests/FakeGitLab*.cs`, a stand-in made from what the
lab's GitLab answered (REST v4 and GraphQL, paging, Git LFS, wikis, issues, merge requests, pipelines with zipped
artifacts and ranges, releases, browser sessions, and the sign-in page with its Kerberos, LDAP and password forms),
in-process, or on 127.0.0.1 with Windows' own sign-in for the Negotiate page (Windows only). `GitLabLiveTests` read the
lab's GitLab ([provisioning/gitlab](../provisioning/gitlab/README.md)) when `ANYFS_TEST_GITLAB_URL` (the server, by name
or address) and `ANYFS_TEST_GITLAB_TOKEN` (the tester's token) are set; `ANYFS_TEST_GITLAB_USER` (`sp_user@anyfs.lab`) and
`_PASSWORD` add the Windows account's sign-in (LDAP, where Kerberos cannot get through), and `ANYFS_TEST_GITLAB_BROWSER=1`
the web session with Edge (a profile of its own, signed in through GitLab's LDAP form). AnyFS.E2E's `gitlab` rows serve
the seeded project's `files/e2e` from each host (a token by address on Linux and macOS, the tester's domain account by name,
through Kerberos, on Windows; `ANYFS_E2E_GITLAB_SIGNIN=token` uses the token everywhere).

The Teams source's unit tests run against `tests/AnyFS.Sources.Tests/FakeTeams.cs`, a stand-in for Microsoft's Teams
services made from what the Microsoft 365 developer sandbox answered (the aggregator's teams, channels and chats; the chat
service's messages with their reactions, edits, deletions, shared files and inline images; a team's SharePoint library and
the media service), in-process. It can hold back SharePoint's token as the page at work often does (`SharePointReady`,
and `SharePointComesWhenAsked` with `SeekHold` for one that comes when asked for behind the listings), and refuses its
file calls without it or Graph's (`GraphReady`) as the live seam does; with Graph's alone it gives the group sites
without their groups, as Microsoft Search does. `TeamsRestTests` read the services' answers through a fake handler, and
`TeamsFilesTests` check where its tokens may go. `TeamsLiveTests` read the sandbox when `ANYFS_TEST_TEAMS_ACCOUNT` (the tester,
`anyfs-tester@<tenant>.onmicrosoft.com`) and `ANYFS_TEAMS_PROFILES` (the root of the browser profiles signed in as it, such as
`%LOCALAPPDATA%\AnyFS\tests\outlook-browser`: Teams signs in from the same Microsoft 365 session) are set;
`ANYFS_TEAMS_BROWSER` names the browser (a Firefox takes the WebDriver route that a work machine's Edge policy can make the
only one). `TheLibrariesAreReadThroughGraphWithoutSharePointsToken` reads the libraries through Microsoft Graph alone, as
at work: the sandbox's Teams page mints no Graph token, so it takes the AnyFS app's from a `tests/fixtures/SharePointSignIn`
sign-in as the tester (`ANYFS_TEST_GRAPH_CACHE`, else the fixture's default file) and withholds SharePoint's. `TeamsSandboxSeed` seeds the sandbox for them, once, with `ANYFS_SEED_TEAMS=1` beside those two: the team "AnyFS
Lab" with its SharePoint site, a thread in General with replies, reactions, an edit, a deletion, a shared file and an inline
image, files in the channel's folder, and the same in the tester's first chat; a run skips what it finds seeded. Teams is
cloud only, so there is no lab machine or E2E row for it.

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

The SMB provider's server is [LibSmb](../src/LibSmb/README.md), AnyFS's own SMB 2 and 3 server,
which runs in the anyfs process; its README lists what it implements and what it leaves out, and
`tests/LibSmb.Tests` drives it over the wire with a client of its own, and `tests/LibSmb.E2E` with
the lab's Windows, Linux and macOS clients (below). The WinFsp installer pinned in
`scripts/winfsp.json` and the `winfsp.net` package must share a major.minor version, or the .NET
layer refuses the driver it installs; the App project checks this at build time.

## Continuous integration and release

[build.yml](../.github/workflows/build.yml) runs on every push to `main` and on pull requests, on
Windows, Linux and macOS: restore, build with warnings as errors, unit tests, the trimmed publish
for that runner, the smoke test, and the executable as an artifact.

A tag `v<version>` runs [release.yml](../.github/workflows/release.yml): the five runtimes are
published (trimmed; smoke-tested where the runner can run them) and attached to a GitHub release
as bare executables named by runtime (`anyfs-win-x64.exe`, `anyfs-linux-x64`, `anyfs-linux-arm64`,
`anyfs-osx-arm64`, `anyfs-osx-x64`; a download has no execute bit, so Linux and macOS users
`chmod +x` it), with `SHA256SUMS.txt` and the SDK packages (`scripts/pack.ps1`). The notes come
from `docs/release-notes/<tag>.md` when that file exists. The packages also go to NuGet.org when the `NUGET_API_KEY` secret is
set. Nothing runs on commit: there are no git hooks.

## Test lab

[provisioning/](../provisioning/README.md) defines the end-to-end lab as code: `vagrant up` for the
Debian upstream, the Debian host and the Windows host, `.\mac.ps1 up` for the macOS VM on a Mac,
`.\publish.ps1` to build what the tests run, `.\check.ps1` to see it is all ready, then
`dotnet test tests/AnyFS.E2E` from the development PC. The E2E suite deploys the published anyfs to
every host and exercises every mount provider from the host itself and from each other machine
against a generated fixture; a machine that is not reachable turns its rows inconclusive rather
than red. `ANYFS_E2E_MOUNTS=Smb` (or `ANYFS_E2E_CLIENTS=local,macos`) before `dotnet test` runs
only those rows. `dotnet test tests/LibSmb.E2E` is for after a change to LibSmb or the SMB mount: on
every host, the SMB conditions ported from portable-smb-server's rig from every client, every client
at once on one share, and a stress run with `SmbStress` (tests/fixtures/SmbStress), against an anyfs
that serves the conditions' own shares; `ANYFS_E2E_SMB_CONDITIONS` picks among them (the lab's
README lists them). The two share the lab's plumbing, `tests/AnyFS.TestLab`. Note that
`dotnet test AnyFS.slnx` includes both: harmless while the lab is down (their rows go inconclusive
in seconds), several hours each while it is up.

## Layout

```
src/AnyFS.Sdk            interfaces, DTOs, SourceBase, caching helpers (the NuGet package)
src/AnyFS.Sdk.Testing    contract tests and fakes for plugin authors (and our own tests)
src/AnyFS.Core           model, plugin host, router, read-only layers, status, settings, secrets, stats, logging
src/AnyFS.App            anyfs: the window (Gui/) and the command line (Cli/)
src/LibOutlook           a self-contained reader of Outlook's PST and OST files (no AnyFS reference; its own package)
src/LibSmb               a self-contained SMB 2 and 3 server over a file system you supply (no AnyFS reference)
src/Sources/Mirror       the Mirror source
src/Sources/Sshfs        the SSHFS source over SSH.NET
src/Sources/Common       what more than one source needs: the web session's sign-in in a browser of AnyFS's own over
                         DevTools (Selenium WebDriver where that is closed), Windows account credentials, small helpers
src/Sources/Microsoft365 what the Microsoft 365 sources share: the Microsoft clouds, the app sign-in (MSAL, as AnyFS's
                         registration or the organisation's own) and Microsoft Graph's calls at a tenant's pace
src/Sources/SharePoint   the SharePoint source: Graph after an MSAL sign-in, REST with a Windows account or a web
                         session read from a browser
src/Sources/ThreeDX      the 3DX source: a 3DEXPERIENCE platform's documents through 3DSearch and 3DSpace, read-only,
                         signed in through 3DPassport (single sign-on, a browser, or a Windows account)
src/Sources/Outlook      the Outlook source: a mailbox as folders, from a PST or OST file (LibOutlook), Exchange Online
                         through Outlook on the web's session, or an Exchange server over EWS with a Windows account
src/Sources/Teams        the Teams source: an account's teams, channels, chats and their files, read-only, over Microsoft's
                         own Teams services with the tokens the Teams web client mints in a browser of AnyFS's own
src/Mounts/WinFsp        the WinFsp mount (Windows)
src/Mounts/Smb           the SMB mount: LibSmb serving each collection as a share, one server per address and port
src/Mounts/Fuse          the FUSE mount (Linux)
src/Mounts/WebDav        the WebDAV server and mount
src/Mounts/Ftp           the FTP mount: AnyFS's own FTP server
src/Mounts/Http          the HTTP mount
tests/                   unit tests per assembly, GUI tests (headless Avalonia), the lab's AnyFS.E2E and LibSmb.E2E
                         (over AnyFS.TestLab), fixtures/MakeTree, fixtures/SamplePlugin, fixtures/SharePointSignIn (the
                         live SharePoint tests' sign-in), fixtures/SmbStress (LibSmb.E2E's load)
scripts/                 publish.ps1, smoke.ps1, pack.ps1, fetch-winfsp.ps1 (+ winfsp.json)
provisioning/            the test lab: settings.yml, Vagrantfile, ansible/, windows/, macos/, mac.ps1, check.ps1, publish.ps1
tools/winfsp/            the fetched WinFsp installer (not in git)
docs/                    settings-format.md, plugin-authoring.md, mounts.md, this page
```

Plugin authoring (the contract, the settings attributes, the guaranteed framework set and
`anyfs plugin check`) is in [plugin-authoring.md](plugin-authoring.md); how each mount is reached
from each OS without mapping anything is in [mounts.md](mounts.md).
