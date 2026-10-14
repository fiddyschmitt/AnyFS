using System.Diagnostics.CodeAnalysis;
using AnyFS.Sdk;

namespace AnyFS.Sources.Sshfs;

public sealed class SshfsSettings
{
    [Setting("Host", Required = true, Order = 0, Hint = "e.g. aus-files-01", Placeholder = "server name or address")]
    public string Host { get; set; } = "";

    [Setting("Port", Required = true, Order = 1)]
    public int Port { get; set; } = 22;

    [Setting("Username", Required = true, Order = 2)]
    public string Username { get; set; } = "";

    [Setting("Remote root", Order = 3, Hint = "/ to serve everything, ~ for the home folder, or any absolute path.")]
    public string Root { get; set; } = "/";

    [Setting("Password", Secret = true, Order = 4, Group = "Authentication", Hint = "Leave empty to use the key below, or to be asked at login.")]
    public string? Password { get; set; }

    [Setting("Private key file", Kind = SettingKind.FilePath, Order = 5, Group = "Authentication", Hint = "OpenSSH, PEM or PuTTY format.")]
    public string? KeyFile { get; set; }

    [Setting("Key passphrase", Secret = true, Order = 6, Group = "Authentication", Hint = "Leave empty to be asked when the key needs one.")]
    public string? Passphrase { get; set; }

    [Setting("Connections", Advanced = true, Order = 10, Hint = "SFTP throughput is per connection; open files are spread across them.")]
    public int Connections { get; set; } = 4;

    [Setting("Connect timeout (seconds)", Advanced = true, Order = 11)]
    public int ConnectTimeoutSeconds { get; set; } = 20;
}

/// <summary>
/// SSHFS (type <c>sshfs</c>, folder <c>sshfs</c>, spec 7.2): a folder on any machine reachable over SSH, through
/// SFTP with SSH.NET. Ranged reads are native to SFTP, so a read is one request on a pooled connection; listings
/// are cached for two seconds because clients stat everything they list; writes go through when the collection
/// allows them. Host keys are trusted on first use and pinned per source; two-factor codes and passwords the
/// settings don't hold arrive through the host's prompts.
/// </summary>
public sealed class SshfsPlugin : ISourcePlugin, IValidatable
{
    public string TypeId => "sshfs";

    public string DisplayName => "SSHFS";

    public string Description => "A folder on any machine you can reach over SSH (SFTP): keys or passwords, two-factor prompts, ranged reads and writes.";

    public string TypeFolder => "sshfs";

    public Stream? OpenIcon() => null;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(SshfsSettings);

    public object CreateDefaultSettings() => new SshfsSettings();

    public ISource CreateSource(object settings) => new SshfsSource((SshfsSettings)settings);

    public IEnumerable<ValidationMessage> Validate(object settings)
    {
        var s = (SshfsSettings)settings;
        var host = s.Host?.Trim() ?? "";
        if (host.Length == 0)
        {
            yield return new ValidationMessage("Give the server's name or address.", nameof(SshfsSettings.Host));
        }
        else if (host.Any(char.IsWhiteSpace) || host.Contains('/') || host.Contains('@'))
        {
            yield return new ValidationMessage("The host is a name or an address alone: no user@, no path.", nameof(SshfsSettings.Host));
        }

        if (s.Port is < 1 or > 65535)
        {
            yield return new ValidationMessage("The port must be between 1 and 65535.", nameof(SshfsSettings.Port));
        }

        if (string.IsNullOrWhiteSpace(s.Username))
        {
            yield return new ValidationMessage("Give the user name to log in as.", nameof(SshfsSettings.Username));
        }

        if (!RemotePath.TryParseRoot(s.Root, out _, out var reason))
        {
            yield return new ValidationMessage(reason, nameof(SshfsSettings.Root));
        }

        if (!string.IsNullOrWhiteSpace(s.KeyFile) && !File.Exists(s.KeyFile))
        {
            // a shared settings file may name a key that lives on another machine, so this is a warning
            yield return new ValidationMessage($"The key file was not found on this machine: {s.KeyFile}", nameof(SshfsSettings.KeyFile), IsError: false);
        }

        if (s.Connections is < 1 or > 16)
        {
            yield return new ValidationMessage("Connections must be between 1 and 16.", nameof(SshfsSettings.Connections));
        }

        if (s.ConnectTimeoutSeconds < 1)
        {
            yield return new ValidationMessage("The connect timeout must be at least 1 second.", nameof(SshfsSettings.ConnectTimeoutSeconds));
        }
    }
}

/// <summary>The remote root setting and how source paths map under it.</summary>
public static class RemotePath
{
    /// <summary>
    /// Accepts "/", "/abs/path", "~", "~/sub" and "" (the home folder); backslashes are taken as slashes.
    /// <paramref name="root"/> comes back as "/", "/abs/path", "~" or "~/sub" with no trailing slash.
    /// </summary>
    public static bool TryParseRoot(string? text, out string root, out string reason)
    {
        root = "";
        reason = "";
        var t = (text ?? "").Trim().Replace('\\', '/');
        if (t.Length == 0 || t == "~" || t == "~/")
        {
            root = "~";
            return true;
        }

        if (t.StartsWith("~/", StringComparison.Ordinal))
        {
            if (!TryClean(t[2..], out var rest, out reason))
            {
                return false;
            }

            root = rest.Length == 0 ? "~" : "~/" + rest;
            return true;
        }

        if (t.StartsWith('/'))
        {
            if (!TryClean(t[1..], out var rest, out reason))
            {
                return false;
            }

            root = "/" + rest;
            return true;
        }

        reason = "The remote root is an absolute path (/srv/data), / for everything, or ~ for the home folder.";
        return false;
    }

    /// <summary>The absolute root once the server has said where home is (SFTP's working directory after login).</summary>
    public static string Resolve(string root, string home)
    {
        var trimmedHome = home.Length > 1 ? home.TrimEnd('/') : home;
        if (root == "~")
        {
            return trimmedHome.Length == 0 ? "/" : trimmedHome;
        }

        return root.StartsWith("~/", StringComparison.Ordinal) ? Join(trimmedHome.Length == 0 ? "/" : trimmedHome, root[2..]) : root;
    }

    /// <summary>The remote path of a normalised source path under an absolute root.</summary>
    public static string Join(string absoluteRoot, string path)
    {
        if (path.Length == 0)
        {
            return absoluteRoot;
        }

        return absoluteRoot == "/" ? "/" + path : absoluteRoot + "/" + path;
    }

    private static bool TryClean(string text, out string cleaned, out string reason)
    {
        cleaned = "";
        reason = "";
        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or ".."))
        {
            reason = "'.' and '..' are not allowed in the remote root.";
            return false;
        }

        cleaned = string.Join('/', parts);
        return true;
    }
}
