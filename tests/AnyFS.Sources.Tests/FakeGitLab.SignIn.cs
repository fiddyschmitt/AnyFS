using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AnyFS.Sources.Tests;

/// <summary>
/// The fake's sign-in page and its ways in, as spike B saw GitLab's: the page's CSRF token (a meta tag, tied to the
/// anonymous session's cookie), the Kerberos button's form (<c>/users/auth/kerberos</c>, then <c>/negotiate</c> and its
/// callback), an LDAP form and GitLab's own password form, each signing a session in with a cookie and a redirect, or
/// sending the browser back to the sign-in page. In-process the Negotiate page always answers 401 (no ticket); listening on
/// 127.0.0.1 (<see cref="Listen"/>) it takes Windows' own sign-in (NTLM over loopback), as the SharePoint farm's fake does.
/// </summary>
internal sealed partial class FakeGitLab
{
    private readonly Dictionary<string, string> _csrf = new(StringComparer.Ordinal);
    private HttpListener? _listener;

    /// <summary>Whether the sign-in page offers Kerberos (an Enterprise Edition server with it on).</summary>
    public bool KerberosSignIn { get; set; } = true;

    /// <summary>Whether the sign-in page offers an LDAP server's form.</summary>
    public bool LdapSignIn { get; set; } = true;

    /// <summary>Whom a Windows sign-in on the Negotiate page counts as (the listener's: whatever Windows account the test runs as).</summary>
    public string? WindowsUserAs { get; set; }

    /// <summary>Every sign-in form posted, in order: "ldap tester", "password tester", "kerberos" .</summary>
    public List<string> SignIns { get; } = [];

