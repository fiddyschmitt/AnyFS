using AnyFS.Core.Plugins;
using AnyFS.Mounts.Ftp;
using AnyFS.Mounts.Http;
using AnyFS.Mounts.WebDav;
using AnyFS.Sources.Mirror;

namespace AnyFS.App;

/// <summary>The plugins compiled into anyfs, registered directly (no reflection, so trimming is safe).</summary>
public static class BuiltIns
{
    public static PluginRegistry CreateRegistry()
    {
        var registry = new PluginRegistry();
        registry.AddBuiltIn(new MirrorPlugin());
        registry.AddBuiltIn(new HttpMountProvider());
        registry.AddBuiltIn(new WebDavMountProvider());
        registry.AddBuiltIn(new FtpMountProvider());
        return registry;
    }
}
