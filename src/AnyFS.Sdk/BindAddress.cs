using System.Net;
using System.Net.Sockets;

namespace AnyFS.Sdk;

/// <summary>Bind-address settings shared by the network mount providers: parsing the user's text and naming the host in access paths.</summary>
public static class BindAddress
{
    /// <summary>"127.0.0.1", "0.0.0.0", "::", an IP, "localhost" (loopback), "*" / "any" / "all" (every interface).</summary>
    public static bool TryParse(string? text, out IPAddress address)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case null or "" or "localhost" or "loopback":
                address = IPAddress.Loopback;
                return true;
            case "*" or "any" or "all":
                address = IPAddress.Any;
                return true;
            default:
                return IPAddress.TryParse(text.Trim(), out address!);
        }
    }

    /// <summary>The host name to show in access paths for a bind address: localhost, this machine's name for "any", else the address.</summary>
    public static string HostFor(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return "localhost";
        }

        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return Environment.MachineName.ToLowerInvariant();
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
    }

    /// <summary>True when the address serves other machines (anything but loopback).</summary>
    public static bool IsLanExposed(IPAddress address) => !IPAddress.IsLoopback(address);

    /// <summary>The usual bind-address and port checks, as validation messages for a settings form.</summary>
    public static IEnumerable<ValidationMessage> Validate(string? bindAddress, int port, string bindProperty, string portProperty)
    {
        if (!TryParse(bindAddress, out _))
        {
            yield return new ValidationMessage("Use an IP address such as 127.0.0.1, 0.0.0.0 or ::, or 'localhost'.", bindProperty);
        }

        if (port is < 1 or > 65535)
        {
            yield return new ValidationMessage("The port must be between 1 and 65535.", portProperty);
        }
        else if (port < 1024)
        {
            yield return new ValidationMessage("Ports under 1024 need administrator rights on every OS.", portProperty, IsError: false);
        }
    }
}
