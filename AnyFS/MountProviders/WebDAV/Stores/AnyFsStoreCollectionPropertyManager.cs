using System.IO;
using System.Linq;
using System.Xml.Linq;
using libVirtualFileSystem.Files;
using libVirtualFileSystem.Folders;
using Microsoft.AspNetCore.Http;
using NWebDav.Server;
using NWebDav.Server.Locking;
using NWebDav.Server.Props;

namespace AnyFS.MountProviders.WebDAV.Stores;

public class AnyFsStoreCollectionPropertyManager : PropertyManager<AnyFsStoreCollection>
{
    private static readonly XElement s_xDavCollection = new(WebDavNamespaces.DavNs + "collection");

    public AnyFsStoreCollectionPropertyManager(IHttpContextAccessor httpContextAccessor, ILockingManager lockingManager) : base(GetProperties(httpContextAccessor, lockingManager))
    {
    }

    private static DavProperty<AnyFsStoreCollection>[] GetProperties(IHttpContextAccessor httpContextAccessor, ILockingManager lockingManager) => new DavProperty<AnyFsStoreCollection>[]
    {
        // RFC-2518 properties
        new DavCreationDate<AnyFsStoreCollection>(httpContextAccessor)
        {
            Getter = collection => collection.DirectoryInfo.Created.ToUniversalTime(),
            Setter = (collection, value) => DavStatusCode.Forbidden
        },
        new DavDisplayName<AnyFsStoreCollection>
        {
            Getter = collection => collection.DirectoryInfo.Name
        },
        new DavGetLastModified<AnyFsStoreCollection>
        {
            Getter = collection => collection.DirectoryInfo.Modified.ToUniversalTime(),
            Setter = (collection, value) => DavStatusCode.Forbidden
        },
        new DavGetResourceType<AnyFsStoreCollection>
        {
            Getter = _ => new[] { s_xDavCollection }
        },

        // Default locking property handling via the LockingManager
        new DavLockDiscoveryDefault<AnyFsStoreCollection>(lockingManager),
        new DavSupportedLockDefault<AnyFsStoreCollection>(lockingManager),

        // Hopmann/Lippert collection properties
        new DavExtCollectionChildCount<AnyFsStoreCollection>
        {
            //Getter = collection => collection.DirectoryInfo.Children.Count()
            Getter = collection =>
            {
                var fileCount = collection.FileSystemAdapter.GetFiles(collection.FullPath).Count;
                var subfolderCount = collection.FileSystemAdapter.GetFolders(collection.FullPath).Count;
                var childCount = fileCount + subfolderCount;
                return childCount;
            }
        },
        new DavExtCollectionIsFolder<AnyFsStoreCollection>
        {
            Getter = _ => true
        },
        new DavExtCollectionIsHidden<AnyFsStoreCollection>
        {
            Getter = collection => false
        },
        new DavExtCollectionIsStructuredDocument<AnyFsStoreCollection>
        {
            Getter = _ => false
        },
        new DavExtCollectionHasSubs<AnyFsStoreCollection>
        {
            //Getter = collection => collection.DirectoryInfo.Children.OfType<Folder>().Any()
            Getter = collection =>
            {
                var hasSubs = collection.FileSystemAdapter.GetFolders(collection.FullPath).Any();
                return hasSubs;
            }
        },
        new DavExtCollectionNoSubs<AnyFsStoreCollection>
        {
            Getter = _ => false
        },
        new DavExtCollectionObjectCount<AnyFsStoreCollection>
        {
            //Getter = collection => collection.DirectoryInfo.Children.OfType<FileEntry>().Count()
            Getter = collection =>
            {
                var fileCount = collection.FileSystemAdapter.GetFiles(collection.FullPath).Count;
                var subfolderCount = collection.FileSystemAdapter.GetFolders(collection.FullPath).Count;
                var childCount = fileCount + subfolderCount;
                return childCount;
            }
        },
        new DavExtCollectionReserved<AnyFsStoreCollection>
        {
            Getter = collection => !collection.IsWritable
        },
        new DavExtCollectionVisibleCount<AnyFsStoreCollection>
        {
            Getter = collection =>
            {
                var fileCount = collection.FileSystemAdapter.GetFiles(collection.FullPath).Count;
                var subfolderCount = collection.FileSystemAdapter.GetFolders(collection.FullPath).Count;
                var childCount = fileCount + subfolderCount;
                return childCount;
            }
        },

        // Win32 extensions
        new Win32CreationTime<AnyFsStoreCollection>
        {
            Getter = collection => collection.DirectoryInfo.Created.ToUniversalTime(),
            Setter = (collection, value) => DavStatusCode.Forbidden
        },
        new Win32LastAccessTime<AnyFsStoreCollection>
        {
            Getter = collection => collection.DirectoryInfo.Accessed.ToUniversalTime(),
            Setter = (collection, value) => DavStatusCode.Forbidden
        },
        new Win32LastModifiedTime<AnyFsStoreCollection>
        {
            Getter = collection => collection.DirectoryInfo.Modified.ToUniversalTime(),
            Setter = (collection, value) => DavStatusCode.Forbidden
        },
        new Win32FileAttributes<AnyFsStoreCollection>
        {
            Getter = collection => FileAttributes.Directory,
            Setter = (collection, value) => DavStatusCode.Forbidden
        }
    };
}