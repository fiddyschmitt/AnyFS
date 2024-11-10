using libVirtualFileSystem.FileSystemAdapters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AnyFS.MountProviders.WebDAV.Stores;

public class AnyFsStoreOptions
{
    public IFileSystemAdapter FileSystemAdapter;
}

public sealed class AnyFsStore : AnyFsStoreBase
{
    private readonly IOptions<AnyFsStoreOptions> _options;

    public AnyFsStore(
        IOptions<AnyFsStoreOptions> options,
        AnyFsStoreCollectionPropertyManager diskStoreCollectionPropertyManager,
        AnyFsStoreItemPropertyManager diskStoreItemPropertyManager,
        ILoggerFactory loggerFactory)
        : base(diskStoreCollectionPropertyManager, diskStoreItemPropertyManager, loggerFactory)
    {
        _options = options;
    }

    public override IFileSystemAdapter FileSystemAdapter => _options.Value.FileSystemAdapter;
}