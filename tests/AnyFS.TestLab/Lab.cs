using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AnyFS.TestLab;

/// <summary>A login the tests use on the lab's machines.</summary>
public sealed class Login
{
    public string User { get; set; } = "";

    public string Password { get; set; } = "";
}

/// <summary>A SharePoint Server farm's test site, the account the rows use on it, its product (se, 2019) and its collection name in the matrix.</summary>
public sealed record SharePointLab(string SiteUrl, string User, string Password, string MachineName, string Product, string Collection);

/// <summary>A machine from settings.yml: `os` is linux, windows or macos; `role` is host (runs anyfs), upstream, dc or sharepoint.</summary>
public sealed class LabMachine
{
    public string Name { get; set; } = "";

    public string Os { get; set; } = "";

    public string Role { get; set; } = "";

    public string Ip { get; set; } = "";

    /// <summary>The account the tests log in as, with the lab key.</summary>
    public string User { get; set; } = "";
}

/// <summary>
/// The lab the end-to-end tests run against, as provisioning/settings.yml defines it (the file the lab is built
/// from; see provisioning/README.md): each machine's address by OS and role, the logins, the upstream's paths.
/// The tests look for provisioning/settings.yml in each folder above their binaries; <see cref="DirVariable"/>
/// names the folder instead.
/// </summary>
public static class Lab
{
    public const string DirVariable = "ANYFS_LAB_DIR";

    private static readonly Lazy<SettingsYml> settings = new(Load);

    private static SettingsYml Settings => settings.Value;

    /// <summary>Every machine, the macOS VM included, with the account the tests use on it.</summary>
    public static IReadOnlyList<LabMachine> Machines => machines.Value;

    private static readonly Lazy<IReadOnlyList<LabMachine>> machines = new(() =>
    {
        var list = Settings.Machines.Select(m => new LabMachine
        {
            Name = m.Name,
            Os = m.Os,
            Role = m.Role,
            Ip = m.Ip,
            User = m.Os == "windows" ? Settings.Accounts.Windows.User : Settings.Accounts.Linux.User,
        }).ToList();
        list.AddRange(Settings.Mac.Machines.Select(m => new LabMachine { Name = m.Name, Os = "macos", Role = "host", Ip = m.Ip, User = m.User }));
        return list;
    });

    /// <summary>The machine anyfs serves from: sshd and the fixture tree, the Samba share, and the remote client.</summary>
    public static LabMachine Upstream => Machines.SingleOrDefault(m => m.Role == "upstream") ?? throw new InvalidOperationException("settings.yml has no machine with role upstream");

    /// <summary>The machine that runs anyfs for an OS (linux, windows, macos), or null when the lab has none.</summary>
    public static LabMachine? Host(string os) => Machines.SingleOrDefault(m => m.Os == os && m.Role == "host");

    public static Login LinuxLogin => Settings.Accounts.Linux;

    public static Login WindowsLogin => Settings.Accounts.Windows;

    /// <summary>The lab's SSH key: every machine accepts it.</summary>
    public static string SshKey => ExpandHome(Settings.SshKey);

    public static string DevBoxIp => Settings.DevBox.Ip;

    /// <summary>provisioning/publish on this PC: what publish.ps1 built, shared into the VMs.</summary>
    public static string PublishDir => Path.GetFullPath(Path.Combine(SettingsDir, Settings.DevBox.SharedFolders["publish"]));

    /// <summary>The same folder as the Linux VMs see it.</summary>
    public static string LinuxPublishDir => $"/media/vboxsf/{DevBoxIp}/publish";

    /// <summary>The upstream's fixture directory (the Samba share, the SSHFS root).</summary>
    public static string FixtureDir => Settings.Linux.FixtureDir;

    /// <summary>The key pair the SSHFS source logs in to the upstream with (made at provisioning).</summary>
    public static string SshfsKeyPath => Settings.Linux.SshfsKey;

    /// <summary>The RunRemote client on this PC, which starts anyfs in the Windows host's logged-on session.</summary>
    public static string RunRemoteClient => Settings.Windows.RunremoteClient;

