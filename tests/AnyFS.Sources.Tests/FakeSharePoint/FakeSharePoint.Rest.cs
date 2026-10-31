using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AnyFS.Sources.Common;

namespace AnyFS.Sources.Tests;

/// <summary>
/// SharePoint's REST API as a farm and SharePoint Online answer it (the calls RestDocumentStore and SharePointRestClient
/// make, and those the live tests make to set things up): nometadata JSON, server-relative paths quoted and
/// percent-encoded in decodedurl, a request digest on every POST, CSOM for file times, a file's own address for its
/// bytes (206 for a range), pages as list items, search for site collections.
/// </summary>
internal sealed partial class FakeSharePoint
{
    private const string FarmChallenge = "Basic realm=\"SharePoint\"";

    private readonly Dictionary<string, DateTimeOffset> _digests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Account> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, RestUpload> _restUploads = [];
    private readonly Dictionary<FileItem, int> _pageIds = [];
    private int _nextPageId = 1;

    /// <summary>Sign-ins to the web session so far (the browser's: at the start, and each renewal).</summary>
    public int SessionSignIns { get; private set; }

    /// <summary>Whether a renewal of the web session needs a person (the browser profile's sign-in has lapsed too).</summary>
    public bool BrowserLapsed { get; set; }

    /// <summary>Whether SharePoint Online lets a site's own folder take a page (custom script allowed; it is not on modern sites).</summary>
    public bool CustomScript { get; set; }

    /// <summary>Whether new subsites may be made (SharePoint Online refuses them in a tenant made since 2024 or so).</summary>
    public bool SubsitesAllowed { get; set; } = true;

    // --- sign-in --------------------------------------------------------------------------------------------------

    /// <summary>
    /// The web session's cookies for <paramref name="account"/>, as a browser signed in to the tenant holds them:
    /// FedAuth on the host, rtFa and SIMI on sharepoint.com. Throws <see cref="SignInRequiredException"/> for a renewal
    /// (not <paramref name="interactive"/>) when <see cref="BrowserLapsed"/>.
    /// </summary>
    public IReadOnlyList<SessionCookie> SignInSession(string account, bool interactive = true)
    {
        lock (_gate)
        {
            if (!interactive && BrowserLapsed)
            {
                throw new SignInRequiredException($"The sign-in to {Host} has lapsed; sign in again with the browser.");
            }

            var known = _accounts.TryGetValue(account, out var a) ? a : throw new InvalidOperationException($"no account {account} here");
            SessionSignIns++;
            var fedAuth = "77u/" + NewSecret(48);
            _sessions[fedAuth] = known;
            return
            [
                new SessionCookie("FedAuth", fedAuth, Host, "/", Secure: true, HttpOnly: true),
                new SessionCookie("rtFa", NewSecret(24), ".sharepoint.com", "/", Secure: true, HttpOnly: true),
                new SessionCookie("SIMI", NewSecret(6), ".sharepoint.com", "/", Secure: true, HttpOnly: true),
            ];
        }
    }

    /// <summary>Every web session's cookies stop working, as when they lapse: requests are refused until it is renewed.</summary>
    public void ExpireSessions()
    {
        lock (_gate)
        {
            _sessions.Clear();
        }
    }

    /// <summary>Every request digest stops working (the 403 "security validation" until a fresh one is asked for).</summary>
    public void ExpireDigests()
    {
        lock (_gate)
        {
            _digests.Clear();
        }
    }

    /// <summary>Who the request is, or the refusal of it: the farm's 401, SharePoint Online's 403.</summary>
    private Response? SignInLocked(Request request)
    {
        if (Flavour == Kind.Online)
        {
            if (request.Cookie("FedAuth") is { } fedAuth && _sessions.TryGetValue(fedAuth, out var signedIn))
            {
                request.Account = signedIn;
                return null;
            }

            // no session, or one that has lapsed: the same 403 as an account refused, without a new session cookie
            return OnlineRefusal(request);
        }

        if (SignIn == FarmSignIn.Windows)
        {
            if (request.WindowsUser is null)
            {
                return Response.Empty(401).With("WWW-Authenticate", "NTLM");
            }

            if (!_accounts.TryGetValue(WindowsUserAs.Length > 0 ? WindowsUserAs : request.WindowsUser, out var windows))
            {
                windows = new Account(request.WindowsUser, null);
                _accounts[request.WindowsUser] = windows;
            }

            request.Account = windows;
            return null;
        }

        // Basic: the user name and password checked here; a wrong one is a 401 with no body at all, as the farms answer
        var header = request.Header("Authorization");
        if (header is null || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return Response.Empty(401).With("WWW-Authenticate", FarmChallenge);
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
        }
        catch (FormatException)
        {
            return Response.Empty(401).With("WWW-Authenticate", FarmChallenge);
        }

        var colon = decoded.IndexOf(':');
        var user = colon < 0 ? decoded : decoded[..colon];
        var password = colon < 0 ? "" : decoded[(colon + 1)..];
        if (!_accounts.TryGetValue(user, out var account) || account.Password != password)
        {
            return Response.Empty(401).With("WWW-Authenticate", FarmChallenge);
        }

        request.Account = account;
        return null;
    }

