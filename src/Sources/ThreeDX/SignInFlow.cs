using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.ThreeDX;

/// <summary>Where a <see cref="SignInFlow"/> got to: through to the service, or where it stopped and why.</summary>
/// <param name="Route">The hosts and paths it went through, for the log (no query strings: they carry tickets).</param>
/// <param name="Refused">A server refused the account given (a password, or the Windows sign-in): trying again unchanged cannot help.</param>
/// <param name="RefusedAtStart">The address the flow started from refused it (a 401 asking for no Windows sign-in) instead of sending it to the sign-in, as a service may answer its API without a session: a page of the service may start it instead.</param>
internal sealed record FlowResult(bool SignedIn, string Route, string? Problem = null, bool Refused = false, bool RefusedAtStart = false);

/// <summary>
/// A sign-in without a browser (spec 7.6.2): from a page of the service, the redirects a browser would follow to the sign-in
/// and back, with the session's cookies throughout. A server that asks for a Windows sign-in (401 Negotiate or NTLM) gets
/// the credentials the handler holds; a page that hands the sign-in on by a form posting itself (a SAML request or answer, a
/// WS-Federation result) is posted on; 3DPassport's own sign-in, or a plain user name and password page, gets the user name
/// and password when the source has them. It stops at anything else (a page asking for something it has no answer to: the
/// web session then opens a browser) and after <see cref="MaxSteps"/> steps. Mount3DX's smartcard badge sign-in is not
/// here but in <see cref="SingleSignOn"/>, its own steps on a client of its own, which the web session and as yourself try
/// as well.
/// </summary>
internal sealed partial class SignInFlow
{
    public const int MaxSteps = 40;

    /// <summary>Some identity providers (AD FS) offer the Windows sign-in only to browsers they know.</summary>
    public const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36 Edg/141.0.0.0";

    private const int MaxPage = 4 << 20;

