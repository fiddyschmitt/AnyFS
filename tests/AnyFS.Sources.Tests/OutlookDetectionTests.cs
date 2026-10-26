using System.Text;
using AnyFS.Sources.Microsoft365;
using AnyFS.Sources.Outlook;
using Microsoft.Win32;

namespace AnyFS.Sources.Tests;

/// <summary>
/// The Detect button's reading of classic Outlook's profiles (<see cref="OutlookDetection"/>), from what Spike 17 read
/// in the lab's Outlook (26-Oct-2026), apart from the registry; and on Windows the registry walk itself, over a key of
/// the test's own.
/// </summary>
[TestClass]
public sealed class OutlookDetectionTests
{
    private string _folder = null!;

    [TestInitialize]
    public void Start()
    {
        _folder = Path.Combine(Path.GetTempPath(), "anyfs-detect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [TestCleanup]
    public void Stop() => Directory.Delete(_folder, recursive: true);

    private static Dictionary<string, string> Section(params (string Tag, string Value)[] values) => values.ToDictionary(v => v.Tag, v => v.Value, StringComparer.OrdinalIgnoreCase);

    private string Touch(string name)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, [0]);
        return path;
    }

    [TestMethod]
    public void TheLabsProfileGivesItsMailboxItsOfflineCopyAndItsArchives()
    {
        // the sections of the lab's profile that hold what Detect reads, its files made here
        var ost = Touch("anyfs-tester@y0w7606wv.onmicrosoft.com.ost");
        var nst = Touch("anyfs-tester@y0w7606wv.onmicrosoft.com(2).nst");
        var recurrence = Touch("fixture-recurrence-061014.pst");
        var smime = Touch("fixture-smime.pst");
        var profile = new OutlookProfile("Outlook",
        [
            Section((OutlookDetection.NameTag, "Outlook Address Book")),
            Section((OutlookDetection.ArchiveTag, recurrence), (OutlookDetection.NameTag, "AnyFS recurrence fixture")),
            Section((OutlookDetection.SmtpTag, "SMTP:anyfs-tester@y0w7606wv.onmicrosoft.com"), (OutlookDetection.OfflineTag, ost), (OutlookDetection.NameTag, "anyfs-tester@y0w7606wv.onmicrosoft.com"),
                (OutlookDetection.AutodiscoverTag, "https://outlook.office365.com/autodiscover/autodiscover.xml")),
            Section((OutlookDetection.SmtpTag, "anyfs-tester@y0w7606wv.onmicrosoft.com"), (OutlookDetection.OfflineTag, nst), (OutlookDetection.NameTag, "anyfs-tester@y0w7606wv.onmicrosoft.com")),
            Section((OutlookDetection.SmtpTag, "SMTP:anyfs-tester@y0w7606wv.onmicrosoft.com"), (OutlookDetection.NameTag, "Microsoft Exchange Transport")),
            Section((OutlookDetection.ArchiveTag, smime), (OutlookDetection.NameTag, "AnyFS S/MIME fixture")),
            Section((OutlookDetection.ArchiveTag, Path.Combine(_folder, "gone.pst")), (OutlookDetection.NameTag, "A file since deleted")),
        ]);

        var found = OutlookDetection.Find(new DetectionPlaces([profile], [_folder], "someone.else@contoso.com"));
        CollectionAssert.AreEqual(new[]
        {
            "Mailbox anyfs-tester@y0w7606wv.onmicrosoft.com (Exchange Online)",
            $"Offline copy of anyfs-tester@y0w7606wv.onmicrosoft.com ({ost})",
            $"Data file AnyFS recurrence fixture ({recurrence})",
            $"Data file AnyFS S/MIME fixture ({smime})",
        }, found.Select(f => f.Label).ToList(), "the group's .nst, the transport's repeat, a missing file, the folder's files (named already) and the Windows address (a profile has a mailbox) are left out");

        var mailbox = (OutlookSettings)found[0].Settings;
        Assert.AreEqual("anyfs-tester@y0w7606wv.onmicrosoft.com", mailbox.Path);
        Assert.AreEqual(OutlookSettings.WebSession, mailbox.SignInMethod);
        Assert.AreEqual(MicrosoftCloud.GlobalName, mailbox.Cloud);
        Assert.AreEqual(OutlookKind.Mailbox, mailbox.Kind);
        Assert.AreEqual(ost, ((OutlookSettings)found[1].Settings).Path);
        Assert.AreEqual(OutlookKind.DataFile, ((OutlookSettings)found[1].Settings).Kind);
        var plugin = new OutlookPlugin();
        Assert.IsTrue(found.All(f => !plugin.Validate(f.Settings).Any(m => m.IsError)), "each is ready to use as it is");
    }