    /// <summary>
    /// The SharePoint Server farms (provisioning/sharepoint/README.md), one per machine with role sharepoint: each
    /// test site by address, which is how machines outside the lab's domain reach it, the tester's account, and
    /// the collection the farm shows as in the E2E matrix (<c>sharepoint</c> for Subscription Edition,
    /// <c>sharepoint2019</c> for 2019). Empty when settings.yml has no such machine.
    /// </summary>
    public static IReadOnlyList<SharePointLab> SharePoints => sharePoints.Value;

    private static readonly Lazy<IReadOnlyList<SharePointLab>> sharePoints = new(() =>
    {
        var sp = Settings.Sharepoint;
        if (string.IsNullOrWhiteSpace(sp.Site) || string.IsNullOrWhiteSpace(sp.Accounts.Tester.User))
        {
            return [];
        }

        return Settings.Machines.Where(m => m.Role == "sharepoint").Select(m =>
        {
            var product = string.IsNullOrWhiteSpace(m.Product) ? "se" : m.Product.Trim().ToLowerInvariant();
            var collection = product == "se" ? "sharepoint" : "sharepoint" + product;
            return new SharePointLab($"http://{m.Ip}{sp.Site}", $"{sp.Netbios}\\{sp.Accounts.Tester.User}", sp.Accounts.Tester.Password, m.Name, product, collection);
        }).ToList();
    });

    public static string SettingsDir => Path.GetDirectoryName(FindSettings() ?? throw NoSettings())!;

    /// <summary>The path of the lab's settings.yml, or null when there's none to be found.</summary>
    public static string? FindSettings()
    {
        var dir = Environment.GetEnvironmentVariable(DirVariable);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            return Path.Combine(dir, "settings.yml");
        }

        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "provisioning", "settings.yml");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static FileNotFoundException NoSettings() => new($"No provisioning/settings.yml above {AppContext.BaseDirectory}; set {DirVariable} to the folder that has it.");

    private static SettingsYml Load()
    {
        var path = FindSettings() ?? throw NoSettings();
        return new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<SettingsYml>(File.ReadAllText(path));
    }

    private static string ExpandHome(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith(@"~\", StringComparison.Ordinal)
            ? Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..])
            : path;

    // The parts of settings.yml the tests read; the rest is for Vagrant and the provisioners.
    private sealed class SettingsYml
    {
        public string SshKey { get; set; } = "";

        public DevBoxYml DevBox { get; set; } = new();

        public AccountsYml Accounts { get; set; } = new();

        public List<MachineYml> Machines { get; set; } = [];

        public LinuxYml Linux { get; set; } = new();

        public WindowsYml Windows { get; set; } = new();

        public MacYml Mac { get; set; } = new();

        public SharepointYml Sharepoint { get; set; } = new();
    }

    private sealed class SharepointYml
    {
        public string Netbios { get; set; } = "";

        public string Site { get; set; } = "";

        public SharepointAccountsYml Accounts { get; set; } = new();
    }

    private sealed class SharepointAccountsYml
    {
        public Login Tester { get; set; } = new();
    }

    private sealed class DevBoxYml
    {
        public string Ip { get; set; } = "";

        public Dictionary<string, string> SharedFolders { get; set; } = [];
    }

    private sealed class AccountsYml
    {
        public Login Linux { get; set; } = new();

        public Login Windows { get; set; } = new();
    }

    private sealed class MachineYml
    {
        public string Name { get; set; } = "";

        public string Os { get; set; } = "";

        public string Role { get; set; } = "";

        public string Ip { get; set; } = "";

        /// <summary>A sharepoint machine's product: se or 2019.</summary>
        public string Product { get; set; } = "";
    }

    private sealed class LinuxYml
    {
        public string FixtureDir { get; set; } = "";

        public string SshfsKey { get; set; } = "";
    }

    private sealed class WindowsYml
    {
        public string RunremoteClient { get; set; } = "";
    }

    private sealed class MacYml
    {
        public string Host { get; set; } = "";

        public string User { get; set; } = "";

        public List<MacVmYml> Machines { get; set; } = [];
    }

    private sealed class MacVmYml
    {
        public string Name { get; set; } = "";

        public string Ip { get; set; } = "";

        public string User { get; set; } = "";
    }
}
