using System.Globalization;
using System.Text.Json;
using System.Web;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.ThreeDX;

/// <summary>What Mount3DX's single sign-on came to: signed in or not, where it stopped, and whether it got as far as the badge (Windows asked for the PIN).</summary>
/// <param name="Route">The hosts and paths it went through, for the log (no query strings: they carry tickets).</param>
internal sealed record SignOnResult(bool SignedIn, string Route, string? Problem = null, bool BadgeTaken = false);

/// <summary>
/// Mount3DX's single sign-on (lib3dx/3dxLogin.cs, <c>LogInUsingHttpClient</c>), step for step (spec 7.6.2), for the web session
/// and as yourself. Its client is Mount3DX's, made afresh for each sign-in (<see cref="ThreeDXSession"/>): redirects followed,
/// a cookie jar of its own that starts empty, the person's smartcard certificates on every connection
/// (<see cref="ClientCertificates"/>), no Windows credentials, no user agent of its own:
/// <list type="number">
/// <item>3DSpace's own page, asked for with Firefox's user agent (the redirects carry it; no request after has one), lands on
/// 3DPassport's SAML request;</item>
/// <item>the request (alone) is posted to the identity provider, whose sign-in page's address carries resumeUrl, resumePath,
/// instanceId and assuranceLevel;</item>
/// <item>the badge sign-in's path is found in the page's scripts, every one fetched as Mount3DX fetches it (the page's scheme,
/// host and path, without its port or query, and the script's, joined with a slash: for an inline script, the page again),
/// the page's own inline scripts read first; its port in <c>configuration/internal</c> (a number, or a number as text);</item>
/// <item>the badge sign-in is asked for (Windows asks for the smartcard's PIN) with the resume parameters and Mount3DX's own
/// (pingfedDropoff, errorUrl, gasWebClient, coordinates, countryCodeRequested, usaItarGeoRequested);</item>
/// <item>its SAML response (alone) is posted back to 3DPassport, which sends the client on to 3DSpace, signed in.</item>
/// </list>
/// Beyond Mount3DX, so that it gets through wherever Mount3DX's does: a page on a port of its own has its scripts and
/// configuration asked for there too, relative addresses are resolved, a script that cannot be fetched is passed over.
/// Where the organisation signs in some other way it stops early, before any PIN prompt: no SAML request on the page 3DSpace
/// leads to, or no resume parameters on the identity provider's page; AnyFS then tries its other ways (<see cref="SignInFlow"/>,
/// a browser).
/// </summary>
internal static class SingleSignOn
{
    /// <summary>The user agent Mount3DX's single sign-on gives its first request.</summary>
    public const string FirefoxUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0";

    private const string PathVariable = "_INTERNAL_SECURE_BADGE_AUTH";

