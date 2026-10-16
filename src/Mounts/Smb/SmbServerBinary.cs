using System.Reflection;
using System.Security.Cryptography;

namespace AnyFS.Mounts.Smb;

/// <summary>
/// Finds the portable-smb-server executable: the one embedded in the build, unpacked into the provider's cache folder
/// on first use and named by its content hash, so a new build never runs an old copy and every SMB mount shares one
/// file; copies of other versions are removed when a new one lands. For development, ANYFS_SMB_SERVER or a copy
/// beside anyfs take precedence; neither is mentioned to users, since every build carries the server.
/// </summary>
internal static class SmbServerBinary
{
    public const string EnvironmentVariable = "ANYFS_SMB_SERVER";
    public const string ResourceName = "portable-smb-server";

    /// <summary>Unpacked copies are <c>portable-smb-server-&lt;hash&gt;</c>, plus <c>.exe</c> on Windows.</summary>
    public const string UnpackedPrefix = "portable-smb-server-";

    public static string FileName => OperatingSystem.IsWindows() ? "portable-smb-server.exe" : "portable-smb-server";

    private static string Extension => OperatingSystem.IsWindows() ? ".exe" : "";

    /// <summary>Whether a binary can be found without unpacking anything (for <c>UnavailableReason</c>).</summary>
    public static bool IsAvailable(out string reason)
    {
        if (FromEnvironment() is not null || BesideExecutable() is not null)
        {
            reason = "";
            return true;
        }

        if (HasEmbedded())
        {
            reason = "";
            return true;
        }

        reason = "The bundled SMB server is missing from this build: run scripts/fetch-go-servers.ps1 and build again.";
        return false;
    }

    /// <summary>The path to run, unpacking the embedded copy into <paramref name="cacheDirectory"/> when that is what we have.</summary>
    public static string Locate(string cacheDirectory)
    {
        if (FromEnvironment() is { } env)
        {
            return EnsureExecutable(env);
        }

        if (BesideExecutable() is { } beside)
        {
            return EnsureExecutable(beside);
        }

        using var resource = OpenEmbedded()
            ?? throw new InvalidOperationException("the bundled SMB server is missing from this build: run scripts/fetch-go-servers.ps1 and build again");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        return Unpack(buffer.ToArray(), cacheDirectory);
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> into <paramref name="cacheDirectory"/> as <c>portable-smb-server-&lt;hash&gt;</c> unless that
    /// file is already there whole, makes it executable, and deletes every other unpacked copy in the folder: other
    /// versions, and the temporary file of a write that never finished. A copy that cannot be deleted because an older
    /// anyfs is still running it is left for next time.
    /// </summary>
    internal static string Unpack(byte[] bytes, string cacheDirectory)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
        var dir = Directory.CreateDirectory(cacheDirectory).FullName;
        var path = Path.Combine(dir, UnpackedPrefix + hash + Extension);
        if (!File.Exists(path) || new System.IO.FileInfo(path).Length != bytes.Length)
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, bytes);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            try
            {
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && File.Exists(path))
            {
                // another anyfs wrote (and may be running) the same file meanwhile; same content, so use it
                File.Delete(temp);
            }
        }

        RemoveOtherCopies(dir, path);
        return path;
    }

    private static void RemoveOtherCopies(string directory, string keep)
    {
        foreach (var file in Directory.EnumerateFiles(directory, UnpackedPrefix + "*"))
        {
            if (string.Equals(Path.GetFileName(file), Path.GetFileName(keep), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Windows refuses while a process runs it (an older anyfs, or one being stopped): the next unpack gets it
            }
        }
    }

    /// <summary>
    /// A copy that was downloaded or checked out on Linux/macOS may carry no execute bit (a bare download has no mode);
    /// set it rather than fail to start. A location we cannot change is left alone and the start reports its own error.
    /// </summary>
    private static string EnsureExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                var mode = File.GetUnixFileMode(path);
                if ((mode & UnixFileMode.UserExecute) == 0)
                {
                    File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return path;
    }

    private static string? FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return !string.IsNullOrWhiteSpace(value) && File.Exists(value) ? value : null;
    }

    private static string? BesideExecutable()
    {
        var path = Path.Combine(AppContext.BaseDirectory, FileName);
        return File.Exists(path) ? path : null;
    }

    private static bool HasEmbedded()
    {
        using var stream = OpenEmbedded();
        return stream is not null;
    }

    /// <summary>The embedded copy: the host executable carries it (it alone knows its RID at publish), this assembly may too.</summary>
    private static Stream? OpenEmbedded()
    {
        foreach (var assembly in new[] { Assembly.GetEntryAssembly(), typeof(SmbServerBinary).Assembly })
        {
            if (assembly?.GetManifestResourceStream(ResourceName) is { } stream)
            {
                return stream;
            }
        }

        return null;
    }
}