    // the fields a form that hands a sign-in on carries (SAML, WS-Federation, OpenID Connect's form post, a CAS ticket)
    private static readonly HashSet<string> HandOverFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "SAMLRequest", "SAMLResponse", "RelayState", "wa", "wresult", "wctx", "code", "id_token", "access_token", "session_state", "state", "ticket",
    };

    private static readonly string[] VisibleTypes = ["text", "email", "password", "tel", "number", "search", "url"];

    private readonly HttpClient _http;
    private readonly NetworkCredential? _password;
    private readonly ILogger _logger;

    /// <param name="http">A client over the session's handler (its cookies and Windows credentials), following no redirects itself.</param>
    /// <param name="password">The user name and password for a sign-in page that asks for them; null when the source has none.</param>
    public SignInFlow(HttpClient http, ILogger logger, NetworkCredential? password = null)
    {
        _http = http;
        _logger = logger;
        _password = password;
    }

    /// <summary>
    /// Goes from <paramref name="start"/> until <paramref name="done"/> says the service answered (the response, its body),
    /// or the flow cannot go on.
    /// </summary>
    public async Task<FlowResult> RunAsync(Uri start, Func<HttpResponseMessage, string, bool> done, CancellationToken ct)
    {
        var route = new List<string>();
        var posted = new HashSet<string>(StringComparer.Ordinal);
        var passwordGiven = false;
        var passportGiven = false;
        var request = Get(start);
        for (var step = 1; step <= MaxSteps; step++)
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var url = response.RequestMessage?.RequestUri ?? request.RequestUri!;
            route.Add($"{url.Host}{url.AbsolutePath} {(int)response.StatusCode}{Challenge(response)}");
            _logger.LogDebug("3dx: sign-in step {Step}: {Method} {Host}{Path} -> {Status}", step, response.RequestMessage?.Method, url.Host, url.AbsolutePath, (int)response.StatusCode);

            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                request = Follow(response, new Uri(url, location));
                continue;
            }

            var body = await BodyAsync(response, ct).ConfigureAwait(false);
            if (done(response, body))
            {
                return new FlowResult(true, Route(route));
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                var schemes = response.Headers.WwwAuthenticate.Select(h => h.Scheme).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var windows = schemes.Any(s => s.Equals("Negotiate", StringComparison.OrdinalIgnoreCase) || s.Equals("NTLM", StringComparison.OrdinalIgnoreCase) || s.Equals("Kerberos", StringComparison.OrdinalIgnoreCase));
                if (step == 1 && !windows)
                {
                    // the service refuses a request without a session instead of sending it to the sign-in: start from a page
                    return new FlowResult(false, Route(route), $"{url.Host}{url.AbsolutePath} refused the request without sending it to a sign-in (401)", RefusedAtStart: true);
                }

                return new FlowResult(false, Route(route), $"{url.Host} refused the sign-in (401{(schemes.Count > 0 ? $"; it asks for {string.Join(", ", schemes)}" : "")})", Refused: windows);
            }

            if (!IsHtml(response) || body.Length == 0)
            {
                return new FlowResult(false, Route(route), $"{url.Host}{url.AbsolutePath} answered {(int)response.StatusCode} with {response.Content.Headers.ContentType?.MediaType ?? "nothing"} instead of the service");
            }

            var page = await new HtmlParser().ParseDocumentAsync(body, ct).ConfigureAwait(false);
            var next = HandOver(url, page, posted);
            if (next is null && _password is not null)
            {
                // the password goes once: a page asking for it again after it is a refusal, and giving it again and again
                // would lock the account (a page asking for the user name alone, before the password, is no refusal)
                var passport = IsPassportLogin(url);
                if (passport && passportGiven || AsksPassword(page) && passwordGiven)
                {
                    return new FlowResult(false, Route(route), $"{url.Host} refused the user name and password for {_password.UserName}", Refused: true);
                }

                if (passport && await PassportAsync(url, ct).ConfigureAwait(false) is { } signIn)
                {
                    passportGiven = passwordGiven = true;
                    next = signIn;
                }
                else if (PasswordPage(url, page, posted) is { } filled)
                {
                    passwordGiven |= filled.WithPassword;
                    next = filled.Request;
                }
            }

            next ??= Redirect(url, page, body);
            if (next is null)
            {
                return new FlowResult(false, Route(route), $"the sign-in stopped at {url.Host}{url.AbsolutePath}, {What(page)}");
            }

            request = next;
        }

        return new FlowResult(false, Route(route), $"the sign-in went on for more than {MaxSteps} steps without reaching the service");
    }

    private static string Route(List<string> route) => string.Join(" -> ", route);

    private static HttpRequestMessage Get(Uri url) => new(HttpMethod.Get, url);

    /// <summary>The request a redirect asks for: the same method and body for 307 and 308, a GET for the rest (as browsers do).</summary>
    private static HttpRequestMessage Follow(HttpResponseMessage response, Uri target)
    {
        if (response.StatusCode is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect && response.RequestMessage is { } previous && previous.Method != HttpMethod.Get)
        {
            var again = new HttpRequestMessage(previous.Method, target);
            if (previous.Content is FormUrlEncodedContent or StringContent)
            {
                again.Content = previous.Content;
            }

            return again;
        }

        return Get(target);
    }

    private static string Challenge(HttpResponseMessage response)
        => response.StatusCode == HttpStatusCode.Unauthorized && response.Headers.WwwAuthenticate.Count > 0
            ? $" ({string.Join(", ", response.Headers.WwwAuthenticate.Select(h => h.Scheme).Distinct(StringComparer.OrdinalIgnoreCase))})"
            : "";

    private static bool IsHtml(HttpResponseMessage response)
        => response.Content.Headers.ContentType?.MediaType is { } type && type.Contains("html", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> BodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaxPage)
        {
            return "";
        }

        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A form that hands the sign-in on (a SAML request or answer, a WS-Federation result): it carries one of their fields,
    /// or nothing a person would fill in and the page posts it itself. Each one is posted once.
    /// </summary>
    private HttpRequestMessage? HandOver(Uri url, IDocument page, HashSet<string> posted)
    {
        var selfPosting = page.Source.Text.Contains("submit()", StringComparison.OrdinalIgnoreCase);
        foreach (var form in page.Forms)
        {
            // a form with something for a person to fill in is a sign-in page, whatever it carries hidden (a relay state)
            if (Inputs(form).Any(IsVisible))
            {
                continue;
            }

            var fields = Fields(form);
            var handsOver = fields.Any(f => HandOverFields.Contains(f.Key) && f.Value.Length > 0 && IsHidden(form, f.Key));
            if (!handsOver && !(fields.Count > 0 && selfPosting))
            {
                continue;
            }

            var submit = Submit(url, form, fields);
            if (posted.Add($"{submit.Method} {submit.RequestUri} {string.Join("&", fields.Select(f => f.Key))}"))
            {
                _logger.LogDebug("3dx: posting the sign-in on to {Host}{Path} ({Fields})", submit.RequestUri!.Host, submit.RequestUri.AbsolutePath, string.Join(", ", fields.Select(f => f.Key)));
                return submit;
            }
        }

        return null;
    }

    /// <summary>
    /// 3DPassport's own sign-in (its login page), when the source has a user name and password: the login ticket from
    /// <c>login?action=get_auth_params</c>, then the user name, password and ticket posted to the page, as 3DEXPERIENCE's own
    /// scripts sign in. The page's form is drawn by script, so it is not read from the page.
    /// </summary>
    private async Task<HttpRequestMessage?> PassportAsync(Uri url, CancellationToken ct)
    {
        if (_password is null)
        {
            return null;
        }

        string? ticket;
        try
        {
            using var response = await _http.GetAsync(new Uri(url.GetLeftPart(UriPartial.Path) + "?action=get_auth_params"), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            ticket = json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("lt", out var lt) ? lt.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }

        if (string.IsNullOrEmpty(ticket))
        {
            return null;
        }

        _logger.LogDebug("3dx: signing in to 3DPassport at {Host}{Path} as {User}", url.Host, url.AbsolutePath, _password.UserName);
        return new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(
            [
                new("lt", ticket),
                new("username", _password.UserName),
                new("password", _password.Password),
                new("rememberMe", "no"),
            ]),
        };
    }

    /// <summary>A page at 3DPassport's sign-in address (<c>.../login</c>): its sign-in is asked for by script, not by the page's form.</summary>
    private static bool IsPassportLogin(Uri url) => url.AbsolutePath.EndsWith("/login", StringComparison.OrdinalIgnoreCase);

    private static bool AsksPassword(IDocument page) => page.Forms.SelectMany(Inputs).Any(i => Type(i) == "password");

    /// <summary>
    /// A page asking for a user name and password (or the user name first), filled in when the source has them; each form
    /// once. Whether the password went with it.
    /// </summary>
    private (HttpRequestMessage Request, bool WithPassword)? PasswordPage(Uri url, IDocument page, HashSet<string> posted)
    {
        if (_password is null)
        {
            return null;
        }

        foreach (var form in page.Forms)
        {
            var inputs = Inputs(form).ToList();
            var password = inputs.FirstOrDefault(i => Type(i) == "password");
            var user = inputs.Where(i => Type(i) is "text" or "email" && IsVisible(i)).OrderByDescending(i => LooksLikeUser(i.GetAttribute("name"))).FirstOrDefault();
            if (password is null && (user is null || !LooksLikeUser(user.GetAttribute("name"))))
            {
                continue;
            }

            var fields = Fields(form);
            if (user?.GetAttribute("name") is { Length: > 0 } userField)
            {
                fields[userField] = _password.UserName;
            }

            if (password?.GetAttribute("name") is { Length: > 0 } passwordField)
            {
                fields[passwordField] = _password.Password;
            }

            var submit = Submit(url, form, fields);
            if (posted.Add($"{submit.Method} {submit.RequestUri} {string.Join("&", fields.Keys)}"))
            {
                _logger.LogDebug("3dx: filling in the sign-in page at {Host}{Path} as {User}", url.Host, url.AbsolutePath, _password.UserName);
                return (submit, password is not null);
            }
        }

        return null;
    }

    private static bool LooksLikeUser(string? name)
        => name is not null && UserField().IsMatch(name);

    [GeneratedRegex("user|login|email|identifier|account|loginfmt|j_username|uid", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserField();

    /// <summary>A page that sends the browser on without a form: a refresh, or a script setting the location (short pages only).</summary>
    private static HttpRequestMessage? Redirect(Uri url, IDocument page, string body)
    {
        var refresh = page.QuerySelectorAll("meta").FirstOrDefault(m => string.Equals(m.GetAttribute("http-equiv"), "refresh", StringComparison.OrdinalIgnoreCase))?.GetAttribute("content");
        if (refresh is not null && RefreshTarget().Match(refresh) is { Success: true } target && Uri.TryCreate(url, target.Groups[1].Value.Trim().Trim('\'', '"'), out var to))
        {
            return Get(to);
        }

        if (body.Length < 16 * 1024 && page.Forms.Length == 0 && ScriptLocation().Match(body) is { Success: true } script
            && Uri.TryCreate(url, WebUtility.HtmlDecode(script.Groups[1].Success ? script.Groups[1].Value : script.Groups[2].Value), out var go))
        {
            return Get(go);
        }

        return null;
    }

    [GeneratedRegex(@"url\s*=\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RefreshTarget();

    [GeneratedRegex(@"(?:(?:window|document|self|top)\.)?location(?:\.href)?\s*=\s*[""']([^""']+)[""']|location\.replace\(\s*[""']([^""']+)[""']\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptLocation();

    /// <summary>What the page the flow stopped at asks for, for the message.</summary>
    private static string What(IDocument page)
    {
        var inputs = page.Forms.SelectMany(Inputs).Where(IsVisible).Select(i => Type(i)).Distinct().ToList();
        var title = page.Title?.Trim();
        var named = string.IsNullOrEmpty(title) ? "a page" : $"\"{title}\"";
        return inputs.Contains("password") ? $"{named}, which asks for a user name and password"
            : inputs.Count > 0 ? $"{named}, which asks for something to be filled in"
            : $"{named}, which a browser would need to go on from";
    }

    private static IEnumerable<IElement> Inputs(IHtmlFormElement form) => form.QuerySelectorAll("input, select, textarea");

    private static string Type(IElement input) => input.LocalName == "input" ? (input.GetAttribute("type") ?? "text").ToLowerInvariant() : input.LocalName;

    private static bool IsVisible(IElement input) => VisibleTypes.Contains(Type(input)) || input.LocalName is "select" or "textarea";

    private static bool IsHidden(IHtmlFormElement form, string name)
        => Inputs(form).Any(i => string.Equals(i.GetAttribute("name"), name, StringComparison.Ordinal) && Type(i) == "hidden");

    /// <summary>The form's named values as a browser would send them: inputs (checked boxes only), selects' chosen options, text areas; no buttons.</summary>
    private static Dictionary<string, string> Fields(IHtmlFormElement form)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in Inputs(form))
        {
            var name = input.GetAttribute("name");
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var type = Type(input);
            if (type is "submit" or "button" or "image" or "reset" or "file" || type is "checkbox" or "radio" && !input.HasAttribute("checked"))
            {
                continue;
            }

            fields.TryAdd(name, input switch
            {
                IHtmlSelectElement select => select.Value ?? "",
                IHtmlTextAreaElement area => area.Value,
                _ => input.GetAttribute("value") ?? (type is "checkbox" or "radio" ? "on" : ""),
            });
        }

        return fields;
    }

    /// <summary>The request a form makes when submitted with <paramref name="fields"/>: to its action (the page when it has none), by its method.</summary>
    private static HttpRequestMessage Submit(Uri url, IHtmlFormElement form, Dictionary<string, string> fields)
    {
        var action = form.GetAttribute("action");
        var target = string.IsNullOrWhiteSpace(action) ? url : new Uri(url, WebUtility.HtmlDecode(action.Trim()));
        if (string.Equals(form.GetAttribute("method"), "get", StringComparison.OrdinalIgnoreCase))
        {
            var query = string.Join("&", fields.Select(f => $"{Uri.EscapeDataString(f.Key)}={Uri.EscapeDataString(f.Value)}"));
            return Get(new UriBuilder(target) { Query = query }.Uri);
        }

        return new HttpRequestMessage(HttpMethod.Post, target) { Content = new FormUrlEncodedContent(fields) };
    }
}
