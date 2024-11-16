using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using NWebDav.Server;
using NWebDav.Server.Helpers;
using NWebDav.Server.Locking;
using NWebDav.Server.Props;

namespace AnyFS.MountProviders.WebDAV.Stores;

public class AnyFsStoreItemPropertyManager : PropertyManager<AnyFsStoreItem>
{
    public AnyFsStoreItemPropertyManager(IHttpContextAccessor httpContextAccessor, ILockingManager lockingManager) : base(GetProperties(httpContextAccessor, lockingManager))
    {
    }

    private static DavProperty<AnyFsStoreItem>[] GetProperties(IHttpContextAccessor httpContextAccessor, ILockingManager lockingManager) => new DavProperty<AnyFsStoreItem>[]
    {
        // RFC-2518 properties
        new DavCreationDate<AnyFsStoreItem>(httpContextAccessor)
        {
            Getter = item => item.FileInfo.CreatedUTC,
            Setter = (item, value) => DavStatusCode.Forbidden
        },
        new DavDisplayName<AnyFsStoreItem>
        {
            Getter = item => item.FileInfo.Name
        },
        new DavGetContentLength<AnyFsStoreItem>
        {
            Getter = item => item.FileInfo.Size
        },
        new DavGetContentType<AnyFsStoreItem>
        {
            Getter = item => MimeTypeHelper.GetMimeType(item.FileInfo.Name)
        },
        new DavGetEtag<AnyFsStoreItem>
        {
            // Calculating the Etag is an expensive operation,
            // because we need to scan the entire file.
            IsExpensive = true,
            GetterAsync = async (item, ct) =>
            {
                var stream = File.OpenRead(item.FileInfo.FullPath);
                await using (stream.ConfigureAwait(false))
                {
                    var hash = await SHA256.Create().ComputeHashAsync(stream, ct).ConfigureAwait(false);
                    return BitConverter.ToString(hash).Replace("-", string.Empty);
                }

            }
        },
        new DavGetLastModified<AnyFsStoreItem>
        {
            Getter = item => item.FileInfo.ModifiedUTC,
            Setter = (item, value) => DavStatusCode.Forbidden
        },
        new DavGetResourceType<AnyFsStoreItem>
        {
            Getter = _ => null
        },

        // Default locking property handling via the LockingManager
        new DavLockDiscoveryDefault<AnyFsStoreItem>(lockingManager),
        new DavSupportedLockDefault<AnyFsStoreItem>(lockingManager),

        // Hopmann/Lippert collection properties
        // (although not a collection, the IsHidden property might be valuable)
        new DavExtCollectionIsHidden<AnyFsStoreItem>
        {
            Getter = item => false
        },

        // Win32 extensions
        new Win32CreationTime<AnyFsStoreItem>
        {
            Getter = item => item.FileInfo.CreatedUTC,
            Setter = (item, value) => DavStatusCode.Forbidden
        },
        new Win32LastAccessTime<AnyFsStoreItem>
        {
            Getter = item => item.FileInfo.AccessedUTC,
            Setter = (item, value) => DavStatusCode.Forbidden
        },
        new Win32LastModifiedTime<AnyFsStoreItem>
        {
            Getter = item => item.FileInfo.ModifiedUTC,
            Setter = (item, value) => DavStatusCode.Forbidden
        },
        new Win32FileAttributes<AnyFsStoreItem>
        {
            Getter = item => {
                return FileAttributes.None;
            },
            Setter = (item, value) => DavStatusCode.Forbidden
        }
    };
}