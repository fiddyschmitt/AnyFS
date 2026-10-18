using AnyFS.Core.Plugins;
using AnyFS.Mounts.Ftp;
using AnyFS.Mounts.Fuse;
using AnyFS.Mounts.Http;
using AnyFS.Mounts.Smb;
using AnyFS.Mounts.WebDav;
using AnyFS.Mounts.WinFsp;
using AnyFS.Sources.Mirror;
using AnyFS.Sources.SharePoint;
using AnyFS.Sources.Sshfs;

namespace AnyFS.App;

/// <summary>The plugins compiled into anyfs, registered directly (no reflection, so trimming is safe).</summary>
public static class BuiltIns
{
    public static PluginRegistry CreateRegistry()
    {
        var registry = new PluginRegistry();
        registry.AddBuiltIn(new MirrorPlugin());
        registry.AddBuiltIn(new SshfsPlugin());
        registry.AddBuiltIn(new SharePointOnlinePlugin());
        registry.AddBuiltIn(new SharePointServerPlugin());
        registry.AddBuiltIn(new SharePointEmbeddedPlugin());
        registry.AddBuiltIn(new WinFspMountProvider());
        registry.AddBuiltIn(new FuseMountProvider());
        registry.AddBuiltIn(new SmbMountProvider());
        registry.AddBuiltIn(new WebDavMountProvider());
        registry.AddBuiltIn(new FtpMountProvider());
        registry.AddBuiltIn(new HttpMountProvider());
        return registry;
    }
}
