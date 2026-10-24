namespace LibOutlook.Tests;

/// <summary>
/// Outlook keeps a byte-range lock on the header of every file it has open (bytes 0-481 of a PST, 0-532 of an OST), which
/// makes an ordinary read of the header fail; the reader goes through a memory-mapped view, which Windows does not
/// apply such locks to.
/// </summary>
[TestClass]
public class LockedFileTests
{
    [TestMethod]
    public void Reads_a_file_whose_header_another_handle_has_locked()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("byte-range locks are mandatory on Windows only");
            return;
        }

        var temp = Path.Combine(Path.GetTempPath(), $"liboutlook-locked-{Guid.NewGuid():N}.pst");
        File.Copy(Fixtures.Path("unicode.pst"), temp);
        try
        {
            using var holder = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            holder.Lock(0, 482);

            using (var plain = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1))
            {
                Assert.ThrowsExactly<IOException>(() => plain.ReadExactly(new byte[564]), "an ordinary read of the header fails on the lock");
            }

            using var pst = PstFile.Open(temp);
            Assert.AreEqual("AnyFS fixture", pst.DisplayName);
            Assert.AreEqual("Top of Outlook data file", pst.TopFolder.Name);
            Assert.IsTrue(pst.TopFolder.Folders.Count > 0);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [TestMethod]
    public void Opens_a_file_other_handles_hold_for_writing()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"liboutlook-shared-{Guid.NewGuid():N}.pst");
        File.Copy(Fixtures.Path("unicode.pst"), temp);
        try
        {
            using var writer = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            using var pst = PstFile.Open(temp);
            Assert.AreEqual("AnyFS fixture", pst.DisplayName);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
