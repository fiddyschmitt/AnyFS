using System.Net;
using AnyFS.Sdk;

namespace AnyFS.Sources.ThreeDX;

/// <summary>How a 3DX source signs in (spec 7.6.2).</summary>
internal enum ThreeDXSignIn
{
    /// <summary>Single sign-on without a browser where the organisation's sign-in allows it, else a browser of AnyFS's own: the person signs in there and AnyFS keeps the session.</summary>
    WebSession,

    /// <summary>As the person is signed in to Windows, a smartcard too, through the redirects to the sign-in and back; no browser.</summary>
    WindowsCurrent,

    /// <summary>A user name and password: a Windows sign-in where the servers ask for one, or 3DPassport's own sign-in.</summary>
    Windows,
}

public sealed class ThreeDXSettings
{
    public const string WebSession = "Web session, single sign-on first";
    public const string WindowsCurrent = WindowsAccount.AsYourself;
    public const string Windows = WindowsAccount.WithPassword;

    /// <summary>
    /// The search Mount3DX lists documents with: every document and controlled document, without the templates and the
    /// versions and proxies behind them.
    /// </summary>
    public const string DefaultQuery = "((flattenedtaxonomies:types/Document) OR (flattenedtaxonomies:\"types/CONTROLLED DOCUMENTS\")) NOT ((policy:\"Controlled Document Template\") OR (policy:\"Rendition Document Template\") OR (policy:Version) OR (policy:ProxyItem))";

    [Setting("3DSpace URL", Required = true, Primary = true, Order = 0, Placeholder = "https://3dx.contoso.com/3dspace",
        Hint = "The address of the platform's 3DSpace service, as Mount3DX takes it: usually the server and /3dspace. A 3DSpace page's address pasted here is cut back to the service's.")]
    public string ServerUrl { get; set; } = "";

    [Setting("Sign in with", Order = 1, Group = "Sign-in", Kind = SettingKind.Choice, Choices = [WebSession, WindowsCurrent, Windows],
        Hint = "Web session: AnyFS signs in without a browser where your organisation's sign-in lets it (your Windows sign-in, or your smartcard where it is asked for), as Mount3DX does; otherwise a browser window of AnyFS's own opens for you to sign in, and AnyFS keeps that session. Windows account, as yourself: as you are signed in to Windows (a smartcard or Windows Hello sign-in too), with nothing to type and no browser. Windows account: a user name and password, for 3DPassport's own sign-in or a server that asks for a Windows sign-in.")]
    public string SignInMethod { get; set; } = WebSession;

    [Setting("Username", Order = 2, Group = "Sign-in", Placeholder = @"name, or CONTOSO\name",
        Hint = "The account to sign in as: your 3DEXPERIENCE user name for 3DPassport's own sign-in, or a Windows account (DOMAIN\\name or name@domain) where the servers ask for one.")]
    public string? Username { get; set; }

    [Setting("Password", Secret = true, Order = 3, Group = "Sign-in")]
    public string? Password { get; set; }

    [Setting("Show _link.url and _metadata.json", Order = 4,
        Hint = "In each document's folder, a shortcut that opens the document in 3DX and a file of its attributes as 3DX gives them (made when it is read), as Mount3DX shows them.")]
    public bool MetadataFiles { get; set; } = true;

    [Setting("Browser", Advanced = true, Order = 10, Kind = SettingKind.FilePath,
        Hint = "The Edge, Chrome or Chromium to open for the web session's sign-in. Leave empty to find one automatically. AnyFS opens it with a profile of its own, never your everyday browser's; where a policy keeps AnyFS from driving it, the sign-in goes through Selenium WebDriver (your default browser, then Firefox and the others).")]
    public string? BrowserPath { get; set; }

    [Setting("Trust any certificate", Advanced = true, Order = 11,
        Hint = "Accept the servers' HTTPS certificates whatever they are (a lab with a self-signed one). Off for a real server.")]
    public bool TrustAnyCertificate { get; set; }

    [Setting("Refresh the document list every (minutes)", Advanced = true, Order = 20,
        Hint = "How long after one listing of every document the next begins, in the background; the last list is served meanwhile and kept for the next start. 0 lists at the start only. Pressing refresh twice in a document's folder fetches that document afresh at once.")]
    public int RefreshMinutes { get; set; } = 30;

    [Setting("Keep the session alive every (minutes)", Advanced = true, Order = 21,
        Hint = "How often AnyFS touches 3DSpace and 3DSearch so their sessions do not time out while nothing is read, as Mount3DX does. 0 never.")]
    public int KeepAliveMinutes { get; set; } = 5;

    [Setting("Queries at a time", Advanced = true, Order = 22, Hint = "Pages of the document list fetched together.")]
    public int QueryThreads { get; set; } = 8;

