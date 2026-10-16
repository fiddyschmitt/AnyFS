using AnyFS.Core.Model;
using AnyFS.Sdk;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace AnyFS.Core.Settings;

/// <summary>
/// Secrets at rest (spec 9.3): values protected with ASP.NET Core Data Protection (DPAPI-backed keys on
/// Windows, a per-user key ring in state/keys elsewhere), stored in state/secrets.json keyed
/// "&lt;owner id&gt;/&lt;property&gt;". Works headless and needs no native dependencies.
/// </summary>
public sealed class SecretStore
{
    private readonly string _file;
    private readonly IDataProtector _protector;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _protected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _cache = new(StringComparer.Ordinal);

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
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            if (!_protected.TryGetValue(key, out var blob))
            {
                return null;
            }

            try
            {
                var value = _protector.Unprotect(blob);
                _cache[key] = value;
                return value;
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
            {
                _logger.LogWarning("secret {Key} could not be decrypted (keys changed?); it will need to be entered again", key);
                return null;
            }
        }
    }

    public void Set(Guid ownerId, string property, string? value)
    {
        var key = Key(ownerId, property);
        lock (_gate)
        {
            if (string.IsNullOrEmpty(value))
            {
                if (!_protected.Remove(key))
                {
                    return;
                }

                _cache.Remove(key);
            }
            else
            {
                if (_cache.TryGetValue(key, out var existing) && existing == value)
                {
                    return;
                }

                _protected[key] = _protector.Protect(value);
                _cache[key] = value;
            }

            Save();
        }
    }

    public void RemoveAll(Guid ownerId)
    {
        var prefix = ownerId.ToString("N") + "/";
        lock (_gate)
        {
            var removed = false;
            foreach (var key in _protected.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                _protected.Remove(key);
                _cache.Remove(key);
                removed = true;
            }

            if (removed)
            {
                Save();
            }
        }
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

    private void Save()
    {
        var file = new SecretsFile();
        foreach (var (k, v) in _protected.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            file.Secrets[k] = v;
        }

        JsonFiles.WriteAtomic(_file, file, CoreJsonContext.Default.SecretsFile);
    }

    private sealed class Scoped(SecretStore store, Guid ownerId) : ISecretStore
    {
        public string? Get(string key) => store.Get(ownerId, key);

        public void Set(string key, string? value) => store.Set(ownerId, key, value);
    }
}
