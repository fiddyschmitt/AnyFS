using System.Net;
using System.Net.Sockets;
using System.Text;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.Sshfs;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace AnyFS.Sources.Tests;

/// <summary>
/// Where a live SSH server for the tests is: ANYFS_TEST_SSH_HOST, _PORT (22), _USER (vagrant), _KEY (a private key
/// file), _ROOT (~/anyfs-fixture, the tree tests/fixtures/sshfs-fixture.sh makes), and for the password tests
/// _PASSWORD_USER and _PASSWORD. Without a host the live tests are inconclusive (spec 16.1: the protocol is covered
/// end to end in the lab).
/// </summary>
internal static class SshTestServer
{
    public static string? Host => Env("ANYFS_TEST_SSH_HOST");

    public static int Port => int.TryParse(Env("ANYFS_TEST_SSH_PORT"), out var port) ? port : 22;

    public static string User => Env("ANYFS_TEST_SSH_USER") ?? "vagrant";

    public static string? Key => Env("ANYFS_TEST_SSH_KEY");

    public static string Root => Env("ANYFS_TEST_SSH_ROOT") ?? "~/anyfs-fixture";

    public static string? PasswordUser => Env("ANYFS_TEST_SSH_PASSWORD_USER");

    public static string? Password => Env("ANYFS_TEST_SSH_PASSWORD");

    public static void Require()
    {
        if (Host is null)
        {
            Assert.Inconclusive("no SSH test server: set ANYFS_TEST_SSH_HOST (and _PORT, _USER, _KEY, _ROOT)");
        }
    }

    public static SshfsSettings Settings(string? root = null) => new() { Host = Host!, Port = Port, Username = User, KeyFile = Key, Root = root ?? Root };

    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

/// <summary>The SDK's contract run against the real server (readme.txt is "hello readme from sshd\n"; scratch/ is writable).</summary>
[TestClass]
public class SshfsContractTests : SourceContractTests
{
    protected override async Task<SourceContractSetup> SetUpAsync()
    {
        SshTestServer.Require();
        var source = new SshfsPlugin().CreateSource(SshTestServer.Settings());
        await source.InitialiseAsync(new TestSourceContext("sshfs"), CancellationToken.None);
        return new SourceContractSetup(source, "", "readme.txt", Encoding.UTF8.GetBytes("hello readme from sshd\n"), WritableDirectory: "scratch");
    }
}

[TestClass]
public class SshfsLiveTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<SshfsSource> StartAsync(SshfsSettings settings, TestSourceContext? context = null)
    {
        var source = (SshfsSource)new SshfsPlugin().CreateSource(settings);
        await source.InitialiseAsync(context ?? new TestSourceContext("sshfs"), None);
        return source;
    }

    [TestMethod]
    public async Task OnAServerAStatAndAnOpenInAFolderWhoseListingIsLongAskAboutTheOnePath()
    {
        SshTestServer.Require();
        using var context = new TestSourceContext("sshfs");
        var source = (SshfsSource)new SshfsPlugin().CreateSource(SshTestServer.Settings());

        // the fixture's root, a dozen entries, counts as long
        source.LongListing = 2;
        await source.InitialiseAsync(context, None);
        await using var stopped = source;
        var listed = new Dictionary<string, VfsEntry>(StringComparer.Ordinal);
        await foreach (var entry in source.ListAsync("", None))
        {
            listed[entry.Name] = entry;
        }

        // the listing's two seconds over
        await Task.Delay(2200);
        var asked = context.UpstreamRequests;
        var before = asked.Requests.Count;
        Assert.AreEqual(listed["medium.bin"], await source.StatAsync("medium.bin", None), "a file as its listing has it");
        Assert.AreEqual(listed["link-to-docs"], await source.StatAsync("link-to-docs", None), "a link by what it points at, as its listing has it");
        Assert.IsNull(await source.StatAsync("not-there.bin", None));
        await using (var file = await source.OpenAsync("medium.bin", VfsOpenMode.Read, None))
        {
            Assert.AreEqual(listed["medium.bin"].Length, file.Length);
            var bytes = new byte[16];
            Assert.AreEqual(16, await file.ReadAsync(0, bytes, None));
        }

        await Assert.ThrowsExactlyAsync<VfsException>(() => source.OpenAsync("not-there.bin", VfsOpenMode.Read, None));
        var since = asked.Requests.Skip(before).ToList();
        Assert.AreEqual(0, since.Count(r => r.What == "SFTP list"), "the folder was not listed again");
        Assert.AreEqual(5, since.Count(r => r.What == "SFTP stat"), string.Join(", ", since.Select(r => r.What)));

        // whoever lists it gets a listing, which answers while it is held
        before = asked.Requests.Count;
        var again = 0;
        await foreach (var _ in source.ListAsync("", None))
        {
            again++;
        }

        Assert.AreEqual(listed.Count, again);
        Assert.AreEqual(listed["medium.bin"], await source.StatAsync("medium.bin", None));
        since = asked.Requests.Skip(before).ToList();
        Assert.AreEqual(1, since.Count(r => r.What == "SFTP list"));
        Assert.AreEqual(0, since.Count(r => r.What == "SFTP stat" && r.Target.EndsWith("/medium.bin", StringComparison.Ordinal)), "answered from the listing held");
    }

    [TestMethod]
    public async Task WhatTheServerIsAskedIsARowForEachCall()
    {
        SshTestServer.Require();
        using var context = new TestSourceContext("sshfs");
        await using (var source = await StartAsync(SshTestServer.Settings(), context))
        {
            var asked = context.UpstreamRequests;
            Assert.AreEqual(("SSH connect", $"{SshTestServer.Host}:{SshTestServer.Port}"), (asked.Requests[0].What, asked.Requests[0].Target));
            Assert.AreEqual("SFTP stat", asked.Requests[1].What, "the root checked as it starts");

            var names = new List<string>();
            await foreach (var entry in source.ListAsync("", None))
            {
                names.Add(entry.Name);
            }

            var list = asked.Requests.First(r => r.What == "SFTP list");
            Assert.AreEqual(names.Count, list.Entries, "the dot entries are no entries");
            Assert.IsTrue(list.IsDone);
            Assert.IsTrue(asked.Requests.Any(r => r.What == "SFTP stat" && r.Target.EndsWith("/link-to-docs", StringComparison.Ordinal)), "a link is resolved by a call of its own");

            await using (var file = await source.OpenAsync("medium.bin", VfsOpenMode.Read, None))
            {
                var open = asked.Requests[^1];
                Assert.AreEqual("SFTP open", open.What);
                StringAssert.EndsWith(open.Target, "/medium.bin");
                Assert.IsFalse(open.IsDone, "an open file is one request until it is closed");
                var buffer = new byte[4096];
                Assert.AreEqual(4096, await file.ReadFullyAsync(0, buffer, None));
                Assert.AreEqual(4096, open.Bytes, "its bytes go by on it");
            }

            var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () =>
            {
                await foreach (var _ in source.ListAsync("no-such-folder", None))
                {
                }
            });
            Assert.AreEqual(VfsError.NotFound, ex.Error);
            var refused = asked.Requests.Last(r => r.What == "SFTP list");
            StringAssert.EndsWith(refused.Target, "/no-such-folder");
            Assert.IsNotNull(refused.Error, "what the server refused says so");
            Assert.IsTrue(refused.IsDone);

            Assert.IsNotNull(await source.GetSpaceAsync("", None));
            Assert.IsTrue(asked.Requests.Any(r => r.What == "SFTP statvfs"));
        }