    /// <summary>
    /// SharePoint Online's refusal, the same for a lapsed session and for an account refused a site or file (20-Oct-2026):
    /// 403 with X-Forms_Based_Auth_Required and its fellows, an UnauthorizedAccessException body; for a page, "403
    /// FORBIDDEN". A signed-in request's refusal sets the session cookies again, as every signed-in answer does.
    /// </summary>
    private Response OnlineRefusal(Request request, Web? web = null)
    {
        var page = !request.RawPath.Contains("/_api/", StringComparison.OrdinalIgnoreCase) && !request.RawPath.Contains("/_vti_bin/", StringComparison.OrdinalIgnoreCase);
        var source = page ? request.RawPath : "/_vti_bin/client.svc/" + request.RawPath[(request.RawPath.IndexOf("/_api/", StringComparison.OrdinalIgnoreCase) + 6)..];
        var response = page
            ? Response.Text(403, "403 FORBIDDEN")
            : Response.Json(403, RestError("-2147024891, System.UnauthorizedAccessException", "Attempted to perform an unauthorized operation."));
        response
            .With("X-Forms_Based_Auth_Required", $"{Origin}/_forms/default.aspx?ReturnUrl=/_layouts/15/error.aspx&Source={Uri.EscapeDataString(Uri.UnescapeDataString(source)).ToLowerInvariant()}")
            .With("X-Forms_Based_Auth_Return_Url", $"{Origin}/_layouts/15/error.aspx")
            .With("X-MSDAVEXT_Error", "917656; Access+denied.+Before+opening+files+in+this+location%2c+you+must+first+browse+to+the+web+site+and+select+the+option+to+login+automatically.")
            .With("X-IDCRL_AUTH_PARAMS_V1", $"IDCRL Type=\"BPOSIDCRL\", EndPoint=\"{web?.Path}/_vti_bin/idcrl.svc/\", RootDomain=\"sharepoint.com\", Policy=\"MBI\"");
        return Answered(request, response);
    }

    /// <summary>A signed-in answer: on SharePoint Online it sets the session cookies again, 5 days on (20-Oct-2026), refusals included.</summary>
    private Response Answered(Request request, Response response)
    {
        if (Flavour == Kind.Online && request.Account is not null && request.Cookie("FedAuth") is { } fedAuth)
        {
            var expires = DateTimeOffset.UtcNow.AddDays(5).ToString("R", CultureInfo.InvariantCulture);
            response.With("Set-Cookie", $"FedAuth={fedAuth}; expires={expires}; path=/; SameSite=None; secure; HttpOnly");
            response.With("Set-Cookie", $"rtFa={request.Cookie("rtFa") ?? NewSecret(24)}; domain=sharepoint.com; expires={expires}; path=/; SameSite=None; secure; HttpOnly");
        }

        response.With("SPRequestGuid", Guid.NewGuid().ToString());
        response.With("MicrosoftSharePointTeamServices", Flavour == Kind.Online ? "16.0.0.27807" : "16.0.0.10337");
        return response;
    }

    /// <summary>The refusal of an account signed in but not allowed: the farm's 401 with SharePoint's own error (a Windows sign-in), SharePoint Online's 403.</summary>
    private Response Denied(Request request, Web web) => Flavour == Kind.Online
        ? OnlineRefusal(request, web)
        : Answered(request, Response.Json(401, RestError("-2147024891, System.UnauthorizedAccessException", "Access denied. You do not have permission to perform this action or access this resource.")).With("WWW-Authenticate", SignIn == FarmSignIn.Windows ? "NTLM" : FarmChallenge));

    // --- SharePoint's own addresses --------------------------------------------------------------------------------

    /// <summary>A request to the farm's or the tenant's SharePoint: the site API, CSOM, or a file's own address.</summary>
    private Response SharePoint(Request request)
    {
        if (SignInLocked(request) is { } refusal)
        {
            return refusal;
        }

        var api = request.RawPath.IndexOf("/_api/", StringComparison.OrdinalIgnoreCase);
        if (api < 0 && request.RawPath.EndsWith("/_api", StringComparison.OrdinalIgnoreCase))
        {
            api = request.RawPath.Length - 5;
        }

        if (api >= 0)
        {
            // the API answers only at a web's own address: under a library or a folder (or nothing) it is 404
            var webPath = Uri.UnescapeDataString(request.RawPath[..api]);
            var web = WebAt(webPath);
            if (web is null)
            {
                return Answered(request, Response.Text(404, Flavour == Kind.Online ? "" : "404 NOT FOUND"));
            }

            var rest = api + 5 >= request.RawPath.Length ? "" : request.RawPath[(api + 6)..];
            return Answered(request, Api(request, web, rest));
        }

        if (request.RawPath.EndsWith("/_vti_bin/client.svc/ProcessQuery", StringComparison.OrdinalIgnoreCase))
        {
            var webPath = Uri.UnescapeDataString(request.RawPath[..request.RawPath.IndexOf("/_vti_bin/", StringComparison.OrdinalIgnoreCase)]);
            return WebAt(webPath) is { } web ? Answered(request, ProcessQuery(request, web)) : Answered(request, Response.Empty(404));
        }

        return Answered(request, Content(request));
    }

    private Response Api(Request request, Web web, string rest)
    {
        var calls = ParseCalls(rest);
        var head = calls.Count == 0 ? "" : calls[0].Name;
        if (head.Equals("contextinfo", StringComparison.OrdinalIgnoreCase) && request.Method == "POST")
        {
            return RoleOf(web, request.Account) >= Role.Read ? ContextInfo(web) : Denied(request, web);
        }

        if (RoleOf(web, request.Account) < Role.Read)
        {
            return Denied(request, web);
        }

        if (request.Method == "POST" && !DigestValid(request.Header("X-RequestDigest")))
        {
            return Response.Json(403, RestError("-2130575252, Microsoft.SharePoint.SPException", "The security validation for this page is invalid and might be corrupted. Please use your web browser's Back button to try your operation again."));
        }

        try
        {
            return head.ToLowerInvariant() switch
            {
                "web" => WebApi(request, web, calls.Skip(1).ToList()),
                "site" => Response.Json(200, new JsonObject { ["Url"] = web.Collection.Url, ["Id"] = web.Collection.Id.ToString() }),
                "search" when calls.Count > 1 && calls[1].Name.Equals("query", StringComparison.OrdinalIgnoreCase) => Search(request),
                "sitepages" => SitePagesApi(request, web, calls.Skip(1).ToList()),
                _ => NotUnderstood(rest),
            };
        }
        catch (RestFault fault)
        {
            return fault.Response;
        }
    }

