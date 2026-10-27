using System.Diagnostics;

namespace AnyFS.Fixtures.SmbStress;

/// <summary>A published file: share-relative, slash-separated (its content key), and its size.</summary>
internal sealed record PubFile(string Rel, long Size);

/// <summary>
/// One worker driving the share through its mount with the OS's own file API, as every application does
/// (test/rig/worker/fsmode.go). Several run at once in one process, so one client has requests in flight together on its
/// connection; several clients run at once on several machines. Each worker owns stress/&lt;client&gt;/w&lt;N&gt;/ on the
/// share. The files it publishes there are named after their size (pub_&lt;seq&gt;_&lt;size&gt;.bin) and their content is a
/// function of their share-relative path, so any other client, and the audit, can check them by name alone.
/// </summary>
internal sealed class Worker
{
    private readonly Settings _settings;
    private readonly Recorder _recorder;
    private readonly int _id;
    private readonly Random _rng;
    private readonly string _rel;
    private readonly List<PubFile> _pubs = [];
    private int _seq;
    private long _written;
    private long _read;

    public Worker(Settings settings, Recorder recorder, int id)
    {
        _settings = settings;
        _recorder = recorder;
        _id = id;
        _rng = new Random(unchecked((int)(settings.Seed ^ (Content.HashKey(settings.Name) << 8) ^ (ulong)id)));
        _rel = $"stress/{settings.Name}/w{id}";
    }

    public static string PubName(int seq, long size) => $"pub_{seq}_{size}.bin";

    public static bool TryParsePubName(string name, out long size)
    {
        size = 0;
        if (!name.StartsWith("pub_", StringComparison.Ordinal) || !name.EndsWith(".bin", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = name["pub_".Length..^".bin".Length].Split('_');
        return parts.Length == 2 && long.TryParse(parts[1], out size);
    }

    /// <summary>Names other software leaves in a folder (Finder's .DS_Store, Windows' thumbnails), which listing checks pass over.</summary>
    public static bool Ignored(string name) => name.StartsWith('.') || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase);

    /// <summary>The shared read-only corpus the harness seeds on the host, which every client reads throughout.</summary>
    public static IReadOnlyList<PubFile> Corpus { get; } =
        new long[] { 1 << 10, 7 << 10, 64 << 10, 300 << 10, 1 << 20, 3 << 20, 5 << 20, 8 << 20 }.Select((s, i) => new PubFile($"corpus/c{i}_{s}.bin", s)).ToList();

    public IEnumerable<ExpectedFile> Expected => _pubs.Select(p => new ExpectedFile(p.Rel, p.Size));

    private string Abs(string rel) => Path.Combine(_settings.Root, rel.Replace('/', Path.DirectorySeparatorChar));

    private int Next() => ++_seq;

    private void Note(long written, long read)
    {
        _written += written;
        _read += read;
    }

    public void Run(DateTime deadline)
    {
        var started = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(Abs(_rel));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _recorder.Begin(_id, "mkdir", _rel);
            _recorder.End(_id, "mkdir", _rel, started, 0, 0, ex);
            return;
        }

        (string Name, int Weight, Action Run)[] ops =
        [
            ("small", 25, OpSmall),
            ("medium", 12, OpMedium),
            ("large", _settings.LargeMaxMb > 0 ? 4 : 0, OpLarge),
            ("publish", 10, OpPublish),
            ("mutate", 12, OpMutate),
            ("tree", 8, OpTree),
            ("list", 8, OpList),
            ("corpus", 12, OpCorpus),
            ("cross", 9, OpCross),
            ("randio", 10, OpRandIo),
        ];
        var total = ops.Sum(o => o.Weight);
        while (DateTime.UtcNow < deadline)
        {
            var pick = _rng.Next(total);
            var op = ops[^1];
            foreach (var candidate in ops)
            {
                if (pick < candidate.Weight)
                {
                    op = candidate;
                    break;
                }

                pick -= candidate.Weight;
            }

            var watch = Stopwatch.StartNew();
            _recorder.Begin(_id, op.Name, _rel);
            Exception? error = null;
            try
            {
                op.Run();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                error = ex;
            }

            _recorder.End(_id, op.Name, _rel, watch, _written, _read, error);
            (_written, _read) = (0, 0);
        }

        // leave only what the audit expects: the published files
        try
        {
            foreach (var entry in new DirectoryInfo(Abs(_rel)).EnumerateFileSystemInfos())
            {
                if (!TryParsePubName(entry.Name, out _) && !Ignored(entry.Name))
                {
                    if (entry is DirectoryInfo dir)
                    {
                        dir.Delete(recursive: true);
                    }
                    else
                    {
                        entry.Delete();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"cleaning {_rel}: {ex.Message}");
        }
    }

    // ---- file helpers ----

    private static FileStream Create(string path) => new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 0);

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 0);

