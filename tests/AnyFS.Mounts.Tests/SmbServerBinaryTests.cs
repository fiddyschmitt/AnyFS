using AnyFS.Mounts.Smb;

namespace AnyFS.Mounts.Tests;

/// <summary>
/// The unpacked SMB server (spec 8.2): one file per server version in the provider's cache folder, named by content,
/// written once, with other versions and half-written leftovers removed when a new one lands.
/// </summary>
[TestClass]
public class SmbServerBinaryTests
{
    private string _dir = "";

    [TestInitialize]
    public void Init() => _dir = Path.Combine(Path.GetTempPath(), "anyfs-tests", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static byte[] Bytes(byte seed, int length = 4096) => Enumerable.Range(0, length).Select(i => (byte)((i * 7 + seed) % 251)).ToArray();

    [TestMethod]
    public void UnpacksOnceAndNamesTheFileByItsContent()
    {
        var bytes = Bytes(1);
        var path = SmbServerBinary.Unpack(bytes, _dir);

        Assert.AreEqual(Directory.CreateDirectory(_dir).FullName, Path.GetDirectoryName(path));
        StringAssert.StartsWith(Path.GetFileName(path), SmbServerBinary.UnpackedPrefix);
        Assert.AreEqual(OperatingSystem.IsWindows(), path.EndsWith(".exe", StringComparison.Ordinal));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        if (!OperatingSystem.IsWindows())
        {
            Assert.IsTrue(File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute), "executable");
        }

        var stamp = DateTime.UtcNow.AddHours(-1);
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.AreEqual(path, SmbServerBinary.Unpack(bytes, _dir), "the same content lands in the same file");
        Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(path), "a whole copy is not written again");
        Assert.AreEqual(1, Directory.GetFiles(_dir).Length);
    }

    [TestMethod]
    public void OtherVersionsAndLeftoversAreRemoved()
    {
        var old = SmbServerBinary.Unpack(Bytes(1), _dir);
        var leftover = Path.Combine(_dir, SmbServerBinary.UnpackedPrefix + "0123456789ab.deadbeef.tmp");
        File.WriteAllText(leftover, "a write that never finished");
        var unrelated = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(unrelated, "not ours");

        var current = SmbServerBinary.Unpack(Bytes(2), _dir);

        Assert.AreNotEqual(old, current);
        Assert.IsTrue(File.Exists(current));
        Assert.IsFalse(File.Exists(old), "the previous version is removed");
        Assert.IsFalse(File.Exists(leftover), "a half-written temporary file is removed");
        Assert.IsTrue(File.Exists(unrelated), "other files are not ours to delete");
    }

    [TestMethod]
    public void ACopyStillInUseIsLeftForNextTime()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("only Windows refuses to delete a file that is open");
        }

        var old = SmbServerBinary.Unpack(Bytes(1), _dir);
        string current;
        using (new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            current = SmbServerBinary.Unpack(Bytes(2), _dir);
            Assert.IsTrue(File.Exists(current), "the new version is unpacked regardless");
            Assert.IsTrue(File.Exists(old), "a copy another anyfs still runs stays");
        }

        Assert.AreEqual(current, SmbServerBinary.Unpack(Bytes(2), _dir));
        Assert.IsFalse(File.Exists(old), "removed once it is free");
    }

    [TestMethod]
    public void ATruncatedCopyIsWrittenAgain()
    {
        var bytes = Bytes(3);
        var path = SmbServerBinary.Unpack(bytes, _dir);
        File.WriteAllBytes(path, bytes[..100]);

        Assert.AreEqual(path, SmbServerBinary.Unpack(bytes, _dir));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
    }
}