    private Response WebApi(Request request, Web web, List<Call> calls)
    {
        if (calls.Count == 0)
        {
            return Response.Json(200, new JsonObject { ["Title"] = web.Title, ["Url"] = web.Url, ["ServerRelativeUrl"] = web.Path.Length == 0 ? "/" : web.Path, ["Id"] = web.Id.ToString() });
        }

        var name = calls[0].Name.ToLowerInvariant();
        switch (name)
        {
            case "title":
                return Response.Json(200, new JsonObject { ["value"] = web.Title });
            case "lists":
                return Lists(request, web);
            case "getsubwebsfilteredforcurrentuser":
                return Response.Json(200, new JsonObject
                {
                    ["value"] = new JsonArray(web.Subwebs.Where(w => RoleOf(w, request.Account) >= Role.Read)
                        .Select(w => (JsonNode)new JsonObject { ["ServerRelativeUrl"] = w.Path, ["Title"] = w.Title, ["Url"] = w.Url }).ToArray()),
                });
            case "webinfos" when calls.Count > 1 && calls[1].Name.Equals("add", StringComparison.OrdinalIgnoreCase):
                return AddWeb(request, web);
            case "rootfolder":
                return RootFolderApi(request, web, calls.Skip(1).ToList());
            case "folders" when calls.Count > 1 && calls[1].Name.Equals("AddUsingPath", StringComparison.OrdinalIgnoreCase):
                return AddFolder(request, web, calls[1].Arg("decodedurl"));
            case "getfolderbyserverrelativepath":
                return FolderApi(request, web, calls[0].Arg("decodedurl"), calls.Skip(1).ToList());
            case "getfilebyserverrelativepath":
                return FileApi(request, web, calls[0].Arg("decodedurl"), calls.Skip(1).ToList());
            default:
                return NotUnderstood(string.Join('/', calls.Select(c => c.Name)));
        }
    }

    // --- lists, subsites, search -----------------------------------------------------------------------------------

    private Response Lists(Request request, Web web)
    {
        IEnumerable<Library> lists = web.Libraries;
        if (request.Query.TryGetValue("$filter", out var filter))
        {
            var templates = Regex.Matches(filter, @"BaseTemplate\s+eq\s+(\d+)", RegexOptions.IgnoreCase).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToHashSet();
            if (templates.Count > 0)
            {
                lists = lists.Where(l => templates.Contains(l.BaseTemplate));
            }

            if (Regex.IsMatch(filter, @"Hidden\s+eq\s+false", RegexOptions.IgnoreCase))
            {
                lists = lists.Where(l => !l.Hidden);
            }
        }

        return Response.Json(200, new JsonObject
        {
            ["value"] = new JsonArray(lists.Select(l => (JsonNode)new JsonObject
            {
                ["Title"] = l.Title,
                ["BaseTemplate"] = l.BaseTemplate,
                ["Hidden"] = l.Hidden,
                ["Id"] = l.Id.ToString(),
                ["LastItemModifiedDate"] = l.LastItemModified.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["ItemCount"] = l.ItemCount,
                ["RootFolder"] = new JsonObject { ["ServerRelativeUrl"] = l.Url },
            }).ToArray()),
        });
    }

    private Response AddWeb(Request request, Web web)
    {
        if (RoleOf(web, request.Account) < Role.Edit)
        {
            return Denied(request, web);
        }

        if (!SubsitesAllowed)
        {
            return Response.Json(403, RestError("-2147024891, System.UnauthorizedAccessException", "New subsites are not available for your organization. Create a new site instead."));
        }

        var parameters = JsonNode.Parse(request.Body)?["parameters"];
        var leaf = parameters?["Url"]?.GetValue<string>() ?? throw new RestFault(Response.Json(400, RestError("-1, Microsoft.SharePoint.Client.InvalidClientQueryException", "The parameter Url does not exist.")));
        if (web.Subwebs.Any(w => w.Leaf.Equals(leaf, StringComparison.OrdinalIgnoreCase)))
        {
            return Response.Json(500, RestError("-2130575257, Microsoft.SharePoint.SPException", $"The Web site address \"{web.Path}/{leaf}\" is already in use."));
        }

        var sub = new Web(this, $"{web.Path}/{leaf}", parameters?["Title"]?.GetValue<string>() ?? leaf, web) { UniquePermissions = parameters?["UseUniquePermissions"]?.GetValue<bool>() ?? false };
        web.Subwebs.Add(sub);
        SeedTeamSite(sub);
        return Response.Json(200, new JsonObject { ["ServerRelativeUrl"] = sub.Path, ["Title"] = sub.Title, ["Url"] = sub.Url });
    }

    /// <summary>The site collections search finds for the account (contentclass:STS_Site), every one it may open, rowlimit at a time.</summary>
    private Response Search(Request request)
    {
        if (!SearchService)
        {
            return Response.Json(500, RestError("-1, Microsoft.Office.Server.Search.REST.SearchServiceException", "The search request was unable to connect to the Search Service."));
        }

        var query = request.Query.TryGetValue("querytext", out var q) ? q : "";
        var sites = query.Contains("STS_Site", StringComparison.OrdinalIgnoreCase)
            ? _collections.Where(c => c.Searchable && RoleOf(c, request.Account) >= Role.Read).OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        var start = request.Query.TryGetValue("startrow", out var s) && int.TryParse(s, CultureInfo.InvariantCulture, out var startRow) ? startRow : 0;
        var limit = request.Query.TryGetValue("rowlimit", out var r) && int.TryParse(r, CultureInfo.InvariantCulture, out var rowLimit) ? rowLimit : 10;
        var page = sites.Skip(start).Take(limit).ToList();
        var rows = new JsonArray(page.Select(site => (JsonNode)new JsonObject
        {
            ["Cells"] = new JsonArray(
                new JsonObject { ["Key"] = "Rank", ["Value"] = "16.0", ["ValueType"] = "Edm.Double" },
                new JsonObject { ["Key"] = "Path", ["Value"] = site.Url, ["ValueType"] = "Edm.String" },
                new JsonObject { ["Key"] = "Title", ["Value"] = site.Title, ["ValueType"] = "Edm.String" }),
        }).ToArray());
        return Response.Json(200, new JsonObject
        {
            ["ElapsedTime"] = 12,
            ["PrimaryQueryResult"] = new JsonObject
            {
                ["RelevantResults"] = new JsonObject
                {
                    ["RowCount"] = page.Count,
                    ["TotalRows"] = sites.Count,
                    ["TotalRowsIncludingDuplicates"] = sites.Count,
                    ["Table"] = new JsonObject { ["Rows"] = rows },
                },
            },
        });
    }

