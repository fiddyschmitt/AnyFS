using System.Text.Json;

namespace AnyFS.Sources.Common;

/// <summary>
/// What a signed token (a JWT) says of itself, read without checking who signed it: for telling what a token is for,
/// whose it is and when it runs out, never for trusting it. Three sessions each decoded one for themselves.
/// </summary>
public static class TokenClaims
{
    /// <summary>
    /// A token's claims as <paramref name="read"/> reads them; the default for anything that is not a signed token (a
    /// reference token, an API key) or whose claims are no object.
    /// </summary>
    public static T? Read<T>(string? token, Func<JsonElement, T?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var parts = token?.Split('.');
        if (parts is not { Length: >= 2 })
        {
            return default;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.ValueKind == JsonValueKind.Object ? read(doc.RootElement) : default;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return default;
        }
    }

    /// <summary>A claim that is text; null where it is not there, or is something else.</summary>
    public static string? Text(JsonElement claims, string name)
        => claims.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A claim that is a time, in seconds since 1970 (<c>exp</c>, <c>iat</c>); null where it is not there.</summary>
    public static DateTimeOffset? Time(JsonElement claims, string name)
        => claims.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds) && seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
}
