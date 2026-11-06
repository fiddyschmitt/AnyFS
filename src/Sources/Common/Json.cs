using System.Globalization;
using System.Text.Json;

namespace AnyFS.Sources.Common;

/// <summary>
/// Reading a server's JSON by hand (no reflection, so the trimmed build keeps it working): a missing or null property reads
/// as nothing, never as an error. GitLab's and the Atlassian products' sources read theirs with these.
/// </summary>
public static class Json
{
    /// <summary>A string property; a number or a boolean as its text; null for anything else.</summary>
    public static string? Str(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        } : null;

    /// <summary>A whole number, as a number or as a string (GraphQL's BigInt comes as one); 0 for anything else.</summary>
    public static long Long(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => 0,
        } : 0;

    /// <summary>True for <c>true</c>, as a boolean or as the string.</summary>
    public static bool Bool(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.String && v.GetString() == "true");

    /// <summary>An object property, or a default element (whose own properties then read as nothing).</summary>
    public static JsonElement Obj(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    /// <summary>An array property's elements, or none.</summary>
    public static IEnumerable<JsonElement> Arr(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    /// <summary>Whether the property is there with a value (not null).</summary>
    public static bool Has(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    /// <summary>A time as a string property: ISO 8601 (<c>2026-10-30T14:02:11Z</c>), Confluence's <c>2026-10-30T14:02:11.000+10:00</c>, or Jira's <c>2026-10-30T14:02:11.000+1000</c>.</summary>
    public static DateTimeOffset? Date(this JsonElement e, string name) => ParseDate(e.Str(name));

    /// <summary>A time as Bitbucket writes it: milliseconds since the epoch (<c>1761832931000</c>); null for none.</summary>
    public static DateTimeOffset? Epoch(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var ms) && ms > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;

    /// <summary>A time as the servers write it (<see cref="Date"/>); null for none, or for text that is no time.</summary>
    public static DateTimeOffset? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Jira writes the offset without a colon (+1000): given one, .NET reads it as any other
        var t = text.Trim();
        if (t.Length > 10 && t[^5] is '+' or '-' && t[^4..].All(char.IsAsciiDigit))
        {
            t = t[..^2] + ":" + t[^2..];
        }

        return DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
    }
}
