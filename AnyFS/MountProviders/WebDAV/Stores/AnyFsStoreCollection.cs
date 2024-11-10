using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using libVirtualFileSystem.Files;
using libVirtualFileSystem.FileSystemAdapters;
using libVirtualFileSystem.Folders;
using Microsoft.Extensions.Logging;
using NWebDav.Server;
using NWebDav.Server.Props;
using NWebDav.Server.Stores;

namespace AnyFS.MountProviders.WebDAV.Stores;

[DebuggerDisplay("{DirectoryInfo.FullPath}\\")]
public sealed class AnyFsStoreCollection : IStoreCollection
{
    private readonly AnyFsStoreBase _store;
    private readonly ILogger<AnyFsStoreCollection> _logger;

    public AnyFsStoreCollection(AnyFsStoreBase store, AnyFsStoreCollectionPropertyManager propertyManager, Folder directoryInfo, IFileSystemAdapter fileSystemAdapter, ILogger<AnyFsStoreCollection> logger)
    {
        _store = store;
        DirectoryInfo = directoryInfo;
        FileSystemAdapter = fileSystemAdapter;
        _logger = logger;
        PropertyManager = propertyManager;
    }

    public Folder DirectoryInfo { get; }
    public IFileSystemAdapter FileSystemAdapter { get; }

    public string Name => DirectoryInfo.Name;
    public string UniqueKey => DirectoryInfo.FullPath;
    public string FullPath => DirectoryInfo.FullPath;
    public bool IsWritable => false;

    // Disk collections (a.k.a. directories don't have their own data)
    public Task<Stream> GetReadableStreamAsync(CancellationToken cancellationToken) => Task.FromResult(Stream.Null);
    public Task<DavStatusCode> UploadFromStreamAsync(Stream inputStream, CancellationToken cancellationToken) => Task.FromResult(DavStatusCode.Conflict);

    public IPropertyManager PropertyManager { get; }

    public Task<IStoreItem?> GetItemAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.Combine(FullPath, name);
        return Task.FromResult(_store.CreateFromPath(fullPath));
    }

    // Not async, but this is the easiest way to return an IAsyncEnumerable
    public async IAsyncEnumerable<IStoreItem> GetItemsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var subDirectory in FileSystemAdapter.GetFolders(DirectoryInfo.FullPath))
            yield return _store.CreateCollection(subDirectory, FileSystemAdapter);

        // Add all files
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var file in FileSystemAdapter.GetFiles(DirectoryInfo.FullPath))
            yield return _store.CreateItem(file);
    }

    public async Task<StoreItemResult> CreateItemAsync(string name, Stream stream, bool overwrite, CancellationToken cancellationToken)
    {
        return new StoreItemResult(DavStatusCode.Forbidden);
    }

    public Task<StoreCollectionResult> CreateCollectionAsync(string name, bool overwrite, CancellationToken cancellationToken)
    {
        return Task.FromResult(new StoreCollectionResult(DavStatusCode.Forbidden));
    }

    public async Task<StoreItemResult> CopyAsync(IStoreCollection destinationCollection, string name, bool overwrite, CancellationToken cancellationToken)
    {
        // Just create the folder itself
        var result = await destinationCollection.CreateCollectionAsync(name, overwrite, cancellationToken).ConfigureAwait(false);
        return new StoreItemResult(result.Result, result.Collection);
    }

    public bool SupportsFastMove(IStoreCollection destination, string destinationName, bool overwrite)
    {
        // We can only move disk-store collections
        return destination is AnyFsStoreCollection;
    }

    public async Task<StoreItemResult> MoveItemAsync(string sourceName, IStoreCollection destinationCollection, string destinationName, bool overwrite, CancellationToken cancellationToken)
    {
        return new StoreItemResult(DavStatusCode.Forbidden);
    }

    public Task<DavStatusCode> DeleteItemAsync(string name, CancellationToken cancellationToken)
    {
        return Task.FromResult(DavStatusCode.Forbidden);
    }

    public InfiniteDepthMode InfiniteDepthMode => InfiniteDepthMode.Rejected;
}