using System.Net;
using System.Text;
using System.Text.Json;

namespace AnyFS.Sources.Common;

/// <summary>
/// A web session's cookies in a <see cref="CookieContainer"/> (spec 7.0): the browser's taken in, a new sign-in's in place
/// of the old, and all of them written out as JSON for the source's secret store and read back at the next start (those
/// since expired left out). 3DX's and GitLab's sessions keep their sign-ins this way.
/// </summary>
public static class CookieJar
{
    /// <summary>A jar big enough for an identity provider's cookies as well as the site's.</summary>
    public static CookieContainer New() => new(capacity: 3000, perDomainCapacity: 300, maxCookieSize: 64 * 1024);

    /// <summary>The browser's cookies into <paramref name="jar"/>; one .NET will not hold (a name or value it refuses) is left out.</summary>
    public static void Add(CookieContainer jar, IEnumerable<SessionCookie> cookies)
    {
        foreach (var c in cookies)
        {
            try
            {
                jar.Add(new Cookie(c.Name, c.Value, string.IsNullOrEmpty(c.Path) ? "/" : c.Path, c.Domain) { Secure = c.Secure, HttpOnly = c.HttpOnly });
            }
            catch (CookieException)
            {
                // a cookie .NET will not hold: the session goes without it
            }
        }
    }

    /// <summary>The cookies of <paramref name="from"/> in place of all of <paramref name="into"/>'s: a new sign-in's, which those of before would only get in the way of.</summary>
    public static void Replace(CookieContainer into, CookieContainer from)
    {
        Clear(into);
        foreach (Cookie c in from.GetAllCookies())
        {
            try
            {
                into.Add(c);
            }
            catch (CookieException)
            {
                // a cookie the session's jar will not hold: the session goes without it
            }
        }
    }

    /// <summary>Every cookie of <paramref name="jar"/> gone (expired).</summary>
    public static void Clear(CookieContainer jar)
    {
        foreach (Cookie old in jar.GetAllCookies())
        {
            old.Expired = true;
        }
    }

    /// <summary>The jar's cookies as JSON (name, value, domain, path, secure, httpOnly, expires), those expired left out.</summary>
    public static string Export(CookieContainer jar)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            foreach (Cookie c in jar.GetAllCookies())
            {
                if (c.Expired)
                {
                    continue;
                }

                json.WriteStartObject();
                json.WriteString("name", c.Name);
                json.WriteString("value", c.Value);
                json.WriteString("domain", c.Domain);
                json.WriteString("path", c.Path);
                json.WriteBoolean("secure", c.Secure);
                json.WriteBoolean("httpOnly", c.HttpOnly);
                if (c.Expires != DateTime.MinValue)
                {
                    json.WriteString("expires", c.Expires.ToUniversalTime());
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// The cookies <see cref="Export"/> wrote, into <paramref name="jar"/> (those since expired left out; one with no domain
    /// gets <paramref name="host"/>): how many, or -1 when the text is no such list.
    /// </summary>
    public static int Import(CookieContainer jar, string text, string host)
    {
        var count = 0;
        try
        {
            using var doc = JsonDocument.Parse(text);
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                var expires = c.TryGetProperty("expires", out var e) && e.TryGetDateTime(out var when) ? when : DateTime.MinValue;
                if (expires != DateTime.MinValue && expires < DateTime.UtcNow)
                {
                    continue;
                }

                try
                {
                    var cookie = new Cookie(c.GetProperty("name").GetString()!, c.GetProperty("value").GetString() ?? "", c.GetProperty("path").GetString() ?? "/", c.GetProperty("domain").GetString() ?? host)
                    {
                        Secure = c.GetProperty("secure").GetBoolean(),
                        HttpOnly = c.GetProperty("httpOnly").GetBoolean(),
                    };
                    if (expires != DateTime.MinValue)
                    {
                        cookie.Expires = expires;
                    }

                    jar.Add(cookie);
                    count++;
                }
                catch (CookieException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return -1;
        }

        return count;
    }
}
