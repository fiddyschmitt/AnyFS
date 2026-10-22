using System.Security.Cryptography.X509Certificates;

namespace AnyFS.Sources.ThreeDX;

/// <summary>
/// The certificates a smartcard (a badge) signs the person in with (spec 7.6.2), for the one step of a single sign-on that
/// asks for one, as Mount3DX's does: those in the person's own store meant for signing in to a server (client
/// authentication), with a private key, valid now; a smartcard's (smartcard logon) when there are any, since that is what a
/// badge is. Windows asks for the card's PIN itself when the server takes one. Only that step's connections carry them, so
/// no other server in a sign-in can set off a PIN prompt.
/// </summary>
internal static class ClientCertificates
{
    private const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";
    private const string SmartcardLogon = "1.3.6.1.4.1.311.20.2.2";

    public static X509Certificate2Collection Load()
    {
        var chosen = new X509Certificate2Collection();
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var now = DateTime.Now;
            var usable = store.Certificates
                .Where(c => c.NotBefore <= now && now <= c.NotAfter && Usages(c).Contains(ClientAuthentication) && c.HasPrivateKey)
                .ToList();
            var smartcards = usable.Where(c => Usages(c).Contains(SmartcardLogon)).ToList();
            chosen.AddRange((smartcards.Count > 0 ? smartcards : usable).ToArray());
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // no store for the person (a service account), or one that cannot be opened: no certificate to give
        }

        return chosen;
    }

    private static IEnumerable<string?> Usages(X509Certificate2 certificate)
        => certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SelectMany(e => e.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Select(o => o.Value));
}