    // --- the site's own folder -------------------------------------------------------------------------------------

    private Response RootFolderApi(Request request, Web web, List<Call> calls)
    {
        if (calls.Count == 0)
        {
            return Response.Json(200, FolderJson(web.OwnFolder));
        }

        if (!calls[0].Name.Equals("Files", StringComparison.OrdinalIgnoreCase))
        {
            return NotUnderstood("RootFolder/" + calls[0].Name);
        }

        if (calls.Count == 1)
        {
            // listing the site's own folder needs Browse Directories, which a reader lacks (the SE farm, 20-Oct-2026)
            if (RoleOf(web, request.Account) < Role.Edit)
            {
                return Denied(request, web);
            }

            return Response.Json(200, new JsonObject { ["value"] = new JsonArray(web.OwnFolder.Children.OfType<FileItem>().Select(f => (JsonNode)FileJson(f)).ToArray()) });
        }

        if (calls[1].Name.Equals("add", StringComparison.OrdinalIgnoreCase) && request.Method == "POST")
        {
            if (RoleOf(web, request.Account) < Role.Edit)
            {
                return Denied(request, web);
            }

            if (Flavour == Kind.Online && !CustomScript)
            {
                // a modern site of SharePoint Online takes no page there: custom script is off
                return Response.Json(403, RestError("-2147024891, System.UnauthorizedAccessException", "Access denied. You do not have permission to perform this action or access this resource."));
            }

            var name = calls[1].Arg("url");
            var file = web.OwnFolder.AddFile(name, request.Body);
            return Response.Json(200, FileJson(file));
        }

        return NotUnderstood("RootFolder/Files/" + calls[1].Name);
    }

    // --- folders and files -----------------------------------------------------------------------------------------

    private Response FolderApi(Request request, Web web, string path, List<Call> calls)
    {
        var (_, library, item) = ResolveIn(web, path);
        if (request.Header("X-HTTP-Method") is "DELETE")
        {
            return Delete(request, web, item as FolderItem, path);
        }

        if (calls.Count == 0)
        {
            if (item is not FolderItem folder || library is null)
            {
                return Response.Json(200, new JsonObject { ["Exists"] = false, ["Folders"] = new JsonArray(), ["Files"] = new JsonArray() });
            }

            return Listing(request, folder);
        }

        var call = calls[0];
        if (call.Name.Equals("Files", StringComparison.OrdinalIgnoreCase) && calls.Count > 1 && calls[1].Name.Equals("AddUsingPath", StringComparison.OrdinalIgnoreCase))
        {
            if (item is not FolderItem parent || library is null)
            {
                return FileNotFound();
            }

            return AddFile(request, web, parent, calls[1].Arg("decodedurl"), calls[1].Arg("overwrite").Equals("true", StringComparison.OrdinalIgnoreCase));
        }

        if (call.Name.Equals("MoveToUsingPath", StringComparison.OrdinalIgnoreCase))
        {
            return item is FolderItem source && library is not null ? Move(request, web, source, call.Arg("DecodedUrl"), replace: false) : FileNotFound();
        }

        return NotUnderstood(call.Name);
    }

    private Response Listing(Request request, FolderItem folder)
    {
        var expand = request.Query.TryGetValue("$expand", out var e) ? e : "";
        var select = request.Query.TryGetValue("$select", out var s) ? s : "";
        var json = FolderJson(folder);
        if (expand.Contains("Folders", StringComparison.OrdinalIgnoreCase))
        {
            json["Folders"] = new JsonArray(folder.Children.OfType<FolderItem>().Select(f => (JsonNode)FolderJson(f)).ToArray());
        }

        if (expand.Contains("Files", StringComparison.OrdinalIgnoreCase))
        {
            var fields = expand.Contains("ListItemAllFields", StringComparison.OrdinalIgnoreCase);
            if (fields && !ModernPages && select.Contains("CanvasContent1", StringComparison.OrdinalIgnoreCase))
            {
                return Response.Json(400, RestError("-1, Microsoft.SharePoint.Client.InvalidClientQueryException", "The field or property 'CanvasContent1' does not exist."));
            }

            json["Files"] = new JsonArray(folder.Children.OfType<FileItem>().Select(f =>
            {
                var file = FileJson(f);
                if (fields)
                {
                    file["ListItemAllFields"] = ListItemJson(f);
                }

                return (JsonNode)file;
            }).ToArray());
        }

        return Response.Json(200, json);
    }

