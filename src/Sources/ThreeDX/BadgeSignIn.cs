using System.Text.Json;
using AngleSharp.Dom;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.ThreeDX;

/// <summary>
/// The smartcard (badge) single sign-on Mount3DX signs in with (spec 7.6.2), as one step of a <see cref="SignInFlow"/>. After
/// 3DPassport hands the sign-in to the organisation's identity provider, the provider's sign-in page comes with
/// <c>resumeUrl</c>, <c>resumePath</c>, <c>instanceId</c> and <c>assuranceLevel</c> in its address (a PingFederate
/// adapter's). One of the page's scripts names the badge sign-in's path (<c>_INTERNAL_SECURE_BADGE_AUTH</c>) and the page's
/// <c>configuration/internal</c> the port it listens on (<c>internal.secure_badge_auth_port</c>). The sign-in is a GET there
/// with the resume parameters, over a connection that presents the person's smartcard (Windows asks for the PIN); it answers
/// with the SAML response, which the flow posts back to 3DPassport. Addresses are put together as Mount3DX puts them (the
/// page's address without its query, joined with a slash), with a script's address also resolved the usual way.
/// </summary>
internal static class BadgeSignIn
{
    private const string PathVariable = "_INTERNAL_SECURE_BADGE_AUTH";

    /// <summary>Whether the page is the identity provider's sign-in that offers the badge: its address carries the resume parameters.</summary>
    public static bool Applies(Uri page)
    {
        var query = Query(page);
        return query.ContainsKey("resumePath") && query.ContainsKey("instanceId") && query.ContainsKey("resumeUrl");
    }

    /// <summary>Signs in with the badge from the page at <paramref name="page"/>: the response that carries the SAML answer (or whatever the badge sign-in said).</summary>
    /// <param name="http">The flow's client, for the page's scripts and configuration.</param>
    /// <param name="badge">A client whose connections present the person's smartcard.</param>
    public static async Task<HttpResponseMessage> RunAsync(HttpClient http, HttpClient badge, Uri page, IDocument document, ILogger logger, CancellationToken ct)
    {
        var query = Query(page);
        string Required(string name) => query.TryGetValue(name, out var value) ? value : throw new SignInRequiredException($"the badge sign-in at {page.Host} has no {name} in its address");
        var resumeUrl = Required("resumeUrl");
        var resumePath = Required("resumePath");
        var instanceId = Required("instanceId");
        var assuranceLevel = Required("assuranceLevel");

        var pageBase = $"{page.Scheme}://{page.Authority}{page.AbsolutePath}";
        var path = await BadgePathAsync(http, page, pageBase, document, ct).ConfigureAwait(false)
            ?? throw new SignInRequiredException($"the badge sign-in at {page.Host} names no {PathVariable} in its scripts");

        int port;
        using (var config = await http.GetAsync(Join(pageBase, "configuration/internal"), ct).ConfigureAwait(false))
        {
            config.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await config.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            port = json.RootElement.TryGetProperty("configuration", out var configuration) && configuration.TryGetProperty("internal.secure_badge_auth_port", out var p) && p.TryGetInt32(out var n)
                ? n
                : throw new SignInRequiredException($"the badge sign-in at {page.Host} gives no internal.secure_badge_auth_port in its configuration");
        }

        var parameters = new (string Name, string Value)[]
        {
            ("resumeUrl", resumeUrl),
            ("resumePath", resumePath),
            ("instanceId", instanceId),
            ("pingfedDropoff", "true"),
            ("assuranceLevel", assuranceLevel),
            ("errorUrl", pageBase),
            ("gasWebClient", "true"),
            ("coordinates", "null"),
            ("countryCodeRequested", "false"),
            ("usaItarGeoRequested", "false"),
        };
        var target = Join($"{page.Scheme}://{page.Host}:{port}", path) + "?" + string.Join("&", parameters.Select(p => $"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(p.Value)}"));
        logger.LogInformation("3dx: badge sign-in at {Host}:{Port}; Windows may ask for the smartcard's PIN", page.Host, port);
        return await badge.GetAsync(new Uri(target), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    /// <summary>The badge sign-in's path, from the first of the page's scripts that names it (fetched together).</summary>
    private static async Task<string?> BadgePathAsync(HttpClient http, Uri page, string pageBase, IDocument document, CancellationToken ct)
    {
        var scripts = document.QuerySelectorAll("script[src]")
            .Select(s => s.GetAttribute("src")!)
            .SelectMany(src => new[] { Uri.TryCreate(page, src, out var resolved) ? resolved.AbsoluteUri : null, Join(pageBase, src) })
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var texts = await Task.WhenAll(scripts.Select(async script =>
        {
            try
            {
                using var response = await http.GetAsync(script, ct).ConfigureAwait(false);
                return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : "";
            }
            catch (HttpRequestException)
            {
                return "";
            }
        })).ConfigureAwait(false);

        foreach (var text in texts)
        {
            var at = text.LastIndexOf(PathVariable + "=\"", StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            var start = at + PathVariable.Length + 2;
            var end = text.IndexOf('"', start);
            if (end > start)
            {
                return text[start..end];
            }
        }

        return null;
    }

    /// <summary>Two parts of an address joined with one slash, as Mount3DX joins them.</summary>
    private static string Join(string left, string right) => $"{left.TrimEnd('/')}/{right.TrimStart('/')}";

    /// <summary>The page address's query parameters, decoded (the first of each name).</summary>
    internal static Dictionary<string, string> Query(Uri page)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in page.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = Decode(eq < 0 ? pair : pair[..eq]);
            var value = eq < 0 ? "" : Decode(pair[(eq + 1)..]);
            parameters.TryAdd(name, value);
        }

        return parameters;
    }

    private static string Decode(string text) => Uri.UnescapeDataString(text.Replace('+', ' '));
}
