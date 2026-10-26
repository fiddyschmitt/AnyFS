using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Outlook.Ews;

/// <summary>
/// Autodiscover as Outlook asks it of an Exchange server (spec 7.7, address books; Spike 16): the plain-old-XML POST to
/// <c>/autodiscover/autodiscover.xml</c> with <c>X-MapiHttpCapability</c>, which answers the <c>mapiHttp</c> protocol's
/// endpoints. From it comes the address book URL (<c>/mapi/nspi/?MailboxId=…</c>) the <see cref="NspiAddressBook"/>
/// needs; the server reports it under its own name, so the host is set back to the one the mailbox is reached at.
/// </summary>
internal static class ExchangeAutodiscover
{
    private static readonly XNamespace Request = "http://schemas.microsoft.com/exchange/autodiscover/outlook/requestschema/2006";

    /// <summary>
    /// The mapiHttp address book URL, tried for each address in turn until one answers (the signed-in mailbox's own SMTP
    /// address is what Autodiscover wants); null when none is found or the server offers no mapiHttp address book.
    /// </summary>
    public static async Task<Uri?> AddressBookUrlAsync(HttpClient http, Uri server, IReadOnlyList<string> addresses, ILogger logger, CancellationToken ct)
    {
        var endpoint = new Uri(server, "/autodiscover/autodiscover.xml");
        foreach (var address in addresses.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Uri? found;
            try
            {
                found = await AskAsync(http, endpoint, address, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Xml.XmlException || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogInformation("outlook: Autodiscover of {Host} for {Address} did not answer ({Error}); the address book is left out unless another address answers", server.Host, address, ex.Message);
                continue;
            }

            if (found is not null)
            {
                // Autodiscover names the host by a name only the server's own DNS may know; reach it where the mailbox is reached
                return new UriBuilder(found) { Scheme = server.Scheme, Host = server.Host, Port = server.Port }.Uri;
            }
        }

        return null;
    }

    private static async Task<Uri?> AskAsync(HttpClient http, Uri endpoint, string address, CancellationToken ct)
    {
        var pox = new XDocument(new XElement(Request + "Autodiscover",
            new XElement(Request + "Request",
                new XElement(Request + "EMailAddress", address),
                new XElement(Request + "AcceptableResponseSchema", "http://schemas.microsoft.com/exchange/autodiscover/outlook/responseschema/2006a")))).ToString(SaveOptions.DisableFormatting);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(pox, Encoding.UTF8, "text/xml") };
        request.Headers.Add("X-MapiHttpCapability", "1");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var document = XDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        foreach (var protocol in document.Descendants().Where(e => e.Name.LocalName == "Protocol"))
        {
            var type = protocol.Elements().FirstOrDefault(e => e.Name.LocalName == "Type")?.Value ?? (string?)protocol.Attribute("Type");
            if (!string.Equals(type, "mapiHttp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var book = protocol.Elements().FirstOrDefault(e => e.Name.LocalName == "AddressBook");
            var url = book?.Elements().FirstOrDefault(e => e.Name.LocalName is "InternalUrl")?.Value
                ?? book?.Elements().FirstOrDefault(e => e.Name.LocalName is "ExternalUrl")?.Value;
            if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }
}
