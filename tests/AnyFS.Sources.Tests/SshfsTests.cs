using System.Text;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.Sshfs;

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

        Assert.IsTrue(RemotePath.IsDriveHome("/C:/Users/Gabrielle"), "Windows OpenSSH home");
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
}
