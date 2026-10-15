namespace AnyFS.Mounts.Ftp;

/// <summary>
/// An FTP server shared by every collection mounted on one bind address and port: AnyFS's own
/// (<see cref="Builtin.BuiltinFtpServer"/>, the default) or the FubarDev.FtpServer library
/// (<see cref="FtpHost"/>), chosen per mount by the <c>Server</c> setting. A collection registers
/// itself with the server it is mounted on and leaves when the mount stops.
/// </summary>
internal interface IFtpCollectionServer : IAsyncDisposable
{
    void Register(FtpCollection collection);

    void Unregister(string collectionName);

    int CollectionCount { get; }
}
