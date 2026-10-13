using AnyFS.Sdk;

namespace AnyFS.Mounts.WebDav;

/// <summary>One active WebDAV write lock.</summary>
internal sealed class DavLock(string collection, string path, string token, bool infiniteDepth, string? ownerXml, TimeSpan timeout)
{
    public string Collection { get; } = collection;

    public string Path { get; } = path;

    public string Token { get; } = token;

    public bool InfiniteDepth { get; } = infiniteDepth;

    /// <summary>The client's &lt;D:owner&gt; element, verbatim, echoed in lockdiscovery.</summary>
    public string? OwnerXml { get; } = ownerXml;

    public TimeSpan Timeout { get; set; } = timeout;

    public DateTimeOffset Expires { get; set; }

    public string TimeoutHeader => Timeout >= LockManager.MaxTimeout ? "Infinite" : $"Second-{(long)Timeout.TotalSeconds}";
}

/// <summary>
/// Class 2 locking (spec 8.4, N7): exclusive write locks with opaque tokens, held in memory with a timeout, refreshed
/// through LOCK with the token in If, never persisted across restarts. Keys are collection + normalised path.
/// </summary>
internal sealed class LockManager(TimeProvider? time = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromDays(7);

    private readonly object _gate = new();
    private readonly Dictionary<string, DavLock> _locks = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                Purge();
                return _locks.Count;
            }
        }
    }

    /// <summary>The lock on exactly this path, if any.</summary>
    public DavLock? Find(string collection, string path)
    {
        lock (_gate)
        {
            Purge();
            return _locks.GetValueOrDefault(Key(collection, path));
        }
    }

    /// <summary>The lock that covers this path: one on the path itself, or an infinite-depth lock on an ancestor.</summary>
    public DavLock? Covering(string collection, string path)
    {
        lock (_gate)
        {
            Purge();
            return CoveringLocked(collection, path);
        }
    }

    /// <summary>True when the path isn't locked, or one of the client's tokens is the lock's.</summary>
    public bool IsAllowed(string collection, string path, IReadOnlyCollection<string> tokens)
        => Covering(collection, path) is not { } held || tokens.Contains(held.Token);

    /// <summary>
    /// Takes a new lock, or refreshes the existing one when the client presents its token. Null with
    /// <paramref name="conflict"/> set when someone else holds a lock on the path, an ancestor (infinite) or, for an
    /// infinite-depth request, a descendant.
    /// </summary>
    public DavLock? Lock(string collection, string path, bool infiniteDepth, string? ownerXml, TimeSpan timeout, IReadOnlyCollection<string> tokens, out bool conflict)
    {
        conflict = false;
        timeout = Clamp(timeout);
        lock (_gate)
        {
            Purge();
            var now = _time.GetUtcNow();
            if (CoveringLocked(collection, path) is { } held)
            {
                if (tokens.Contains(held.Token))
                {
                    held.Timeout = timeout;
                    held.Expires = now + timeout;
                    return held;
                }

                conflict = true;
                return null;
            }

            if (infiniteDepth && _locks.Values.Any(l => l.Collection == collection && VfsPath.IsUnder(l.Path, path)))
            {
                conflict = true;
                return null;
            }

            var created = new DavLock(collection, path, "opaquelocktoken:" + Guid.NewGuid().ToString("D"), infiniteDepth, ownerXml, timeout) { Expires = now + timeout };
            _locks[Key(collection, path)] = created;
            return created;
        }
    }

    /// <summary>Removes the lock when the token matches; false when there is no such lock or the token is wrong.</summary>
    public bool Unlock(string collection, string path, string token)
    {
        lock (_gate)
        {
            Purge();
            var held = _locks.GetValueOrDefault(Key(collection, path)) ?? CoveringLocked(collection, path);
            if (held is null || !string.Equals(held.Token, token, StringComparison.Ordinal))
            {
                return false;
            }

            _locks.Remove(Key(held.Collection, held.Path));
            return true;
        }
    }

    /// <summary>A resource that moved or vanished takes its locks (and its descendants') with it.</summary>
    public void Forget(string collection, string path)
    {
        lock (_gate)
        {
            foreach (var key in _locks.Where(kv => kv.Value.Collection == collection && (kv.Value.Path == path || VfsPath.IsUnder(kv.Value.Path, path))).Select(kv => kv.Key).ToList())
            {
                _locks.Remove(key);
            }
        }
    }

    public void ForgetCollection(string collection)
    {
        lock (_gate)
        {
            foreach (var key in _locks.Where(kv => kv.Value.Collection == collection).Select(kv => kv.Key).ToList())
            {
                _locks.Remove(key);
            }
        }
    }

    public static TimeSpan Clamp(TimeSpan timeout) => timeout <= TimeSpan.Zero ? DefaultTimeout : timeout > MaxTimeout ? MaxTimeout : timeout;

    /// <summary>Parses a Timeout header ("Second-600", "Infinite", a list of them); the default when absent or unparseable.</summary>
    public static TimeSpan ParseTimeout(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return DefaultTimeout;
        }

        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("Infinite", StringComparison.OrdinalIgnoreCase))
            {
                return MaxTimeout;
            }

            if (part.StartsWith("Second-", StringComparison.OrdinalIgnoreCase) && long.TryParse(part.AsSpan(7), out var seconds) && seconds > 0)
            {
                return Clamp(TimeSpan.FromSeconds(seconds));
            }
        }

        return DefaultTimeout;
    }

    private DavLock? CoveringLocked(string collection, string path)
    {
        if (_locks.TryGetValue(Key(collection, path), out var exact))
        {
            return exact;
        }

        var ancestor = path;
        while (ancestor.Length > 0)
        {
            ancestor = VfsPath.Parent(ancestor);
            if (_locks.TryGetValue(Key(collection, ancestor), out var held) && held.InfiniteDepth)
            {
                return held;
            }
        }

        return null;
    }

    private void Purge()
    {
        var now = _time.GetUtcNow();
        foreach (var key in _locks.Where(kv => kv.Value.Expires <= now).Select(kv => kv.Key).ToList())
        {
            _locks.Remove(key);
        }
    }

    private static string Key(string collection, string path) => collection + "\n" + path;
}
