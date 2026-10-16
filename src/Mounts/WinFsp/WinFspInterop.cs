using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace AnyFS.Mounts.WinFsp;

/// <summary>
/// Hands the runtime winfsp-msil (WinFsp's .NET layer) as a real file when the host has none to offer. The layer's
/// static initialiser compares the driver's version with its own, which it reads through
/// <c>FileVersionInfo.GetVersionInfo(Assembly.Location)</c>; inside a single-file bundle <c>Location</c> is empty and
/// the check throws before anything can be mounted. So the published bundle leaves winfsp-msil out
/// (AnyFS.App.csproj, <c>KeepWinFspInteropOutOfBundle</c>), this assembly carries the identical DLL as a resource,
/// and the first reference to it lands here: extracted once per user, loaded into the default context. In a normal
/// build the DLL sits beside the host and the runtime never asks.
/// </summary>
internal static class WinFspInterop
{
    public const string AssemblyName = "winfsp-msil";
    public const string ResourceName = "winfsp-msil.dll";

    private static readonly object Gate = new();
    private static bool _registered;
    private static Assembly? _loaded;

    /// <summary>Where the copy goes: the per-user AnyFS folder (not a temp folder, which some policies refuse to load DLLs from).</summary>
    public static string Directory { get; set; } = DefaultDirectory();

    /// <summary>Hooks the default load context's last-chance resolution. Called from the provider's static constructor, before any mount.</summary>
    public static void Register()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            _registered = true;
            AssemblyLoadContext.Default.Resolving += Resolve;
        }
    }

    public static bool HasEmbedded()
    {
        using var stream = Open();
        return stream is not null;
    }

    /// <summary>
    /// Writes the embedded copy to <paramref name="directory"/> (or <see cref="Directory"/>) and returns its path. The
    /// file is named by its content hash, so a build with a different winfsp-msil gets its own file and a copy another
    /// anyfs has loaded is never overwritten.
    /// </summary>
    public static string Extract(string? directory = null)
    {
        using var stream = Open() ?? throw new InvalidOperationException("This build of AnyFS.Mounts.WinFsp carries no copy of winfsp-msil.");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
        var dir = directory ?? Directory;
        System.IO.Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{AssemblyName}-{hash}.dll");
        if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, bytes);
            try
            {
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && File.Exists(path))
            {
                // another anyfs wrote (and may have loaded) the same file meanwhile; same content, so use it
                File.Delete(temp);
            }
        }

        RemoveOtherCopies(dir, path);
        return path;
    }

    /// <summary>
    /// One copy per version: every other <c>winfsp-msil-*</c> in the folder (an older build's layer, the temporary file
    /// of a write that never finished) goes. A copy another anyfs has loaded cannot be deleted and is left for next time.
    /// </summary>
    private static void RemoveOtherCopies(string directory, string keep)
    {
        foreach (var file in System.IO.Directory.EnumerateFiles(directory, AssemblyName + "-*"))
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

    private static Assembly? Resolve(AssemblyLoadContext context, System.Reflection.AssemblyName name)
    {
        if (!string.Equals(name.Name, AssemblyName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        lock (Gate)
        {
            return _loaded ??= Load(context, Extract());
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The same assembly is in the trimmed app, rooted whole (TrimmerRootAssembly winfsp-msil in AnyFS.App), so everything it needs survived trimming; this copy only gives it a file path.")]
    private static Assembly Load(AssemblyLoadContext context, string path) => context.LoadFromAssemblyPath(path);

    private static Stream? Open() => typeof(WinFspInterop).Assembly.GetManifestResourceStream(ResourceName);

    private static string DefaultDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return local.Length > 0 ? Path.Combine(local, "AnyFS", "winfsp") : Path.Combine(Path.GetTempPath(), "anyfs", "winfsp");
    }
}