    private Response FileApi(Request request, Web web, string path, List<Call> calls)
    {
        var (_, _, item) = ResolveIn(web, path);
        var file = item as FileItem;
        if (request.Header("X-HTTP-Method") is "DELETE")
        {
            return Delete(request, web, file, path);
        }

        if (calls.Count == 0)
        {
            if (file is null)
            {
                return FileNotFound();
            }

            var json = FileJson(file);
            var expand = request.Query.TryGetValue("$expand", out var e) ? e : "";
            if (expand.Contains("ListItemAllFields", StringComparison.OrdinalIgnoreCase))
            {
                var select = request.Query.TryGetValue("$select", out var s) ? s : "";
                if (!ModernPages && select.Contains("CanvasContent1", StringComparison.OrdinalIgnoreCase))
                {
                    return Response.Json(400, RestError("-1, Microsoft.SharePoint.Client.InvalidClientQueryException", "The field or property 'CanvasContent1' does not exist."));
                }

                json["ListItemAllFields"] = ListItemJson(file);
            }

            return Response.Json(200, json);
        }

        if (file is null)
        {
            return FileNotFound();
        }

        var call = calls[0];
        if (RoleOf(web, request.Account) < Role.Edit)
        {
            return Denied(request, web);
        }

        switch (call.Name.ToLowerInvariant())
        {
            case "startupload":
            {
                if (file.Locked)
                {
                    return Locked();
                }

                var upload = new RestUpload(file);
                upload.Data.Write(request.Body);
                _restUploads[Guid.Parse(call.Arg("uploadId"))] = upload;
                return Response.Json(200, new JsonObject { ["value"] = upload.Data.Length.ToString(CultureInfo.InvariantCulture) });
            }

            case "continueupload":
            case "finishupload":
            {
                if (!_restUploads.TryGetValue(Guid.Parse(call.Arg("uploadId")), out var upload) || upload.File != file)
                {
                    return Response.Json(500, RestError("-2147024809, System.ArgumentException", "Value does not fall within the expected range."));
                }

                if (long.Parse(call.Arg("fileOffset"), CultureInfo.InvariantCulture) != upload.Data.Length)
                {
                    return Response.Json(500, RestError("-2147024809, System.ArgumentException", "The upload's offset is not where the last piece ended."));
                }

                upload.Data.Write(request.Body);
                if (call.Name.Equals("continueupload", StringComparison.OrdinalIgnoreCase))
                {
                    return Response.Json(200, new JsonObject { ["value"] = upload.Data.Length.ToString(CultureInfo.InvariantCulture) });
                }

                _restUploads.Remove(Guid.Parse(call.Arg("uploadId")));
                file.Replace(upload.Data.ToArray());
                return Response.Json(200, FileJson(file));
            }

            case "movetousingpath":
                return Move(request, web, file, call.Arg("DecodedUrl"), replace: call.Arg("moveOperations") == "1");
            default:
                return NotUnderstood(call.Name);
        }
    }

    private Response AddFile(Request request, Web web, FolderItem parent, string name, bool overwrite)
    {
        if (RoleOf(web, request.Account) < Role.Edit)
        {
            return Denied(request, web);
        }

        if (!ValidName(name))
        {
            return InvalidName();
        }

        if (parent.Child(name) is { } existing)
        {
            if (existing is FolderItem || !overwrite)
            {
                return Response.Json(500, RestError("-2130575257, Microsoft.SharePoint.SPException", $"A file with the name {existing.ServerRelativeUrl.TrimStart('/')} already exists."));
            }

            if (((FileItem)existing).Locked)
            {
                return Locked();
            }
        }

        var file = parent.AddFile(name, request.Body);
        return Response.Json(200, FileJson(file));
    }

    private Response AddFolder(Request request, Web web, string path)
    {
        if (RoleOf(web, request.Account) < Role.Edit)
        {
            return Denied(request, web);
        }

        var (_, library, item) = ResolveIn(web, path);
        if (item is FolderItem existing && library is not null)
        {
            // SharePoint hands back a folder that is already there without a word
            return Response.Json(200, FolderJson(existing));
        }

        var slash = path.TrimEnd('/').LastIndexOf('/');
        var (_, parentLibrary, parent) = ResolveIn(web, path[..slash]);
        var name = path.TrimEnd('/')[(slash + 1)..];
        if (parent is not FolderItem folder || parentLibrary is null)
        {
            return FileNotFound();
        }

        if (!ValidName(name))
        {
            return InvalidName();
        }

        if (folder.Child(name) is FileItem)
        {
            return Response.Json(500, RestError("-2130575257, Microsoft.SharePoint.SPException", $"A file with the name {path.TrimStart('/')} already exists."));
        }

        return Response.Json(200, FolderJson(folder.AddFolder(name)));
    }

    private Response Delete(Request request, Web web, Item? item, string path)
    {
        if (RoleOf(web, request.Account) < Role.Edit)
        {
            return Denied(request, web);
        }

        if (item is null || item.Parent is null)
        {
            return FileNotFound();
        }

        if (item is FileItem { Locked: true })
        {
            return Locked();
        }

        item.Parent.Children.Remove(item);
        item.Parent.Touch();
        return new Response { Status = 200 };
    }

    private Response Move(Request request, Web web, Item item, string to, bool replace)
    {
        var (_, library, existing) = ResolveIn(web, to);
        if (existing is not null && !replace)
        {
            return Response.Json(500, RestError("-2130575257, Microsoft.SharePoint.SPException", $"A file or folder with the name {to.TrimStart('/')} already exists."));
        }

        var slash = to.TrimEnd('/').LastIndexOf('/');
        var (_, parentLibrary, parent) = ResolveIn(web, to[..slash]);
        if (parent is not FolderItem folder || parentLibrary is null)
        {
            return FileNotFound();
        }

        var name = to.TrimEnd('/')[(slash + 1)..];
        if (!ValidName(name))
        {
            return InvalidName();
        }

        if (existing is not null)
        {
            existing.Parent?.Children.Remove(existing);
        }

        item.Parent!.Children.Remove(item);
        item.Parent.Touch();
        item.Name = name;
        item.Parent = folder;
        folder.Children.Add(item);
        folder.Touch();
        item.Version++;
        return new Response { Status = 200 };
    }

    /// <summary>A path the request names, only within the web it was asked of: SharePoint answers for a file through its own web only (M6, 19-Oct-2026).</summary>
    private (Web Web, Library? Library, Item? Item) ResolveIn(Web web, string path)
    {
        var resolved = Resolve(path);
        return resolved.Web == web ? resolved : (web, null, null);
    }

    // --- a file's bytes, and pages as a browser gets them ---------------------------------------------------------

    private Response Content(Request request)
    {
        var path = Uri.UnescapeDataString(request.RawPath);
        var (web, library, item) = Resolve(path);
        if (RoleOf(web, request.Account) < Role.Read)
        {
            return Denied(request, web);
        }

        var translate = request.Header("Translate") is "f" or "F";
        if (item is FolderItem && library is null && item == web.OwnFolder && !translate)
        {
            // a site's own address as a browser asks for it: forwarded to its home page (relative, 20-Oct-2026)
            return Response.Text(302, "Object moved").With("Location", $"{web.Path}/SitePages/Home.aspx");
        }

        if (item is not FileItem file)
        {
            return Flavour == Kind.Online ? Response.Text(404, "404 NOT FOUND") : FileNotFound();
        }

        if (!translate && file.Name.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase))
        {
            if (library is null && file.Name.Equals("default.aspx", StringComparison.OrdinalIgnoreCase) && web.Modern)
            {
                return Response.Text(302, "Object moved").With("Location", $"{web.Path}/SitePages/Home.aspx");
            }

            return Response.Text(200, ClassicPageHtml(web, file), "text/html; charset=utf-8");
        }

