# Settings files

AnyFS keeps three JSON files under `settings/`, plus state beside it. Everything is UTF-8,
indented, with stable property order and a `version` field (currently 1); files are written
atomically (temp file + rename). A file that fails to parse is moved aside as
`<name>.bad-<timestamp>` and AnyFS starts with defaults for it. Edits made by hand while AnyFS
runs are picked up on the next start.

## Where

| Item | Portable (exe folder writable) | Otherwise |
|---|---|---|
| settings | `<exe dir>/settings/` | `%APPDATA%\AnyFS\settings`, `~/.config/anyfs/settings`, `~/Library/Application Support/AnyFS/settings` |
| state (stats, status, keys, secrets, per-source state) | `<exe dir>/state/` | sibling of settings |
| cache (spool files, unpacked helper programs) | `<exe dir>/cache/` | `%LOCALAPPDATA%\AnyFS\cache`, `~/.cache/anyfs`, `~/Library/Caches/AnyFS` |
| plugins | `<exe dir>/plugins/` | sibling of settings |
| log | `<exe dir>/anyfs.log` | sibling of settings |

`--settings-dir DIR` makes `DIR` the settings folder; state, cache, plugins and the log become
its siblings.

## settings/app.json

```json
{
  "version": 1,
  "logLevel": "info",          // trace, debug, info, warn, error (--log-level overrides)
  "bindAddress": "127.0.0.1",  // default offered to new network mounts
  "theme": "system",           // system (follow the OS), light or dark; the window reads it at start
  "countFilesOnStart": null,   // default for new sources: null follows the source type (quick ones, like Mirror, count), true or false forces it
  "window": { "x": 0, "y": 0, "width": 0, "height": 0, "maximised": false }   // written by the window when it closes
}
```

## settings/sources.json

Every source, defined once, usable by any number of collections. This is the file people share;
secret properties never enter it.

```json
{
  "version": 1,
  "sources": [
    {
      "id": "11111111-1111-1111-1111-111111111111",
      "name": "aus-files-01",
      "type": "mirror",
      "enabled": true,
      "countFilesOnStart": false,
      "settings": { "paths": "\\\\aus-files-01\\shared\nD:/Photos", "checkIntervalSeconds": 60 }
    }
  ]
}
```

- `id`: a GUID, referenced from collections. Generated if missing.
- `type`: the plugin's type id (`mirror`, `sshfs`, or a third-party id).
- `settings`: the plugin's settings object (property names camelCase). Properties the plugin
  marks `Secret` are stored protected in `state/secrets.json` under `<id>/<PropertyName>` and
  filled back in at load, so anyfs never writes them here. A secret you (or a script) do write
  here is moved into the secret store on the next start and the file is rewritten without it; the
  same goes for a mount's secrets in `collections.json`.

### Mirror (`mirror`)