    [TestMethod]
    public void TheAutodiscoverAddressSaysWhichExchange()
    {
        var gccOst = Touch("jane@agency.gov.ost");
        var labOst = Touch("ben@contoso.com.ost");
        var gcc = Section((OutlookDetection.SmtpTag, "SMTP:Jane@Agency.gov"), (OutlookDetection.OfflineTag, gccOst), (OutlookDetection.AutodiscoverTag, "https://autodiscover-s.office365.us/autodiscover/autodiscover.xml"));
        var server = Section((OutlookDetection.SmtpTag, "SMTP:ben@contoso.com"), (OutlookDetection.OfflineTag, labOst), (OutlookDetection.AutodiscoverTag, "https://mail.contoso.com/autodiscover/autodiscover.xml"));
        var found = OutlookDetection.Find(new DetectionPlaces([new OutlookProfile("Work", [gcc]), new OutlookProfile("Lab", [server])], [], null));
        CollectionAssert.AreEqual(new[]
        {
            "Mailbox jane@agency.gov (Exchange Online, US Government (GCC High))",
            "Mailbox ben@contoso.com on mail.contoso.com (Exchange server, your Windows sign-in)",
            $"Offline copy of jane@agency.gov ({gccOst})",
            $"Offline copy of ben@contoso.com ({labOst})",
        }, found.Select(f => f.Label).ToList(), "every profile's mailboxes first, then their files");

        var online = (OutlookSettings)found[0].Settings;
        Assert.AreEqual("jane@agency.gov", online.Path);
        Assert.AreEqual(MicrosoftCloud.UsGovernmentName, online.Cloud);
        Assert.AreSame(MicrosoftCloud.UsGovernment, online.KnownCloud);

        var onPremises = (OutlookSettings)found[1].Settings;
        Assert.AreEqual(OutlookKind.Server, onPremises.Kind);
        Assert.AreEqual("https://mail.contoso.com/EWS/Exchange.asmx", onPremises.EwsUrl!.AbsoluteUri);
        Assert.AreEqual(OutlookSignIn.WindowsCurrent, onPremises.EffectiveSignIn);

        Assert.AreSame(MicrosoftCloud.Global, OutlookDetection.CloudOf("outlook.office365.com"));
        Assert.AreSame(MicrosoftCloud.Global, OutlookDetection.CloudOf("autodiscover-s.outlook.com"));
        Assert.IsNull(OutlookDetection.CloudOf("autodiscover.contoso.com"));
        Assert.IsNull(OutlookDetection.CloudOf("notoffice365.us.contoso.com"), "a host only named like it");
    }

    [TestMethod]
    public void WithNoProfileTheWindowsAddressAndOutlooksFoldersStillGiveSomething()
    {
        // new Outlook keeps no profile; an archive sits in Outlook Files
        var archive = Touch("archive.pst");
        Touch("notes.txt");
        var found = OutlookDetection.Find(new DetectionPlaces([], [_folder, Path.Combine(_folder, "missing")], "Jane@Contoso.com"));
        CollectionAssert.AreEqual(new[] { "Mailbox jane@contoso.com (your Windows sign-in's address, on Exchange Online)", $"Data file archive.pst ({_folder})" }, found.Select(f => f.Label).ToList());
        Assert.AreEqual(archive, ((OutlookSettings)found[1].Settings).Path);
        Assert.AreEqual(0, OutlookDetection.Find(new DetectionPlaces([], [], null)).Count, "a local account has no address, and nothing else is there");
        Assert.IsNull(OutlookDetection.Address("not an address"));
        Assert.AreEqual("jane@contoso.com", OutlookDetection.Address(" smtp:Jane@Contoso.com "));
    }

    [TestMethod]
    public void TheRegistryIsReadAsOutlookWritesIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("classic Outlook's profiles are in the Windows registry");
            return;
        }

        // a key of the test's own, shaped as Outlook's Software\Microsoft\Office, deleted after
        var root = @"Software\AnyFS-tests-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var outlook = Registry.CurrentUser.CreateSubKey(root + @"\16.0\Outlook"))
            {
                outlook.SetValue("DefaultProfile", "Work");
                using (var other = outlook.CreateSubKey(@"Profiles\Another"))
                using (var archive = other.CreateSubKey("772d19c56563344c9603cd0f9b4c90d1"))
                {
                    archive.SetValue(OutlookDetection.ArchiveTag, Utf16(@"C:\Mail\other.pst"), RegistryValueKind.Binary);
                }

                using var work = outlook.CreateSubKey(@"Profiles\Work");
                using (var store = work.CreateSubKey("4a963888f41e424bac619aa506bc1d82"))
                {
                    store.SetValue(OutlookDetection.SmtpTag, Utf16("SMTP:jane@contoso.com"), RegistryValueKind.Binary);
                    store.SetValue(OutlookDetection.AutodiscoverTag, Utf16("https://outlook.office365.com/autodiscover/autodiscover.xml"), RegistryValueKind.Binary);
                    store.SetValue("00036601", new byte[] { 0x84, 0x09, 0, 0 }, RegistryValueKind.Binary);
                }

                using (work.CreateSubKey("9375CFF0413111d3B88A00104B2A6676"))
                {
                }
            }

            var profiles = OutlookDetection.ReadProfiles(root);
            CollectionAssert.AreEqual(new[] { "Work", "Another" }, profiles.Select(p => p.Name).ToList(), "the default profile first");
            var section = profiles[0].Sections.Single();
            Assert.AreEqual("SMTP:jane@contoso.com", section[OutlookDetection.SmtpTag]);
            Assert.AreEqual("https://outlook.office365.com/autodiscover/autodiscover.xml", section[OutlookDetection.AutodiscoverTag]);
            Assert.AreEqual(2, section.Count, "only the values Detect reads; a section with none is left out");
            Assert.AreEqual(@"C:\Mail\other.pst", profiles[1].Sections.Single()[OutlookDetection.ArchiveTag]);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }

    private static byte[] Utf16(string text) => Encoding.Unicode.GetBytes(text + "\0");
}
