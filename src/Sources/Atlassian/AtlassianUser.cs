using System.Text.Json;

namespace AnyFS.Sources.Atlassian;

/// <summary>Someone Jira or Confluence names: their display name and their user name (or key, where only that is given).</summary>
internal sealed record AtlassianUser(string DisplayName, string Name)
{
    /// <summary>
    /// The account a who-am-I call answers: Jira's <c>/rest/api/2/myself</c>, Confluence's <c>/rest/api/user/current</c>.
    /// Null for no account, or for Confluence's anonymous user (Confluence answers the call for anyone where the site lets
    /// anonymous people in).
    /// </summary>
    public static AtlassianUser? Parse(AtlassianProduct product, JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (product == AtlassianProduct.Confluence && !string.Equals(e.Str("type"), "known", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = e.Str("name") ?? e.Str("username") ?? e.Str("key") ?? e.Str("userKey");
        return string.IsNullOrEmpty(name) ? null : new AtlassianUser(e.Str("displayName") ?? name, name);
    }

    /// <summary>Someone a field or a comment names (an assignee, an author): null where there is nobody.</summary>
    public static AtlassianUser? Person(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var name = e.Str("name") ?? e.Str("username") ?? e.Str("key") ?? e.Str("userKey") ?? e.Str("accountId");
        var display = e.Str("displayName") ?? name;
        return display is null ? null : new AtlassianUser(display, name ?? display);
    }

    public override string ToString() => DisplayName == Name ? DisplayName : $"{DisplayName} ({Name})";
}
