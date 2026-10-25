// SharePointSignIn: signs in to Microsoft 365 as the AnyFS app registration, in the browser (or with a device
// code), and writes the sign-in (the serialised MSAL cache, refresh token included) to a file the SharePoint
// live tests load into their source's secret store. DPAPI-protected on Windows; elsewhere the file is plain and
// made private, so keep it out of shared folders. Run it again when the tests report the sign-in has lapsed.
//
// usage: SharePointSignIn [--account name@tenant] [--tenant organizations|<domain>|<id>] [--scopes site|containers|all|mail] [--device-code] [--force-login] [--print-url] [--out <file>] [--get <graph path>]... [--cloud global|usgov] [--client-id <id>]
//
// --cloud usgov signs in to the US Government cloud (GCC High) and asks its Graph; AnyFS's own registration does
// not exist there, so it needs --client-id with the organisation's own (and usually --tenant contoso.onmicrosoft.us).
// --print-url prints the sign-in page's address instead of opening a browser, for signing in from another
// browser or machine (the reply still arrives on this machine's loopback port). The default file is what the
// tests look for when ANYFS_TEST_SPO_CACHE is not set. The sign-in asks for the SharePoint Online and the
// SharePoint Embedded scopes together (--scopes narrows it), so one cache serves both sets of live tests. --scopes mail
// asks for the Outlook source's (its Graph store's live test, OutlookGraphTests), which the tenant's administrator must
// have consented to first; the sign-in goes into the same file beside the others.
// --get prints what Graph answers for a path (relative to v1.0) with that sign-in: for looking at the rig.
// --force-login makes the sign-in page ask for credentials instead of offering the browser's own signed-in
// accounts (18-Oct-2026: a sign-in meant for the test account came back as the administrator twice); with
// --account, a sign-in that ends as someone else is refused.

using System.Security.Cryptography;
using AnyFS.Sdk;
using AnyFS.Sources.Common;
using AnyFS.Sources.Microsoft365;
using AnyFS.Sources.Outlook;
using AnyFS.Sources.SharePoint;
using Microsoft.Extensions.Logging.Abstractions;

string? account = null;
var tenant = "organizations";
var deviceCode = false;
var forceLogin = false;
IReadOnlyList<string> scopes = [.. SharePointPlugin.SiteScopes.Concat(SharePointPlugin.ContainerScopes).Distinct()];
var printUrl = false;
var output = SignInCacheFile.DefaultPath;
var gets = new List<string>();
var cloud = MicrosoftCloud.Global;
var clientId = EntraSignIn.DefaultClientId;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--account": account = args[++i]; break;
        case "--tenant": tenant = args[++i]; break;
        case "--scopes":
            scopes = args[++i] switch
            {
                "site" => SharePointPlugin.SiteScopes,
                "containers" => SharePointPlugin.ContainerScopes,
                "all" => scopes,
                "mail" => OutlookPlugin.MailScopes,
                var other => throw new ArgumentException($"--scopes {other}: site, containers, all or mail"),
            };
            break;
        case "--device-code": deviceCode = true; break;
        case "--force-login": forceLogin = true; break;
        case "--print-url": printUrl = true; break;
        case "--out": output = args[++i]; break;
        case "--get": gets.Add(args[++i]); break;
        case "--cloud":
            cloud = args[++i] switch
            {
                "global" => MicrosoftCloud.Global,
                "usgov" => MicrosoftCloud.UsGovernment,
                var other => throw new ArgumentException($"--cloud {other}: global or usgov"),
            };
            break;
        case "--client-id": clientId = args[++i]; break;
        default:
            Console.Error.WriteLine($"unknown argument {args[i]}");
            return 2;
    }
}

var signIn = new EntraSignIn(new SignInOptions(
    SharePointPlugin.Service, clientId, tenant, account, deviceCode ? SignInMethod.DeviceCode : SignInMethod.Browser, scopes,
    Progress: Console.WriteLine,
    Prompt: (title, message) => { Console.WriteLine($"{title}: {message}"); return Task.CompletedTask; },
    LoadCache: () => SignInCacheFile.Read(output),
    SaveCache: bytes => SignInCacheFile.Write(output, bytes),
    OpenBrowser: printUrl ? uri => { Console.WriteLine($"OPEN THIS URL TO SIGN IN: {uri.AbsoluteUri}"); return true; } : null,
    ForceLogin: forceLogin,
    Cloud: cloud), NullLogger.Instance);
try
{
    await signIn.SignInAsync(CancellationToken.None);
}
catch (SignInRequiredException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

if (account is not null && !string.Equals(signIn.Username, account, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"signed in as {signIn.Username}, not {account}: run again with --force-login and sign in as {account} (choose \"Use another account\" if the page offers the wrong one)");
    return 1;
}

Console.WriteLine($"signed in as {signIn.Username}; the sign-in is kept in {output}");
if (gets.Count > 0)
{
    using var graph = new GraphClient((ct, refresh) => signIn.GetAccessTokenAsync(ct, refresh), NullLogger.Instance, SharePointPlugin.Service, cloud: cloud);
    foreach (var path in gets)
    {
        Console.WriteLine($"--- GET {path}");
        try
        {
            using var doc = await graph.GetJsonAsync(path, path, CancellationToken.None);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(doc.RootElement, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (VfsException ex)
        {
            Console.WriteLine($"{ex.Error}: {ex.Message}");
        }
    }
}

return 0;

/// <summary>Where a sign-in lives between runs, and how it is protected.</summary>
public static class SignInCacheFile
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnyFS", "tests", "sharepoint-online-signin.bin");

    public static byte[]? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = File.ReadAllBytes(path);
        return OperatingSystem.IsWindows() ? ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser) : bytes;
    }

    public static void Write(string path, byte[] cache)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = OperatingSystem.IsWindows() ? ProtectedData.Protect(cache, null, DataProtectionScope.CurrentUser) : cache;
        File.WriteAllBytes(path, bytes);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