        await UpstreamRequestRows.AllEndAsync(context);
    }

    [TestMethod]
    public async Task LinksHiddenNamesAndReadOnlyFilesShowAsTheyShould()
    {
        SshTestServer.Require();
        await using var source = await StartAsync(SshTestServer.Settings());
        var entries = new Dictionary<string, VfsEntry>(StringComparer.Ordinal);
        await foreach (var entry in source.ListAsync("", None))
        {
            entries[entry.Name] = entry;
        }

        Assert.IsFalse(entries.ContainsKey(".") || entries.ContainsKey(".."), "dot entries are skipped");
        Assert.IsTrue(entries["link-to-docs"].IsDirectory, "a link to a folder is a folder");
        Assert.IsFalse(entries["dangling"].IsDirectory, "a dangling link is an empty file");
        Assert.AreEqual(0, entries["dangling"].Length);
        Assert.IsTrue(entries[".hidden"].Attributes.HasFlag(VfsAttributes.Hidden));
        Assert.IsFalse(entries["readme.txt"].Attributes.HasFlag(VfsAttributes.Hidden));
        Assert.IsFalse(entries["readme.txt"].Attributes.HasFlag(VfsAttributes.ReadOnly));
        Assert.IsNotNull(entries["readme.txt"].Modified);
        Assert.AreEqual(50_000, entries["medium.bin"].Length);

        var a = await source.StatAsync("docs/a.txt", None);
        Assert.IsNotNull(a);
        Assert.IsTrue(a.Attributes.HasFlag(VfsAttributes.ReadOnly), "chmod 444 shows as read-only");

        var viaLink = new List<string>();
        await foreach (var entry in source.ListAsync("link-to-docs", None))
        {
            viaLink.Add(entry.Name);
        }

        CollectionAssert.Contains(viaLink, "a.txt");
    }

    [TestMethod]
    public async Task RangedAndParallelReadsAgreeWithTheWholeFile()
    {
        SshTestServer.Require();
        var settings = SshTestServer.Settings();
        settings.Connections = 2;
        await using var source = await StartAsync(settings);

        byte[] whole;
        await using (var file = await source.OpenAsync("medium.bin", VfsOpenMode.Read, None))
        {
            Assert.AreEqual(50_000, file.Length);
            whole = new byte[file.Length];
            Assert.AreEqual(whole.Length, await file.ReadFullyAsync(0, whole, None));
        }

        await using (var file = await source.OpenAsync("medium.bin", VfsOpenMode.Read, None))
        {
            var part = new byte[1000];
            Assert.AreEqual(1000, await file.ReadFullyAsync(12_345, part, None));
            CollectionAssert.AreEqual(whole[12_345..13_345], part);
            Assert.AreEqual(0, await file.ReadAsync(60_000, new byte[10], None), "past the end reads nothing");
        }

        var reads = Enumerable.Range(0, 8).Select(async i =>
        {
            await using var file = await source.OpenAsync("medium.bin", VfsOpenMode.Read, None);
            var buffer = new byte[7_000];
            var n = await file.ReadFullyAsync(i * 5_000, buffer, None);
            return (Index: i, Data: buffer[..n]);
        }).ToList();
        foreach (var (index, data) in await Task.WhenAll(reads))
        {
            var start = index * 5_000;
            CollectionAssert.AreEqual(whole[start..Math.Min(start + 7_000, whole.Length)], data, $"handle {index}");
        }
    }

    [TestMethod]
    public async Task ListingAFileIsNotADirectoryAndTheHomeRootResolves()
    {
        SshTestServer.Require();
        await using var source = await StartAsync(SshTestServer.Settings("~/anyfs-fixture"));
        StringAssert.StartsWith(source.RemoteRoot, "/", "~ resolved to the home folder");
        StringAssert.EndsWith(source.RemoteRoot, "/anyfs-fixture");
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () =>
        {
            await foreach (var _ in source.ListAsync("readme.txt", None))
            {
            }
        });
        Assert.AreEqual(VfsError.NotADirectory, ex.Error);
        Assert.IsNull(await source.StatAsync("docs/nothing-here", None));
    }

    [TestMethod]
    public async Task CountingListsFoldersInParallelAndDoesNotFollowLinks()
    {
        SshTestServer.Require();
        await using var source = await StartAsync(SshTestServer.Settings());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await source.CountFilesAsync(None);
        Console.WriteLine($"counted {source.Counters.FilesKnown} files in {source.Counters.DirectoriesKnown} folders in {sw.ElapsedMilliseconds} ms");

        // readme.txt, medium.bin, .hidden, docs/a.txt, plus the two links counted as entries and not followed
        Assert.AreEqual(6, source.Counters.FilesKnown);
        Assert.AreEqual(3, source.Counters.DirectoriesKnown, "docs, emptydir, scratch (contract leftovers aside)");

        var root = SshTestServer.Root;
        if (Environment.GetEnvironmentVariable("ANYFS_TEST_SSH_BIG_ROOT") is { Length: > 0 } big)
        {
            // an optional bigger tree (tests/fixtures/sshfs-fixture.sh makes none): the parallel count against the
            // host's generic one-folder-at-a-time walk (what SourceRunner.WalkAsync does)
            await using var bigSource = await StartAsync(SshTestServer.Settings(big));
            sw.Restart();
            await bigSource.CountFilesAsync(None);
            var parallelMs = sw.ElapsedMilliseconds;
            Assert.IsTrue(bigSource.Counters.FilesKnown > 0);

            sw.Restart();
            long files = 0, folders = 0;
            var queue = new Queue<string>();
            queue.Enqueue("");
            while (queue.Count > 0)
            {
                var dir = queue.Dequeue();
                await foreach (var entry in bigSource.ListAsync(dir, None))
                {
                    if (entry.IsDirectory)
                    {
                        folders++;
                        queue.Enqueue(VfsPath.Join(dir, entry.Name));
                    }
                    else
                    {
                        files++;
                    }
                }
            }

            var timing = $"big tree {big}: parallel count {bigSource.Counters.FilesKnown} files / {bigSource.Counters.DirectoriesKnown} folders in {parallelMs} ms; sequential walk {files} files / {folders} folders in {sw.ElapsedMilliseconds} ms";
            Console.WriteLine(timing);
            if (Environment.GetEnvironmentVariable("ANYFS_TEST_SSH_TIMING_FILE") is { Length: > 0 } timingFile)
            {
                File.AppendAllText(timingFile, timing + Environment.NewLine);
            }

            Assert.AreEqual(files, bigSource.Counters.FilesKnown, "both ways agree on the files");
        }
    }

    /// <summary>A Windows OpenSSH server shows its drives at "/" (ANYFS_TEST_SSH_WIN_HOST, _WIN_USER, _WIN_PASSWORD).</summary>
    [TestMethod]
    public async Task AWindowsServerShowsItsDrivesAsLetterFolders()
    {
        var host = Environment.GetEnvironmentVariable("ANYFS_TEST_SSH_WIN_HOST");
        var user = Environment.GetEnvironmentVariable("ANYFS_TEST_SSH_WIN_USER");
        var password = Environment.GetEnvironmentVariable("ANYFS_TEST_SSH_WIN_PASSWORD");
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
        {
            Assert.Inconclusive("set ANYFS_TEST_SSH_WIN_HOST, _WIN_USER and _WIN_PASSWORD to a Windows OpenSSH server");
            return;
        }

        await using var source = await StartAsync(new SshfsSettings { Host = host, Username = user, Password = password, Root = "/" });
        StringAssert.StartsWith(source.RemoteRoot, "/");
        var top = new List<VfsEntry>();
        await foreach (var entry in source.ListAsync("", None))
        {
            top.Add(entry);
        }

        Assert.IsTrue(top.Count > 0, "the drives are listed");
        Assert.IsTrue(top.All(e => e.IsDirectory && e.Name.Length == 1 && char.IsAsciiLetterLower(e.Name[0])), string.Join(", ", top.Select(e => e.Name)));
        var drive = top[0].Name;
        Assert.IsNotNull(await source.StatAsync(drive, None));
        var users = new List<string>();
        await foreach (var entry in source.ListAsync(drive + "/Users", None))
        {
            users.Add(entry.Name);
        }

        Assert.IsTrue(users.Count > 0, "a folder under the drive lists");
        Assert.IsNull(await source.StatAsync("q", None), "a drive that isn't there is simply absent");

        // the Windows spelling of a root works too
        await using var spelled = await StartAsync(new SshfsSettings { Host = host, Username = user, Password = password, Root = drive.ToUpperInvariant() + @":\Users" });
        Assert.AreEqual("/" + drive.ToUpperInvariant() + ":/Users", spelled.RemoteRoot);
        Assert.IsTrue(await spelled.ListAsync("", None).AnyAsync());
    }

    [TestMethod]
    [DataRow(5)]
    [DataRow(100_000)]
    public async Task ATimeSetOnAFileStillOpenIsItsTimeAfterItsClose(int length)
    {
        // as a mount copies a file in: its bytes, its time while it is still open, its close. The last of the bytes went
        // to the server at the close, after the time, and the file had the time of its copying (27-Nov-2026)
        SshTestServer.Require();
        await using var source = await StartAsync(SshTestServer.Settings());
        var path = $"scratch/timed-{Guid.NewGuid():N}.bin";
        var modified = new DateTimeOffset(2026, 11, 2, 3, 4, 5, TimeSpan.Zero);
        try
        {
            await using (var file = await source.OpenAsync(path, VfsOpenMode.CreateNew, None))
            {
                var bytes = new byte[length];
                for (var at = 0; at < length; at += 65536)
                {
                    await file.WriteAsync(at, bytes.AsMemory(at, Math.Min(65536, length - at)), None);
                }

                await source.SetTimesAsync(path, null, modified, None);
            }

            var mine = await source.StatAsync(path, None);
            Assert.AreEqual(modified, mine?.Modified, "by the source that wrote it");
            Assert.AreEqual(length, mine?.Length);

            // and by the server's own word, to a source that has listed nothing yet
            await using var other = await StartAsync(SshTestServer.Settings());
            var theirs = await other.StatAsync(path, None);
            Assert.AreEqual(modified, theirs?.Modified, "by the server");
            Assert.AreEqual(length, theirs?.Length);
        }
        finally
        {
            await source.DeleteAsync(path, None);
        }
    }

    [TestMethod]
    public async Task AMissingRootFailsPermanentlyWithTheReason()
    {
        SshTestServer.Require();
        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => StartAsync(SshTestServer.Settings("/definitely/not/here")));
        Assert.IsTrue(ex.Permanent);
        StringAssert.Contains(ex.Message, "does not exist");
    }

    [TestMethod]
    public async Task AChangedHostKeyFailsTheSourceNamingBothFingerprints()
    {
        SshTestServer.Require();
        using var context = new TestSourceContext("sshfs");
        await using (var first = await StartAsync(SshTestServer.Settings(), context))
        {
            Assert.IsNotNull(await first.StatAsync("readme.txt", None));
        }

        var file = Path.Combine(context.StateDirectory, "hostkeys.txt");
        var lines = File.ReadAllLines(file);
        Assert.IsTrue(lines.Length >= 1, "trust on first use stored the key");
        var parts = lines[0].Split(' ');
        File.WriteAllLines(file, [parts[0] + " SHA256:AAAAtampered"]);

        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => StartAsync(SshTestServer.Settings(), context));
        Assert.IsTrue(ex.Permanent);
        StringAssert.Contains(ex.Message, "changed");
        StringAssert.Contains(ex.Message, "AAAAtampered");
        StringAssert.Contains(ex.Message, parts[1][..Math.Min(parts[1].Length, 16)], "the key the server offered is named too");
    }

    [TestMethod]
    public async Task PasswordLoginsAndPromptsBehave()
    {
        SshTestServer.Require();
        if (SshTestServer.PasswordUser is null || SshTestServer.Password is null)
        {
            Assert.Inconclusive("set ANYFS_TEST_SSH_PASSWORD_USER and ANYFS_TEST_SSH_PASSWORD");
        }

        SshfsSettings Settings(string? password) => new() { Host = SshTestServer.Host!, Port = SshTestServer.Port, Username = SshTestServer.PasswordUser!, Password = password, Root = "~" };

        await using (var source = await StartAsync(Settings(SshTestServer.Password)))
        {
            Assert.IsNotNull(await source.StatAsync("anyfs-fixture/hello.txt", None), "a stored password logs in");
        }

        // no password stored: the server's keyboard-interactive prompt reaches the user, and the answer logs in
        using var asking = new TestSourceContext("sshfs");
        asking.PromptHandler = request => request.Kind == PromptKind.Password ? PromptResult.FromText(SshTestServer.Password!) : PromptResult.Cancelled;
        await using (var source = await StartAsync(Settings(null), asking))
        {
            Assert.IsTrue(asking.Prompts.Count >= 1, "the user was asked");
            Assert.AreEqual(PromptKind.Password, asking.Prompts[0].Kind);
            StringAssert.Contains(asking.Prompts[0].Title, SshTestServer.Host!);
            Assert.IsNotNull(await source.StatAsync("anyfs-fixture/hello.txt", None));
        }

        // the prompt declined (or headless without a console): Needs input, retried only on demand
        using var declining = new TestSourceContext("sshfs");
        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => StartAsync(Settings(null), declining));
        Assert.IsTrue(ex.Permanent);
        StringAssert.Contains(ex.Message, "Needs input");

        // a wrong stored password: refused outright, or the server asks again and nobody answers; permanent either way
        using var wrong = new TestSourceContext("sshfs");
        ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => StartAsync(Settings("not-the-password"), wrong));
        Assert.IsTrue(ex.Permanent, ex.Message);
    }
}

