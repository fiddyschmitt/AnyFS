using System.Globalization;
using System.Text.Json;

namespace AnyFS.Sources.Atlassian;

/// <summary>Reading Jira's and Confluence's JSON: a missing or null property reads as nothing, never as an error.</summary>
internal static class Json
{
    public static string? Str(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        } : null;

    public static long Long(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => 0,
        } : 0;

    public static bool Bool(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.String && v.GetString() == "true");

    public static JsonElement Obj(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    public static IEnumerable<JsonElement> Arr(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    public static bool Has(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    /// <summary>A time as Jira (<c>2026-10-30T14:02:11.000+1000</c>) or Confluence (<c>2026-10-30T14:02:11.000+10:00</c>) writes it.</summary>
    public static DateTimeOffset? Date(this JsonElement e, string name) => ParseDate(e.Str(name));

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