    /// <param name="http">Mount3DX's client: redirects followed, a cookie jar of its own, the person's certificates, no user agent of its own.</param>
    /// <param name="loginUrl">3DSpace's address as given (Mount3DX's ServerUrl).</param>
    public static async Task<SignOnResult> RunAsync(HttpClient http, Uri loginUrl, ILogger logger, CancellationToken ct)
    {
        var route = new List<string>();
        var badgeTaken = false;
        SignOnResult Stop(string problem) => new(false, string.Join(" -> ", route), problem, badgeTaken);
        try
        {
            // 1. 3DSpace's page, with Firefox's user agent, to 3DPassport's SAML request
            var first = new HttpRequestMessage(HttpMethod.Get, loginUrl);
            first.Headers.UserAgent.ParseAdd(FirefoxUserAgent);
            using var landing = await http.SendAsync(first, ct).ConfigureAwait(false);
            var landed = Where(landing, loginUrl, route);
            if (!landing.IsSuccessStatusCode)
            {
                return Stop($"{landed.Host}{landed.AbsolutePath} answered {(int)landing.StatusCode} to 3DSpace's page");
            }

            var (page, _) = await ParseAsync(landing, ct).ConfigureAwait(false);
            var samlRequest = page.QuerySelector("input[name='SAMLRequest']")?.GetAttribute("value");
            var toIdentityProvider = page.QuerySelector("form")?.GetAttribute("action");
            if (string.IsNullOrEmpty(samlRequest) || string.IsNullOrEmpty(toIdentityProvider))
            {
                return Stop($"3DSpace's page led to {landed.Host}{landed.AbsolutePath}, with no SAML request to hand on");
            }

            // 2. the SAML request, alone, to the identity provider: its sign-in page, with the resume parameters
            using var posted = await http.PostAsync(new Uri(landed, toIdentityProvider), new FormUrlEncodedContent([new("SAMLRequest", samlRequest)]), ct).ConfigureAwait(false);
            var signInPage = Where(posted, landed, route);
            var query = Query(signInPage);
            if (new[] { "resumeUrl", "resumePath", "instanceId", "assuranceLevel" }.FirstOrDefault(name => !query.ContainsKey(name)) is { } missing)
            {
                return Stop($"the identity provider's page ({signInPage.Host}{signInPage.AbsolutePath}, {(int)posted.StatusCode}) has no {missing} in its address ({Shape(signInPage)}): it is no badge sign-in");
            }

            var (document, body) = await ParseAsync(posted, ct).ConfigureAwait(false);

            // 3. the badge sign-in's path, from the page's scripts, and its port, from the page's configuration; both at the
            // page's address as Mount3DX writes it (no port: the scheme's own), then, if the page is on a port of its own, there
            var bases = Bases(signInPage);
            var path = await BadgePathAsync(http, signInPage, bases, document, body, ct).ConfigureAwait(false);
            if (path is null)
            {
                return Stop($"no script of the identity provider's page ({signInPage.Host}{signInPage.AbsolutePath}) names {PathVariable}");
            }

            var (configured, port) = await PortAsync(http, bases, ct).ConfigureAwait(false);
            if (!configured)
            {
                return Stop($"{Join(bases[0], "configuration/internal")} gives no configuration");
            }

            if (port is null)
            {
                logger.LogInformation("3dx: single sign-on: the page's configuration gives no internal.secure_badge_auth_port; the badge sign-in at the scheme's own port, as Mount3DX goes");
            }

            // 4. the badge sign-in: Windows asks for the smartcard's PIN
            var badgeAddress = Address(signInPage, port, path, query, bases[0]);
            badgeTaken = true;
            logger.LogInformation("3dx: single sign-on: the badge sign-in at {Host}:{Port}; Windows may ask for the smartcard's PIN", badgeAddress.Host, badgeAddress.Port);
            using var badge = await http.GetAsync(badgeAddress, ct).ConfigureAwait(false);
            var answered = Where(badge, badgeAddress, route);
            if (!badge.IsSuccessStatusCode)
            {
                return Stop($"the badge sign-in ({answered.Host}:{answered.Port}{answered.AbsolutePath}) answered {(int)badge.StatusCode}");
            }

            var (answer, _) = await ParseAsync(badge, ct).ConfigureAwait(false);
            var samlResponse = answer.QuerySelector("input[name='SAMLResponse']")?.GetAttribute("value");
            var back = answer.QuerySelector("form")?.GetAttribute("action");
            if (string.IsNullOrEmpty(samlResponse) || string.IsNullOrEmpty(back))
            {
                return Stop($"the badge sign-in ({answered.Host}{answered.AbsolutePath}) answered with no SAML response");
            }

            // 5. the SAML response, alone, back to 3DPassport, which sends the client on to 3DSpace
            using var done = await http.PostAsync(new Uri(answered, back), new FormUrlEncodedContent([new("SAMLResponse", samlResponse)]), ct).ConfigureAwait(false);
            var end = Where(done, answered, route);
            return done.IsSuccessStatusCode
                ? new SignOnResult(true, string.Join(" -> ", route), BadgeTaken: true)
                : Stop($"3DPassport answered {(int)done.StatusCode} at {end.Host}{end.AbsolutePath} to the badge's SAML response");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // as Mount3DX's: whatever stops it, the other ways come next
            return Stop(ex switch
            {
                TaskCanceledException => "a server in the sign-in did not answer in time (a PIN not given?)",
                HttpRequestException or IOException => ex.Message,
                _ => $"{ex.GetType().Name}: {ex.Message}",
            });
        }
    }

    /// <summary>Where a request ended up after the redirects the client followed, noted on the route.</summary>
    private static Uri Where(HttpResponseMessage response, Uri asked, List<string> route)
    {
        var at = response.RequestMessage?.RequestUri ?? asked;
        route.Add($"{at.Host}{(at.IsDefaultPort ? "" : ":" + at.Port.ToString(CultureInfo.InvariantCulture))}{at.AbsolutePath} {(int)response.StatusCode}");
        return at;
    }

