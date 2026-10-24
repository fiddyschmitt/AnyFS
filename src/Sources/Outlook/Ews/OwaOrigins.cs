using System.Text.Json;

namespace AnyFS.Sources.Outlook.Ews;

/// <summary>Where Outlook on the web answers for a mailbox: the cloud's origin, from the settings or from Microsoft's public realm lookup for the address.</summary>
internal static class OwaOrigins
{
    public static readonly Uri Global = new("https://outlook.office.com");

    public static readonly Uri UsGovernment = new("https://outlook.office365.us");

    /// <summary>The origin the settings name, or null for Automatic.</summary>
    public static Uri? FromChoice(string? cloud) => (cloud ?? "").Trim() switch
    {
        OutlookSettings.CloudGlobal => Global,
        OutlookSettings.CloudUsGovernment => UsGovernment,
        _ => null,
    };

    /// <summary>
    /// The origin for an address, from the realm lookup (GetUserRealm.srf, no sign-in needed): the US Government cloud's
    /// when the account's cloud instance is microsoftonline.us, the global cloud's otherwise, or when the lookup fails.
    /// </summary>
    public static async Task<Uri> DetectAsync(string address, HttpMessageHandler? handler, CancellationToken ct)
    {
        if (address.EndsWith(".onmicrosoft.us", StringComparison.OrdinalIgnoreCase))
        {
            return UsGovernment;
        }

        try
        {
            using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
            http.Timeout = TimeSpan.FromSeconds(15);
            using var response = await http.GetAsync($"https://login.microsoftonline.com/GetUserRealm.srf?login={Uri.EscapeDataString(address)}&json=1", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Global;
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var instance = json.RootElement.TryGetProperty("CloudInstanceName", out var c) ? c.GetString() : null;
            return instance is not null && instance.EndsWith("microsoftonline.us", StringComparison.OrdinalIgnoreCase) ? UsGovernment : Global;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Global;
        }
    }
}
