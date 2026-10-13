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
| cache (spool files) | `<exe dir>/cache/` | `%LOCALAPPDATA%\AnyFS\cache`, `~/.cache/anyfs`, `~/Library/Caches/AnyFS` |
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
  filled back in at load; leave them out here.

### Mirror (`mirror`)

| Property | Meaning |
|---|---|
| `paths` | One path per line: UNC paths and drive paths on Windows (`\\server\share`, `C:\`, `D:\Photos`), any folder on Linux/macOS (`/mnt/nas`). Forward slashes are fine. Each path appears under its own chain: `\\h\s\x` as `h/s/x`, `C:\a` as `c/a`, `/mnt/nas` as `mnt/nas`. |
| `checkIntervalSeconds` | How often unreachable paths are retried (default 60). |

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
| `driveLetter` | Optional, D to Z: also map a drive letter for programs that can't open UNC paths. Empty (the default) means `\\anyfs\<collection>` only. |

### SMB mount (`smb`)

| Property | Meaning |
|---|---|
| `bindAddress` | As for HTTP. |
| `port` | Default 1445. Every collection mounted on the same address and port shares one server process and one login. |
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
