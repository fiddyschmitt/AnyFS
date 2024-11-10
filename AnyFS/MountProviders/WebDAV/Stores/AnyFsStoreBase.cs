using System;
using System.IO;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using libVirtualFileSystem.Files;
using libVirtualFileSystem.FileSystemAdapters;
using libVirtualFileSystem.Folders;
using Microsoft.Extensions.Logging;
using NWebDav.Server.Helpers;
using NWebDav.Server.Stores;

namespace AnyFS.MountProviders.WebDAV.Stores;

public abstract class AnyFsStoreBase : IStore
{
    private readonly AnyFsStoreCollectionPropertyManager _diskStoreCollectionPropertyManager;
    private readonly AnyFsStoreItemPropertyManager _diskStoreItemPropertyManager;
    private readonly ILoggerFactory _loggerFactory;

    protected AnyFsStoreBase(
        AnyFsStoreCollectionPropertyManager diskStoreCollectionPropertyManager,
        AnyFsStoreItemPropertyManager diskStoreItemPropertyManager,
        ILoggerFactory loggerFactory)
    {
        _diskStoreCollectionPropertyManager = diskStoreCollectionPropertyManager;
        _diskStoreItemPropertyManager = diskStoreItemPropertyManager;
        _loggerFactory = loggerFactory;
    }

    public abstract IFileSystemAdapter FileSystemAdapter { get; }

    public Task<IStoreItem?> GetItemAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = GetPathFromUri(uri);
        var item = CreateFromPath(path);
        return Task.FromResult(item);
    }

    public Task<IStoreCollection?> GetCollectionAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Determine the path from the uri
        var path = GetPathFromUri(uri);

        var folder = FileSystemAdapter.GetFolder(path);
        return Task.FromResult<IStoreCollection?>(CreateCollection(folder, FileSystemAdapter));
    }

    private string GetPathFromUri(Uri uri)
    {
        // Determine the path
        var requestedPath = UriHelper.GetDecodedPath(uri)[1..].Replace('/', Path.DirectorySeparatorChar);

        return requestedPath;
    }

    internal IStoreItem? CreateFromPath(string path)
    {
        var folder = FileSystemAdapter.GetFolder(path);

        if (folder != null)
        {
            var result = new AnyFsStoreCollection(this, _diskStoreCollectionPropertyManager, folder, FileSystemAdapter, null);
            return result;
        }

        var file = FileSystemAdapter.GetFile(path);
        if (file != null)
        {
            var result = new AnyFsStoreItem(this, _diskStoreItemPropertyManager, file, FileSystemAdapter, null);
            return result;
        }

        return null;
    }

    internal AnyFsStoreCollection CreateCollection(Folder directoryInfo, IFileSystemAdapter fileSystemAdapter) =>
        new(this, _diskStoreCollectionPropertyManager, directoryInfo, fileSystemAdapter, _loggerFactory.CreateLogger<AnyFsStoreCollection>());

    internal AnyFsStoreItem CreateItem(FileEntry file) =>
        new(this, _diskStoreItemPropertyManager, file, FileSystemAdapter, _loggerFactory.CreateLogger<AnyFsStoreItem>());
}