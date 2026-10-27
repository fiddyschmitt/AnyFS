using AnyFS.Core.Plugins;
using AnyFS.Mounts.Ftp;
using AnyFS.Mounts.Fuse;
using AnyFS.Mounts.Http;
using AnyFS.Mounts.Smb;
using AnyFS.Mounts.WebDav;
using AnyFS.Mounts.WinFsp;
using AnyFS.Sources.GitLab;
using AnyFS.Sources.Mirror;
using AnyFS.Sources.Outlook;
using AnyFS.Sources.SharePoint;
using AnyFS.Sources.Sshfs;
using AnyFS.Sources.ThreeDX;

namespace AnyFS.App;

/// <summary>The plugins compiled into anyfs, registered directly (no reflection, so trimming is safe).</summary>
public static class BuiltIns
{
    public static PluginRegistry CreateRegistry()
    {
        var registry = new PluginRegistry();
        registry.AddBuiltIn(new MirrorPlugin());
        registry.AddBuiltIn(new SshfsPlugin());
        registry.AddBuiltIn(new SharePointPlugin());
        registry.AddBuiltIn(new ThreeDXPlugin());
        registry.AddBuiltIn(new OutlookPlugin());
        registry.AddBuiltIn(new GitLabPlugin());
        registry.AddBuiltIn(new WinFspMountProvider());
        registry.AddBuiltIn(new FuseMountProvider());
        registry.AddBuiltIn(new SmbMountProvider());
        registry.AddBuiltIn(new WebDavMountProvider());
        registry.AddBuiltIn(new FtpMountProvider());
        registry.AddBuiltIn(new HttpMountProvider());
        return registry;
    }
}
