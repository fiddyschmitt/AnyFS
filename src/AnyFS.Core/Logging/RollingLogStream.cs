namespace AnyFS.Core.Logging;

/// <summary>
/// anyfs.log is always the current file; when it passes the size limit it becomes anyfs.1.log, the old
/// anyfs.1.log becomes anyfs.2.log, and so on, keeping <see cref="KeepFiles"/> in all (logrotate style).
/// ZLogger writes to this from its single background thread.
/// </summary>
public sealed class RollingLogStream : Stream
{
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _keepFiles;
    private readonly object _gate = new();
    private FileStream _current;
    private long _length;

    public RollingLogStream(string path, long maxBytes, int keepFiles)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(keepFiles, 1);
        _path = path;
        _maxBytes = maxBytes;
        _keepFiles = keepFiles;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _current = Open();
        _length = _current.Length;
    }

    public string Path_ => _path;

    public int KeepFiles => _keepFiles;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _length;

    public override long Position
    {
        get => _length;
        set => throw new NotSupportedException();
    }

    /// <summary>anyfs.log, anyfs.1.log, anyfs.2.log...</summary>
    public static string RolledName(string path, int index)
    {
        if (index == 0)
        {
            return path;
        }

        var dir = System.IO.Path.GetDirectoryName(path) ?? "";
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var ext = System.IO.Path.GetExtension(path);
        return System.IO.Path.Combine(dir, $"{name}.{index}{ext}");
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        lock (_gate)
        {
            if (_length > 0 && _length + buffer.Length > _maxBytes)
            {
                Roll();
            }

            _current.Write(buffer);
            _length += buffer.Length;
        }
    }

    public override void Flush()
    {
        lock (_gate)
        {
            _current.Flush();
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                _current.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private FileStream Open()
        => new(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);

    private void Roll()
    {
        _current.Dispose();
        try
        {
            var last = RolledName(_path, _keepFiles - 1);
            if (File.Exists(last))
            {
                File.Delete(last);
            }

            for (var i = _keepFiles - 2; i >= 0; i--)
            {
                var from = RolledName(_path, i);
                if (File.Exists(from))
                {
                    File.Move(from, RolledName(_path, i + 1), overwrite: true);
                }
            }
        }
        catch (IOException)
        {
            // another process has the file: keep appending to the current one rather than lose lines
        }
        catch (UnauthorizedAccessException)
        {
        }

        _current = Open();
        _length = _current.Length;
    }
}
