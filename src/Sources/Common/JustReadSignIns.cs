using System.Collections.Concurrent;

namespace AnyFS.Sources.Common;

/// <summary>
/// The sign-in a browser session read from a profile a moment ago, for the other sources of the same host as they
/// start. A profile is one host's and one account's, shared by every source of that host, and each source launched the
/// browser on it for itself, one after another: N sources of a host were N launches at every start of the program
/// (the review of 21-Nov-2026, R9d). What one launch reads is what the next would read, so for a minute the next start
/// takes it.
/// <para>
/// Only a start takes it. A renewal follows a refusal of what the session holds, and what was read before that refusal
/// may be the very thing refused: a renewal reads anew, and what it reads is then here for the others.
/// </para>
/// </summary>
/// <param name="sharedFor">How long a sign-in read serves other starts (a minute by default).</param>
public sealed class JustReadSignIns(TimeSpan? sharedFor = null, TimeProvider? time = null)
{
    private readonly TimeSpan _for = sharedFor ?? TimeSpan.FromMinutes(1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (IReadOnlyList<SessionCookie> Cookies, long At)> _read = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The one every browser session of the process shares.</summary>
    public static JustReadSignIns Shared { get; } = new();

    /// <summary>A sign-in was read from <paramref name="profileDir"/> for <paramref name="host"/>: kept for the starts that follow.</summary>
    public void Remember(string profileDir, string host, IReadOnlyList<SessionCookie> cookies)
    {
        ArgumentNullException.ThrowIfNull(cookies);
        if (cookies.Count > 0)
        {
            _read[Key(profileDir, host)] = (cookies, _time.GetTimestamp());
        }
    }

    /// <summary>The sign-in read from the profile for the host within the last minute; null where there is none.</summary>
    public IReadOnlyList<SessionCookie>? Find(string profileDir, string host)
    {
        var key = Key(profileDir, host);
        if (!_read.TryGetValue(key, out var read))
        {
            return null;
        }

        if (_time.GetElapsedTime(read.At) < _for)
        {
            return read.Cookies;
        }

        _read.TryRemove(key, out _);
        return null;
    }

    private static string Key(string profileDir, string host)
    {
        ArgumentNullException.ThrowIfNull(profileDir);
        ArgumentNullException.ThrowIfNull(host);
        return profileDir + "|" + host;
    }
}
