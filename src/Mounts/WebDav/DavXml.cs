using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AnyFS.Sdk;
using Microsoft.AspNetCore.StaticFiles;

namespace AnyFS.Mounts.WebDav;

internal enum PropfindKind
{
    AllProp,
    PropName,
    Prop,
}

internal sealed record PropfindRequest(PropfindKind Kind, IReadOnlyList<XName> Names);

internal sealed record LockRequest(bool Exclusive, bool Write, string? OwnerXml);

/// <summary>The XML side of RFC 4918: request bodies in, multistatus and lockdiscovery out. Everything is in the DAV: namespace with the D prefix.</summary>
internal static class DavXml
{
    public static readonly XNamespace D = "DAV:";
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        MaxCharactersInDocument = 1 << 20,
        XmlResolver = null,
        Async = true,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    /// <summary>The live properties every resource has (what allprop returns).</summary>
    public static readonly XName[] LiveProps =
    [
        D + "displayname", D + "resourcetype", D + "getcontentlength", D + "getlastmodified", D + "creationdate",
        D + "getetag", D + "getcontenttype", D + "supportedlock", D + "lockdiscovery",
    ];

    public static readonly XName QuotaAvailable = D + "quota-available-bytes";
    public static readonly XName QuotaUsed = D + "quota-used-bytes";

    /// <summary>Reads a request body as XML; null when the body is empty. Throws <see cref="XmlException"/> on bad XML.</summary>
    public static async Task<XDocument?> ReadAsync(Stream body, long? contentLength, CancellationToken ct)
    {
        if (contentLength == 0)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        await body.CopyToAsync(buffer, ct).ConfigureAwait(false);
        if (buffer.Length == 0)
        {
            return null;
        }

        buffer.Position = 0;
        using var reader = XmlReader.Create(buffer, ReaderSettings);
        return await XDocument.LoadAsync(reader, LoadOptions.None, ct).ConfigureAwait(false);
    }

    public static PropfindRequest ParsePropfind(XDocument? doc)
    {
        var root = doc?.Root;
        if (root is null || root.Name != D + "propfind")
        {
            return new PropfindRequest(PropfindKind.AllProp, []);
        }

        if (root.Element(D + "propname") is not null)
        {
            return new PropfindRequest(PropfindKind.PropName, []);
        }

        if (root.Element(D + "prop") is { } prop)
        {
            return new PropfindRequest(PropfindKind.Prop, prop.Elements().Select(e => e.Name).ToList());
        }

        return new PropfindRequest(PropfindKind.AllProp, root.Element(D + "include")?.Elements().Select(e => e.Name).ToList() ?? []);
    }

    /// <summary>The property names a PROPPATCH sets or removes, in order.</summary>
    public static List<(XName Name, bool Set)> ParseProppatch(XDocument? doc)
    {
        var result = new List<(XName, bool)>();
        var root = doc?.Root;
        if (root is null || root.Name != D + "propertyupdate")
        {
            return result;
        }

        foreach (var update in root.Elements())
        {
            var set = update.Name == D + "set";
            if (!set && update.Name != D + "remove")
            {
                continue;
            }

            foreach (var name in update.Elements(D + "prop").SelectMany(p => p.Elements()).Select(e => e.Name))
            {
                result.Add((name, set));
            }
        }

        return result;
    }

    public static LockRequest? ParseLockInfo(XDocument? doc)
    {
        var root = doc?.Root;
        if (root is null || root.Name != D + "lockinfo")
        {
            return null;
        }

        var exclusive = root.Element(D + "lockscope")?.Element(D + "exclusive") is not null;
        var write = root.Element(D + "locktype")?.Element(D + "write") is not null;
        var owner = root.Element(D + "owner")?.ToString(SaveOptions.DisableFormatting);
        return new LockRequest(exclusive, write, owner);
    }

    public static XElement MultiStatus(IEnumerable<XElement> responses)
        => new(D + "multistatus", new XAttribute(XNamespace.Xmlns + "D", D.NamespaceName), responses);

    public static XElement Response(string href, params XElement[] propstats)
        => new(D + "response", new XElement(D + "href", href), propstats);

    public static XElement Propstat(IEnumerable<XElement> props, int status)
        => new(D + "propstat", new XElement(D + "prop", props), new XElement(D + "status", StatusLine(status)));

