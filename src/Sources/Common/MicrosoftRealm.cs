using System.Text.Json;

namespace AnyFS.Sources.Common;

/// <summary>
/// Which Microsoft cloud an organisation is in, from what the identity platform publishes for its domain (its OpenID
/// Connect discovery document; no sign-in needed): the global cloud (Microsoft 365, GCC "moderate" among it), or the US
/// Government cloud, GCC High (sub scope DODCON) or DoD (sub scope DOD), whose accounts sign in at
/// login.microsoftonline.us. Either cloud's sign-in host answers for any organisation's domain (checked against the
/// sandbox, 30-Oct-2026), so the second is asked where the first cannot be reached.
/// </summary>
/// <param name="Domain">The domain looked up: the account's, after its @.</param>
/// <param name="CloudInstance">Where its accounts sign in (cloud_instance_name): microsoftonline.com or microsoftonline.us.</param>
/// <param name="Region">tenant_region_scope: NA, EU, AS and so on, or USGov.</param>
/// <param name="SubScope">tenant_region_sub_scope: GCC, DODCON (GCC High) or DOD; null for none.</param>
/// <param name="GraphHost">msgraph_host: graph.microsoft.com, graph.microsoft.us or dod-graph.microsoft.us.</param>
public sealed record MicrosoftRealm(string Domain, string? CloudInstance, string? Region, string? SubScope, string? GraphHost)
{
    private static readonly string[] LoginHosts = ["login.microsoftonline.com", "login.microsoftonline.us"];

    /// <summary>In the US Government cloud: GCC High or DoD.</summary>
    public bool IsUsGovernment => CloudInstance?.EndsWith("microsoftonline.us", StringComparison.OrdinalIgnoreCase) == true || IsDod
        || string.Equals(SubScope, "DODCON", StringComparison.OrdinalIgnoreCase);

    /// <summary>In DoD (its sub scope, or DoD's own Graph).</summary>
    public bool IsDod => string.Equals(SubScope, "DOD", StringComparison.OrdinalIgnoreCase) || string.Equals(GraphHost, "dod-graph.microsoft.us", StringComparison.OrdinalIgnoreCase);

    /// <summary>What the lookup found, for the log: <c>contoso.com: USGov/DODCON, microsoftonline.us</c>.</summary>
    public override string ToString() => $"{Domain}: {Region ?? "no region"}{(SubScope is null ? "" : "/" + SubScope)}, {CloudInstance ?? "no cloud instance"}";

    /// <summary>The domain of an account's address (after its @), in lower case; null where there is none to look up.</summary>
    public static string? DomainOf(string? address)
    {
        var domain = address is null ? "" : address[(address.LastIndexOf('@') + 1)..].Trim().ToLowerInvariant();
        return domain.Length > 0 && domain.Contains('.', StringComparison.Ordinal) && Uri.CheckHostName(domain) == UriHostNameType.Dns ? domain : null;
    }

    /// <summary>
    /// What the identity platform publishes for the organisation <paramref name="address"/>'s domain belongs to; null when it
    /// knows no organisation by that domain (one not verified in any) or neither sign-in host could be read.
    /// </summary>
    /// <param name="handler">The handler the lookup goes through: a test's (left open); null for the network.</param>
    public static async Task<MicrosoftRealm?> LookupAsync(string address, HttpMessageHandler? handler, CancellationToken ct)
    {
        if (DomainOf(address) is not { } domain)
        {
            return null;
        }

        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(8);
        foreach (var host in LoginHosts)
        {
            try
            {
                using var response = await http.GetAsync($"https://{host}/{domain}/v2.0/.well-known/openid-configuration", ct).ConfigureAwait(false);
                if ((int)response.StatusCode >= 500)
                {
                    continue;
                }

                // 400 is no organisation by that domain (AADSTS90002), which the other host says as well
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                string? S(string name) => json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
                var realm = new MicrosoftRealm(domain, S("cloud_instance_name"), S("tenant_region_scope"), S("tenant_region_sub_scope"), S("msgraph_host"));
                return realm.CloudInstance is null && realm.Region is null ? null : realm;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                // out of reach, or something else answered (a captive portal's page): the other host
            }
        }

        return null;
    }
}
