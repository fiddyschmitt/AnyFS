using System.Security.Cryptography;
using AnyFS.Fixtures;

namespace AnyFS.TestLab;

/// <summary>
/// The expected tree: tests/fixtures/MakeTree's output from the default seed, generated once here (so the
/// manifest and the bytes for the ranged reads are known) and by each machine for itself.
/// </summary>
public static class Fixture
{
    public const string ManifestName = "fixture.sha256";

    /// <summary>What a listing of the tree's root shows (MakeTree writes the manifest into the tree).</summary>
    public static readonly IReadOnlyList<string> RootNames =
        ["emptydir", "empty.txt", "fixture.sha256", "large.bin", "long", "many", "medium.bin", "nested", "readme.txt", "small.bin", "spaces", "unicode"];

    public const int ManyCount = 5000;

    /// <summary>Lines in fixture.sha256: 5 top-level files, 4 nested, and the many/ files.</summary>
    public const int ManifestLines = 5 + 4 + ManyCount;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> manifest = new(Generate);

    /// <summary>sha256 by relative path ('/'-separated), as fixture.sha256 lists them.</summary>
    public static IReadOnlyDictionary<string, string> Manifest => manifest.Value;

    public static string LocalDir => Path.Combine(Path.GetTempPath(), "anyfs-e2e", "fixture");

    public static string Sha256Of(string relativePath) => Manifest[relativePath];

    /// <summary>The bytes of a generated binary from <paramref name="offset"/>, for the ranged reads.</summary>
    public static byte[] Range(string name, long offset, int length)
    {
        var all = Bytes(name);
        return all.AsSpan((int)offset, length).ToArray();
    }

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static long SizeOf(string name) => name switch
    {
        "small.bin" => 1024,
        "medium.bin" => 1L << 20,
        "large.bin" => TreeGenerator.DefaultLargeSize,
        _ => throw new ArgumentException(name),
    };

    private static readonly Dictionary<string, byte[]> cache = [];

    private static byte[] Bytes(string name)
    {
        lock (cache)
        {
            if (!cache.TryGetValue(name, out var bytes))
            {
                cache[name] = bytes = TreeGenerator.Bytes(TreeGenerator.SeedOf(name), SizeOf(name));
            }

            return bytes;
        }
    }

    private static IReadOnlyDictionary<string, string> Generate()
    {
        var dir = LocalDir;
        var manifestPath = Path.Combine(dir, ManifestName);
        if (!File.Exists(manifestPath) || File.ReadAllLines(manifestPath).Length != ManifestLines)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }

            TreeGenerator.Write(dir);
        }

        return File.ReadAllLines(manifestPath)
            .Where(l => l.Length > 0)
            .Select(l => l.Split("  ", 2))
            .ToDictionary(p => p[1], p => p[0], StringComparer.Ordinal);
    }
}