        var bytes = file.Content;
        var range = request.Header("Range");
        if (range is not null && HonoursRanges && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var spec = range[6..].Split('-');
            var from = long.Parse(spec[0], CultureInfo.InvariantCulture);
            var to = spec.Length > 1 && spec[1].Length > 0 ? Math.Min(long.Parse(spec[1], CultureInfo.InvariantCulture), bytes.LongLength - 1) : bytes.LongLength - 1;
            if (from >= bytes.LongLength)
            {
                return Response.Empty(416).With("Content-Range", $"bytes */{bytes.LongLength}");
            }

            return Response.Bytes(206, bytes[(int)from..(int)(to + 1)]).With("Content-Range", $"bytes {from}-{to}/{bytes.LongLength}").With("ETag", file.RestETag);
        }

        return Response.Bytes(200, bytes).With("ETag", file.RestETag);
    }

    /// <summary>A classic page as the farm renders it in its master page: the title area, the main area, and the form state around them.</summary>
    private static string ClassicPageHtml(Web web, FileItem file)
    {
        var aspx = Encoding.UTF8.GetString(file.Content);
        string Placeholder(string id)
        {
            var match = Regex.Match(aspx, $"<asp:Content[^>]*ContentPlaceHolderId=\"{id}\"[^>]*>(.*?)</asp:Content>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : "";
        }

        var title = Placeholder("PlaceHolderPageTitleInTitleArea");
        var main = Placeholder("PlaceHolderMain");
        if (title.Length == 0 && main.Length == 0)
        {
            title = web.Title;
            main = $"<div class=\"ms-rtestate-field\">{WebUtility.HtmlEncode(file.Title ?? web.Title)}</div>";
        }

        return $"""
            <!DOCTYPE html PUBLIC "-//W3C//DTD XHTML 1.0 Strict//EN" "http://www.w3.org/TR/xhtml1/DTD/xhtml1-strict.dtd">
            <html dir="ltr" class="ms-isBot" lang="en-US"><head><meta name="GENERATOR" content="Microsoft SharePoint" /><title>{WebUtility.HtmlEncode(web.Title)} - {WebUtility.HtmlEncode(file.Name)}</title><script type="text/javascript">var _spPageContextInfo = null;</script></head>
            <body><form method="post" action="./{file.Name}" id="aspnetForm"><div class="aspNetHidden"><input type="hidden" name="__VIEWSTATE" id="__VIEWSTATE" value="/wEPDwUBMA9kFgJmD2QWAgIBD2QWBAIBD2QWAgIOD2QWAgIBD2QWAgIBDxYCHgdWaXNpYmxlaGRk" /></div>
            <div id="s4-workspace"><div id="s4-titlerow"><h1 id="pageTitle" class="ms-core-pageTitle"><span id="DeltaPlaceHolderPageTitleInTitleArea">{title}</span></h1></div>
            <div id="contentBox"><div id="DeltaPlaceHolderMain">{main}</div></div></div></form></body></html>
            """;
    }

    // --- CSOM: the one thing REST cannot do -------------------------------------------------------------------------

    /// <summary>
    /// _vti_bin/client.svc/ProcessQuery for a file's or folder's times: SetFieldValue Created and Modified, then
    /// UpdateOverwriteVersion. SharePoint honours the times only for a site collection administrator, and for anyone
    /// else keeps them as they were without a word (18-Oct-2026, Subscription Edition).
    /// </summary>
    private Response ProcessQuery(Request request, Web web)
    {
        if (RoleOf(web, request.Account) < Role.Read)
        {
            return Denied(request, web);
        }

        if (!DigestValid(request.Header("X-RequestDigest")))
        {
            return Response.Json(403, RestError("-2130575252, Microsoft.SharePoint.SPException", "The security validation for this page is invalid and might be corrupted. Please use your web browser's Back button to try your operation again."));
        }

        XDocument query;
        try
        {
            query = XDocument.Parse(Encoding.UTF8.GetString(request.Body));
        }
        catch (System.Xml.XmlException ex)
        {
            return Response.Text(400, ex.Message);
        }

        XNamespace ns = "http://schemas.microsoft.com/sharepoint/clientquery/2009";
        var target = query.Descendants(ns + "Method").FirstOrDefault(m => (string?)m.Attribute("Name") is "GetFileByServerRelativeUrl" or "GetFolderByServerRelativeUrl");
        var path = target?.Descendants(ns + "Parameter").FirstOrDefault()?.Value ?? "";
        var (_, _, item) = ResolveIn(web, path);
        var info = new JsonObject { ["SchemaVersion"] = "15.0.0.0", ["LibraryVersion"] = "16.0.0.0", ["TraceCorrelationId"] = Guid.NewGuid().ToString() };
        if (item is null)
        {
            info["ErrorInfo"] = new JsonObject { ["ErrorMessage"] = "File Not Found.", ["ErrorValue"] = null, ["ErrorCode"] = -2147024894, ["ErrorTypeName"] = "System.IO.FileNotFoundException" };
            return Response.Json(200, new JsonArray(info), "application/json; charset=utf-8");
        }

        if (RoleOf(web, request.Account) < Role.Edit)
        {
            info["ErrorInfo"] = new JsonObject { ["ErrorMessage"] = "Access denied. You do not have permission to perform this action or access this resource.", ["ErrorCode"] = -2147024891, ["ErrorTypeName"] = "System.UnauthorizedAccessException" };
            return Response.Json(200, new JsonArray(info), "application/json; charset=utf-8");
        }

        if (RoleOf(web, request.Account) == Role.Owner)
        {
            foreach (var set in query.Descendants(ns + "Method").Where(m => (string?)m.Attribute("Name") == "SetFieldValue"))
            {
                var parameters = set.Descendants(ns + "Parameter").ToList();
                var when = DateTimeOffset.Parse(parameters[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                if (parameters[0].Value == "Created")
                {
                    item.Created = when.ToUniversalTime();
                }
                else if (parameters[0].Value == "Modified")
                {
                    item.Modified = when.ToUniversalTime();
                }
            }
        }

        info["ErrorInfo"] = null;
        return Response.Json(200, new JsonArray(info), "application/json; charset=utf-8");
    }

    // --- the request digest ----------------------------------------------------------------------------------------

    private Response ContextInfo(Web web)
    {
        var digest = $"0x{NewSecret(64)},{DateTimeOffset.UtcNow.ToString("dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture)} -0000";
        _digests[digest] = DateTimeOffset.UtcNow + DigestLifetime;
        return Response.Json(200, new JsonObject
        {
            ["FormDigestTimeoutSeconds"] = (int)DigestLifetime.TotalSeconds,
            ["FormDigestValue"] = digest,
            ["LibraryVersion"] = Flavour == Kind.Online ? "16.0.27807.12006" : "16.0.10337.12109",
            ["SiteFullUrl"] = web.Collection.Url,
            ["SupportedSchemaVersions"] = new JsonArray("14.0.0.0", "15.0.0.0"),
            ["WebFullUrl"] = web.Url,
        });
    }

    private bool DigestValid(string? digest) => digest is not null && _digests.TryGetValue(digest, out var until) && until > DateTimeOffset.UtcNow;

    // --- site pages: the API the live tests make their page with ---------------------------------------------------

    private Response SitePagesApi(Request request, Web web, List<Call> calls)
    {
        var library = web.Libraries.FirstOrDefault(l => l.Pages) ?? throw new RestFault(Response.Json(404, RestError("-2147024894, System.IO.FileNotFoundException", "List 'Site Pages' does not exist at site with URL '" + web.Url + "'.")));
        if (calls.Count == 0 || !calls[0].Name.Equals("pages", StringComparison.OrdinalIgnoreCase))
        {
            return NotUnderstood("sitepages");
        }

        var pages = library.Root.Children.OfType<FileItem>().Where(f => f.Name.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)).ToList();
        if (calls[0].Args.Count == 0)
        {
            if (request.Method == "GET")
            {
                return Response.Json(200, new JsonObject { ["value"] = new JsonArray(pages.Select(p => (JsonNode)PageJson(p)).ToArray()) });
            }

            // a new page, named for now; saving it with a title renames it after the title
            var created = library.Root.AddFile($"Untitled_{_nextPageId}.aspx", Encoding.UTF8.GetBytes("<%@ Page %>"));
            created.Title = "";
            created.CanvasContent1 = "";
            return Response.Json(201, PageJson(created));
        }

        var id = int.Parse(calls[0].Args[""], CultureInfo.InvariantCulture);
        var page = pages.FirstOrDefault(p => PageId(p) == id) ?? throw new RestFault(Response.Json(404, RestError("-2130575338, Microsoft.SharePoint.SPException", "Item does not exist. It may have been deleted by another user.")));
        var action = calls.Count > 1 ? calls[1].Name.ToLowerInvariant() : "";
        switch (action)
        {
            case "":
                return Response.Json(200, PageJson(page));
            case "checkoutpage":
            case "publish":
                return Response.Json(200, new JsonObject { ["value"] = true });
            case "savepage":
            {
                var body = JsonNode.Parse(request.Body);
                if (body?["Title"]?.GetValue<string>() is { } title)
                {
                    page.Title = title;
                    var named = Regex.Replace(title.Trim(), @"\s+", "-") + ".aspx";
                    if (!page.Name.Equals(named, StringComparison.OrdinalIgnoreCase) && library.Root.Child(named) is null)
                    {
                        page.Name = named;
                    }
                }

                if (body?["CanvasContent1"]?.GetValue<string>() is { } canvas)
                {
                    // the API takes the canvas as JSON (an HTML body fails its parse, the SE farm 20-Oct-2026) and the list field keeps HTML
                    JsonArray controls;
                    try
                    {
                        controls = JsonNode.Parse(canvas)?.AsArray() ?? [];
                    }
                    catch (JsonException ex)
                    {
                        return Response.Json(400, RestError("-1, Newtonsoft.Json.JsonReaderException", ex.Message));
                    }

                    page.CanvasContent1 = CanvasHtml(controls.Select(c => c?["innerHTML"]?.GetValue<string>() ?? ""));
                }

                page.Touch();
                return Response.Json(200, new JsonObject { ["value"] = true });
            }

            default:
                return NotUnderstood("sitepages/pages/" + action);
        }
    }

    private int PageId(FileItem page)
    {
        if (!_pageIds.TryGetValue(page, out var id))
        {
            id = _nextPageId++;
            _pageIds[page] = id;
        }

        return id;
    }

    private JsonObject PageJson(FileItem page) => new() { ["Id"] = PageId(page), ["Title"] = page.Title ?? "", ["FileName"] = page.Name, ["Url"] = $"SitePages/{page.Name}" };

    /// <summary>A modern page's CanvasContent1 as SharePoint keeps it: each text web part's HTML inside its control's markup, colons in its data as &amp;#58;.</summary>
    public static string CanvasHtml(IEnumerable<string> texts)
    {
        var html = new StringBuilder("<div>");
        var index = 1;
        foreach (var text in texts)
        {
            var data = "&#123;&quot;controlType&quot;&#58;4,&quot;id&quot;&#58;&quot;" + Guid.NewGuid() + "&quot;,&quot;position&quot;&#58;&#123;&quot;zoneIndex&quot;&#58;1,&quot;sectionIndex&quot;&#58;1,&quot;controlIndex&quot;&#58;" + index++ + "&#125;&#125;";
            html.Append($"<div data-sp-canvascontrol=\"\" data-sp-canvasdataversion=\"1.0\" data-sp-controldata=\"{data}\"><div data-sp-rte=\"\">{text.Replace("https://", "https&#58;//", StringComparison.Ordinal)}</div></div>");
        }

        return html.Append("</div>").ToString();
    }

    // --- JSON as REST gives it --------------------------------------------------------------------------------------

    private static string RestTime(DateTimeOffset when) => when.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static JsonObject FileJson(FileItem file) => new()
    {
        ["Exists"] = true,
        ["Name"] = file.Name,
        ["ServerRelativeUrl"] = file.ServerRelativeUrl,

        // nometadata gives an Int64 as a string
        ["Length"] = file.Content.LongLength.ToString(CultureInfo.InvariantCulture),
        ["TimeCreated"] = RestTime(file.Created),
        ["TimeLastModified"] = RestTime(file.Modified),
        ["UniqueId"] = file.UniqueId.ToString(),
        ["ETag"] = file.RestETag,
    };

    private static JsonObject FolderJson(FolderItem folder) => new()
    {
        ["Exists"] = true,
        ["Name"] = folder.Parent is null && folder.Library is null ? "" : folder.Name,
        ["ServerRelativeUrl"] = folder.Library is null && folder.Parent is null ? $"{folder.Web!.Path}/" : folder.ServerRelativeUrl,
        ["TimeCreated"] = RestTime(folder.Created),
        ["TimeLastModified"] = RestTime(folder.Modified),
        ["UniqueId"] = folder.UniqueId.ToString(),
        ["ItemCount"] = folder.Children.Count,
    };

    private JsonObject ListItemJson(FileItem file)
    {
        var fields = new JsonObject { ["Title"] = file.Title, ["WikiField"] = file.WikiField };
        if (ModernPages)
        {
            fields["CanvasContent1"] = file.CanvasContent1;
        }

        return fields;
    }

    /// <summary>The error body REST gives: odata.error, its code an HRESULT and the exception's type, its message in the request's language.</summary>
    public static JsonObject RestError(string code, string message) => new()
    {
        ["odata.error"] = new JsonObject { ["code"] = code, ["message"] = new JsonObject { ["lang"] = "en-US", ["value"] = message } },
    };

    /// <summary>A missing file: 500 on the farms (SharePoint Server says so), 404 on SharePoint Online.</summary>
    private Response FileNotFound() => Response.Json(Flavour == Kind.Online ? 404 : 500, RestError("-2147024894, System.IO.FileNotFoundException", "File Not Found."));

    private static Response Locked() => Response.Json(423, RestError("-2130575245, Microsoft.SharePoint.SPFileLockException", "The file is checked out or locked for editing by someone."));

    private static Response InvalidName() => Response.Json(400, RestError("-2130575251, Microsoft.SharePoint.SPException", "The file or folder name contains characters that are not permitted. Please use a different name."));

    private static Response NotUnderstood(string what) => Response.Json(400, RestError("-1, Microsoft.SharePoint.Client.InvalidClientQueryException", $"The expression \"{what}\" is not valid."));

    /// <summary>A name SharePoint takes for a file or folder.</summary>
    private static bool ValidName(string name)
        => name.Length > 0 && name.IndexOfAny(['"', '*', ':', '<', '>', '?', '/', '\\', '|']) < 0 && name.Trim() == name && !name.EndsWith('.') && !name.StartsWith("~$", StringComparison.Ordinal) && !name.Contains("_vti_", StringComparison.OrdinalIgnoreCase);

    private sealed class RestFault(Response response) : Exception
    {
        public Response Response { get; } = response;
    }

    private sealed class RestUpload(FileItem file)
    {
        public FileItem File { get; } = file;

        public MemoryStream Data { get; } = new();
    }

    // --- the API's address, call by call ----------------------------------------------------------------------------

    /// <summary>One step of an API address: its name and its arguments (quoted values unquoted and percent-decoded; a lone value under "").</summary>
    private sealed record Call(string Name, Dictionary<string, string> Args)
    {
        public string Arg(string name) => Args.TryGetValue(name, out var value) ? value : throw new RestFault(Response.Json(400, RestError("-1, Microsoft.SharePoint.Client.InvalidClientQueryException", $"The parameter {name} does not exist in method {Name}.")));
    }

    /// <summary>"web/GetFolderByServerRelativePath(decodedurl='%2Fx')/Files/AddUsingPath(decodedurl='a%27%27b.txt',overwrite=true)" call by call.</summary>
    private static List<Call> ParseCalls(string raw)
    {
        var calls = new List<Call>();
        foreach (var segment in SplitOutsideQuotes(raw, '/'))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            var open = segment.IndexOf('(');
            if (open < 0 || !segment.EndsWith(')'))
            {
                calls.Add(new Call(Uri.UnescapeDataString(segment), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
                continue;
            }

            var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var inner = segment[(open + 1)..^1];
            foreach (var arg in SplitOutsideQuotes(inner, ','))
            {
                if (arg.Length == 0)
                {
                    continue;
                }

                var eq = IndexOutsideQuotes(arg, '=');
                if (eq < 0)
                {
                    args[""] = Value(arg);
                }
                else
                {
                    args[arg[..eq].Trim()] = Value(arg[(eq + 1)..]);
                }
            }

            calls.Add(new Call(segment[..open], args));
        }

        return calls;
    }

    private static string Value(string raw)
    {
        raw = raw.Trim();
        if (raw.StartsWith("guid'", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw[4..];
        }

        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
        {
            return Uri.UnescapeDataString(raw[1..^1]).Replace("''", "'", StringComparison.Ordinal);
        }

        return Uri.UnescapeDataString(raw);
    }

    private static List<string> SplitOutsideQuotes(string text, char separator)
    {
        var parts = new List<string>();
        var quoted = false;
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'')
            {
                quoted = !quoted;
            }
            else if (!quoted && c == '(')
            {
                depth++;
            }
            else if (!quoted && c == ')')
            {
                depth--;
            }
            else if (!quoted && depth == 0 && c == separator)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }

        parts.Add(text[start..]);
        return parts;
    }

    private static int IndexOutsideQuotes(string text, char wanted)
    {
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\'')
            {
                quoted = !quoted;
            }
            else if (!quoted && text[i] == wanted)
            {
                return i;
            }
        }

        return -1;
    }
}