    /// <summary>A response with the properties asked for: a 200 propstat for the ones we have, a 404 one for the rest.</summary>
    public static XElement ResponseFor(string href, VfsEntry entry, DavLock? held, string? lockRootHref, VfsSpace? space, PropfindRequest request, bool writable)
    {
        if (request.Kind == PropfindKind.PropName)
        {
            return Response(href, Propstat(LiveProps.Select(n => new XElement(n)), 200));
        }

        var names = request.Kind == PropfindKind.AllProp ? LiveProps.Concat(request.Names).Distinct().ToList() : request.Names.ToList();
        var found = new List<XElement>();
        var missing = new List<XElement>();
        foreach (var name in names)
        {
            var value = LiveProp(name, entry, held, lockRootHref, space, writable);
            if (value is null)
            {
                missing.Add(new XElement(name));
            }
            else
            {
                found.Add(value);
            }
        }

        var propstats = new List<XElement>();
        if (found.Count > 0 || missing.Count == 0)
        {
            propstats.Add(Propstat(found, 200));
        }

        if (missing.Count > 0)
        {
            propstats.Add(Propstat(missing, 404));
        }

        return Response(href, propstats.ToArray());
    }

    public static XElement ActiveLock(DavLock held, string lockRootHref)
    {
        var active = new XElement(D + "activelock",
            new XElement(D + "locktype", new XElement(D + "write")),
            new XElement(D + "lockscope", new XElement(D + "exclusive")),
            new XElement(D + "depth", held.InfiniteDepth ? "infinity" : "0"));
        if (held.OwnerXml is { } owner)
        {
            try
            {
                active.Add(XElement.Parse(owner));
            }
            catch (XmlException)
            {
            }
        }

        active.Add(new XElement(D + "timeout", held.TimeoutHeader));
        active.Add(new XElement(D + "locktoken", new XElement(D + "href", held.Token)));
        active.Add(new XElement(D + "lockroot", new XElement(D + "href", lockRootHref)));
        return active;
    }

    public static XElement LockDiscoveryProp(DavLock held, string lockRootHref)
        => new(D + "prop", new XAttribute(XNamespace.Xmlns + "D", D.NamespaceName), new XElement(D + "lockdiscovery", ActiveLock(held, lockRootHref)));

    public static XElement Error(XName condition)
        => new(D + "error", new XAttribute(XNamespace.Xmlns + "D", D.NamespaceName), new XElement(condition));

    public static byte[] ToBytes(XElement root)
        => Encoding.UTF8.GetBytes(new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString(SaveOptions.DisableFormatting));

    public static string ETagFor(VfsEntry entry)
        => entry.ETag is { Length: > 0 } tag ? '"' + tag.Trim('"') + '"' : $"\"{entry.Length:x}-{(entry.Modified?.UtcTicks ?? 0):x}\"";

    public static string ContentTypeFor(string name) => ContentTypes.TryGetContentType(name, out var known) ? known : "application/octet-stream";

    public static string StatusLine(int status) => status switch
    {
        200 => "HTTP/1.1 200 OK",
        403 => "HTTP/1.1 403 Forbidden",
        404 => "HTTP/1.1 404 Not Found",
        409 => "HTTP/1.1 409 Conflict",
        423 => "HTTP/1.1 423 Locked",
        424 => "HTTP/1.1 424 Failed Dependency",
        _ => $"HTTP/1.1 {status} {(status >= 500 ? "Internal Server Error" : "Error")}",
    };

    private static XElement? LiveProp(XName name, VfsEntry entry, DavLock? held, string? lockRootHref, VfsSpace? space, bool writable)
    {
        if (name.Namespace != D)
        {
            return null;
        }

        switch (name.LocalName)
        {
            case "displayname":
                return new XElement(name, entry.Name);
            case "resourcetype":
                return entry.IsDirectory ? new XElement(name, new XElement(D + "collection")) : new XElement(name);
            case "getcontentlength":
                return entry.IsDirectory ? null : new XElement(name, entry.Length.ToString(CultureInfo.InvariantCulture));
            case "getlastmodified":
                return new XElement(name, (entry.Modified ?? DateTimeOffset.UnixEpoch).UtcDateTime.ToString("R", CultureInfo.InvariantCulture));
            case "creationdate":
                return new XElement(name, (entry.Created ?? entry.Modified ?? DateTimeOffset.UnixEpoch).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            case "getetag":
                return entry.IsDirectory ? null : new XElement(name, ETagFor(entry));
            case "getcontenttype":
                return entry.IsDirectory ? new XElement(name, "httpd/unix-directory") : new XElement(name, ContentTypeFor(entry.Name));
            case "supportedlock":
                return writable
                    ? new XElement(name, new XElement(D + "lockentry", new XElement(D + "lockscope", new XElement(D + "exclusive")), new XElement(D + "locktype", new XElement(D + "write"))))
                    : new XElement(name);
            case "lockdiscovery":
                return held is null || lockRootHref is null ? new XElement(name) : new XElement(name, ActiveLock(held, lockRootHref));
            case "quota-available-bytes":
                return space is null ? null : new XElement(name, space.FreeBytes.ToString(CultureInfo.InvariantCulture));
            case "quota-used-bytes":
                return space is null ? null : new XElement(name, Math.Max(0, space.TotalBytes - space.FreeBytes).ToString(CultureInfo.InvariantCulture));
            default:
                return null;
        }
    }
}
