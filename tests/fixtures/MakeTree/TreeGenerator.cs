using System.Security.Cryptography;
using System.Text;

namespace AnyFS.Fixtures;

/// <summary>
/// The deterministic fixture tree every test and lab machine generates from a seed, so no large files
/// travel over the LAN: readme.txt, small.bin (1 KiB), medium.bin (1 MiB), large.bin (200 MiB by default),
/// empty.txt, spaces/, unicode/, long/, nested/, emptydir/ and many/ (5,000 files), plus fixture.sha256.
/// </summary>
public static class TreeGenerator
{
    public const ulong DefaultSeed = 20261011UL;

    public const long DefaultLargeSize = 200L << 20;

    /// <summary>Writes the tree; returns the manifest lines "sha256  relative/path".</summary>
    public static IReadOnlyList<string> Write(string outDir, ulong seed = DefaultSeed, long largeSize = DefaultLargeSize, int manyCount = 5000)
    {
        Directory.CreateDirectory(outDir);
        var manifest = new List<string>();

        WriteText(outDir, manifest, "readme.txt", "AnyFS fixture tree. Deterministic from seed " + seed + ".\n");
        WriteBinary(outDir, manifest, "small.bin", 1024, seed + 1);
        WriteBinary(outDir, manifest, "medium.bin", 1L << 20, seed + 2);
        WriteBinary(outDir, manifest, "large.bin", largeSize, seed + 3);
        WriteText(outDir, manifest, "empty.txt", "");
        WriteText(outDir, manifest, Path.Combine("spaces", "a b c.txt"), "spaces in names\n");
        WriteText(outDir, manifest, Path.Combine("unicode", "ünïcödé ✓ 日本語.txt"), "unicode names\n");
        WriteText(outDir, manifest, Path.Combine("long", new string('n', 200) + ".txt"), "a 204-char name\n");
        WriteText(outDir, manifest, Path.Combine("nested", "a", "b", "c", "deep.txt"), "deep\n");
        Directory.CreateDirectory(Path.Combine(outDir, "emptydir"));
        for (var i = 0; i < manyCount; i++)
        {
            WriteText(outDir, manifest, Path.Combine("many", $"file{i:D4}.txt"), $"file {i}\n");
        }

        File.WriteAllText(Path.Combine(outDir, "fixture.sha256"), string.Join('\n', manifest) + "\n");
        return manifest;
    }

    /// <summary>The bytes of one generated binary file without writing it (for asserting ranged reads).</summary>
    public static byte[] Bytes(ulong fileSeed, long length)
    {
        var buffer = new byte[length];
        Fill(buffer, fileSeed, 0);
        return buffer;
    }

    /// <summary>The seed of a named binary file: small.bin is seed+1, medium.bin seed+2, large.bin seed+3.</summary>
    public static ulong SeedOf(string name, ulong seed = DefaultSeed) => name switch
    {
        "small.bin" => seed + 1,
        "medium.bin" => seed + 2,
        "large.bin" => seed + 3,
        _ => throw new ArgumentException($"not a generated binary: {name}"),
    };

    private static void WriteBinary(string outDir, List<string> manifest, string rel, long length, ulong fileSeed)
    {
        var path = Path.Combine(outDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[1 << 20];
        var state = fileSeed ^ 0x9E3779B97F4A7C15UL;
        long remaining = length;
        while (remaining > 0)
        {
            var n = (int)Math.Min(buf.Length, remaining);
            state = FillChunk(buf.AsSpan(0, n), state);
            fs.Write(buf, 0, n);
            sha.AppendData(buf, 0, n);
            remaining -= n;
        }

        manifest.Add($"{Convert.ToHexStringLower(sha.GetHashAndReset())}  {rel.Replace('\\', '/')}");
    }

    private static void Fill(Span<byte> buffer, ulong fileSeed, long startOffset)
    {
        if (startOffset != 0)
        {
            throw new ArgumentException("only whole-file generation is supported", nameof(startOffset));
        }

        var state = fileSeed ^ 0x9E3779B97F4A7C15UL;
        // the writer generates in 1 MiB chunks; chunking doesn't change the stream, it's one xorshift sequence
        FillChunk(buffer, state);
    }

    /// <summary>xorshift64*, 8 bytes at a time: fast, deterministic, incompressible.</summary>
    private static ulong FillChunk(Span<byte> span, ulong x)
    {
        for (var i = 0; i < span.Length; i += 8)
        {
            x ^= x >> 12;
            x ^= x << 25;
            x ^= x >> 27;
            var v = x * 0x2545F4914F6CDD1DUL;
            BitConverter.TryWriteBytes(span.Slice(i, Math.Min(8, span.Length - i)), v);
        }

        return x;
    }

    private static void WriteText(string outDir, List<string> manifest, string rel, string text)
    {
        var path = Path.Combine(outDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Encoding.UTF8.GetBytes(text);
        File.WriteAllBytes(path, bytes);
        manifest.Add($"{Convert.ToHexStringLower(SHA256.HashData(bytes))}  {rel.Replace('\\', '/')}");
    }
}

internal static class Program
{
    // usage: MakeTree <outDir> [seed] [--large-mb N] [--many N]
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: MakeTree <outDir> [seed] [--large-mb N] [--many N]");
            return 2;
        }

        var outDir = args[0];
        var seed = TreeGenerator.DefaultSeed;
        var large = TreeGenerator.DefaultLargeSize;
        var many = 5000;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--large-mb" && i + 1 < args.Length)
            {
                large = long.Parse(args[++i]) << 20;
            }
            else if (args[i] == "--many" && i + 1 < args.Length)
            {
                many = int.Parse(args[++i]);
            }
            else if (ulong.TryParse(args[i], out var s))
            {
                seed = s;
            }
        }

        var manifest = TreeGenerator.Write(outDir, seed, large, many);
        Console.WriteLine($"wrote {outDir}: {manifest.Count} files listed in fixture.sha256");
        return 0;
    }
}