    private static FileStream OpenReadWrite(string path) => new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 0);

    public static void WriteFile(string path, Content content, long size, int chunk, bool sync)
    {
        using var f = Create(path);
        content.WriteTo(f, 0, size, chunk);
        if (sync)
        {
            f.Flush(flushToDisk: true);
        }
    }

    /// <summary>The file has exactly the expected size and bytes; how many were read.</summary>
    public static long VerifyFile(string path, Content content, long size)
    {
        using var f = OpenRead(path);
        if (f.Length != size)
        {
            throw new IOException($"\"{content.Key}\": size {f.Length}, want {size}");
        }

        return content.VerifyReader(f, 0, size, atEnd: true);
    }

    /// <summary>
    /// A check of names or attributes that may briefly disagree with the server because the client caches them: macOS's
    /// smbfs holds directory entries and attributes for a few seconds, so a name it just deleted, or a listing it just
    /// changed, can linger in its own cache after the server applied the change (close-to-open consistency, not at once).
    /// It gets a bounded window to catch up. The server's own state is the audit's to judge, strictly. Linux's cifs and
    /// Windows' redirector are coherent here, so they are checked once.
    /// </summary>
    private static void Settle(Action check)
    {
        try
        {
            check();
            return;
        }
        catch (IOException) when (OperatingSystem.IsMacOS())
        {
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            Thread.Sleep(250);
            try
            {
                check();
                return;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
            }
        }
    }

    private static void ExpectGone(string path) => Settle(() =>
    {
        if (Path.Exists(path))
        {
            throw new IOException($"{Path.GetFileName(path)} still exists after the delete");
        }
    });

    /// <summary>A folder's entries: name to size, -1 for a folder.</summary>
    private static Dictionary<string, long> ListDir(string dir)
    {
        var entries = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            if (!Ignored(e.Name))
            {
                entries[e.Name] = e is FileInfo f ? f.Length : -1;
            }
        }

        return entries;
    }

    private static void DiffListing(string dir, Dictionary<string, long> got, Dictionary<string, long> want)
    {
        var problems = new List<string>();
        foreach (var (name, size) in want)
        {
            if (!got.TryGetValue(name, out var g))
            {
                problems.Add("missing " + name);
            }
            else if (g != size)
            {
                problems.Add($"{name}: size {g}, want {size}");
            }
        }

        problems.AddRange(got.Keys.Where(n => !want.ContainsKey(n)).Select(n => "unexpected " + n));
        if (problems.Count > 0)
        {
            problems.Sort(StringComparer.Ordinal);
            List<string> shown = problems.Count > 6 ? [.. problems.Take(6), $"... {problems.Count - 6} more"] : problems;
            throw new IOException($"the listing of {dir} is wrong: {string.Join("; ", shown)}");
        }
    }

    // ---- the operations ----

    /// <summary>Writes a file, reads it back whole, deletes it.</summary>
    private void Churn(string op, long size, int chunk, bool sync)
    {
        var rel = $"{_rel}/{op}_{Next()}.bin";
        var content = new Content(_settings.Seed, rel);
        var p = Abs(rel);
        WriteFile(p, content, size, chunk, sync);
        Note(size, 0);
        Note(0, VerifyFile(p, content, size));
        File.Delete(p);
        ExpectGone(p);
    }

    private void OpSmall() => Churn("small", _rng.Next(60 << 10) + (4 << 10), 64 << 10, false);

    private void OpMedium() => Churn("medium", _rng.Next(7 << 20) + (1 << 20), 1 << 20, _rng.Next(3) == 0);

    /// <summary>Streams a big file, checks it front to back, then with reads at random offsets.</summary>
    private void OpLarge()
    {
        var size = (long)(_rng.Next(_settings.LargeMaxMb * 3 / 4) + (_settings.LargeMaxMb / 4) + 1) << 20;
        var rel = $"{_rel}/large_{Next()}.bin";
        var content = new Content(_settings.Seed, rel);
        var p = Abs(rel);
        WriteFile(p, content, size, 1 << 20, true);
        Note(size, 0);
        Note(0, VerifyFile(p, content, size));
        using (var f = OpenRead(p))
        {
            for (var i = 0; i < 8; i++)
            {
                var n = _rng.Next(1 << 20) + 1;
                var offset = _rng.NextInt64(size - n + 1);
                content.VerifyAt(f.SafeFileHandle, offset, n);
                Note(0, n);
            }
        }

        File.Delete(p);
        ExpectGone(p);
    }

    /// <summary>Writes a temporary file and renames it to its final, checkable name; with the quota of published files reached, the oldest goes first (and must be gone).</summary>
    private void OpPublish()
    {
        if (_pubs.Count >= _settings.MaxPub)
        {
            var old = _pubs[0];
            _pubs.RemoveAt(0);
            File.Delete(Abs(old.Rel));
            ExpectGone(Abs(old.Rel));
        }

        var size = _rng.Next(3 << 20) + (1 << 10);
        var name = PubName(Next(), size);
        var rel = $"{_rel}/{name}";
        var tmp = Abs($"{_rel}/tmp_{name}");
        var content = new Content(_settings.Seed, rel);
        WriteFile(tmp, content, size, 256 << 10, false);
        Note(size, 0);
        File.Move(tmp, Abs(rel), overwrite: true);
        ExpectGone(tmp);
        Note(0, VerifyFile(Abs(rel), content, size));
        _pubs.Add(new PubFile(rel, size));
    }

    /// <summary>Overwrites random blocks of, appends to or shrinks a private file, checking the whole of it each time: the writes must land and nothing stale may come back.</summary>
    private void OpMutate()
    {
        long size = _rng.Next(2 << 20) + (64 << 10);
        var rel = $"{_rel}/mut_{Next()}.bin";
        var content = new Content(_settings.Seed, rel);
        var p = Abs(rel);
        WriteFile(p, content, size, 256 << 10, false);
        Note(size, 0);
        var buffer = new byte[1 << 20];
        using (var f = OpenReadWrite(p))
        {
            for (var step = 0; step < 6; step++)
            {
                switch (_rng.Next(4))
                {
                    case 0 or 1:
                    {
                        // overwrite a block-aligned range in place
                        if (size < Content.BlockSize)
                        {
                            continue;
                        }

                        var blocks = size / Content.BlockSize;
                        var b0 = _rng.NextInt64(blocks);
                        var n = _rng.NextInt64(Math.Min(blocks - b0, 64)) + 1;
                        content.Bump(b0, b0 + n);
                        var (offset, length) = (b0 * Content.BlockSize, (int)(n * Content.BlockSize));
                        content.Fill(buffer.AsSpan(0, length), offset);
                        RandomAccess.Write(f.SafeFileHandle, buffer.AsSpan(0, length), offset);
                        Note(length, 0);
                        break;
                    }

                    case 2:
                    {
                        // append; the blocks it touches keep their generation, and a partial last block is rewritten whole as it is
                        var n = _rng.Next(256 << 10) + 1;
                        var start = size - (size % Content.BlockSize);
                        var length = (int)(size + n - start);
                        content.Fill(buffer.AsSpan(0, length), start);
                        RandomAccess.Write(f.SafeFileHandle, buffer.AsSpan(0, length), start);
                        Note(n, 0);
                        size += n;
                        break;
                    }

                    default:
                    {
                        // shrink
                        if (size <= Content.BlockSize)
                        {
                            continue;
                        }

                        size = _rng.NextInt64(size - 1) + 1;
                        f.SetLength(size);
                        content.Truncate(size);
                        break;
                    }
                }

                content.VerifyAt(f.SafeFileHandle, 0, size);
                var length2 = RandomAccess.GetLength(f.SafeFileHandle);
                if (length2 != size)
                {
                    throw new IOException($"\"{rel}\": size {length2} after step {step}, want {size}");
                }

                Note(0, size);
            }
        }

        // once more from a fresh open: what a later reader sees
        Note(0, VerifyFile(p, content, size));
        File.Delete(p);
        ExpectGone(p);
    }

    /// <summary>How many 4 KiB requests one randio operation makes; its time over this is the round trip of one.</summary>
    public const int RandIoCount = 64;

    /// <summary>
    /// What an application does with a file it keeps open (a database's pages, a document being saved, a zip's directory):
    /// small reads and writes at random offsets through one handle, 4 KiB at a time, one write in four, every read checked.
    /// A sequential copy hides the round trip of each request behind pipelining; this measures it.
    /// </summary>
    private void OpRandIo()
    {
        const int size = 4 << 20;
        var rel = $"{_rel}/rand_{Next()}.bin";
        var content = new Content(_settings.Seed, rel);
        var p = Abs(rel);
        WriteFile(p, content, size, 1 << 20, false);
        Note(size, 0);
        var buffer = new byte[Content.BlockSize];
        using (var f = OpenReadWrite(p))
        {
            const int blocks = size / Content.BlockSize;
            for (var i = 0; i < RandIoCount; i++)
            {
                var b = _rng.NextInt64(blocks);
                var offset = b * Content.BlockSize;
                if (i % 4 == 3)
                {
                    content.Bump(b, b + 1);
                    content.Fill(buffer, offset);
                    RandomAccess.Write(f.SafeFileHandle, buffer, offset);
                    Note(Content.BlockSize, 0);
                    continue;
                }

                content.VerifyAt(f.SafeFileHandle, offset, Content.BlockSize);
                Note(0, Content.BlockSize);
            }
        }

        File.Delete(p);
        ExpectGone(p);
    }

    /// <summary>Builds a nested tree, checks every listing, renames a folder and a file, and removes it all.</summary>
    private void OpTree()
    {
        var root = $"{_rel}/tree_{Next()}";
        var files = new List<(string Rel, long Size)>();
        var dirs = new List<string> { root };

        // three levels, two folders each, two or three files per folder
        for (var level = 0; level < 3; level++)
        {
            var next = new List<string>();
            foreach (var d in dirs)
            {
                Directory.CreateDirectory(Abs(d));
                var count = 2 + _rng.Next(2);
                for (var i = 0; i < count; i++)
                {
                    var f = (Rel: $"{d}/f{i}.dat", Size: (long)_rng.Next(32 << 10) + 1);
                    WriteFile(Abs(f.Rel), new Content(_settings.Seed, f.Rel), f.Size, 32 << 10, false);
                    Note(f.Size, 0);
                    files.Add(f);
                }

                if (level < 2)
                {
                    next.Add(d + "/a");
                    next.Add(d + "/b");
                }
            }

            dirs = next;
        }

        // every folder lists exactly what went in
        var want = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        foreach (var (rel, size) in files)
        {
            var d = rel[..rel.LastIndexOf('/')];
            if (!want.TryGetValue(d, out var entries))
            {
                want[d] = entries = new Dictionary<string, long>(StringComparer.Ordinal);
            }

            entries[rel[(rel.LastIndexOf('/') + 1)..]] = size;
        }

        foreach (var (d, entries) in want)
        {
            foreach (var sub in new[] { "a", "b" })
            {
                if (Directory.Exists(Abs(d + "/" + sub)))
                {
                    entries[sub] = -1;
                }
            }

            DiffListing(d, ListDir(Abs(d)), entries);
        }

        // a folder renamed, then a file inside one: readable at the new names, gone from the old
        var oldDir = root + "/a";
        var newDir = root + "/a-renamed";
        Directory.Move(Abs(oldDir), Abs(newDir));
        ExpectGone(Abs(oldDir));
        foreach (var (rel, size) in files.Where(f => f.Rel.StartsWith(oldDir + "/", StringComparison.Ordinal)))
        {
            // the content key stays the first path: the bytes don't change with the name
            var moved = newDir + rel[oldDir.Length..];
            try
            {
                Note(0, VerifyFile(Abs(moved), new Content(_settings.Seed, rel), size));
            }
            catch (IOException ex)
            {
                throw new IOException("after the folder rename: " + ex.Message, ex);
            }
        }

        var first = files[0];
        var renamed = first.Rel[..first.Rel.LastIndexOf('/')] + "/renamed.dat";
        File.Move(Abs(first.Rel), Abs(renamed));
        ExpectGone(Abs(first.Rel));
        try
        {
            Note(0, VerifyFile(Abs(renamed), new Content(_settings.Seed, first.Rel), first.Size));
        }
        catch (IOException ex)
        {
            throw new IOException("after the file rename: " + ex.Message, ex);
        }

        Directory.Delete(Abs(root), recursive: true);
        ExpectGone(Abs(root));
    }

    /// <summary>The client sees exactly the files this worker has published (every other operation cleans up after itself).</summary>
    private void OpList()
    {
        var want = _pubs.ToDictionary(p => p.Rel[(p.Rel.LastIndexOf('/') + 1)..], p => p.Size, StringComparer.Ordinal);
        Settle(() => DiffListing(_rel, ListDir(Abs(_rel)), want));
    }

    /// <summary>Checks a file some client published (or the corpus), whole or in parts.</summary>
    private void ReadShared(PubFile p)
    {
        var content = new Content(_settings.Seed, p.Rel);
        if (_rng.Next(2) == 0 || p.Size < 2 * Content.BlockSize)
        {
            Note(0, VerifyFile(Abs(p.Rel), content, p.Size));
            return;
        }

        using var f = OpenRead(Abs(p.Rel));
        for (var i = 0; i < 4; i++)
        {
            var n = Math.Min(_rng.Next(64 << 10) + 1, p.Size);
            var offset = _rng.NextInt64(p.Size - n + 1);
            content.VerifyAt(f.SafeFileHandle, offset, n);
            Note(0, n);
        }
    }

    /// <summary>Reads a file of the corpus the harness seeded on the host.</summary>
    private void OpCorpus() => ReadShared(Corpus[_rng.Next(Corpus.Count)]);

    /// <summary>Reads a file another client (or another worker of this one) published.</summary>
    private void OpCross()
    {
        var candidates = new List<PubFile>();
        foreach (var client in new DirectoryInfo(Abs("stress")).EnumerateDirectories())
        {
            if (Ignored(client.Name))
            {
                continue;
            }

            try
            {
                foreach (var worker in client.EnumerateDirectories())
                {
                    var rel = $"stress/{client.Name}/{worker.Name}";
                    if (rel == _rel)
                    {
                        continue;
                    }

                    foreach (var file in worker.EnumerateFiles())
                    {
                        if (TryParsePubName(file.Name, out var size))
                        {
                            candidates.Add(new PubFile($"{rel}/{file.Name}", size));
                        }
                    }
                }
            }
            catch (DirectoryNotFoundException)
            {
                // a client starting up, or one done and cleaned up
            }
        }

        if (candidates.Count == 0)
        {
            return;    // nobody has published yet
        }

        try
        {
            ReadShared(candidates[_rng.Next(candidates.Count)]);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // the publisher rotated it out between the listing and the read (its quota is bounded): a file that went is
            // fine, a wrong one is not
        }
    }
}

/// <summary>What <c>run</c> was told.</summary>
internal sealed record Settings(string Root, string Name, ulong Seed, TimeSpan Duration, int Workers, TimeSpan OpTimeout, int LargeMaxMb, int MaxPub);
