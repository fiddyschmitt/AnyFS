# LibSmb

An SMB 2/3 file server in pure C#. It serves shares from a file system you implement (`ISmbFileSystem`), or from a
folder of the machine (`LocalFileSystem`), to the SMB clients built into Windows, macOS and Linux.

- Dialects 2.0.2, 2.1, 3.0 and 3.0.2 (a client that opens with an SMB1 negotiate is moved on to SMB2).
- Sign-in with NTLMv2 through SPNEGO (or bare NTLMSSP, as Linux's cifs sends it) for one account, or guests.
- Message signing: HMAC-SHA256 for 2.x, AES-CMAC for 3.x; signed requests are checked.
- Compounded requests, related operations included.
- Apple's SMB2 extensions: macOS gets each directory entry's access, mode and Finder facts in the listing itself.
- A lookup by name is one stat, not a listing filtered.
- Read-only shares, and files that are read-only themselves.
- Shares can be added and removed while the server runs.
- Any port, so no administrator rights are needed (Windows 11 24H2 connects to another port with
  `net use \\host\share /TCPPORT:1445`; macOS with `smb://host:1445/share`; Linux with `mount -t cifs -o port=1445`).

```csharp
await using var server = SmbServer.Start(
    new SmbServerOptions { EndPoint = new IPEndPoint(IPAddress.Any, 1445), User = "user", Password = "secret" },
    [new SmbShare("data", new LocalFileSystem(@"D:\Data"))]);
```

Not implemented: SMB 3.1.1, encryption, byte-range lock enforcement (locks are granted), oplocks and leases, change
notifications, named pipes (so no share list for `net view`), DFS, alternate data streams and security descriptors.
