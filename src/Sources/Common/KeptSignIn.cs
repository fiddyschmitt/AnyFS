using System.Collections.Concurrent;
using System.Security.Cryptography;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Common;

/// <summary>
/// Where a session keeps a sign-in it has made, and where it looks for one before it asks the person. A source keeps
/// its own in its secret store, for its next start. A dialog's Test has no store: it leaves its sign-in for the source
/// the dialog starts next, in memory and for a quarter of an hour, so that source does not ask the person again.
/// <para>
/// Four sessions had a copy of this each, and three of them had the two mixed up (the review of 21-Nov-2026, C2 and
/// R9c): every sign-in and every renewal, a source's too, was left where the Test leaves its own, under the server's
/// name alone, and every start looked there first. Two sources of one server with different accounts crossed at each
/// start of the program: the second to start took the first's sign-in, ran as that account, and kept it as its own.
/// A Test took what a running source had left, and said a password worked that it had never tried.
/// </para>
/// Here a Test leaves and never takes: it tries the settings as they are typed. A source takes, once, and what it
/// takes is from then on its own. What is left is left under the product, the server and who the settings say signs
/// in, so it goes only to a source of the same settings.
/// </summary>
public sealed class KeptSignIn
{
    /// <summary>How long a Test's sign-in waits for the source the dialog starts.</summary>
    public static readonly TimeSpan HandedOverFor = TimeSpan.FromMinutes(15);

    private static readonly ConcurrentDictionary<string, (string SignIn, long At)> Left = new(StringComparer.Ordinal);

    private readonly ISecretStore? _secrets;
    private readonly string _secret;
    private readonly string _product;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    /// <param name="secrets">The source's secret store; null for a dialog's Test, which has none.</param>
    /// <param name="secret">The name the sign-in is kept under in the store.</param>
    /// <param name="product">The product, as the log names it and as part of what a handed-over sign-in is left under.</param>
    public KeptSignIn(ISecretStore? secrets, string secret, string product, ILogger logger, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(logger);
        _secrets = secrets;
        _secret = secret;
        _product = product;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Whether this is a dialog's Test, which keeps nothing of its own.</summary>
    public bool IsTest => _secrets is null;

    /// <summary>
    /// The sign-ins to try before the person is asked, the nearest first: what a Test just left for these settings
    /// (taken: no other source finds it), then what the source kept at its last run. None for a Test.
    /// </summary>
    /// <param name="who">Who the settings say signs in: the account they name, or the kind of sign-in where they name none.</param>
    public IReadOnlyList<string> Find(Uri server, string who)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (_secrets is null)
        {
            return [];
        }

        var found = new List<string>(2);
        if (Left.TryRemove(Key(server, who), out var left) && _time.GetElapsedTime(left.At) < HandedOverFor)
        {
            found.Add(left.SignIn);
        }

        if (_secrets.Get(_secret) is { Length: > 0 } kept && !found.Contains(kept, StringComparer.Ordinal))
        {
            found.Add(kept);
        }

        return found;
    }

    /// <summary>Keeps a sign-in: a source's in its store for its next start, a Test's for the source the dialog starts.</summary>
    public void Keep(Uri server, string who, string signIn)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(signIn);
        if (_secrets is null)
        {
            Left[Key(server, who)] = (signIn, _time.GetTimestamp());
            return;
        }

        try
        {
            _secrets.Set(_secret, signIn);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _logger.LogWarning("{Product}: the sign-in could not be kept for the next start: {Error}", _product, ex.Message);
        }
    }

    private string Key(Uri server, string who) => $"{_product}|{server.AbsoluteUri}|{who}";
}