[TestClass]
public class SshfsUnitTests
{
    [TestMethod]
    public void RemoteRootForms()
    {
        Assert.AreEqual("/", Root("/"));
        Assert.AreEqual("~", Root(""));
        Assert.AreEqual("~", Root("~"));
        Assert.AreEqual("~", Root("~/"));
        Assert.AreEqual("~/data", Root("~/data/"));
        Assert.AreEqual("/srv/data", Root("/srv//data/"));
        Assert.AreEqual("/srv/x", Root(@"\srv\x"));
        Assert.IsFalse(RemotePath.TryParseRoot("relative/path", out _, out var reason));
        StringAssert.Contains(reason, "absolute");
        Assert.IsFalse(RemotePath.TryParseRoot("/a/../b", out _, out reason));
        StringAssert.Contains(reason, "..");

        Assert.AreEqual("/C:/Users", Root(@"C:\Users"), "a Windows server's own spelling");
        Assert.AreEqual("/D:", Root("d:/"));
        Assert.AreEqual("/C:/Users", Root("/C:/Users"));

        Assert.IsTrue(RemotePath.IsDriveHome("/C:/Users/alice"), "Windows OpenSSH home");
        Assert.IsFalse(RemotePath.IsDriveHome("/home/smith"));
        Assert.IsFalse(RemotePath.IsDriveHome("/"));
        Assert.AreEqual("c", RemotePath.DriveEntryName("C:"));
        Assert.IsNull(RemotePath.DriveEntryName("Users"));
        Assert.IsNull(RemotePath.DriveEntryName("C:x"));
        Assert.AreEqual("/", RemotePath.JoinDriveRoot(""));
        Assert.AreEqual("/C:", RemotePath.JoinDriveRoot("c"));
        Assert.AreEqual("/C:/Users/x", RemotePath.JoinDriveRoot("c/Users/x"));
        Assert.AreEqual("/D:/a", RemotePath.JoinDriveRoot("D/a"));
        Assert.AreEqual("/odd/name", RemotePath.JoinDriveRoot("odd/name"), "not a drive: the server says no such file");

        Assert.AreEqual("/", RemotePath.Join("/", ""));
        Assert.AreEqual("/a/b", RemotePath.Join("/", "a/b"));
        Assert.AreEqual("/srv/a", RemotePath.Join("/srv", "a"));
        Assert.AreEqual("/home/u", RemotePath.Resolve("~", "/home/u/"));
        Assert.AreEqual("/home/u/x", RemotePath.Resolve("~/x", "/home/u"));
        Assert.AreEqual("/abs", RemotePath.Resolve("/abs", "/home/u"));
        Assert.AreEqual("/", RemotePath.Resolve("~", "/"));
        Assert.AreEqual("/x", RemotePath.Resolve("~/x", "/"));

        static string Root(string text)
        {
            Assert.IsTrue(RemotePath.TryParseRoot(text, out var root, out var reason), reason);
            return root;
        }
    }

