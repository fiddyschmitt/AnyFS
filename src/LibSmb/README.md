# LibSmb

An SMB 2/3 file server in pure C#. It serves shares from a file system you implement (`ISmbFileSystem`), or from a
folder of the machine (`LocalFileSystem`), to the SMB clients built into Windows, macOS and Linux.

```
dotnet add package LibSmb
```

- Dialects 2.0.2, 2.1, 3.0 and 3.0.2 (a client that opens with an SMB1 negotiate is moved on to SMB2).
- Sign-in with NTLMv2 through SPNEGO (or bare NTLMSSP, as Linux's cifs sends it) for one account, or guests.
- Message signing: HMAC-SHA256 for 2.x, AES-CMAC for 3.x; signed requests are checked.
- Compounded requests, related operations included.
- A connection's requests run side by side. A client machine sends everything over one connection, so a slow file
  system call holds up only its own request, and small replies go out ahead of big READ data. One open's requests take
  turns. `MaxConcurrentRequests` (64 by default) bounds how many of a connection's requests the file system works on
  at once: a request waiting for its open's turn, or whose reply waits for the wire, counts for none.
- Every message is read as it comes. A request past that bound waits its turn in memory, an ECHO is answered at once,
  and a CANCEL reaches a request that waits as well as one that runs.
- A request still running, or waiting, after half a second (`InterimResponseDelay`) gets an interim STATUS_PENDING
  reply, so clients wait out a slow call instead of giving up on the connection. A client's CANCEL fires the call's
  cancellation token, and the request is answered STATUS_CANCELLED. A call that gives up by itself with an
  `OperationCanceledException` (a time-out) fails its command with STATUS_IO_TIMEOUT, also inside a compound chain,
  and the connection carries on.
- Apple's SMB2 extensions: macOS gets each directory entry's access, mode and Finder facts in the listing itself.
- A lookup by name is one stat, not a listing filtered.
- Read-only shares, and files that are read-only themselves.
- Shares can be added and removed while the server runs.
- Any port, so no administrator rights are needed (Windows 11 24H2 connects to another port with
  `net use \\host\share /TCPPORT:1445`; macOS with `smb://host:1445/share`; Linux with `mount -t cifs -o port=1445`).

```csharp
using System.Net;
using LibSmb;

await using var server = SmbServer.Start(
    new SmbServerOptions { EndPoint = new IPEndPoint(IPAddress.Any, 1445), User = "user", Password = "secret" },
    [new SmbShare("data", new LocalFileSystem(@"D:\Data"))]);
Console.ReadLine(); // it serves until disposed
```

Not implemented: SMB 3.1.1, encryption, large MTU (a READ or a WRITE carries at most 64 KiB, and clients send many
side by side), byte-range lock enforcement (locks are granted), oplocks and leases, change notifications, named pipes
(so no share list for `net view`), DFS, alternate data streams and security descriptors.

MIT licensed. The source is in [AnyFS](https://github.com/fiddyschmitt/AnyFS), which LibSmb was written for, under
`src/LibSmb`.
