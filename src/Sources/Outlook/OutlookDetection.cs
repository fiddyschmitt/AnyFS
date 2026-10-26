using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using AnyFS.Sdk;
using AnyFS.Sources.Microsoft365;
using Microsoft.Win32;

namespace AnyFS.Sources.Outlook;

/// <summary>A classic Outlook profile as the registry holds it: its name, and each of its sections' text values by MAPI property tag ("001f6641").</summary>
internal sealed record OutlookProfile(string Name, IReadOnlyList<IReadOnlyDictionary<string, string>> Sections);

/// <summary>What Detect reads: the profiles (the default first), Outlook's own folders for data files, the Windows sign-in's address.</summary>
internal sealed record DetectionPlaces(IReadOnlyList<OutlookProfile> Profiles, IReadOnlyList<string> DataFolders, string? WindowsAddress);

/// <summary>
/// The Detect button (spec 7.7, Detect): what classic Outlook's profiles on this machine hold, offered as ready-made
/// sources. A profile's mailboxes are its Exchange stores' SMTP addresses (PR 0x6641), each of a section that is an
/// account's own (with the Autodiscover address the profile uses, 0x664A, or an offline copy, 0x6610); the Autodiscover
/// host says which Exchange: Exchange Online's (and in which cloud) or a server's own. Then each mailbox's offline copy
/// (its OST) and the archives attached (PSTs, 0x6700, with their display names, 0x3001). Data files in Outlook's own
/// folders that no profile names follow; and with no mailbox in any profile (new Outlook keeps none), the Windows
/// sign-in's own address. Nothing is looked up and nothing signed in to (Spike 17, 26-Oct-2026, on the lab's Outlook).
/// </summary>
internal static class OutlookDetection
{
    /// <summary>PR_PROFILE_USER_SMTP_EMAIL_ADDRESS: "SMTP:jane@contoso.com" (or the address alone).</summary>
    internal const string SmtpTag = "001f6641";

    /// <summary>PR_PROFILE_OFFLINE_STORE_PATH: the mailbox's offline copy (.ost; a group's .nst).</summary>
    internal const string OfflineTag = "001f6610";

    /// <summary>PR_PST_PATH: an attached data file (.pst).</summary>
    internal const string ArchiveTag = "001f6700";

    /// <summary>PR_DISPLAY_NAME: the store's name in Outlook's folder pane.</summary>
    internal const string NameTag = "001f3001";

    /// <summary>The Autodiscover address the profile's Exchange account uses (https://outlook.office365.com/autodiscover/autodiscover.xml).</summary>
    internal const string AutodiscoverTag = "001f664a";

    private static readonly string[] Tags = [SmtpTag, OfflineTag, ArchiveTag, NameTag, AutodiscoverTag];

    /// <summary>The sources what was read holds: mailboxes first, then their offline copies, then archives, each once.</summary>
    public static IReadOnlyList<DetectedSource> Find(DetectionPlaces places)
    {
        var found = new List<DetectedSource>();
        var mailboxes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mailboxCount = 0;
        foreach (var profile in places.Profiles)
        {
            // a section with an address is an account's own when it has its Autodiscover address or its offline copy
            // (the transport's and the message store service's sections repeat the address; another mailbox opened beside
            // one's own has neither, and the source reads only the signed-in mailbox)
            foreach (var section in profile.Sections.Where(s => s.ContainsKey(SmtpTag) && (s.ContainsKey(AutodiscoverTag) || s.ContainsKey(OfflineTag))))
            {
                if (Address(section[SmtpTag]) is { } address && mailboxes.Add(address))
                {
                    found.Insert(mailboxCount++, Mailbox(address, section.GetValueOrDefault(AutodiscoverTag)));
                }
            }

            foreach (var section in profile.Sections)
            {
                if (section.GetValueOrDefault(OfflineTag) is { } ost && IsDataFile(ost) && File.Exists(ost) && files.Add(Full(ost)))
                {
                    var owner = section.GetValueOrDefault(SmtpTag) is { } smtp ? Address(smtp) : null;
                    found.Add(new DetectedSource(owner is null ? $"Offline copy {ost}" : $"Offline copy of {owner} ({ost})", new OutlookSettings { Path = ost }));
                }
            }

            foreach (var section in profile.Sections)
            {
                if (section.GetValueOrDefault(ArchiveTag) is { } pst && IsDataFile(pst) && File.Exists(pst) && files.Add(Full(pst)))
                {
                    var name = section.GetValueOrDefault(NameTag)?.Trim();
                    found.Add(new DetectedSource($"Data file {(string.IsNullOrEmpty(name) ? Path.GetFileNameWithoutExtension(pst) : name)} ({pst})", new OutlookSettings { Path = pst }));
                }
            }
        }

        // data files in Outlook's own folders that no profile names: an old archive, an offline copy left behind
        foreach (var folder in places.DataFolders)
        {
            foreach (var file in DataFilesIn(folder))
            {
                if (files.Add(Full(file)))
                {
                    found.Add(new DetectedSource($"Data file {Path.GetFileName(file)} ({Path.GetDirectoryName(file)})", new OutlookSettings { Path = file }));
                }
            }
        }

        // new Outlook keeps no profile: the Windows sign-in's own address is likely the mailbox
        if (mailboxes.Count == 0 && Address(places.WindowsAddress) is { } upn)
        {
            found.Insert(0, new DetectedSource($"Mailbox {upn} (your Windows sign-in's address, on Exchange Online)", new OutlookSettings { Path = upn, SignInMethod = OutlookSettings.WebSession }));
        }

        return found;
    }

