using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using OpenQA.Selenium;
using OpenQA.Selenium.Firefox;

namespace AnyFS.Sources.Common;

/// <summary>
/// geckodriver, Firefox's WebDriver driver, as AnyFS carries it (spec 7.3.2): scripts/fetch-geckodriver.ps1 fetches the
/// release scripts/geckodriver.json pins, and src/AnyFS.App embeds the one for its runtime as "geckodriver". A Firefox
/// sign-in writes it out once per version, under the user's AnyFS folder, and hands Selenium its path, so Selenium
/// Manager is never asked for one: a machine that cannot reach GitHub signs in through Firefox all the same. One
/// geckodriver drives every recent Firefox, where Chromium's drivers must match the browser's version; those stay
/// Selenium Manager's to find or download. A build that carries none (a test run, a build before the fetch) leaves
/// Firefox's driver to Selenium Manager too. geckodriver is MPL-2.0, conveyed unmodified; its source is at
/// https://github.com/mozilla/geckodriver.
/// </summary>
internal static class GeckoDriver
{
    public const string ResourceName = "geckodriver";

    private static readonly Lock Gate = new();

    /// <summary>The carried driver: the host executable's, which alone knows its runtime, else this assembly's; null when neither carries one. Tests hand it a file.</summary>
    internal static Func<Stream?> Open { get; set; } = OpenEmbedded;

    /// <summary>Where it is written: <c>%LOCALAPPDATA%\AnyFS\drivers</c>, beside the browser profiles and bound to the machine as they are, or the folder <c>ANYFS_DRIVERS</c> names (tests).</summary>
    internal static string Folder => Environment.GetEnvironmentVariable("ANYFS_DRIVERS") is { Length: > 0 } folder ? folder : DefaultFolder();

    /// <summary>
    /// Firefox's driver service: the carried geckodriver, written out if it is not there yet; Selenium Manager's when this
    /// build carries none, or when it cannot be written, which is logged.
    /// </summary>
    public static FirefoxDriverService Service(ILogger logger, string logPrefix)
    {
        string? path;
        try
        {
            path = Write();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogInformation("{Prefix}: the geckodriver AnyFS carries could not be written to {Folder} ({Error}); Selenium Manager finds one instead", logPrefix, Folder, ex.Message);
            path = null;
        }

        return path is null
            ? FirefoxDriverService.CreateDefaultService()
            : FirefoxDriverService.CreateDefaultService(Path.GetDirectoryName(path)!, Path.GetFileName(path));
    }

    /// <summary>The driver a service ran, for the log: the carried geckodriver, or the one Selenium Manager found or downloaded.</summary>
    public static string FileOf(DriverService service) => Path.Combine(service.DriverServicePath ?? "", service.DriverServiceExecutableName ?? "");

    /// <summary>
    /// The carried driver as a file, named by its content hash, so a build with another geckodriver writes its own and a
    /// copy another AnyFS runs is never written over; written again when it has gone (cleaned away meanwhile); copies of
    /// other versions go. Null when this build carries none.
    /// </summary>
    internal static string? Write()
    {
        lock (Gate)
        {
            byte[] bytes;
            using (var stream = Open())
            {
                if (stream is null)
                {
                    return null;
                }

                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
            }

            var hash = Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
            var folder = Folder;
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{ResourceName}-{hash}{(OperatingSystem.IsWindows() ? ".exe" : "")}");
            if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
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
                    // another AnyFS wrote the same file meanwhile, and may be running it: the same content, so it is used
                    File.Delete(temp);
                }
            }

            RemoveOtherCopies(folder, path);
            return path;
        }
    }

    /// <summary>One copy per version: every other <c>geckodriver-*</c> in the folder (an older build's driver, the temporary file of a write that never finished) goes; one still running is left for next time.</summary>
    private static void RemoveOtherCopies(string folder, string keep)
    {
        foreach (var file in Directory.EnumerateFiles(folder, ResourceName + "-*"))
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
            }
        }
    }

    private static Stream? OpenEmbedded()
    {
        foreach (var assembly in new[] { Assembly.GetEntryAssembly(), typeof(GeckoDriver).Assembly })
        {
            if (assembly?.GetManifestResourceStream(ResourceName) is { } stream)
            {
                return stream;
            }
        }

        return null;
    }

    private static string DefaultFolder()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return local.Length > 0 ? Path.Combine(local, "AnyFS", "drivers") : Path.Combine(Path.GetTempPath(), "anyfs", "drivers");
    }
}