    [TestMethod]
    public void ValidationMessages()
    {
        var plugin = new SshfsPlugin();
        var empty = plugin.Validate(new SshfsSettings()).ToList();
        Assert.IsTrue(empty.Any(m => m.IsError && m.Property == nameof(SshfsSettings.Host)));
        Assert.IsTrue(empty.Any(m => m.IsError && m.Property == nameof(SshfsSettings.Username)));

        var good = new SshfsSettings { Host = "aus-files-01", Username = "smith", Password = "pw" };
        Assert.AreEqual(0, plugin.Validate(good).Count(m => m.IsError));
        Assert.AreEqual(0, plugin.Validate(good).Count(m => !m.IsError), "no warnings without a key file");

        Assert.IsTrue(plugin.Validate(new SshfsSettings { Host = "smith@host", Username = "x" }).Any(m => m.IsError && m.Property == nameof(SshfsSettings.Host)));
        Assert.IsTrue(plugin.Validate(new SshfsSettings { Host = "h", Username = "x", Port = 0 }).Any(m => m.IsError && m.Property == nameof(SshfsSettings.Port)));
        Assert.IsTrue(plugin.Validate(new SshfsSettings { Host = "h", Username = "x", Root = "data" }).Any(m => m.IsError && m.Property == nameof(SshfsSettings.Root)));
        Assert.IsTrue(plugin.Validate(new SshfsSettings { Host = "h", Username = "x", Connections = 0 }).Any(m => m.IsError && m.Property == nameof(SshfsSettings.Connections)));
        var missingKey = plugin.Validate(new SshfsSettings { Host = "h", Username = "x", KeyFile = Path.Combine(Path.GetTempPath(), "no-such-key-" + Guid.NewGuid().ToString("N")) }).ToList();
        Assert.IsTrue(missingKey.Any(m => !m.IsError && m.Property == nameof(SshfsSettings.KeyFile)), "a key that isn't on this machine is a warning, since settings files are shared");
        Assert.AreEqual(0, missingKey.Count(m => m.IsError));
    }