| Property | Meaning |
|---|---|
| `paths` | One path per line: UNC paths and drive paths on Windows (`\\server\share`, `C:\`, `D:\Photos`), any folder on Linux/macOS (`/mnt/nas`). Forward slashes are fine. Each path appears under its own chain: `\\h\s\x` as `h/s/x`, `C:\a` as `c/a`, `/mnt/nas` as `mnt/nas`. |
| `checkIntervalSeconds` | How often unreachable paths are retried (default 60). |

### SSHFS (`sshfs`)

| Property | Meaning |
|---|---|
| `host` | The server's name or address (no `user@`). |
| `port` | Default 22. |
| `username` | Who to log in as. |
| `root` | `/` (everything, the default), `~` or `~/sub` (the home folder), or an absolute path. The source's tree starts there. A Windows OpenSSH server puts its drives at `/`, so with the root there the source shows them as folders `c`, `d`... (a colon can't be in a Windows file name); `C:\Users` or `/C:/Users` work as a root too. |
| `password` | Secret: in `state/secrets.json`, never here. Optional: without it the key is used, or the server's own prompt reaches you. |
| `keyFile` | A private key file on this machine (OpenSSH, PEM or PuTTY). Optional. |
| `passphrase` | Secret, optional: the key's passphrase; left empty, you are asked when the key needs one. |
| `connections` | Advanced, default 4: SFTP throughput is per connection, open files are spread across them. |
| `connectTimeoutSeconds` | Advanced, default 20. |

Logging in tries the key, then the password, then keyboard-interactive, whose prompts are
answered with the saved password once and otherwise put to you (a two-factor code, a PIN, a
password that isn't stored); headless without a console that is "Needs input" and the source
waits for a Retry. The server's host key is trusted on first use and pinned per algorithm in
`state/sources/<id>/hostkeys.txt`; a changed key fails the source naming both fingerprints, and
deleting the line lets you accept the new one. Listings are cached for two seconds; a symbolic
link shows as what it points at; dot names are hidden; a file nobody may write is read-only.
"Count files on start" lists folders several at a time over the pool (four per connection) and
counts links as entries without following them.

### Sharing sources: Export... and Import...

The All sources tab's **Export...** writes the ticked sources to a file of exactly this shape
(`sources.json`), without secrets. **Import...** reads one back and merges by `id`: a new id is
added, a known id asks whether to replace yours, keep yours or import the file's as a copy
under a new id; names are kept unique with a `(2)` suffix. Imported sources have no secrets
until you edit them (or answer their prompt when they start).

## settings/collections.json

```json
{
  "version": 1,
  "collections": [
    {
      "id": "22222222-2222-2222-2222-222222222222",
      "name": "work",
      "enabled": true,
      "sources": [
        { "id": "11111111-1111-1111-1111-111111111111", "path": "files", "readOnly": false }
      ],
      "mounts": [
        { "id": "33333333-3333-3333-3333-333333333333", "name": "HTTP", "provider": "http",
          "enabled": true, "readOnly": false, "settings": { "bindAddress": "127.0.0.1", "port": 11001 } }
      ]
    }
  ]
}
```

- `sources[].path`: the *source path*, where the source's tree sits inside this collection,
  `/`-separated, any length, may be empty (the source is the collection's root). Default when
  created from the UI: `<type folder>/<source name>` (`files/aus-files-01`). Segments must be
  valid names on every OS (no `\ / : * ? " < > |`, no leading/trailing dots or spaces, 255 bytes).
  Two sources may share a path; their listings merge and duplicate names are hidden (first by
  source name wins, the other is marked Degraded).
- `sources[].readOnly`: this collection presents the source read-only. The same source can be
  writable in another collection.
- `mounts[].readOnly`: everything through this mount is read-only.
- A write succeeds only if the plugin can write, the membership isn't read-only and the mount
  isn't read-only.

### HTTP mount (`http`)

| Property | Meaning |
|---|---|
| `bindAddress` | `127.0.0.1` (this machine), `0.0.0.0` (the LAN), an IP, `localhost`, `*`. |
| `port` | Default 11001. Mounts on the same address and port share one server; each collection is at `http://host:port/<collection>/`. |

### WinFsp mount (`winfsp`, Windows)

| Property | Meaning |
|---|---|
| `driveLetter` | Optional, D to Z: also map a drive letter for programs that can't open UNC paths; Open then opens the letter. Empty (the default) means `\\anyfs\<collection>` only. |

### SMB mount (`smb`)

| Property | Meaning |
|---|---|
| `bindAddress` | As for HTTP. |
| `port` | Default 1445. Every collection mounted on the same address and port shares one server process and one login. |
| `driveLetter` | Windows only (the box appears nowhere else and the value is ignored there), optional, D to Z: `net use X: \\host\<collection> /TCPPORT:<port>` while the mount runs; Open opens the letter. Needs a user and password where Windows blocks guest logons. |
| `guest` | `true` for guest access (no password; Windows refuses it unless `EnableInsecureGuestLogons` is on). |
| `user` | The login (default `user`) when `guest` is false. |
| `password` | Secret: lives in `state/secrets.json`, never here. Required unless `guest`. |

### FUSE mount (`fuse`, Linux)

| Property | Meaning |
|---|---|
| `mountPoint` | The folder, created if missing; `~` is the home folder. Default `~/anyfs/<collection>`. |
| `allowOther` | `allow_other` (default true), honoured when `/etc/fuse.conf` has `user_allow_other`. |

### WebDAV mount (`webdav`)

| Property | Meaning |
|---|---|
| `bindAddress` | As for HTTP. |
| `port` | Default 11000; mounts on the same address and port share one server. |
| `user`, `password` | Optional Basic authentication (the password is a secret). |

### FTP mount (`ftp`)

| Property | Meaning |
|---|---|
| `bindAddress` | As for HTTP. |
| `port` | Default 2121; mounts on the same address and port share one server. |
| `allowAnonymous` | Default true: `anonymous` with any password sees this collection. |
| `user`, `password` | A named login that sees this collection (the password is a secret). |
| `passivePortFrom`, `passivePortTo` | Optional passive-mode port range, for firewalls. |
| `publicAddress` | Optional IP clients connect back to in passive mode (NAT). |

NFS arrives in milestone 7; a mount of a provider this build doesn't have shows as Failed and is
otherwise ignored.

## state/

- `secrets.json`: `{ "version": 1, "secrets": { "<owner id>/<Property>": "<protected>" } }`.
  Protected with ASP.NET Core Data Protection; keys in `state/keys/` (DPAPI-wrapped on Windows).
- `stats.json`: lifetime counters (`filesServed`, `bytesRead`, `bytesWritten`, per collection).
- `status.json`: written every 2 s by a running `anyfs serve` (pid, collections, sources,
  mounts, states, access paths, stats); `anyfs status` reads it and treats it as live when it is
  under 10 s old and the pid is alive. Removed on a clean stop.
- `sources/<id>/`, `mounts/<id>/`: per-plugin state.

## cache/

- `sources/<id>/`: per-source scratch space (spool files).
- `mount-providers/<provider>/`: files every mount of one provider shares. The SMB provider keeps
  its unpacked `portable-smb-server-<hash>` here, one per server version; copies of other
  versions are deleted when a new one lands.

Everything under `cache/` can be deleted while anyfs is stopped; it is made again on demand.
