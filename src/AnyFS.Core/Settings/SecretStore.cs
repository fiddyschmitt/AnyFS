using AnyFS.Core.Model;
using AnyFS.Sdk;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace AnyFS.Core.Settings;

/// <summary>
/// Secrets at rest (spec 9.3): values protected with ASP.NET Core Data Protection (DPAPI-backed keys on
/// Windows, a per-user key ring in state/keys elsewhere), stored in state/secrets.json keyed
/// "&lt;owner id&gt;/&lt;property&gt;". Works headless and needs no native dependencies.
///
/// Its lock covers the dictionaries alone: protecting and unprotecting (Windows' data protection, which can take
/// seconds on a machine of a domain) and writing the file happen outside it, the writes in order of the changes, so
/// a source keeping its session or token cache never holds up a read (the window's, a source starting) for that long.
/// An operation that takes a second or more is logged.
/// </summary>
public sealed class SecretStore
{
    private static readonly TimeSpan SlowWorthLogging = TimeSpan.FromSeconds(1);

    private readonly string _file;
    private readonly IDataProtector _protector;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly Dictionary<string, string> _protected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _cache = new(StringComparer.Ordinal);

    // every change bumps the version (under _gate); the file is written with the newest one only (under _saveGate)
    private long _version;
    private long _written;

    public SecretStore(SettingsPaths paths, ILogger logger)
        : this(paths.SecretsFile, paths.KeysDirectory, logger)
    {
    }

    public SecretStore(string secretsFile, string keysDirectory, ILogger logger)
    {
        _file = secretsFile;
        _logger = logger;
        Directory.CreateDirectory(keysDirectory);
        var provider = DataProtectionProvider.Create(new DirectoryInfo(keysDirectory), builder =>
        {
            builder.SetApplicationName("AnyFS");
            if (OperatingSystem.IsWindows())
            {
                builder.ProtectKeysWithDpapi();
            }
        });
        _protector = provider.CreateProtector("AnyFS.Secrets.v1");
        Load();
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _protected.Count;
            }
        }
    }

    public string? Get(Guid ownerId, string property)
    {
        var key = Key(ownerId, property);
        string? blob;
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            if (!_protected.TryGetValue(key, out blob))
            {
                return null;
            }
        }

        string value;
        try
        {
            value = Timed("decrypt a secret", () => _protector.Unprotect(blob));
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            _logger.LogWarning("secret {Key} could not be decrypted (keys changed?); it will need to be entered again", key);
            return null;
        }

        lock (_gate)
        {
            // kept only if the secret is still the one decrypted (a Set meanwhile has its own value cached)
            if (_protected.TryGetValue(key, out var now) && now == blob)
            {
                _cache[key] = value;
            }
        }

        return value;
    }

    public void Set(Guid ownerId, string property, string? value)
    {
        var key = Key(ownerId, property);
        if (string.IsNullOrEmpty(value))
        {
            Change(() =>
            {
                if (!_protected.Remove(key))
                {
                    return false;
                }

                _cache.Remove(key);
                return true;
            });
            return;
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var existing) && existing == value)
            {
                return;
            }
        }

        var blob = Timed("encrypt a secret", () => _protector.Protect(value));
        Change(() =>
        {
            _protected[key] = blob;
            _cache[key] = value;
            return true;
        });
    }

    public void RemoveAll(Guid ownerId)
    {
        var prefix = ownerId.ToString("N") + "/";
        Change(() =>
        {
            var removed = false;
            foreach (var key in _protected.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                _protected.Remove(key);
                _cache.Remove(key);
                removed = true;
            }

            return removed;
        });
    }

    /// <summary>Applies a change to the dictionaries under the lock (<paramref name="change"/> says whether there was one), then writes the file outside it.</summary>
    private void Change(Func<bool> change)
    {
        SecretsFile snapshot;
        long version;
        lock (_gate)
        {
            if (!change())
            {
                return;
            }

            version = ++_version;
            snapshot = new SecretsFile();
            foreach (var (k, v) in _protected.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                snapshot.Secrets[k] = v;
            }
        }

        lock (_saveGate)
        {
            // a later change was written already, this one in it
            if (version <= _written)
            {
                return;
            }

            Timed("write secrets.json", () =>
            {
                JsonFiles.WriteAtomic(_file, snapshot, CoreJsonContext.Default.SecretsFile);
                return true;
            });
            _written = version;
        }
    }

    private T Timed<T>(string what, Func<T> action)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = action();
        var took = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        if (took >= SlowWorthLogging)
        {
            _logger.LogInformation("secrets: it took {Milliseconds:F0} ms to {What} (Windows' data protection, or another program holding the file)", took.TotalMilliseconds, what);
        }

        return result;
    }

    /// <summary>The store scoped to one source or mount, as handed to plugins.</summary>
    public ISecretStore For(Guid ownerId) => new Scoped(this, ownerId);

    private static string Key(Guid ownerId, string property) => ownerId.ToString("N") + "/" + property;

    private void Load()
    {
        try
        {
            var file = JsonFiles.Read(_file, CoreJsonContext.Default.SecretsFile);
            if (file is null)
            {
                return;
            }

            foreach (var (k, v) in file.Secrets)
            {
                _protected[k] = v;
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            var moved = JsonFiles.Quarantine(_file);
            _logger.LogError(ex, "secrets.json could not be read; moved to {Path}", moved);
        }
    }

    private sealed class Scoped(SecretStore store, Guid ownerId) : ISecretStore
    {
        public string? Get(string key) => store.Get(ownerId, key);

        public void Set(string key, string? value) => store.Set(ownerId, key, value);
    }
}
