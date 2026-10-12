# Changelog

## Unreleased (milestone 1: headless core)

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