    [TestMethod]
    public void EntriesHideDotNamesAndMarkReadOnly()
    {
        var when = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var hidden = SshfsSource.ToEntry(".profile", false, 5, when, anyoneCanWrite: true);
        Assert.IsTrue(hidden.Attributes.HasFlag(VfsAttributes.Hidden));
        Assert.IsFalse(hidden.Attributes.HasFlag(VfsAttributes.ReadOnly));
        Assert.AreEqual(5, hidden.Length);
        Assert.AreEqual(new DateTimeOffset(when), hidden.Modified);
        Assert.IsNull(hidden.Created, "SFTP has no creation time");

        var readOnly = SshfsSource.ToEntry("a.txt", false, 3, when, anyoneCanWrite: false);
        Assert.IsTrue(readOnly.Attributes.HasFlag(VfsAttributes.ReadOnly));
        Assert.IsFalse(readOnly.Attributes.HasFlag(VfsAttributes.Hidden));

        var dir = SshfsSource.ToEntry("docs", true, 4096, when, anyoneCanWrite: true);
        Assert.IsTrue(dir.IsDirectory);
        Assert.AreEqual(0, dir.Length, "a folder's size is not its block size");

        Assert.IsNull(SshfsSource.ToEntry("epoch", false, 0, new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc), true).Modified, "an unset time is no time");
    }

    [TestMethod]
    public void KeyboardInteractiveDecisions()
    {
        Assert.AreEqual(KeyboardInteractive.Answer.SavedPassword, KeyboardInteractive.Decide("Password: ", false, "pw", passwordAlreadyOffered: false));
        Assert.AreEqual(KeyboardInteractive.Answer.AskHidden, KeyboardInteractive.Decide("Password: ", false, "pw", passwordAlreadyOffered: true), "a refused password isn't offered twice");
        Assert.AreEqual(KeyboardInteractive.Answer.AskHidden, KeyboardInteractive.Decide("Password: ", false, null, false));
        Assert.AreEqual(KeyboardInteractive.Answer.AskHidden, KeyboardInteractive.Decide("Verification code: ", false, "pw", false), "a 2FA code is never the password");
        Assert.AreEqual(KeyboardInteractive.Answer.AskPlain, KeyboardInteractive.Decide("Your name: ", true, "pw", false));

        Assert.AreEqual("Password", KeyboardInteractive.Message("", "Password: ", "u"));
        Assert.AreEqual("Enter the code from your app\nCode", KeyboardInteractive.Message("Enter the code from your app", "Code: ", "u"));
        StringAssert.Contains(KeyboardInteractive.Message(null, "", "smith"), "smith");
    }

    [TestMethod]
    public void HostKeysArePinnedPerAlgorithm()
    {
        var file = Path.Combine(Path.GetTempPath(), "anyfs-tests", "hostkeys-" + Guid.NewGuid().ToString("N"), "hostkeys.txt");
        try
        {
            var store = new HostKeyStore(file);
            Assert.AreEqual((HostKeyVerdict.FirstUse, (string?)null), store.Check("ssh-ed25519", "SHA256:aaa"));
            Assert.AreEqual((HostKeyVerdict.Known, (string?)"SHA256:aaa"), store.Check("ssh-ed25519", "SHA256:aaa"));
            Assert.AreEqual((HostKeyVerdict.Changed, (string?)"SHA256:aaa"), store.Check("ssh-ed25519", "SHA256:bbb"));
            Assert.AreEqual((HostKeyVerdict.FirstUse, (string?)null), store.Check("rsa-sha2-512", "SHA256:ccc"), "another algorithm is a first use");
            Assert.AreEqual(2, File.ReadAllLines(file).Length);
            Assert.AreEqual((HostKeyVerdict.Known, (string?)"SHA256:aaa"), new HostKeyStore(file).Check("ssh-ed25519", "SHA256:aaa"), "survives a restart");
        }
        finally
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(file)!, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>A pool for a server that is not there: a port of this machine that nothing listens on, where a connect is refused.</summary>
    private static SftpPool PoolForNoServer(TestSourceContext context, TimeProvider time, int connections)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var settings = new SshfsSettings { Host = "127.0.0.1", Port = port, Username = "nobody", Password = "none", Connections = connections, ConnectTimeoutSeconds = 5 };
        return new SftpPool(settings, context, new HostKeyStore(Path.Combine(context.StateDirectory, "hostkeys.txt")), time);
    }

    [TestMethod]
    public async Task AServerThatCannotBeReachedIsTriedOnceEveryTenSecondsNotByEveryCall()
    {
        using var context = new TestSourceContext("sshfs");
        var time = new FakeTime();
        await using var pool = PoolForNoServer(context, time, connections: 4);
        int Connects() => context.UpstreamRequests.Requests.Count(r => r.What == "SSH connect");

        var tried = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => pool.GetAsync(CancellationToken.None));
        Assert.IsFalse(tried.Permanent, tried.Message);
        Assert.AreEqual(1, Connects());

        // the calls that come after, whichever of the pool's connections is their turn
        async Task RefusedAsync(int connects)
        {
            for (var i = 0; i < 10; i++)
            {
                var refused = await Assert.ThrowsAsync<Exception>(() => pool.GetAsync(CancellationToken.None));
                Assert.AreEqual(connects, Connects(), "refused at once: no connect of its own, and no row for one");
                Assert.AreEqual(VfsError.Unavailable, (refused as VfsException)?.Error, refused.ToString());
                Assert.AreEqual(tried.Message + " (tried again every 10 s)", refused.Message);
            }
        }

        // with a VPN down each of these connected again and waited out the connect timeout, one behind another
        await RefusedAsync(connects: 1);

        time.Advance(ReachMemory.RetryEvery);
        await Assert.ThrowsExactlyAsync<SourceFailedException>(() => pool.GetAsync(CancellationToken.None));
        Assert.AreEqual(2, Connects(), "ten seconds on, one caller is let through to connect for real");
        await RefusedAsync(connects: 2);
        Assert.AreEqual(0, context.UpstreamRequests.Running.Count);
    }

    [TestMethod]
    public async Task CallersWaitingBehindAConnectThatFailsHaveItsAnswerAndDoNotConnectOneAfterAnother()
    {
        // one connection, so that all wait for the same one: the first is connecting as the others come
        using var context = new TestSourceContext("sshfs");
        await using var pool = PoolForNoServer(context, new FakeTime(), connections: 1);
        var calls = Enumerable.Range(0, 6).Select(async _ =>
        {
            try
            {
                await pool.GetAsync(CancellationToken.None);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }).ToList();
        var answers = await Task.WhenAll(calls);

        Assert.AreEqual(1, context.UpstreamRequests.Requests.Count(r => r.What == "SSH connect"), "one connect for them all");
        Assert.AreEqual(1, answers.Count(a => a is SourceFailedException), "the one who tried");
        Assert.AreEqual(5, answers.Count(a => a is VfsException { Error: VfsError.Unavailable }), "and those who waited behind it, or came after");
    }

    [TestMethod]
    public async Task ASaveTheServerRefusesAsTheFileClosesFailsTheClose()
    {
        // a small file is all still in SSH.NET's buffer as it closes, and only then does the full disk say no
        var stream = new SftpLikeStream { Refusal = new SftpException(StatusCode.Failure, "Failure") };
        var asked = (RecordingUpstreamWatcher.Request)new RecordingUpstreamWatcher().Asked("SFTP open", "/srv/docs/report.txt");
        var closes = 0;
        var cutOff = new List<VfsException>();
        var file = new SshfsFile(stream, "docs/report.txt", 0, writable: true, Counted(() => closes++), asked, cutOff.Add);
        await file.WriteAsync(0, "hello"u8.ToArray(), CancellationToken.None);

        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await file.DisposeAsync());
        Assert.AreEqual(VfsError.IoError, ex.Error, "a write the server refused");
        StringAssert.Contains(ex.Message, "docs/report.txt");
        StringAssert.Contains(ex.Message, "did not all reach the server");
        Assert.AreEqual(0, stream.OnServer.Count, "nothing of it is on the server");
        Assert.IsTrue(stream.HandleClosed, "the handle is closed there all the same");
        Assert.AreEqual(1, closes, "the source is told of the close, as of any close of a file opened for writing");
        Assert.AreEqual(0, cutOff.Count, "a server that answers, though with a refusal, is a connection that is there");
        Assert.IsTrue(asked.IsDone, "and the open's request has ended,");
        Assert.IsInstanceOfType<SftpException>(asked.Error, "as one that failed");
    }

    [TestMethod]
    public async Task ASaveCutOffByALostConnectionFailsTheCloseThoughSshNetSaysNothing()
    {
        var stream = new SftpLikeStream();
        var asked = (RecordingUpstreamWatcher.Request)new RecordingUpstreamWatcher().Asked("SFTP open", "/srv/docs/report.txt");
        var closes = 0;
        var cutOff = new List<VfsException>();
        var file = new SshfsFile(stream, "docs/report.txt", 0, writable: true, Counted(() => closes++), asked, cutOff.Add);
        await file.WriteAsync(0, "hello"u8.ToArray(), CancellationToken.None);
        stream.SessionOpen = false;

        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await file.DisposeAsync());
        Assert.AreEqual(VfsError.Unavailable, ex.Error, "the server never had the chance to refuse it");
        StringAssert.Contains(ex.Message, "docs/report.txt");
        StringAssert.Contains(ex.Message, "did not all reach the server");
        Assert.AreEqual(0, stream.OnServer.Count);
        Assert.AreEqual(1, closes);
        Assert.AreSame(ex, cutOff.Single(), "and the source is told the save was cut off, which is what turns its tile");
        Assert.IsTrue(asked.IsDone);
        Assert.IsNotNull(asked.Error);
    }

    [TestMethod]
    public async Task ACloseWithNothingLeftToSendStaysSilent()
    {
        // only read, and the server gone by the close: nothing is lost, so the close says nothing (its request does)
        var read = new SftpLikeStream { CloseFailure = new SshConnectionException("An established connection was aborted by the server.") };
        read.OnServer.AddRange("hello"u8.ToArray());
        var asked = (RecordingUpstreamWatcher.Request)new RecordingUpstreamWatcher().Asked("SFTP open", "/srv/a.txt");
        var file = new SshfsFile(read, "a.txt", 5, writable: false, Counted(() => Assert.Fail("a file only read changes no listing")), asked);
        Assert.AreEqual(5, await file.ReadAsync(0, new byte[5], CancellationToken.None));
        await file.DisposeAsync();
        Assert.IsTrue(asked.IsDone);
        Assert.IsInstanceOfType<SshConnectionException>(asked.Error);

        // opened for writing and never written, the connection lost meanwhile
        var untouched = new SftpLikeStream { SessionOpen = false };
        file = new SshfsFile(untouched, "b.txt", 0, writable: true, NothingToTell);
        await file.DisposeAsync();

        // written, and all of it sent as the file closes
        var written = new SftpLikeStream();
        file = new SshfsFile(written, "c.txt", 0, writable: true, NothingToTell);
        await file.WriteAsync(0, "hello"u8.ToArray(), CancellationToken.None);
        Assert.AreEqual(0, written.OnServer.Count, "kept until a flush or the close");
        await file.DisposeAsync();
        CollectionAssert.AreEqual("hello"u8.ToArray(), written.OnServer);
        Assert.IsTrue(written.HandleClosed);

        // written and flushed at its writer's word, the connection lost after that: everything had arrived
        var flushed = new SftpLikeStream();
        file = new SshfsFile(flushed, "d.txt", 0, writable: true, NothingToTell);
        await file.WriteAsync(0, "hello"u8.ToArray(), CancellationToken.None);
        await file.FlushAsync(CancellationToken.None);
        flushed.SessionOpen = false;
        await file.DisposeAsync();
        CollectionAssert.AreEqual("hello"u8.ToArray(), flushed.OnServer);
    }

    [TestMethod]
    public async Task AWriteGivenUpClosesSilentlyWhateverWasLeftToSend()
    {
        // a copy that was cancelled: the mount gives the write up and deletes the file next, which a close that threw would stop
        var stream = new SftpLikeStream { Refusal = new SftpException(StatusCode.Failure, "Failure") };
        var asked = (RecordingUpstreamWatcher.Request)new RecordingUpstreamWatcher().Asked("SFTP open", "/srv/docs/report.txt");
        var closes = 0;
        IVfsFile file = new SshfsFile(stream, "docs/report.txt", 0, writable: true, Counted(() => closes++), asked);
        await file.WriteAsync(0, "hello"u8.ToArray(), CancellationToken.None);
        await file.AbandonAsync();
        Assert.IsTrue(stream.HandleClosed);
        Assert.AreEqual(1, closes);
        Assert.IsTrue(asked.IsDone);

        var gone = new SftpLikeStream();
        file = new SshfsFile(gone, "docs/report.txt", 0, writable: true, NothingToTell);
        await file.WriteAsync(0, "hello"u8.ToArray(), CancellationToken.None);
        gone.SessionOpen = false;
        await file.AbandonAsync();
    }

    [TestMethod]
    public async Task ASaveWhoseWriterWasToldItFailedClosesSilently()
    {
        // a write that fills SSH.NET's buffer is sent at once and refused there: the writer has its answer, and a mount
        // that deletes the half-made file as it closes it (SMB's delete on close) is not stopped by the close failing too
        var full = new SftpLikeStream { BufferSize = 4, Refusal = new SftpException(StatusCode.Failure, "Failure") };
        IVfsFile file = new SshfsFile(full, "docs/report.txt", 0, writable: true, NothingToTell);
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await file.WriteAsync(0, "hello"u8.ToArray(), CancellationToken.None));
        Assert.AreEqual(VfsError.IoError, ex.Error);
        await file.DisposeAsync();
        Assert.IsTrue(full.HandleClosed);

        // the same of a flush at the writer's word
        var flushed = new SftpLikeStream { Refusal = new SftpException(StatusCode.Failure, "Failure") };
        file = new SshfsFile(flushed, "docs/report.txt", 0, writable: true, NothingToTell);
        await file.WriteAsync(0, "hello"u8.ToArray(), CancellationToken.None);
        ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await file.FlushAsync(CancellationToken.None));
        Assert.AreEqual(VfsError.IoError, ex.Error);
        await file.DisposeAsync();

        // but a write taken after one that failed is the close's to answer for again
        var again = new SftpLikeStream { BufferSize = 4, Refusal = new SftpException(StatusCode.Failure, "Failure") };
        file = new SshfsFile(again, "docs/report.txt", 0, writable: true, NothingToTell);
        await Assert.ThrowsExactlyAsync<VfsException>(async () => await file.WriteAsync(0, "hello"u8.ToArray(), CancellationToken.None));
        again.BufferSize = 1024;
        await file.WriteAsync(5, "!"u8.ToArray(), CancellationToken.None);
        ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await file.DisposeAsync());
        Assert.AreEqual(VfsError.IoError, ex.Error);
    }

    [TestMethod]
    public async Task AFileWhoseConnectionHasGoneFailsEachCallAsUnavailableAndItsSourceIsToldOnce()
    {
        // An open file is one handle on the connection it was opened on: with that gone, nothing asked through the file
        // is answered for as long as it stays open. SSH.NET says so as "The session is not open.", which went to the
        // mount as it was, an exception of no kind a mount knows, and the source heard nothing of it: its tile stayed
        // Ready (the review of 21-Nov-2026, N17)
        var stream = new SftpLikeStream();
        stream.OnServer.AddRange("hello"u8.ToArray());
        var cutOff = new List<VfsException>();
        var file = new SshfsFile(stream, "docs/a.txt", 5, writable: true, NothingToTell, asked: null, cutOff.Add);
        Assert.AreEqual(5, await file.ReadAsync(0, new byte[5], CancellationToken.None));
        await file.WriteAsync(5, "!"u8.ToArray(), CancellationToken.None);
        Assert.AreEqual(0, cutOff.Count, "nothing to tell while the server answers");

        stream.SessionOpen = false;
        var read = await Assert.ThrowsExactlyAsync<VfsException>(async () => await file.ReadAsync(0, new byte[5], CancellationToken.None));
        Assert.AreEqual(VfsError.Unavailable, read.Error);
        StringAssert.Contains(read.Message, "The session is not open.");
        Assert.AreSame(read, cutOff.Single(), "the source is told as the file finds its connection gone");

        // the same of whatever is asked next, and the source is not told again: a file cut off asks the server nothing
        // more, so what it fails with says nothing new of the server
        var flush = await Assert.ThrowsExactlyAsync<VfsException>(async () => await file.FlushAsync(CancellationToken.None));
        Assert.AreEqual(VfsError.Unavailable, flush.Error);
        var write = await Assert.ThrowsExactlyAsync<VfsException>(async () => await file.WriteAsync(0, "H"u8.ToArray(), CancellationToken.None));
        Assert.AreEqual(VfsError.Unavailable, write.Error);
        var cut = await Assert.ThrowsExactlyAsync<VfsException>(async () => await file.SetLengthAsync(2, CancellationToken.None));
        Assert.AreEqual(VfsError.Unavailable, cut.Error);
        Assert.AreEqual(1, cutOff.Count);

        // its writer was told by the flush, so the close says nothing more; and a file used after its close is its
        // caller's mistake, not the connection's
        await file.DisposeAsync();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await file.ReadAsync(0, new byte[5], CancellationToken.None));
        Assert.AreEqual(1, cutOff.Count);
    }

    [TestMethod]
    public async Task ALostConnectionThatSshNetSaysAsItsOwnExceptionTellsTheSourceToo()
    {
        // while SSH.NET still takes the session for open, a request meets the dead socket: "Client not connected."
        var stream = new SftpLikeStream { BufferSize = 4, Refusal = new SshConnectionException("Client not connected.") };
        var cutOff = new List<VfsException>();
        var file = new SshfsFile(stream, "docs/a.txt", 0, writable: true, NothingToTell, asked: null, cutOff.Add);
        var write = await Assert.ThrowsExactlyAsync<VfsException>(async () => await file.WriteAsync(0, "hello"u8.ToArray(), CancellationToken.None));
        Assert.AreEqual(VfsError.Unavailable, write.Error);
        Assert.AreSame(write, cutOff.Single());
        await file.DisposeAsync();
    }

    [TestMethod]
    public async Task ATimeSetOnAFileStillOpenSendsWhatTheFileKeepsFirst()
    {
        // SSH.NET keeps the last of what is written until the file closes, and a mount sets a copied file's time while it
        // is open: the kept bytes went to the server after the time, and the server put the file's time to that moment
        // (asked of a real server, 27-Nov-2026: the live test below). What the source does before it sets a time:
        var writes = new OpenWrites();
        var stream = new SftpLikeStream();
        var file = new SshfsFile(stream, "docs/a.txt", 0, writable: true, NothingToTell);
        writes.Add(file, "docs/a.txt");
        var other = new SftpLikeStream();
        var beside = new SshfsFile(other, "docs/b.txt", 0, writable: true, NothingToTell);
        writes.Add(beside, "docs/b.txt");
        await file.WriteAsync(0, "hello"u8.ToArray(), CancellationToken.None);
        await beside.WriteAsync(0, "beside"u8.ToArray(), CancellationToken.None);
        Assert.AreEqual(0, stream.OnServer.Count, "kept, as SSH.NET keeps a write until its buffer fills");

        await writes.SendAsync("docs/a.txt", CancellationToken.None);
        CollectionAssert.AreEqual("hello"u8.ToArray(), stream.OnServer, "sent before the time is set");
        Assert.AreEqual(0, other.OnServer.Count, "and no other file's");

        // a connection gone by then fails the time's setting as a server out of reach, and what is kept stays kept: the
        // close tries again and tells the writer, as it would have with no time set
        await file.WriteAsync(5, "!"u8.ToArray(), CancellationToken.None);
        stream.SessionOpen = false;
        var lost = await Assert.ThrowsExactlyAsync<VfsException>(() => writes.SendAsync("docs/a.txt", CancellationToken.None));
        Assert.AreEqual(VfsError.Unavailable, lost.Error);
        var close = await Assert.ThrowsExactlyAsync<VfsException>(async () => await file.DisposeAsync());
        StringAssert.Contains(close.Message, "did not all reach the server");

        // a file closed is asked nothing, and one only read is not among them
        await writes.SendAsync("docs/a.txt", CancellationToken.None);
        await beside.DisposeAsync();
        CollectionAssert.AreEqual("beside"u8.ToArray(), other.OnServer);
        await writes.SendAsync("docs/b.txt", CancellationToken.None);
    }

    [TestMethod]
    public async Task CopyingFilesIntoAFolderListsItOnceNotOnceForEachFile()
    {
        // What a copy of 200 small files into one folder asks of a server that counts, as a mount makes it: for each
        // file a look, the open that makes it, its bytes, its time, its close, and a look at what came of it. Each open
        // that made a file, each time set and each close forgot the listing held of the folder, so the next look listed
        // the folder again with every file copied so far: 201 listings and 20,100 entries for the 200 (the review of
        // 21-Nov-2026, N17). The source's own steps are run here on a listing cache: no server is to be had in a test.
        const int Files = 200;
        var folder = new CountingFolder();
        var cache = new ListingCache(TimeSpan.FromSeconds(2), (_, _) => folder.ListAsync(), time: new FakeTime());
        var bytes = "hello"u8.ToArray();
        var modified = new DateTime(2026, 11, 2, 3, 4, 5, DateTimeKind.Utc);
        for (var i = 0; i < Files; i++)
        {
            var name = $"file-{i:000}.txt";
            var path = "in/" + name;
            Assert.IsNull(await cache.StatAsync(path, CancellationToken.None), "the mount's look before it makes the file");
            var known = await cache.StatAsync(path, CancellationToken.None);
            var stream = folder.Make(name);
            SshfsSource.Made(cache, path, VfsOpenMode.CreateNew, known);
            var file = new SshfsFile(stream, path, 0, writable: true, () => SshfsSource.KeepAsync(cache, path, () => folder.StatAsync(name)));
            await file.WriteAsync(0, bytes, CancellationToken.None);
            folder.SetTime(name, modified);
            await SshfsSource.KeepAsync(cache, path, () => folder.StatAsync(name));
            await file.DisposeAsync();
            Assert.AreEqual(folder.Entry(name), await cache.StatAsync(path, CancellationToken.None), "the file as the server has it, after its close");
        }

        Assert.AreEqual(1, folder.Listings, $"{folder.Listings:N0} listings of the folder, {folder.EntriesListed:N0} entries in all");
        Assert.AreEqual(2 * Files, folder.Stats, "the server is asked for the one file instead: as its time is set, and as it closes");
        CollectionAssert.AreEquivalent(folder.Entries().ToList(), (await cache.GetAsync("in", CancellationToken.None)).ToList(), "and the listing held says what the server says");
        Assert.AreEqual(1, folder.Listings);
    }

    [TestMethod]
    [DataRow(1200, DisplayName = "a folder whose listing is long")]
    [DataRow(40, DisplayName = "a folder of a few files")]
    public async Task AStatInAFolderWhoseListingIsLongAsksAboutTheOnePathAndNotForTheListingAgain(int files)
    {
        // A stat or an open with no listing held listed the folder, whatever its size: in a folder of 20,000 files on
        // the lab's server, 330 to 720 ms for each that came after the listing's two seconds, where the server's
        // answer about one path takes 5 (the review of 21-Nov-2026, P12). The source's own steps are run here on a
        // listing cache: no server is to be had in a test.
        var folder = new CountingFolder();
        for (var i = 0; i < files; i++)
        {
            folder.Make($"file-{i:0000}.txt");
        }

        var time = new FakeTime();
        var longListings = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var cache = new ListingCache(TimeSpan.FromSeconds(2), (path, _) => Noting(path), time: time);
        async IAsyncEnumerable<VfsEntry> Noting(string path)
        {
            var count = 0;
            await foreach (var entry in folder.ListAsync())
            {
                count++;
                yield return entry;
            }

            SshfsSource.Listed(longListings, path, count, longFrom: 1000);
        }

        Task<VfsEntry?> KnownAsync(string name) => SshfsSource.KnownAsync(cache, "in/" + name, longListings.ContainsKey, () => folder.StatAsync(name), CancellationToken.None);

        // the first look lists, as nothing is known of the folder; what follows within its two seconds is answered from that
        Assert.AreEqual(folder.Entry("file-0001.txt"), await KnownAsync("file-0001.txt"));
        Assert.AreEqual(folder.Entry("file-0002.txt"), await KnownAsync("file-0002.txt"));
        Assert.AreEqual((1, 0), (folder.Listings, folder.Stats));

        // each look after that comes when the listing is no longer held
        for (var i = 3; i < 13; i++)
        {
            time.Advance(TimeSpan.FromSeconds(3));
            Assert.AreEqual(folder.Entry($"file-{i:0000}.txt"), await KnownAsync($"file-{i:0000}.txt"));
        }

        time.Advance(TimeSpan.FromSeconds(3));
        Assert.IsNull(await KnownAsync("not-there.txt"));
        if (files >= 1000)
        {
            Assert.AreEqual((1, 11), (folder.Listings, folder.Stats), "the one path asked about each time, the folder not listed again");
        }
        else
        {
            Assert.AreEqual((12, 0), (folder.Listings, folder.Stats), "a short listing is the cheaper way, as it was");
        }

        // whoever lists the folder gets a listing, and while it is held it answers
        var listings = folder.Listings;
        var stats = folder.Stats;
        Assert.AreEqual(files, (await cache.GetAsync("in", CancellationToken.None)).Count);
        Assert.AreEqual(folder.Entry("file-0020.txt"), await KnownAsync("file-0020.txt"));
        Assert.AreEqual((listings + (files >= 1000 ? 1 : 0), stats), (folder.Listings, folder.Stats));

        // a folder that has shrunk is listed as any small one from then on
        for (var i = 30; i < files; i++)
        {
            folder.Remove($"file-{i:0000}.txt");
        }

        time.Advance(TimeSpan.FromSeconds(3));
        Assert.AreEqual(30, (await cache.GetAsync("in", CancellationToken.None)).Count);
        time.Advance(TimeSpan.FromSeconds(3));
        listings = folder.Listings;
        stats = folder.Stats;
        Assert.AreEqual(folder.Entry("file-0005.txt"), await KnownAsync("file-0005.txt"));
        Assert.AreEqual((listings + 1, stats), (folder.Listings, folder.Stats));
    }

    [TestMethod]
    public async Task AFileMadeShowsInTheListingHeldOfItsFolderAndOneOpenedAsItWasLeavesItAlone()
    {
        var folder = new CountingFolder();
        folder.Make("old.txt").OnServer.AddRange("hello"u8.ToArray());
        var cache = new ListingCache(TimeSpan.FromSeconds(2), (_, _) => folder.ListAsync(), time: new FakeTime());
        var old = await cache.StatAsync("in/old.txt", CancellationToken.None);
        Assert.AreEqual(5, old?.Length);

        // made by its open: there from then on, empty, with nothing asked
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        SshfsSource.Made(cache, "in/new.txt", VfsOpenMode.CreateNew, known: null);
        var made = await cache.StatAsync("in/new.txt", CancellationToken.None);
        Assert.IsNotNull(made);
        Assert.IsFalse(made.IsDirectory);
        Assert.AreEqual(0, made.Length);
        Assert.IsTrue(made.Modified >= before, "made now");
        SshfsSource.Made(cache, "in/.profile", VfsOpenMode.Create, known: null);
        Assert.IsTrue((await cache.StatAsync("in/.profile", CancellationToken.None))!.Attributes.HasFlag(VfsAttributes.Hidden), "as a listing would show it");

        // opened as it was: what the listing says of it stands
        SshfsSource.Made(cache, "in/old.txt", VfsOpenMode.OpenOrCreate, old);
        Assert.AreEqual(old, await cache.StatAsync("in/old.txt", CancellationToken.None));

        // cut to nothing by its open
        SshfsSource.Made(cache, "in/old.txt", VfsOpenMode.Create, old);
        Assert.AreEqual(0, (await cache.StatAsync("in/old.txt", CancellationToken.None))?.Length);
        Assert.AreEqual(1, folder.Listings, "none of it listed the folder again");
        Assert.AreEqual(0, folder.Stats, "or asked the server anything");

        // made by its open, or found there, made by someone since the folder was listed: which, only the server knows
        folder.Make("theirs.txt").OnServer.AddRange("someone else's"u8.ToArray());
        SshfsSource.Made(cache, "in/theirs.txt", VfsOpenMode.OpenOrCreate, known: null);
        Assert.IsNull(cache.Held("in"), "the listing is forgotten, as it was at every such open");
        Assert.AreEqual(14, (await cache.StatAsync("in/theirs.txt", CancellationToken.None))?.Length);
        Assert.AreEqual(2, folder.Listings);
    }

    [TestMethod]
    public async Task AListingPutRightByAWriteIsStillListedAgainWhenItsTwoSecondsAreOver()
    {
        // what a write leaves in a listing makes the listing no newer: what someone else did in the folder shows two
        // seconds after it was listed, as it did
        var folder = new CountingFolder();
        var time = new FakeTime();
        var cache = new ListingCache(TimeSpan.FromSeconds(2), (_, _) => folder.ListAsync(), time: time);
        Assert.IsNull(await cache.StatAsync("in/mine.txt", CancellationToken.None));

        time.Advance(TimeSpan.FromSeconds(1.5));
        var stream = folder.Make("mine.txt");
        SshfsSource.Made(cache, "in/mine.txt", VfsOpenMode.CreateNew, known: null);
        var file = new SshfsFile(stream, "in/mine.txt", 0, writable: true, () => SshfsSource.KeepAsync(cache, "in/mine.txt", () => folder.StatAsync("mine.txt")));
        await file.WriteAsync(0, "hello"u8.ToArray(), CancellationToken.None);
        await file.DisposeAsync();
        folder.Make("theirs.txt");
        Assert.AreEqual(5, (await cache.StatAsync("in/mine.txt", CancellationToken.None))?.Length);
        Assert.IsNull(await cache.StatAsync("in/theirs.txt", CancellationToken.None), "the listing is as old as it was");
        Assert.AreEqual(1, folder.Listings);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.IsNotNull(await cache.StatAsync("in/theirs.txt", CancellationToken.None));
        Assert.AreEqual(2, folder.Listings);
    }

    [TestMethod]
    public async Task WithNothingHeldOfAFolderTheServerIsNotAskedAndAListingOnItsWayIsNotKept()
    {
        var folder = new CountingFolder();
        var landing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<VfsEntry> HeldUpAsync()
        {
            await landing.Task;
            await foreach (var entry in folder.ListAsync())
            {
                yield return entry;
            }
        }

        var cache = new ListingCache(TimeSpan.FromSeconds(2), (_, _) => HeldUpAsync(), time: new FakeTime());

        // nothing held and nothing on its way: there is nothing to put right
        await SshfsSource.KeepAsync(cache, "in/a.txt", () => folder.StatAsync("a.txt"));
        Assert.AreEqual(0, folder.Stats);

        // a listing on its way as the file is written: whoever waits for it has it, and it is not kept, as before
        var listing = cache.GetAsync("in", CancellationToken.None).AsTask();
        folder.Make("a.txt");
        await SshfsSource.KeepAsync(cache, "in/a.txt", () => folder.StatAsync("a.txt"));
        Assert.AreEqual(0, folder.Stats);
        landing.SetResult();
        await listing;
        Assert.IsNull(cache.Held("in"));
        Assert.IsNotNull(await cache.StatAsync("in/a.txt", CancellationToken.None));
        Assert.AreEqual(2, folder.Listings);
    }

    [TestMethod]
    public async Task AFileGoneByItsCloseLeavesTheListingAndAServerThatCannotBeAskedHasItForgotten()
    {
        var folder = new CountingFolder();
        folder.Make("a.txt");
        folder.Make("b.txt");
        var cache = new ListingCache(TimeSpan.FromSeconds(2), (_, _) => folder.ListAsync(), time: new FakeTime());
        Assert.IsNotNull(await cache.StatAsync("in/a.txt", CancellationToken.None));

        // gone by the time the server is asked of it (deleted as it closed): out of the listing, nothing listed again
        folder.Remove("a.txt");
        await SshfsSource.KeepAsync(cache, "in/a.txt", () => folder.StatAsync("a.txt"));
        Assert.IsNull(await cache.StatAsync("in/a.txt", CancellationToken.None));
        Assert.IsNotNull(await cache.StatAsync("in/b.txt", CancellationToken.None));
        Assert.AreEqual(1, folder.Listings);

        // the server out of reach: the listing is forgotten, as every write forgot it before, and the next look lists
        await SshfsSource.KeepAsync(cache, "in/b.txt", () => Task.FromException<VfsEntry?>(VfsException.Unavailable("files.example.com is not reachable")));
        Assert.IsNull(cache.Held("in"));
        Assert.IsNotNull(await cache.StatAsync("in/b.txt", CancellationToken.None));
        Assert.AreEqual(2, folder.Listings);
    }

    /// <summary>What a file tells its source as it closes, for a test with nothing to do then.</summary>
    private static readonly Func<Task> NothingToTell = () => Task.CompletedTask;

    /// <summary>The same, for a test that counts the closes, or allows none.</summary>
    private static Func<Task> Counted(Action close) => () =>
    {
        close();
        return Task.CompletedTask;
    };

    /// <summary>
    /// A folder on a server that counts what it is asked: its listings and the entries they came to, and the times it
    /// was asked for one file alone. Its files are the streams written to it.
    /// </summary>
    private sealed class CountingFolder
    {
        private readonly Dictionary<string, (SftpLikeStream Stream, DateTime Modified)> _files = new(StringComparer.Ordinal);

        public int Listings { get; private set; }

        public long EntriesListed { get; private set; }

        public int Stats { get; private set; }

        /// <summary>A new, empty file, as an open that makes one leaves it.</summary>
        public SftpLikeStream Make(string name)
        {
            var stream = new SftpLikeStream();
            _files[name] = (stream, new DateTime(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc));
            return stream;
        }

        public void SetTime(string name, DateTime modifiedUtc) => _files[name] = (_files[name].Stream, modifiedUtc);

        public void Remove(string name) => _files.Remove(name);

        /// <summary>A file as a listing or a stat gives it: as long as what has been sent of it.</summary>
        public VfsEntry? Entry(string name)
            => _files.TryGetValue(name, out var file) ? SshfsSource.ToEntry(name, false, file.Stream.OnServer.Count, file.Modified, anyoneCanWrite: true) : null;

        public IEnumerable<VfsEntry> Entries() => _files.Keys.Select(name => Entry(name)!);

        public Task<VfsEntry?> StatAsync(string name)
        {
            Stats++;
            return Task.FromResult(Entry(name));
        }

        public async IAsyncEnumerable<VfsEntry> ListAsync()
        {
            await Task.Yield();
            Listings++;
            foreach (var entry in Entries().ToList())
            {
                EntriesListed++;
                yield return entry;
            }
        }
    }

    /// <summary>
    /// A stream that keeps and sends as SSH.NET 2026.0.0's SftpFileStream does, by its code: a write stays in its buffer
    /// until the buffer is full or a flush sends it, a flush with nothing kept asks the server nothing, and the close
    /// flushes and then closes the handle while the session is open, and does neither once the session has gone, without
    /// a word. A read and a length set ask the server, and with the session gone fail as a flush of something kept does.
    /// </summary>
    private sealed class SftpLikeStream : Stream
    {
        private readonly List<byte> _kept = [];
        private long _position;

        /// <summary>How much is kept before it is sent unasked (with OpenSSH, about 32 KB).</summary>
        public int BufferSize { get; set; } = 32 * 1024;

        /// <summary>The file's bytes as the server has them.</summary>
        public List<byte> OnServer { get; } = [];

        /// <summary>False once the connection has gone: SSH.NET then answers a request with "The session is not open."</summary>
        public bool SessionOpen { get; set; } = true;

        /// <summary>What the server answers a write with instead of taking it (a full disk, a quota).</summary>
        public Exception? Refusal { get; set; }

        /// <summary>What the close of the handle fails with.</summary>
        public Exception? CloseFailure { get; set; }

        public bool HandleClosed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override void Flush()
        {
            if (_kept.Count == 0)
            {
                return;
            }

            if (!SessionOpen)
            {
                throw new InvalidOperationException("The session is not open.");
            }

            if (Refusal is not null)
            {
                throw Refusal;
            }

            var at = (int)_position - _kept.Count;
            OnServer.RemoveRange(at, Math.Min(_kept.Count, OnServer.Count - at));
            OnServer.InsertRange(at, _kept);
            _kept.Clear();
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();
            Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Flush();
            RequireSession();
            var n = (int)Math.Clamp(OnServer.Count - _position, 0, count);
            OnServer.CopyTo((int)_position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _kept.AddRange(buffer.AsSpan(offset, count));
            _position += count;
            if (_kept.Count >= BufferSize)
            {
                Flush();
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Flush();
            return _position = offset;
        }

        public override void SetLength(long value)
        {
            Flush();
            RequireSession();
            if (value < OnServer.Count)
            {
                OnServer.RemoveRange((int)value, OnServer.Count - (int)value);
            }
            else
            {
                OnServer.AddRange(new byte[value - OnServer.Count]);
            }

            _position = Math.Min(_position, value);
        }

        private void RequireSession()
        {
            if (!SessionOpen)
            {
                throw new InvalidOperationException("The session is not open.");
            }
        }

        public override async ValueTask DisposeAsync()
        {
            if (!SessionOpen)
            {
                return;
            }

            try
            {
                await FlushAsync(CancellationToken.None);
            }
            finally
            {
                HandleClosed = true;
            }

            if (CloseFailure is not null)
            {
                throw CloseFailure;
            }
        }
    }
}
