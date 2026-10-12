namespace AnyFS.Sdk;

public enum VfsError
{
    NotFound,
    AlreadyExists,
    NotADirectory,
    IsADirectory,
    DirectoryNotEmpty,
    /// <summary>Refused by one of the read-only layers (source capability, collection membership, mount).</summary>
    ReadOnly,
    AccessDenied,
    InvalidPath,
    NotSupported,
    /// <summary>The source is starting, failed or its upstream is unreachable.</summary>
    Unavailable,
    IoError,
}

/// <summary>
/// The one exception type that crosses the VFS boundary. Sources throw it (or let <see cref="SourceBase"/>
/// translate common exceptions); mount providers map <see cref="Error"/> to their protocol's status.
/// </summary>
public sealed class VfsException : Exception
{
    public VfsError Error { get; }

    public VfsException(VfsError error, string message, Exception? inner = null) : base(message, inner)
    {
        Error = error;
    }

    public static VfsException NotFound(string path) => new(VfsError.NotFound, $"'{path}' was not found");
    public static VfsException AlreadyExists(string path) => new(VfsError.AlreadyExists, $"'{path}' already exists");
    public static VfsException NotADirectory(string path) => new(VfsError.NotADirectory, $"'{path}' is not a directory");
    public static VfsException IsADirectory(string path) => new(VfsError.IsADirectory, $"'{path}' is a directory");
    public static VfsException DirectoryNotEmpty(string path) => new(VfsError.DirectoryNotEmpty, $"'{path}' is not empty");
    public static VfsException ReadOnly(string path, string reason) => new(VfsError.ReadOnly, $"'{path}' is read-only: {reason}");
    public static VfsException AccessDenied(string path) => new(VfsError.AccessDenied, $"access to '{path}' was denied");
    public static VfsException InvalidPath(string path, string reason) => new(VfsError.InvalidPath, $"invalid path '{path}': {reason}");
    public static VfsException NotSupported(string operation) => new(VfsError.NotSupported, $"{operation} is not supported by this source");
    public static VfsException Unavailable(string what) => new(VfsError.Unavailable, what);

    /// <summary>Maps the usual <see cref="System.IO"/> exceptions so file-backed sources don't have to.</summary>
    public static VfsException FromIo(Exception ex, string path)
    {
        const int ErrorAlreadyExists = unchecked((int)0x800700B7);
        const int ErrorFileExists = unchecked((int)0x80070050);
        const int ErrorDirNotEmpty = unchecked((int)0x80070091);
        return ex switch
        {
            VfsException v => v,
            FileNotFoundException => new VfsException(VfsError.NotFound, $"'{path}' was not found", ex),
            DirectoryNotFoundException => new VfsException(VfsError.NotFound, $"'{path}' was not found", ex),
            UnauthorizedAccessException => new VfsException(VfsError.AccessDenied, $"access to '{path}' was denied", ex),
            PathTooLongException => new VfsException(VfsError.InvalidPath, $"'{path}' is too long", ex),
            IOException io when io.HResult is ErrorAlreadyExists or ErrorFileExists or 17 => new VfsException(VfsError.AlreadyExists, $"'{path}' already exists", ex),
            IOException io when io.HResult is ErrorDirNotEmpty or 39 or 66 => new VfsException(VfsError.DirectoryNotEmpty, $"'{path}' is not empty", ex),
            IOException => new VfsException(VfsError.IoError, $"{path}: {ex.Message}", ex),
            NotSupportedException => new VfsException(VfsError.NotSupported, ex.Message, ex),
            _ => new VfsException(VfsError.IoError, $"{path}: {ex.Message}", ex),
        };
    }
}

/// <summary>Thrown from <see cref="ISource.InitialiseAsync"/> to say whether a retry can help.</summary>
public sealed class SourceFailedException : Exception
{
    /// <summary>True when retrying won't help until the settings change (bad credentials, bad path).</summary>
    public bool Permanent { get; }

    public SourceFailedException(string message, bool permanent = false, Exception? inner = null) : base(message, inner)
    {
        Permanent = permanent;
    }
}
