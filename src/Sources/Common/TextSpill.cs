using System.Buffers;
using System.Text;

namespace AnyFS.Sources.Common;

/// <summary>
/// Text put by on disk while a read of a great many things goes on: each piece is written as it comes and read back when
/// its turn comes, so that what is held in memory meanwhile is a place and a length for each and not the text. For a read
/// that cannot use a thing until it has seen them all (a Confluence space's pages, whose folders' names depend on their
/// siblings', come in no order with their bodies: read whole into memory, a space held every body it has at once, 144 MB
/// for 4,040 pages of 16 KB; the review of 21-Nov-2026, P6). One file, deleted when this is disposed; what a run that
/// was killed left behind is removed by <see cref="Sweep"/>.
/// </summary>
public sealed class TextSpill : IDisposable
{
    private const string Prefix = "put-by-";

    private readonly FileStream _file;
    private readonly Lock _gate = new();
    private long _end;

    /// <param name="directory">Where the file is made (and the folder, if it is not there): a folder of the source's own cache.</param>
    public TextSpill(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory.CreateDirectory(directory);
        _file = new FileStream(Path.Combine(directory, $"{Prefix}{Guid.NewGuid():N}.tmp"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);
    }

    /// <summary>Where a piece of text was put by, and how long it is there.</summary>
    public readonly record struct Kept(long At, int Bytes);

    /// <summary>How much is put by, in bytes.</summary>
    public long Bytes
    {
        get
        {
            lock (_gate)
            {
                return _end;
            }
        }
    }

    /// <summary>Puts <paramref name="text"/> by; what is returned gets it back (<see cref="Take"/>).</summary>
    public Kept Put(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return default;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(text.Length));
        try
        {
            var count = Encoding.UTF8.GetBytes(text, buffer);
            lock (_gate)
            {
                var at = _end;
                _file.Position = at;
                _file.Write(buffer, 0, count);
                _end = at + count;
                return new Kept(at, count);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>The text put by at <paramref name="kept"/>, as often as it is asked for.</summary>
    public string Take(Kept kept)
    {
        if (kept.Bytes == 0)
        {
            return "";
        }

        var buffer = ArrayPool<byte>.Shared.Rent(kept.Bytes);
        try
        {
            lock (_gate)
            {
                ArgumentOutOfRangeException.ThrowIfGreaterThan(kept.At + kept.Bytes, _end, nameof(kept));
                _file.Position = kept.At;
                _file.ReadExactly(buffer, 0, kept.Bytes);
            }

            return Encoding.UTF8.GetString(buffer, 0, kept.Bytes);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Removes what earlier runs left in <paramref name="directory"/>: a run that was killed deletes nothing. For a source
    /// to call as it starts, before it puts anything by; a file another holds open is left.
    /// </summary>
    public static void Sweep(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var left in Directory.EnumerateFiles(directory, Prefix + "*.tmp"))
        {
            try
            {
                File.Delete(left);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public void Dispose() => _file.Dispose();
}
