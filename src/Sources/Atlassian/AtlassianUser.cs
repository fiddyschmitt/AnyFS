using System.Text.Json;
using AnyFS.Sources.Common;

namespace AnyFS.Sources.Atlassian;

/// <summary>Someone an Atlassian product names: their display name and their user name (or key, where only that is given).</summary>
internal sealed record AtlassianUser(string DisplayName, string Name)
{
    /// <summary>
    /// The account a who-am-I call answers: Jira's <c>/rest/api/2/myself</c>, Confluence's <c>/rest/api/user/current</c>,
    /// Bamboo's <c>/rest/api/latest/currentUser</c> (<c>name</c> and <c>fullName</c>), Bitbucket's <c>/rest/api/1.0/users/{slug}</c>
    /// for the name its answers' <c>X-AUSERNAME</c> gives. Null for no account, or for Confluence's anonymous user (Confluence
    /// answers the call for anyone where the site lets anonymous people in).
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

        var name = e.Str("name") ?? e.Str("username") ?? e.Str("key") ?? e.Str("userKey") ?? e.Str("slug");
        return string.IsNullOrEmpty(name) ? null : new AtlassianUser(e.Str("displayName") ?? e.Str("fullName") ?? name, name);
    }

    /// <summary>Someone a field or a comment names (an assignee, an author): null where there is nobody.</summary>
    public static AtlassianUser? Person(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Bitbucket wraps a pull request's author and reviewers in {user, role, approved}
        if (!e.Has("name") && !e.Has("displayName") && e.Obj("user") is { ValueKind: JsonValueKind.Object } inner)
        {
            return Person(inner);
        }

        var name = e.Str("name") ?? e.Str("username") ?? e.Str("key") ?? e.Str("userKey") ?? e.Str("slug") ?? e.Str("accountId");
        var display = e.Str("displayName") ?? e.Str("fullName") ?? name;
        return display is null ? null : new AtlassianUser(display, name ?? display);
    }

    public override string ToString() => DisplayName == Name ? DisplayName : $"{DisplayName} ({Name})";
}
