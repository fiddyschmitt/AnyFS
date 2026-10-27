namespace AnyFS.Sources.Microsoft365;

/// <summary>
/// The pace of requests to one SharePoint Online tenant (by its host), or to Graph for one account (spec 7.3):
/// <see cref="PerSecond"/> a second on average, up to <see cref="Burst"/> at once after a quiet spell, which keeps a crawl
/// within SharePoint Online's published default of 3,000 requests in 5 minutes for a user. SharePoint Server has no such
/// limit and goes unpaced. The bucket and the Retry-After hold are Common's <see cref="RequestPace"/>.
/// </summary>
public static class MicrosoftPace
{
    public const int PerSecond = 10;

    public const int Burst = 100;

    /// <summary>The pace for <paramref name="key"/> (a SharePoint Online host, or an account), shared by all that ask for it.</summary>
    public static RequestPace For(string key) => RequestPace.For(key, Burst, PerSecond);

    /// <summary>The pace for a site's host when it is SharePoint Online's (.sharepoint.com, .sharepoint.us); null for SharePoint Server.</summary>
    public static RequestPace? ForHost(string host) => MicrosoftCloud.FromSiteHost(host) is null ? null : For(host);
}
