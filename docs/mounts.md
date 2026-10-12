# Connecting to a mount

AnyFS never maps a drive letter or creates a persistent connection unless you opt in; every
mount provider is reached through the OS's own path form. This page grows with each provider
(WinFsp, SMB, FUSE, NFS, WebDAV, FTP arrive in milestones 3 and 7).

## HTTP

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
curl -O -C - http://localhost:11001/ben/files/aus-files-01/shared/big.iso
wget -c http://localhost:11001/ben/files/aus-files-01/shared/big.iso
```