    private static async Task<(IDocument Page, string Body)> ParseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return (await new HtmlParser().ParseDocumentAsync(body, ct).ConfigureAwait(false), body);
    }

    /// <summary>
    /// Where the page's scripts and configuration are asked for: Mount3DX's address of the page (its scheme, host and path:
    /// no port, so the scheme's own), and, when the page is on a port of its own, the page's address with it.
    /// </summary>
    internal static IReadOnlyList<string> Bases(Uri page)
    {
        var mount3dx = $"{page.Scheme}://{page.Host}{page.AbsolutePath}";
        var own = $"{page.Scheme}://{page.Authority}{page.AbsolutePath}";
        return own == mount3dx ? [mount3dx] : [mount3dx, own];
    }

    /// <summary>
    /// The badge sign-in's path (<c>_INTERNAL_SECURE_BADGE_AUTH="..."</c>, the last such in a text): from the page's own inline
    /// scripts, else from every script of the page fetched as Mount3DX fetches it (and, for a script with an address, as it
    /// resolves the usual way), all together; a script that cannot be fetched is passed over.
    /// </summary>
    internal static async Task<string?> BadgePathAsync(HttpClient http, Uri page, IReadOnlyList<string> bases, IDocument document, string body, CancellationToken ct)
    {
        if (PathIn(body) is { } inline)
        {
            return inline;
        }

        var addresses = document.QuerySelectorAll("script")
            .Select(s => s.GetAttribute("src") ?? "")
            .SelectMany(src => bases.Select(b => Join(b, src)).Append(src.Length > 0 && Uri.TryCreate(page, src, out var resolved) ? resolved.AbsoluteUri : null))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var texts = await Task.WhenAll(addresses.Select(async address =>
        {
            try
            {
                using var response = await http.GetAsync(address, ct).ConfigureAwait(false);
                return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : "";
            }
            catch (HttpRequestException)
            {
                return "";
            }
        })).ConfigureAwait(false);

        return texts.Select(PathIn).FirstOrDefault(path => path is not null);
    }

    private static string? PathIn(string text)
    {
        var at = text.LastIndexOf(PathVariable + "=\"", StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var start = at + PathVariable.Length + 2;
        var end = text.IndexOf('"', start);
        return end > start ? text[start..end] : null;
    }

    /// <summary>
    /// The badge sign-in's port from the page's <c>configuration/internal</c> (at the first of <paramref name="bases"/> that
    /// gives one): <c>configuration["internal.secure_badge_auth_port"]</c>, a number or a number as text. A configuration
    /// without it gives no port, which Mount3DX's address takes as the scheme's own; none at all is not configured.
    /// </summary>
    internal static async Task<(bool Configured, int? Port)> PortAsync(HttpClient http, IReadOnlyList<string> bases, CancellationToken ct)
    {
        (bool, int?) found = (false, null);
        foreach (var at in bases)
        {
            try
            {
                using var response = await http.GetAsync(Join(at, "configuration/internal"), ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("configuration", out var configuration)
                    || configuration.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                found = (true, configuration.TryGetProperty("internal.secure_badge_auth_port", out var port) ? PortOf(port) : null);
                if (found.Item2 is not null)
                {
                    return found;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                // not at this address: the next
            }
        }

        return found;
    }

    private static int? PortOf(JsonElement port) => port.ValueKind switch
    {
        JsonValueKind.Number when port.TryGetInt32(out var n) => n,
        JsonValueKind.Number when port.TryGetDouble(out var d) && d == Math.Floor(d) && d is > 0 and <= 65535 => (int)d,
        JsonValueKind.String when int.TryParse(port.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        _ => null,
    };

    /// <summary>
    /// The badge sign-in's address: the page's host at the port (none: the scheme's own), the path joined on, and the
    /// parameters Mount3DX sends, in its order; <paramref name="errorUrl"/> is the page's address as Mount3DX writes it.
    /// </summary>
    internal static Uri Address(Uri page, int? port, string path, IReadOnlyDictionary<string, string> query, string errorUrl)
    {
        var parameters = new (string Name, string Value)[]
        {
            ("resumeUrl", query["resumeUrl"]),
            ("resumePath", query["resumePath"]),
            ("instanceId", query["instanceId"]),
            ("pingfedDropoff", "true"),
            ("assuranceLevel", query["assuranceLevel"]),
            ("errorUrl", errorUrl),
            ("gasWebClient", "true"),
            ("coordinates", "null"),
            ("countryCodeRequested", "false"),
            ("usaItarGeoRequested", "false"),
        };
        var at = Join($"{page.Scheme}://{page.Host}" + (port is { } number ? ":" + number.ToString(CultureInfo.InvariantCulture) : ""), path);
        return new Uri(at + (at.Contains('?') ? "&" : "?") + string.Join("&", parameters.Select(p => $"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(p.Value)}")));
    }

    /// <summary>Two parts of an address joined with one slash, as Mount3DX joins them.</summary>
    internal static string Join(string left, string right) => $"{left.TrimEnd('/')}/{right.TrimStart('/')}";

    /// <summary>
    /// The page address's parameters as Mount3DX reads them: the whole address, every '?' made an '&amp;', read as a query
    /// (<see cref="HttpUtility.ParseQueryString(string)"/>). So the parameters after a '#' count too, where an application
    /// that routes by the fragment carries them (<c>/GAS/#/?resumeUrl=...</c>), and names match in any case.
    /// </summary>
    internal static Dictionary<string, string> Query(Uri page)
    {
        var read = HttpUtility.ParseQueryString(page.ToString().Replace("?", "&", StringComparison.Ordinal));
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in read.AllKeys)
        {
            if (name is not null && read[name] is { } value)
            {
                parameters[name] = value;
            }
        }

        return parameters;
    }

    /// <summary>What an address carries, for the log: its parameters' names and whether it has a fragment, no values (they can carry tickets).</summary>
    internal static string Shape(Uri page)
    {
        var names = Query(page).Keys.ToList();
        var parameters = names.Count == 0 ? "no parameters" : "parameters " + string.Join(", ", names);
        return page.Fragment.Length > 1 ? $"{parameters}; a fragment of {page.Fragment.Length - 1} characters" : parameters;
    }
}
