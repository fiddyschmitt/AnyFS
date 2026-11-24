using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Text;
using AnyFS.Mounts.WinFsp;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using Microsoft.Extensions.Logging;

namespace AnyFS.Mounts.Tests;

/// <summary>Spec 8.1 and 16.1: the WinFsp provider through the real driver at \\anyfs\&lt;name&gt;, self-skipping where WinFsp isn't installed.</summary>
[TestClass]
public class WinFspMountTests
{
    private static readonly byte[] Medium = Enumerable.Range(0, 100_000).Select(i => (byte)(i * 31 % 253)).ToArray();

    // longer than two read-ahead fills (1 MiB each), with a tail that is not a whole fill
    private static readonly byte[] Big = Enumerable.Range(0, 2_500_000).Select(i => (byte)((i * 7 + i / 1000) % 251)).ToArray();
    private readonly List<IAsyncDisposable> _cleanup = [];

    [TestCleanup]
    public async Task Cleanup()
    {
        foreach (var d in Enumerable.Reverse(_cleanup))
        {
            await d.DisposeAsync();
        }
    }

    // case-insensitive like a Windows source; the real router also folds case when a source doesn't
    private static MemorySource Tree() => new MemorySource(caseInsensitive: true)
        .AddFile("readme.txt", "hello readme")
        .AddFile("docs/a.txt", "aaa")
        .AddFile("docs/sub/c.txt", "c")
        .AddFile("medium.bin", Medium, new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero))
        .AddFile("big.bin", Big)
        .AddFile("unicode/ünïcödé ✓ 日本語.txt", "unicode names")
        .AddDirectory("emptydir");

    private sealed record Collection(string Name) : ICollectionInfo
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    private static void RequireWinFsp()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("WinFsp is a Windows driver");
        }

        if (new WinFspMountProvider().UnavailableReason is { } reason)
        {
            Assert.Inconclusive(reason);
        }
    }

    private async Task<(IMount Mount, string Unc, MemorySource Source)> MountAsync(bool readOnly = false, Func<IVirtualFileSystem, IVirtualFileSystem>? wrap = null)
    {
        var name = "anyfs-test-" + Guid.NewGuid().ToString("N")[..8];
        var source = Tree();
        await source.InitialiseAsync(new TestSourceContext(name), CancellationToken.None);
        var provider = new WinFspMountProvider();
        var settings = (WinFspMountSettings)provider.CreateDefaultSettings(new Collection(name));
        var mount = provider.CreateMount(settings, new Collection(name));
        var context = new TestMountContext(name, "winfsp", logger: new ConsoleLogger());
        IVirtualFileSystem vfs = new SingleSourceVfs(source, name, readOnly);
        await mount.StartAsync(wrap?.Invoke(vfs) ?? vfs, context, CancellationToken.None);
        _cleanup.Add(mount);
        var unc = $@"\\anyfs\{name}";
        Assert.AreEqual(unc, mount.AccessPaths.Single().Value, "no drive letter: the UNC path is the only access path");
        Assert.IsInstanceOfType<MountOpenAction.FileManager>(mount.OpenAction);
        return (mount, unc, source);
    }

    [TestMethod]
    public async Task ListsReadsAndWritesThroughTheUncPath()
    {
        RequireWinFsp();
        var (_, unc, source) = await MountAsync();

        var names = Directory.GetFileSystemEntries(unc).Select(Path.GetFileName).ToList();
        CollectionAssert.AreEquivalent(new[] { "readme.txt", "docs", "medium.bin", "big.bin", "unicode", "emptydir" }, names);
        Assert.AreEqual("hello readme", File.ReadAllText(Path.Combine(unc, "readme.txt")));
        Assert.AreEqual("unicode names", File.ReadAllText(Path.Combine(unc, "unicode", "ünïcödé ✓ 日本語.txt")));
        Assert.AreEqual(100_000, new FileInfo(Path.Combine(unc, "medium.bin")).Length);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(Path.Combine(unc, "emptydir")).Length);

        using (var stream = new FileStream(Path.Combine(unc, "medium.bin"), FileMode.Open, FileAccess.Read))
        {
            stream.Seek(50_000, SeekOrigin.Begin);
            var window = new byte[1000];
            stream.ReadExactly(window);
            CollectionAssert.AreEqual(Medium[50_000..51_000], window, "a ranged read lands where it should");
        }

        CollectionAssert.AreEqual(Medium, File.ReadAllBytes(Path.Combine(unc, "medium.bin")));

        File.WriteAllText(Path.Combine(unc, "new.txt"), "written");
        Assert.AreEqual("written", Encoding.UTF8.GetString(source.GetFile("new.txt")!), "a write lands upstream");
        Directory.CreateDirectory(Path.Combine(unc, "made"));
        Assert.IsTrue(source.Exists("made"));
        File.Move(Path.Combine(unc, "new.txt"), Path.Combine(unc, "made", "moved.txt"));
        Assert.IsTrue(source.Exists("made/moved.txt"));
        Assert.IsFalse(source.Exists("new.txt"));
        File.AppendAllText(Path.Combine(unc, "made", "moved.txt"), " more");
        Assert.AreEqual("written more", Encoding.UTF8.GetString(source.GetFile("made/moved.txt")!));
        File.Delete(Path.Combine(unc, "made", "moved.txt"));
        Assert.IsFalse(source.Exists("made/moved.txt"));
        Directory.Delete(Path.Combine(unc, "made"));
        Assert.IsFalse(source.Exists("made"));
        Assert.ThrowsExactly<IOException>(() => Directory.Delete(Path.Combine(unc, "docs")), "a non-empty folder stays");
        Assert.IsTrue(source.Exists("docs/a.txt"));
    }

    [TestMethod]
    public async Task ReadOnlyMountRefusesWritesWithoutTheAttribute()
    {
        RequireWinFsp();
        var (_, unc, source) = await MountAsync(readOnly: true);
        Assert.IsFalse(File.GetAttributes(Path.Combine(unc, "readme.txt")).HasFlag(FileAttributes.ReadOnly), "the file is not read-only itself: a copy of it must not be");
        Assert.AreEqual("hello readme", File.ReadAllText(Path.Combine(unc, "readme.txt")), "reads are fine");
        try
        {
            File.AppendAllText(Path.Combine(unc, "readme.txt"), "x");
            Assert.Fail("a write to a file of a read-only mount must fail, attribute or not");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var copy = Path.Combine(Path.GetTempPath(), "anyfs-copy-" + Guid.NewGuid().ToString("N") + ".txt");
        File.Copy(Path.Combine(unc, "readme.txt"), copy);
        try
        {
            Assert.IsFalse(File.GetAttributes(copy).HasFlag(FileAttributes.ReadOnly), "the copy is an ordinary file");
        }
        finally
        {
            File.Delete(copy);
        }

        try
        {
            File.WriteAllText(Path.Combine(unc, "new.txt"), "x");
            Assert.Fail("a write on a read-only mount must fail");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        try
        {
            Directory.CreateDirectory(Path.Combine(unc, "made"));
            Assert.Fail("a folder on a read-only mount must fail");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        Assert.IsFalse(source.Exists("new.txt"));
        Assert.IsFalse(source.Exists("made"));
    }

    /// <summary>
    /// Small sequential reads are served from a per-handle read-ahead (WinFsp hands each application read to user
    /// mode as it is): the bytes must be right across the fills and at the tail, a seek must not be served from the
    /// wrong place, and a write through another handle must show to a fresh reader.
    /// </summary>
    [TestMethod]
    public async Task SmallSequentialReadsGoThroughTheReadAhead()
    {
        RequireWinFsp();
        var (_, unc, _) = await MountAsync();
        var path = Path.Combine(unc, "big.bin");

        // 1000-byte reads straight to the file system (no FileStream buffer): across two 1 MiB fills and a short tail
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1))
        {
            var all = new byte[Big.Length];
            var chunk = new byte[1000];
            var total = 0;
            int n;
            while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                Array.Copy(chunk, 0, all, total, n);
                total += n;
            }

            Assert.AreEqual(Big.Length, total, "the whole file arrives");
            CollectionAssert.AreEqual(Big, all, "sequential 1000-byte reads");
        }

        // seeks: back inside what was read ahead, far forward, back again, and across the end
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1))
        {
            var window = new byte[4096];
            stream.ReadExactly(window);
            stream.ReadExactly(window); // the second sequential read fills the read-ahead from 4096
            CollectionAssert.AreEqual(Big[4096..8192], window);
            stream.Seek(2_000_000, SeekOrigin.Begin);
            stream.ReadExactly(window);
            CollectionAssert.AreEqual(Big[2_000_000..2_004_096], window, "a forward seek");
            stream.Seek(5000, SeekOrigin.Begin);
            stream.ReadExactly(window);
            CollectionAssert.AreEqual(Big[5000..9096], window, "a seek back into the first fill");
            stream.Seek(-100, SeekOrigin.End);
            Assert.AreEqual(100, stream.Read(window, 0, window.Length), "a read across the end is short");
            CollectionAssert.AreEqual(Big[^100..], window[..100]);
            Assert.AreEqual(0, stream.Read(window, 0, window.Length), "and then the end");
        }

        // a write through one handle, then a fresh handle reads the region sequentially in small pieces
        var patch = Enumerable.Range(0, 3000).Select(i => (byte)(255 - (i % 256))).ToArray();
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, bufferSize: 1))
        {
            writer.Seek(1_500_000, SeekOrigin.Begin);
            writer.Write(patch);
            writer.Flush();
        }

        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1))
        {
            reader.Seek(1_499_000, SeekOrigin.Begin);
            var got = new byte[5000];
            for (var done = 0; done < got.Length; done += 500)
            {
                reader.ReadExactly(got, done, 500);
            }

            CollectionAssert.AreEqual(Big[1_499_000..1_500_000], got[..1000], "before the patch");
            CollectionAssert.AreEqual(patch, got[1000..4000], "the patch");
            CollectionAssert.AreEqual(Big[1_503_000..1_504_000], got[4000..], "after the patch");
        }
    }

    [TestMethod]
    public async Task AnOpenOnlyToLookAtAFileOpensNothingInTheSource()
    {
        RequireWinFsp();
        var counting = default(OpenCounting);
        var (_, unc, _) = await MountAsync(wrap: vfs => counting = new OpenCounting(vfs));
        var path = Path.Combine(unc, "big.bin");

        // FILE_READ_ATTRIBUTES, as the shell and search tools open what they only look at (a source may take long to open it)
        var handle = Native.CreateFileW(path, Native.FileReadAttributes, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        Assert.AreNotEqual(new IntPtr(-1), handle, $"opened for its attributes: error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        Assert.IsTrue(Native.GetFileSizeEx(handle, out var size), "and its size asked through the handle");
        Assert.IsTrue(Native.CloseHandle(handle));
        Assert.AreEqual(Big.Length, size, "the listing's size");
        Assert.AreEqual(0, counting!.Opens, "nothing asked of the source");

        // an open for its data is one
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1))
        {
            var start = new byte[1000];
            stream.ReadExactly(start);
            CollectionAssert.AreEqual(Big[..1000], start);
        }

        Assert.AreEqual(1, counting.Opens);
    }

    [TestMethod]
    public async Task ASupersedeWithNoAccessToTheDataStillReplacesTheFile()
    {
        RequireWinFsp();
        var (_, unc, source) = await MountAsync();
        var path = Path.Combine(unc, "medium.bin");
        Assert.AreEqual(Medium.Length, new FileInfo(path).Length);

        // FILE_SUPERSEDE with DELETE alone: the open asks for nothing of the data, and the overwrite comes all the same
        var status = Native.Supersede(@"\??\UNC\" + path.TrimStart('\\'));
        Assert.AreEqual(0, status, $"NTSTATUS 0x{status:X8}");
        Assert.AreEqual(0, (await source.StatAsync("medium.bin", CancellationToken.None))!.Length, "replaced");
    }

    private static class Native
    {
        public const uint FileReadAttributes = 0x80;

        private const uint Delete = 0x10000;
        private const uint Synchronize = 0x100000;
        private const uint CaseInsensitive = 0x40;
        private const uint FileSupersede = 0;
        private const uint FileSynchronousIoNonAlert = 0x20;
        private const uint FileNonDirectoryFile = 0x40;

        /// <summary>NtCreateFile with FILE_SUPERSEDE, which Win32's CreateFile cannot ask for: its NTSTATUS.</summary>
        public static int Supersede(string ntPath)
        {
            var name = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(ntPath);
            try
            {
                var unicode = new UnicodeString { Length = (ushort)(ntPath.Length * 2), MaximumLength = (ushort)(ntPath.Length * 2), Buffer = name };
                var attributes = new ObjectAttributes { Length = System.Runtime.InteropServices.Marshal.SizeOf<ObjectAttributes>(), ObjectName = System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf<UnicodeString>()), Attributes = CaseInsensitive };
                try
                {
                    System.Runtime.InteropServices.Marshal.StructureToPtr(unicode, attributes.ObjectName, false);
                    var status = NtCreateFile(out var handle, Delete | Synchronize, ref attributes, out _, IntPtr.Zero, 0, 7, FileSupersede, FileSynchronousIoNonAlert | FileNonDirectoryFile, IntPtr.Zero, 0);
                    if (status == 0)
                    {
                        CloseHandle(handle);
                    }

                    return status;
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(attributes.ObjectName);
                }
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(name);
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct UnicodeString
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct ObjectAttributes
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct IoStatusBlock
        {
            public IntPtr Status;
            public IntPtr Information;
        }

        [System.Runtime.InteropServices.DllImport("ntdll.dll")]
        private static extern int NtCreateFile(out IntPtr handle, uint access, ref ObjectAttributes attributes, out IoStatusBlock status, IntPtr allocationSize, uint fileAttributes, uint share, uint disposition, uint options, IntPtr eaBuffer, uint eaLength);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetFileSizeEx(IntPtr handle, out long size);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
    }

    /// <summary>A file system that counts what is opened in it.</summary>
    private sealed class OpenCounting(IVirtualFileSystem inner) : IVirtualFileSystem
    {
        private int _opens;

        public int Opens => Volatile.Read(ref _opens);

        public string CollectionName => inner.CollectionName;

        public bool ReadOnly => inner.ReadOnly;

        public bool CanWrite(string path) => inner.CanWrite(path);

        public Task<VfsEntry?> StatAsync(string path, CancellationToken ct) => inner.StatAsync(path, ct);

        public IAsyncEnumerable<VfsEntry> ListAsync(string path, CancellationToken ct) => inner.ListAsync(path, ct);

        public Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct)
        {
            Interlocked.Increment(ref _opens);
            return inner.OpenAsync(path, mode, ct);
        }

        public Task CreateDirectoryAsync(string path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);

        public Task DeleteAsync(string path, CancellationToken ct) => inner.DeleteAsync(path, ct);

        public Task RenameAsync(string fromPath, string toPath, bool replace, CancellationToken ct) => inner.RenameAsync(fromPath, toPath, replace, ct);

        public Task SetTimesAsync(string path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken ct) => inner.SetTimesAsync(path, created, modified, ct);

        public Task<VfsSpace?> GetSpaceAsync(string path, CancellationToken ct) => inner.GetSpaceAsync(path, ct);
    }

    /// <summary>A file system that counts what it is asked of a path and the files open in it, and whose answer about one path fails from the moment a file is opened there.</summary>
    private sealed class Watching(IVirtualFileSystem inner) : IVirtualFileSystem
    {
        private int _stats;
        private int _held;
        private volatile bool _failing;

        /// <summary>How often it was asked what a path is.</summary>
        public int Stats => Volatile.Read(ref _stats);

        /// <summary>The files open now.</summary>
        public int Held => Volatile.Read(ref _held);

        /// <summary>A path the source can no longer say anything of once a file has been opened at it, as one that stops answering then.</summary>
        public string? FailsOnceOpen { get; init; }

        public string CollectionName => inner.CollectionName;

        public bool ReadOnly => inner.ReadOnly;

        public bool CanWrite(string path) => inner.CanWrite(path);

        public Task<VfsEntry?> StatAsync(string path, CancellationToken ct)
        {
            Interlocked.Increment(ref _stats);
            if (_failing && string.Equals(VfsPath.Normalize(path), FailsOnceOpen, StringComparison.OrdinalIgnoreCase))
            {
                throw VfsException.Unavailable("the source stopped answering");
            }

            return inner.StatAsync(path, ct);
        }

        public IAsyncEnumerable<VfsEntry> ListAsync(string path, CancellationToken ct) => inner.ListAsync(path, ct);

        public async Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct)
        {
            var file = await inner.OpenAsync(path, mode, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _held);
            if (string.Equals(VfsPath.Normalize(path), FailsOnceOpen, StringComparison.OrdinalIgnoreCase))
            {
                _failing = true;
            }

            return new HeldFile(file, this);
        }

        public Task CreateDirectoryAsync(string path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);

        public Task DeleteAsync(string path, CancellationToken ct) => inner.DeleteAsync(path, ct);

        public Task RenameAsync(string fromPath, string toPath, bool replace, CancellationToken ct) => inner.RenameAsync(fromPath, toPath, replace, ct);

        public Task SetTimesAsync(string path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken ct) => inner.SetTimesAsync(path, created, modified, ct);

        public Task<VfsSpace?> GetSpaceAsync(string path, CancellationToken ct) => inner.GetSpaceAsync(path, ct);

        private sealed class HeldFile(IVfsFile file, Watching owner) : IVfsFile
        {
            private int _released;

            public long Length => file.Length;

            public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct) => file.ReadAsync(offset, buffer, ct);

            public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct) => file.WriteAsync(offset, data, ct);

            public ValueTask SetLengthAsync(long length, CancellationToken ct) => file.SetLengthAsync(length, ct);

            public ValueTask FlushAsync(CancellationToken ct) => file.FlushAsync(ct);

            public ValueTask AbandonAsync()
            {
                Release();
                return file.AbandonAsync();
            }

            public ValueTask DisposeAsync()
            {
                Release();
                return file.DisposeAsync();
            }

            private void Release()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    Interlocked.Decrement(ref owner._held);
                }
            }
        }
    }

    /// <summary>
    /// What a file is, is asked once for an open, and not again for what is written through the handle. An open asked
    /// three times (WinFsp's own look at the name, the open, and the info the open answers with), a folder's listing
    /// five, and every write once more (the review of 21-Nov-2026, P3).
    /// </summary>
    [TestMethod]
    public async Task AnOpenAsksWhatTheFileIsOnceAndAWriteNotAtAll()
    {
        RequireWinFsp();
        var watching = default(Watching);
        var (_, unc, source) = await MountAsync(wrap: vfs => watching = new Watching(vfs));

        // each on a file nothing has looked at yet: Windows keeps what it was told of one for a second
        var asked = watching!.Stats;
        _ = File.GetAttributes(Path.Combine(unc, "docs", "a.txt"));
        Assert.AreEqual(1, watching.Stats - asked, "a file's attributes");

        asked = watching.Stats;
        Assert.AreEqual("c", File.ReadAllText(Path.Combine(unc, "docs", "sub", "c.txt")));
        Assert.AreEqual(1, watching.Stats - asked, "a file read whole");

        asked = watching.Stats;
        Assert.AreEqual(1, Directory.GetFileSystemEntries(Path.Combine(unc, "unicode")).Length);
        Assert.AreEqual(2, watching.Stats - asked, "a folder listed: the folder, and the one it is in for its '..'");

        var chunk = new byte[4096];
        using (var stream = new FileStream(Path.Combine(unc, "docs", "written.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1))
        {
            asked = watching.Stats;
            for (var i = 0; i < 50; i++)
            {
                stream.Write(chunk);
            }

            stream.Flush(flushToDisk: true);
            Assert.AreEqual(0, watching.Stats - asked, "fifty writes and a flush");
            Assert.AreEqual(50 * 4096, stream.Length, "and the handle says how long the file is");
        }

        Assert.AreEqual(50 * 4096, source.GetFile("docs/written.bin")!.Length);
        Assert.AreEqual(50 * 4096, new FileInfo(Path.Combine(unc, "docs", "written.bin")).Length);
    }

    /// <summary>
    /// An open leaves no file open behind it. Having opened the file, the open asked once more what it was, and where
    /// that failed (a source that had just stopped answering) the caller was refused and so never closed what it did
    /// not have: the file stayed open in the source until the volume went. A new file was the same.
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "a file that is there")]
    [DataRow(true, DisplayName = "a new file")]
    public async Task AnOpenLeavesNoFileOpenBehindIt(bool create)
    {
        RequireWinFsp();
        var watching = default(Watching);
        var name = create ? "new.txt" : "a.txt";
        var (_, unc, source) = await MountAsync(wrap: vfs => watching = new Watching(vfs) { FailsOnceOpen = "docs/" + name });
        var done = false;
        try
        {
            if (create)
            {
                using var stream = new FileStream(Path.Combine(unc, "docs", name), FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1);
                stream.Write("made"u8);
                stream.Write(" here"u8);
            }
            else
            {
                Assert.AreEqual("aaa", File.ReadAllText(Path.Combine(unc, "docs", name)));
            }

            done = true;
        }
        catch (IOException)
        {
            // refused, as it was: what the open left behind is asked below
        }

        // the close comes a moment after the handle has gone
        for (var i = 0; i < 50 && watching!.Held > 0; i++)
        {
            await Task.Delay(100);
        }

        Assert.AreEqual(0, watching!.Held, "nothing is left open in the source");
        Assert.IsTrue(done, "and the open went through: once the file is open nothing more is asked that could fail it");
        if (create)
        {
            Assert.AreEqual("made here", Encoding.UTF8.GetString(source.GetFile("docs/" + name)!), "what was written landed, though the source could say nothing of the file meanwhile");
        }
    }

    /// <summary>
    /// A handle that wrote says of the file what it knows itself: how long the file is now, and the times it was given
    /// (a copy in sets the file's time through the handle it wrote with, and reads it back).
    /// </summary>
    [TestMethod]
    public async Task AHandleThatWroteSaysWhatItKnowsOfTheFile()
    {
        RequireWinFsp();
        var (_, unc, source) = await MountAsync();
        var path = Path.Combine(unc, "docs", "copied.bin");
        var time = new DateTime(2020, 2, 2, 2, 2, 2, DateTimeKind.Utc);
        var local = Path.Combine(Path.GetTempPath(), "anyfs-copy-in-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(local, new byte[3000]);
        File.SetLastWriteTimeUtc(local, time);
        try
        {
            File.Copy(local, path);
        }
        finally
        {
            File.Delete(local);
        }

        Assert.AreEqual(time, File.GetLastWriteTimeUtc(path), "the copy's time, as Windows was told through the handle");
        Assert.AreEqual(3000, new FileInfo(path).Length);
        Assert.AreEqual(new DateTimeOffset(time), (await source.StatAsync("docs/copied.bin", CancellationToken.None))!.Modified, "and as the source has it");

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, bufferSize: 1))
        {
            var handle = stream.SafeFileHandle.DangerousGetHandle();
            stream.SetLength(1000);
            Assert.IsTrue(Native.GetFileSizeEx(handle, out var size));
            Assert.AreEqual(1000, size, "cut through the handle");
            stream.Seek(0, SeekOrigin.End);
            stream.Write(new byte[500]);
            Assert.IsTrue(Native.GetFileSizeEx(handle, out size));
            Assert.AreEqual(1500, size, "and written on");
            Assert.IsTrue(File.GetLastWriteTimeUtc(path) > time.AddYears(1), "a write is the file's last write");
        }

        Assert.AreEqual(1500, source.GetFile("docs/copied.bin")!.Length);
        Assert.AreEqual(1500, new FileInfo(path).Length);
    }

    /// <summary>A handle that has written nothing still learns of a change made elsewhere: the source is asked again when Windows asks again, as it was.</summary>
    [TestMethod]
    public async Task AHandleThatOnlyLooksLearnsThatTheFileGrewElsewhere()
    {
        RequireWinFsp();
        var (_, unc, source) = await MountAsync();
        var handle = Native.CreateFileW(Path.Combine(unc, "docs", "a.txt"), Native.FileReadAttributes, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        Assert.AreNotEqual(new IntPtr(-1), handle);
        try
        {
            Assert.IsTrue(Native.GetFileSizeEx(handle, out var size));
            Assert.AreEqual(3, size);
            source.AddFile("docs/a.txt", "aaa, and more");

            // what Windows was told of the file stands for a second
            await Task.Delay(1500);
            Assert.IsTrue(Native.GetFileSizeEx(handle, out size));
            Assert.AreEqual(13, size, "its length as it is now");
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>A file system whose listing of one folder fails as given, the rest as the inner one has it.</summary>
    private sealed class FailingListing(IVirtualFileSystem inner, string folder, Func<Exception> failure) : IVirtualFileSystem
    {
        public string CollectionName => inner.CollectionName;

        public bool ReadOnly => inner.ReadOnly;

        public bool CanWrite(string path) => inner.CanWrite(path);

        public Task<VfsEntry?> StatAsync(string path, CancellationToken ct) => inner.StatAsync(path, ct);

        public IAsyncEnumerable<VfsEntry> ListAsync(string path, CancellationToken ct)
            => string.Equals(VfsPath.Normalize(path), folder, StringComparison.OrdinalIgnoreCase) ? Failing() : inner.ListAsync(path, ct);

        private async IAsyncEnumerable<VfsEntry> Failing()
        {
            await Task.Yield();
            throw failure();
#pragma warning disable CS0162 // unreachable: an iterator needs a yield
            yield break;
#pragma warning restore CS0162
        }

        public Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct) => inner.OpenAsync(path, mode, ct);

        public Task CreateDirectoryAsync(string path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);

        public Task DeleteAsync(string path, CancellationToken ct) => inner.DeleteAsync(path, ct);

        public Task RenameAsync(string fromPath, string toPath, bool replace, CancellationToken ct) => inner.RenameAsync(fromPath, toPath, replace, ct);

        public Task SetTimesAsync(string path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken ct) => inner.SetTimesAsync(path, created, modified, ct);

        public Task<VfsSpace?> GetSpaceAsync(string path, CancellationToken ct) => inner.GetSpaceAsync(path, ct);
    }

    [TestMethod]
    public async Task AFolderWhoseListingFailsIsAnErrorNotAnEmptyFolder()
    {
        // until 22-Nov-2026 a listing that failed (a source throttled, timed out, signed out) was answered as a folder with
        // nothing in it: "This folder is empty" in Explorer, and a search went past the subtree without a word
        RequireWinFsp();
        var (_, unc, _) = await MountAsync(wrap: vfs => new FailingListing(vfs, "docs", () => VfsException.Unavailable("the source did not answer")));
        Assert.AreEqual(1, Directory.GetFileSystemEntries(Path.Combine(unc, "unicode")).Length, "a folder that lists is listed");
        var unavailable = Assert.Throws<IOException>(() => Directory.GetFileSystemEntries(Path.Combine(unc, "docs")));
        Assert.AreEqual(unchecked((int)0x80070015), unavailable.HResult, "ERROR_NOT_READY, as every other call on a source that does not answer: " + unavailable.Message);
        Assert.AreEqual("aaa", File.ReadAllText(Path.Combine(unc, "docs", "a.txt")), "the folder's files still open by name");
    }

    [TestMethod]
    public async Task AFolderWhoseListingBreaksUnexpectedlyIsAnErrorToo()
    {
        RequireWinFsp();
        var (_, unc, _) = await MountAsync(wrap: vfs => new FailingListing(vfs, "docs", () => new InvalidOperationException("a bug in a source")));
        Assert.Throws<IOException>(() => Directory.GetFileSystemEntries(Path.Combine(unc, "docs")));
        Assert.AreEqual(1, Directory.GetFileSystemEntries(Path.Combine(unc, "unicode")).Length, "and the volume serves on");
    }

    [TestMethod]
    public async Task UnmountRemovesTheVolume()
    {
        RequireWinFsp();
        var (mount, unc, _) = await MountAsync();
        Assert.IsTrue(Directory.Exists(unc));
        await mount.StopAsync(CancellationToken.None);
        Assert.IsFalse(Directory.Exists(unc), "the prefix is gone once the mount stops");
        Assert.AreEqual(0, mount.AccessPaths.Count);
    }

    [TestMethod]
    public void SettingsAndDriveLetters()
    {
        Assert.AreEqual("X", WinFspMountProvider.NormaliseLetter("x"));
        Assert.AreEqual("X", WinFspMountProvider.NormaliseLetter(" X: "));
        Assert.IsNull(WinFspMountProvider.NormaliseLetter("C"), "C is taken");
        Assert.IsNull(WinFspMountProvider.NormaliseLetter(""));
        Assert.IsNull(WinFspMountProvider.NormaliseLetter("XY"));
        var provider = new WinFspMountProvider();
        Assert.AreEqual(0, provider.Validate(new WinFspMountSettings()).Count());
        Assert.IsTrue(provider.Validate(new WinFspMountSettings { DriveLetter = "1" }).Any(m => m.IsError));
        Assert.AreEqual(OperatingSystem.IsWindows() && WinFspRuntime.IsInstalled() ? "Upgrade WinFsp driver" : "Install WinFsp driver", provider.InstallText);
        Assert.IsTrue(provider.InstallDetail.Contains("winget install") || provider.InstallDetail.Contains("bundled"), provider.InstallDetail);
        CollectionAssert.AreEqual(new[] { "/i", @"C:\t\winfsp.msi", "/qn", "/norestart", "/l*v", @"C:\t\winfsp.log" }, WinFspInstaller.Arguments(@"C:\t\winfsp.msi", @"C:\t\winfsp.log").ToList());
        StringAssert.Contains(WinFspInstaller.Describe(1602, installed: false), "cancelled");
        StringAssert.Contains(WinFspInstaller.Describe(1618, installed: false), "in progress");
        StringAssert.Contains(WinFspInstaller.Describe(1638, installed: true), "already installed");
        StringAssert.Contains(WinFspInstaller.Describe(0, installed: false), "still not found");
        StringAssert.Contains(WinFspInstaller.Describe(1234, installed: false), "1234");
    }

    /// <summary>Fsp.Interop.Api.CheckVersion's rule: the driver's major equals the layer's, its minor is at least the layer's.</summary>
    [TestMethod]
    public void DriverVersionRuleIsWinFspsOwn()
    {
        var required = new Version(2, 2, 26286);
        Assert.AreEqual(required, WinFspRuntime.Required);
        Assert.IsTrue(WinFspRuntime.Accepts(new Version(2, 2, 26286), required));
        Assert.IsTrue(WinFspRuntime.Accepts(new Version(2, 3, 27000), required), "a newer 2.x driver serves an older layer");
        Assert.IsFalse(WinFspRuntime.Accepts(new Version(2, 1, 25156), required), "an older minor is 'incorrect dll version': a 2.1 install is asked to upgrade");
        Assert.IsFalse(WinFspRuntime.Accepts(new Version(2, 0, 23075), required));
        Assert.IsFalse(WinFspRuntime.Accepts(new Version(1, 12, 22339), required));
        Assert.IsFalse(WinFspRuntime.Accepts(new Version(3, 0, 1), required));
        StringAssert.Contains(WinFspRuntime.Explain(new TypeLoadException("incorrect dll version (need 2.2, have 2.1)")), "need 2.2, have 2.1");
        StringAssert.Contains(WinFspRuntime.Explain(new TypeInitializationException("Fsp.Interop.Api", new DllNotFoundException("cannot load winfsp-x64.dll"))), "not installed");
        StringAssert.Contains(WinFspRuntime.Explain(new TargetInvocationException(new TypeInitializationException("Fsp.Interop.Api", new ArgumentException("The path is empty.", "path")))), "file version");
        StringAssert.Contains(WinFspRuntime.Explain(new FileNotFoundException("Could not load file or assembly 'winfsp-msil'")), "winfsp-msil");
        if (OperatingSystem.IsWindows() && WinFspRuntime.InstalledVersion() is { } installed)
        {
            Console.WriteLine($"installed WinFsp {installed}; problem: {WinFspRuntime.Problem() ?? "none"}");
            Assert.AreEqual(WinFspRuntime.Accepts(installed, required), WinFspRuntime.Problem() is null);
        }
    }

    /// <summary>The copy the plugin carries for single-file hosts is the layer this build links, and the installer constant follows it.</summary>
    [TestMethod]
    public void InteropCopyIsTheLayerThisBuildLinks()
    {
        Assert.IsTrue(WinFspInterop.HasEmbedded(), "AnyFS.Mounts.WinFsp embeds winfsp-msil.dll");
        var dir = Path.Combine(Path.GetTempPath(), "anyfs-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = WinFspInterop.Extract(dir);
            Assert.AreEqual(path, WinFspInterop.Extract(dir), "the same content lands in the same file");
            Assert.AreEqual(WinFspInterop.AssemblyName, AssemblyName.GetAssemblyName(path).Name);
            var copy = FileVersionInfo.GetVersionInfo(path);
            var copyVersion = new Version(copy.FileMajorPart, copy.FileMinorPart, copy.FileBuildPart);
            Assert.AreEqual(Version.Parse(WinFspInstaller.Version), copyVersion, "WinFspInstaller.Version follows the winfsp.net package in Directory.Packages.props");
            var linked = typeof(Fsp.FileSystemHost).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
            Assert.IsNotNull(linked);
            Assert.AreEqual(Version.Parse(linked), copyVersion, "the embedded copy is the package the tests link");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>One copy per version: an older build's layer and a half-written temporary file go when the current one lands.</summary>
    [TestMethod]
    public void OtherInteropCopiesAreRemoved()
    {
        Assert.IsTrue(WinFspInterop.HasEmbedded(), "AnyFS.Mounts.WinFsp embeds winfsp-msil.dll");
        var dir = Path.Combine(Path.GetTempPath(), "anyfs-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var stale = Path.Combine(dir, WinFspInterop.AssemblyName + "-000000000000.dll");
            var leftover = Path.Combine(dir, WinFspInterop.AssemblyName + "-000000000000.dll.deadbeef.tmp");
            var unrelated = Path.Combine(dir, "notes.txt");
            File.WriteAllText(stale, "an older build's layer");
            File.WriteAllText(leftover, "a write that never finished");
            File.WriteAllText(unrelated, "not ours");

            var path = WinFspInterop.Extract(dir);

            Assert.IsTrue(File.Exists(path));
            Assert.IsFalse(File.Exists(stale), "the older copy is removed");
            Assert.IsFalse(File.Exists(leftover), "the temporary file is removed");
            Assert.IsTrue(File.Exists(unrelated), "other files are not ours to delete");
            CollectionAssert.AreEquivalent(new[] { Path.GetFileName(path), "notes.txt" }, Directory.GetFiles(dir).Select(Path.GetFileName).ToArray());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// WinFsp sends no close for what is open as the volume goes, so nothing closed those files: a source that takes a file
    /// at its close (SharePoint's upload) never had it, and nothing said so (review of 21-Nov-2026, R3). What is open then is
    /// closed by the mount: a file still being written is given up, half a file being no file, and the rest are closed.
    /// </summary>
    [TestMethod]
    public async Task WhatIsOpenAsTheVolumeGoesIsClosedAndAFileStillBeingWrittenIsGivenUp()
    {
        RequireWinFsp();
        ClosesTold told = null!;
        var (mount, unc, _) = await MountAsync(wrap: vfs => told = new ClosesTold(vfs));
        var writing = new FileStream(Path.Combine(unc, "half.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite, 1, FileOptions.None);
        var reading = new FileStream(Path.Combine(unc, "readme.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.None);
        try
        {
            writing.Write(Medium, 0, 50_000);
            writing.Flush();
            Assert.AreEqual((int)'h', reading.ReadByte());

            await mount.StopAsync(CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "given up" }, told.Of("half.bin"), "still being written: " + string.Join(", ", told.Of("half.bin")));
            CollectionAssert.AreEqual(new[] { "closed" }, told.Of("readme.txt"), "open for reading: " + string.Join(", ", told.Of("readme.txt")));
        }
        finally
        {
            foreach (var stream in new[] { writing, reading })
            {
                try
                {
                    stream.Dispose();
                }
                catch (IOException)
                {
                }
            }
        }
    }

    /// <summary>
    /// A file its writer has closed is kept though the volume goes before WinFsp's own close of it comes: Windows holds
    /// that back while anything still refers to the file (here a mapped view, as a virus scanner or the cache does), and
    /// the volume's end drops it (review of 21-Nov-2026, R3: quitting right after a copy, the copy was never uploaded).
    /// </summary>
    [TestMethod]
    public async Task AFileItsWriterClosedIsKeptThoughTheVolumeGoesBeforeItsLastClose()
    {
        RequireWinFsp();
        ClosesTold told = null!;
        var (mount, unc, source) = await MountAsync(wrap: vfs => told = new ClosesTold(vfs));
        var stream = new FileStream(Path.Combine(unc, "copied.bin"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.None);
        MemoryMappedFile? map = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            stream.Write(Medium, 0, Medium.Length);
            stream.Flush();
            map = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
            view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            Assert.AreEqual(Medium[10], view.ReadByte(10));
            stream.Dispose();
            await Task.Delay(1500);
            Assert.AreEqual(0, told.Of("copied.bin").Length, "the view holds WinFsp's close back: " + string.Join(", ", told.Of("copied.bin")));

            await mount.StopAsync(CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "closed" }, told.Of("copied.bin"), "closed by its writer, so kept: " + string.Join(", ", told.Of("copied.bin")));
            CollectionAssert.AreEqual(Medium, source.GetFile("copied.bin"));
        }
        finally
        {
            view?.Dispose();
            map?.Dispose();
            stream.Dispose();
        }
    }

    /// <summary>
    /// A file deleted as it closes (a copy that was cancelled, a temporary file) is given up before it is deleted: closed,
    /// a source that uploads at the close would upload it first, to delete it next.
    /// </summary>
    [TestMethod]
    public async Task AFileDeletedAsItClosesIsGivenUpNotKeptFirst()
    {
        RequireWinFsp();
        ClosesTold told = null!;
        var (_, unc, source) = await MountAsync(wrap: vfs => told = new ClosesTold(vfs));
        using (var stream = new FileStream(Path.Combine(unc, "cancelled.bin"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.DeleteOnClose))
        {
            stream.Write(Medium, 0, 50_000);
        }

        var until = Stopwatch.StartNew();
        while (told.Of("cancelled.bin").Length == 0 && until.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(50);
        }

        CollectionAssert.AreEqual(new[] { "given up" }, told.Of("cancelled.bin"), string.Join(", ", told.Of("cancelled.bin")));
        Assert.IsFalse(File.Exists(Path.Combine(unc, "cancelled.bin")));
        Assert.IsNull(await source.StatAsync("cancelled.bin", CancellationToken.None));
    }

    /// <summary>The provider's log lines into the test output.</summary>
    private sealed class ConsoleLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Console.WriteLine($"[server {logLevel}] {formatter(state, exception)}{(exception is null ? "" : " " + exception.Message)}");
    }
}