    /// <summary>A mailbox as its Autodiscover address places it: on Exchange Online (in its cloud) through the web session, or on that Exchange server with a Windows account.</summary>
    private static DetectedSource Mailbox(string address, string? autodiscover)
    {
        var host = Uri.TryCreate(autodiscover?.Trim(), UriKind.Absolute, out var url) ? url.Host.ToLowerInvariant() : null;
        if (host is not null && CloudOf(host) is null)
        {
            // a server's own Autodiscover host answers EWS too (Exchange serves both from its front end)
            return new DetectedSource($"Mailbox {address} on {host} (Exchange server, your Windows sign-in)",
                new OutlookSettings { Path = $"https://{host}", SignInMethod = OutlookSettings.WindowsCurrent });
        }

        var cloud = CloudOf(host);
        return new DetectedSource($"Mailbox {address} (Exchange Online{(cloud is { IsGlobal: false } ? ", " + cloud.Name : "")})",
            new OutlookSettings { Path = address, SignInMethod = OutlookSettings.WebSession, Cloud = cloud?.Name ?? MicrosoftCloud.AutomaticName });
    }

    /// <summary>The cloud whose Exchange Online an Autodiscover host is: the US Government's under office365.us, the global one's under outlook.com, office365.com, office.com or cloud.microsoft; null for any other host, and for none.</summary>
    internal static MicrosoftCloud? CloudOf(string? host)
    {
        static bool Under(string host, string domain) => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
        if (host is null)
        {
            return null;
        }

        return Under(host, "office365.us") ? MicrosoftCloud.UsGovernment
            : Under(host, "outlook.com") || Under(host, "office365.com") || Under(host, "office.com") || Under(host, "cloud.microsoft") ? MicrosoftCloud.Global
            : null;
    }

    /// <summary>An address as the profile writes it ("SMTP:Jane@Contoso.com"), in lower case; null for anything that is no address.</summary>
    internal static string? Address(string? value)
    {
        var text = value?.Trim();
        if (text is null)
        {
            return null;
        }

        if (text.StartsWith("SMTP:", StringComparison.OrdinalIgnoreCase))
        {
            text = text[5..].Trim();
        }

        return text.IndexOf('@') is > 0 and var at && text.IndexOf('.', at) > at + 1 && !text.Contains(' ') ? text.ToLowerInvariant() : null;
    }

    private static bool IsDataFile(string path) => path.EndsWith(".pst", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".ost", StringComparison.OrdinalIgnoreCase);

    private static string Full(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static IEnumerable<string> DataFilesIn(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.EnumerateFiles(folder).Where(IsDataFile).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // --- this machine (Windows) ---------------------------------------------------------------------------------------------

    /// <summary>What this Windows machine holds: classic Outlook's profiles, its default folders, the signed-in account's address.</summary>
    [SupportedOSPlatform("windows")]
    public static DetectionPlaces OnThisMachine()
        => new(ReadProfiles(@"Software\Microsoft\Office"),
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Outlook"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Outlook Files"),
            ],
            WindowsUpn());

    /// <summary>
    /// The profiles under <paramref name="officeKey"/> (HKCU's Software\Microsoft\Office) for Outlook 2016 and later (16.0)
    /// and 2013 (15.0), the default one first: each profile's sections (its subkeys) with the values Detect reads, the
    /// text ones Outlook keeps as UTF-16 bytes.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static IReadOnlyList<OutlookProfile> ReadProfiles(string officeKey)
    {
        var profiles = new List<OutlookProfile>();
        foreach (var version in new[] { "16.0", "15.0" })
        {
            using var outlook = Registry.CurrentUser.OpenSubKey($@"{officeKey}\{version}\Outlook");
            using var root = outlook?.OpenSubKey("Profiles");
            if (root is null)
            {
                continue;
            }

            var preferred = outlook!.GetValue("DefaultProfile") as string;
            foreach (var name in root.GetSubKeyNames().OrderBy(n => string.Equals(n, preferred, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                using var profile = root.OpenSubKey(name);
                if (profile is null)
                {
                    continue;
                }

                var sections = new List<IReadOnlyDictionary<string, string>>();
                foreach (var sub in profile.GetSubKeyNames())
                {
                    using var section = profile.OpenSubKey(sub);
                    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var tag in Tags)
                    {
                        if (Text(section?.GetValue(tag)) is { Length: > 0 } text)
                        {
                            values[tag] = text;
                        }
                    }

                    if (values.Count > 0)
                    {
                        sections.Add(values);
                    }
                }

                profiles.Add(new OutlookProfile(name, sections));
            }
        }

        return profiles;
    }

    /// <summary>A registry value as text: a string as it is, bytes as the UTF-16 Outlook writes, without the closing null.</summary>
    internal static string? Text(object? value) => value switch
    {
        string s => s.TrimEnd('\0'),
        byte[] b => Encoding.Unicode.GetString(b, 0, b.Length & ~1).TrimEnd('\0'),
        _ => null,
    };

    /// <summary>The signed-in Windows account's user principal name (jane@contoso.com), or null where it has none (a local account).</summary>
    [SupportedOSPlatform("windows")]
    private static string? WindowsUpn()
    {
        var size = 512u;
        var buffer = new char[size];
        return GetUserNameExW(NameUserPrincipal, buffer, ref size) && size is > 0 and <= 512 ? new string(buffer, 0, (int)size) : null;
    }

    private const int NameUserPrincipal = 8;

    [DllImport("secur32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetUserNameExW(int nameFormat, [Out] char[] nameBuffer, ref uint size);
}
