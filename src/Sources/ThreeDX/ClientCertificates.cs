using System.Security.Cryptography.X509Certificates;

namespace AnyFS.Sources.ThreeDX;

/// <summary>
/// The certificates the single sign-on presents when a server asks for one (spec 7.6.2), chosen as Mount3DX chooses them:
/// from the person's own store, those for both smartcard logon and client authentication (a badge's), and of those, the
/// ones a CA named "... Hardware Issuing CA" issued when there are any (Mount3DX's filter, which tells the badge from a
/// Windows Hello or virtual smartcard certificate beside it); else any smartcard certificate; else any meant for client
/// authentication. Only those with a private key and valid now. Windows asks for the card's PIN itself when a server takes one.
/// </summary>
internal static class ClientCertificates
{
    private const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";
    private const string SmartcardLogon = "1.3.6.1.4.1.311.20.2.2";

    /// <summary>The issuer Mount3DX picks its badge certificate by.</summary>
    private const string BadgeIssuer = "Hardware Issuing CA";

    public static X509Certificate2Collection Load()
    {
        var chosen = new X509Certificate2Collection();
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            chosen.AddRange(Choose(store.Certificates.Cast<X509Certificate2>(), DateTime.Now).ToArray());
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // no store for the person (a service account), or one that cannot be opened: no certificate to give
        }

        return chosen;
    }

    /// <summary>Mount3DX's choice among <paramref name="certificates"/> (see the class).</summary>
    internal static IReadOnlyList<X509Certificate2> Choose(IEnumerable<X509Certificate2> certificates, DateTime now)
    {
        var usable = certificates
            .Where(c => c.NotBefore <= now && now <= c.NotAfter && Usages(c).Contains(ClientAuthentication) && c.HasPrivateKey)
            .ToList();
        var smartcards = usable.Where(c => Usages(c).Contains(SmartcardLogon)).ToList();
        var badges = smartcards.Where(c => c.IssuerName.Name.Contains(BadgeIssuer, StringComparison.Ordinal)).ToList();
        return badges.Count > 0 ? badges : smartcards.Count > 0 ? smartcards : usable;
    }

    private static IEnumerable<string?> Usages(X509Certificate2 certificate)
        => certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SelectMany(e => e.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Select(o => o.Value));
}
