using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using libVirtualFileSystem.Files;
using libVirtualFileSystem.FileSystemAdapters;
using Microsoft.Extensions.Logging;
using NWebDav.Server;
using NWebDav.Server.Helpers;
using NWebDav.Server.Props;
using NWebDav.Server.Stores;

namespace AnyFS.MountProviders.WebDAV.Stores;

[DebuggerDisplay("{FileInfo.FullPath}")]
public sealed class AnyFsStoreItem : IStoreItem
{
    private readonly AnyFsStoreBase _store;
    private readonly ILogger<AnyFsStoreItem> _logger;

    public AnyFsStoreItem(AnyFsStoreBase store, AnyFsStoreItemPropertyManager propertyManager, FileEntry fileInfo, IFileSystemAdapter fileSystemAdapter, ILogger<AnyFsStoreItem> logger)
    {
        _store = store;
        FileInfo = fileInfo;
        FileSystemAdapter = fileSystemAdapter;
        PropertyManager = propertyManager;
        _logger = logger;
    }

    public IPropertyManager PropertyManager { get; }

    public FileEntry FileInfo { get; }
    public IFileSystemAdapter FileSystemAdapter { get; }

    public bool IsWritable => false;
    public string Name => FileInfo.Name;
    public string UniqueKey => FileInfo.FullPath;
    public string FullPath => FileInfo.FullPath;
    public Task<Stream> GetReadableStreamAsync(CancellationToken cancellationToken)
    {
        var stream = FileSystemAdapter.Download(FileInfo.FullPath);
        return Task.FromResult(stream);
    }

    public async Task<DavStatusCode> UploadFromStreamAsync(Stream inputStream, CancellationToken cancellationToken)
    {
        return DavStatusCode.Forbidden;
    }

    public async Task<StoreItemResult> CopyAsync(IStoreCollection destination, string name, bool overwrite, CancellationToken cancellationToken)
    {
        try
        {
            // If the destination is also a disk-store, then we can use the FileCopy API
            // (it's probably a bit more efficient than copying in C#)
            if (destination is AnyFsStoreCollection diskCollection)
            {
                // Check if the collection is writable
                if (!diskCollection.IsWritable)
                    return new StoreItemResult(DavStatusCode.PreconditionFailed);

                var destinationPath = Path.Combine(diskCollection.FullPath, name);

                // Check if the file already exists
                var fileExists = File.Exists(destinationPath);
                if (fileExists && !overwrite)
                    return new StoreItemResult(DavStatusCode.PreconditionFailed);

                // Copy the file
                File.Copy(FileInfo.FullPath, destinationPath, true);

                // Return the appropriate status
                return new StoreItemResult(fileExists ? DavStatusCode.NoContent : DavStatusCode.Created);
            }
            else
            {
                // Create the item in the destination collection
                var sourceStream = await GetReadableStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (sourceStream.ConfigureAwait(false))
                {
                    return await destination.CreateItemAsync(name, sourceStream, overwrite, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exc)
        {
            _logger.LogError(exc, "Unexpected exception while copying data.");
            return new StoreItemResult(DavStatusCode.InternalServerError);
        }
    }
}