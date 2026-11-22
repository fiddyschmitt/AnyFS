using System.Text.Json.Nodes;

namespace AnyFS.Sources.Common;

/// <summary>
/// The pages (tabs) open in a browser driven over DevTools, kept from its Target events for a web session that listens
/// to what a page sends rather than asks (Outlook's and Teams'): a page is there from <c>Target.attachedToTarget</c>
/// until <c>Target.detachedFromTarget</c> or <c>Target.targetDestroyed</c>, and <c>Target.targetInfoChanged</c> says
/// where it has gone to. No page left is how a closed window looks while the browser itself stays up in the background.
/// The browser sends these once asked to (<c>Target.setDiscoverTargets</c>, and <c>Target.setAutoAttach</c> flattened).
/// Until 22-Nov-2026 each session kept its own pages and took none out, so a window that was closed was waited on for
/// its ten minutes; and a page's new address was written to every page.
/// </summary>
public sealed class DevToolsPages
{
    private readonly object _gate = new();

    // in the order they came: the first is the one the browser opened with
    private readonly List<(string Session, string Target, string Url)> _pages = [];

    /// <summary>Whether a page is open.</summary>
    public bool Any
    {
        get
        {
            lock (_gate)
            {
                return _pages.Count > 0;
            }
        }
    }

    /// <summary>The DevTools session of the first page still open, for commands to it; null when there is none.</summary>
    public string? FirstSession
    {
        get
        {
            lock (_gate)
            {
                return _pages.Count > 0 ? _pages[0].Session : null;
            }
        }
    }

    /// <summary>Where the first page still open that has an address is; empty when there is none.</summary>
    public string Where
    {
        get
        {
            lock (_gate)
            {
                foreach (var page in _pages)
                {
                    if (page.Url.Length > 0)
                    {
                        return page.Url;
                    }
                }

                return "";
            }
        }
    }

    /// <summary>Takes an event of the browser's (<see cref="DevToolsClient.Event"/>); any but those four is nothing to it.</summary>
    public void On(string method, JsonNode parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        switch (method)
        {
            case "Target.attachedToTarget":
                {
                    // a service worker, a frame or a worker is attached the same way, and is no page
                    var info = parameters["targetInfo"];
                    if (info?["type"]?.GetValue<string>() == "page" && parameters["sessionId"]?.GetValue<string>() is { } session)
                    {
                        lock (_gate)
                        {
                            _pages.Add((session, info["targetId"]?.GetValue<string>() ?? "", info["url"]?.GetValue<string>() ?? ""));
                        }
                    }

                    break;
                }

            case "Target.targetInfoChanged":
                {
                    // said of every target the browser has: the page it is about is found by its target
                    var info = parameters["targetInfo"];
                    if (info?["targetId"]?.GetValue<string>() is { } target)
                    {
                        var url = info["url"]?.GetValue<string>() ?? "";
                        lock (_gate)
                        {
                            for (var i = 0; i < _pages.Count; i++)
                            {
                                if (_pages[i].Target == target)
                                {
                                    _pages[i] = (_pages[i].Session, target, url);
                                }
                            }
                        }
                    }

                    break;
                }

            case "Target.detachedFromTarget":
                {
                    if (parameters["sessionId"]?.GetValue<string>() is { } session)
                    {
                        lock (_gate)
                        {
                            _pages.RemoveAll(p => p.Session == session);
                        }
                    }

                    break;
                }

            case "Target.targetDestroyed":
                {
                    if (parameters["targetId"]?.GetValue<string>() is { } target)
                    {
                        lock (_gate)
                        {
                            _pages.RemoveAll(p => p.Target == target);
                        }
                    }

                    break;
                }
        }
    }
}