    /// <summary>A fake listening on a free port of 127.0.0.1, its Negotiate page signed in to with Windows' own (Windows only).</summary>
    public static FakeGitLab Listen()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var fake = new FakeGitLab($"http://127.0.0.1:{port}");
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.AuthenticationSchemeSelectorDelegate = r => r.Url!.AbsolutePath.EndsWith("/users/auth/kerberos/negotiate", StringComparison.Ordinal)
            ? AuthenticationSchemes.Negotiate | AuthenticationSchemes.Ntlm
            : AuthenticationSchemes.Anonymous;
        listener.Start();
        fake._listener = listener;
        _ = fake.ServeAsync(listener);
        return fake;
    }

    public void Stop() => _listener?.Close();

    private async Task ServeAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    using var body = new MemoryStream();
                    await context.Request.InputStream.CopyToAsync(body).ConfigureAwait(false);
                    var url = context.Request.Url!;
                    var headers = context.Request.Headers.AllKeys.OfType<string>().ToDictionary(k => k, k => context.Request.Headers[k] ?? "", StringComparer.OrdinalIgnoreCase);
                    var segments = url.GetComponents(UriComponents.Path, UriFormat.UriEscaped).Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
                    var query = (url.Query.Length > 1 ? url.Query[1..] : "").Split('&', StringSplitOptions.RemoveEmptyEntries)
                        .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "", StringComparer.Ordinal);
                    var windows = context.User?.Identity is { IsAuthenticated: true } identity ? identity.Name : null;
                    var answer = Answer(new Request(context.Request.HttpMethod, url, segments, query, headers, body.ToArray(), windows));
                    context.Response.StatusCode = answer.Status;
                    foreach (var (name, value) in answer.Headers ?? [])
                    {
                        if (name.Equals("Location", StringComparison.OrdinalIgnoreCase))
                        {
                            context.Response.RedirectLocation = value;
                        }
                        else if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        {
                            context.Response.AppendHeader(name, value);
                        }
                    }

                    if (answer.ContentType is not null)
                    {
                        context.Response.ContentType = answer.ContentType;
                    }

                    context.Response.ContentLength64 = answer.Body.Length;
                    await context.Response.OutputStream.WriteAsync(answer.Body).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
                {
                }
                finally
                {
                    try
                    {
                        context.Response.Close();
                    }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                    }
                }
            });
        }
    }

    /// <summary>The sign-in page and its forms; null for any other page.</summary>
    private Response? SignInPages(Request r, string path)
    {
        var form = r.Method == "POST" ? Form(r.Body) : [];
        switch (r.Method, path)
        {
            case ("GET", "/users/sign_in"):
            {
                var anon = "anon-" + Guid.NewGuid().ToString("N");
                var token = Convert.ToBase64String(Guid.NewGuid().ToByteArray()) + "+/=";
                _csrf[anon] = token;
                var html = new StringBuilder($"<html><head><meta name=\"csrf-token\" content=\"{WebUtility.HtmlEncode(token)}\" /></head><body>");
                if (KerberosSignIn)
                {
                    html.Append("<form class=\"js-omniauth-form\" method=\"post\" action=\"").Append(Root).Append("/users/auth/kerberos\"><button>Kerberos</button></form>");
                }

                if (LdapSignIn)
                {
                    html.Append("<form method=\"post\" action=\"").Append(Root).Append("/users/auth/ldapmain/callback\"><input name=\"username\"><input name=\"password\" type=\"password\"></form>");
                }

                html.Append("<form method=\"post\" action=\"").Append(Root).Append("/users/sign_in\"><input name=\"user[login]\"><input name=\"user[password]\"></form></body></html>");
                return new Response(200, Encoding.UTF8.GetBytes(html.ToString()), "text/html", new() { ["Set-Cookie"] = $"_gitlab_session={anon}; path=/; HttpOnly" });
            }

            case ("POST", "/users/auth/ldapmain/callback") when LdapSignIn:
            case ("POST", "/users/sign_in"):
            {
                if (!CsrfOk(r, form))
                {
                    return new Response(422, Encoding.UTF8.GetBytes("<html>The change you requested was rejected.</html>"), "text/html");
                }

                var ldap = path.Contains("ldap", StringComparison.Ordinal);
                var login = ldap ? form.GetValueOrDefault("username") : form.GetValueOrDefault("user[login]");
                SignIns.Add($"{(ldap ? "ldap" : "password")} {login}");
                var user = _users.FirstOrDefault(u => u.Username == login && u.Password is not null && u.Password == (ldap ? form.GetValueOrDefault("password") : form.GetValueOrDefault("user[password]")));
                return user is null ? Redirect("/users/sign_in", null) : Redirect("/", NewSession(user));
            }

            case ("POST", "/users/auth/kerberos") when KerberosSignIn:
                SignIns.Add("kerberos");
                return CsrfOk(r, form) ? Redirect("/users/auth/kerberos/negotiate", null) : new Response(422, [], "text/html");
            case ("GET", "/users/auth/kerberos/negotiate") when KerberosSignIn:
            {
                var user = r.WindowsUser is not null && WindowsUserAs is { } who ? _users.FirstOrDefault(u => u.Username == who) : null;
                if (user is null)
                {
                    return new Response(401, Encoding.UTF8.GetBytes("<html>Kerberos denied</html>"), "text/html", new() { ["WWW-Authenticate"] = "Negotiate" });
                }

                return Redirect("/users/auth/kerberos/callback", NewSession(user));
            }

            case ("GET", "/users/auth/kerberos/callback"):
                return SessionUser(r) is null ? Redirect("/users/sign_in", null) : Redirect("/dashboard/projects", null);
            default:
                return null;
        }
    }

    private string NewSession(User user)
    {
        var id = Guid.NewGuid().ToString("N");
        _sessions[id] = user;
        return id;
    }

    /// <summary>A redirect at the configured address (GitLab writes its external URL), with a new session cookie or none.</summary>
    private Response Redirect(string path, string? session)
    {
        var headers = new Dictionary<string, string> { ["Location"] = ExternalUrl + path };
        if (session is not null)
        {
            headers["Set-Cookie"] = $"_gitlab_session={session}; path=/; HttpOnly";
        }

        return new Response(302, Encoding.UTF8.GetBytes("<html>redirect</html>"), "text/html", headers);
    }

    private bool CsrfOk(Request r, Dictionary<string, string> form)
    {
        var cookie = r.Headers.GetValueOrDefault("Cookie") ?? "";
        foreach (var part in cookie.Split(';', StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("_gitlab_session=", StringComparison.Ordinal) && _csrf.TryGetValue(part["_gitlab_session=".Length..], out var token))
            {
                return form.GetValueOrDefault("authenticity_token") == token;
            }
        }

        return false;
    }

    private static Dictionary<string, string> Form(byte[] body)
        => Encoding.UTF8.GetString(body).Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => p.Length > 1 ? WebUtility.UrlDecode(p[1]) : "", StringComparer.Ordinal);

    /// <summary>The account's password for the forms.</summary>
    public void SetPassword(string username, string password) => _users.Single(u => u.Username == username).Password = password;
}