    [Setting("Connections", Advanced = true, Order = 23, Hint = "Parallel downloads per open file above the spool size.")]
    public int Connections { get; set; } = 4;

    [Setting("Spool files below (MiB)", Advanced = true, Order = 24,
        Hint = "Files this small are downloaded whole when opened, served as they arrive and kept in the cache; larger ones are read in ranges as the client asks.")]
    public int SpoolBelowMiB { get; set; } = 64;

    [Setting("Search", Advanced = true, Order = 25, Kind = SettingKind.Multiline,
        Hint = "The 3DSearch query that finds the documents to show. The default is Mount3DX's: documents and controlled documents, without templates, versions and proxies.")]
    public string SearchQuery { get; set; } = DefaultQuery;

    [Setting("Tenant", Advanced = true, Order = 26, Hint = "The tenant 3DSearch is asked in: OnPremise for a platform of your own.")]
    public string Tenant { get; set; } = "OnPremise";

    [Setting("Date order", Advanced = true, Order = 27, Kind = SettingKind.Choice, Choices = [ThreeDXDates.ThisComputer, ThreeDXDates.DayFirst, ThreeDXDates.MonthFirst],
        Hint = "How 3DX writes a date like 03/07/2018. 3DX writes dates for a locale; This computer's reads them as this PC writes dates, as Mount3DX did.")]
    public string DateOrder { get; set; } = ThreeDXDates.ThisComputer;

    /// <summary>The 3DSpace service's address with a slash at the end (what its paths are relative to), or null while it is no http(s) address.</summary>
    internal Uri? Space => SpaceOf(ServerUrl);

    internal static Uri? SpaceOf(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || string.IsNullOrEmpty(url.Host))
        {
            return null;
        }

        return new Uri(ServiceAddress(url) + "/");
    }

    /// <summary>The service's address from one of its pages: cut before the first of 3DSpace's own folders, without a query or a slash at the end.</summary>
    internal static string ServiceAddress(Uri url)
    {
        var path = url.AbsolutePath;
        foreach (var inner in new[] { "/common/", "/resources/", "/webapps/", "/servlet/", "/fcs/", "/emxNavigator.jsp", "/emxLogin.jsp" })
        {
            var at = path.IndexOf(inner, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                path = path[..at];
            }
        }

        return $"{url.Scheme}://{url.Authority}{path.TrimEnd('/')}";
    }

    internal ThreeDXSignIn EffectiveSignIn => SignInFrom(SignInMethod) ?? ThreeDXSignIn.WebSession;

    internal static ThreeDXSignIn? SignInFrom(string? choice) => (choice ?? "").Trim() switch
    {
        var c when c.Length == 0 || c.Equals(WebSession, StringComparison.OrdinalIgnoreCase) || c.StartsWith("Web session", StringComparison.OrdinalIgnoreCase) || c.Equals("Automatic", StringComparison.OrdinalIgnoreCase) => ThreeDXSignIn.WebSession,
        var c when c.Equals(WindowsCurrent, StringComparison.OrdinalIgnoreCase) => ThreeDXSignIn.WindowsCurrent,
        var c when c.Equals(Windows, StringComparison.OrdinalIgnoreCase) => ThreeDXSignIn.Windows,
        _ => null,
    };

    internal bool IsWindowsCurrent => EffectiveSignIn == ThreeDXSignIn.WindowsCurrent;

    /// <summary>The account the sign-in goes as, for messages.</summary>
    internal string Who => EffectiveSignIn switch
    {
        ThreeDXSignIn.Windows => WindowsAccount.Name(asYourself: false, Username),
        ThreeDXSignIn.WindowsCurrent => WindowsAccount.Name(asYourself: true, null),
        _ => "the web session",
    };

    /// <summary>The Windows credentials the servers get when they ask: the user name and password, or as yourself; none for a user name left empty under Windows account (it then signs in as yourself).</summary>
    internal ICredentials Credentials() => WindowsAccount.Credentials(IsWindowsCurrent, EffectiveSignIn == ThreeDXSignIn.Windows ? Username : null, Password);

    /// <summary>The user name and password for a sign-in page (3DPassport's): the name without a Windows domain before it; null when there is none to give.</summary>
    internal NetworkCredential? FormCredentials()
    {
        if (EffectiveSignIn != ThreeDXSignIn.Windows || string.IsNullOrWhiteSpace(Username))
        {
            return null;
        }

        var user = Username.Trim();
        return new NetworkCredential(user[(user.IndexOf('\\') + 1)..], Password ?? "");
    }

    internal string? Browser => string.IsNullOrWhiteSpace(BrowserPath) ? null : BrowserPath.Trim();

    internal string Query => string.IsNullOrWhiteSpace(SearchQuery) ? DefaultQuery : SearchQuery.Trim();

    internal string EffectiveTenant => string.IsNullOrWhiteSpace(Tenant) ? "OnPremise" : Tenant.Trim();
}
