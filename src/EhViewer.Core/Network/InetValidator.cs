using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace EhViewer.Core.Network;

/// <summary>
/// Validates IPv4 addresses, hostnames, and TCP ports matching upstream <c>InetValidator</c>.
/// </summary>
public static partial class InetValidator
{
    private const string HostRegexPattern =
        "^([a-zA-Z0-9]([a-zA-Z0-9\\-]{0,61}[a-zA-Z0-9])?\\.)*[a-zA-Z0-9]([a-zA-Z0-9\\-]{0,61}[a-zA-Z0-9])?$";

    [GeneratedRegex(HostRegexPattern)]
    private static partial Regex HostPattern();

    public static bool IsValidInetAddress(string? inetAddress)
    {
        if (string.IsNullOrEmpty(inetAddress))
        {
            return false;
        }

        return IsValidIpv4(inetAddress) || HostPattern().IsMatch(inetAddress);
    }

    public static bool IsValidIpv4(string? ip)
    {
        if (string.IsNullOrEmpty(ip))
        {
            return false;
        }

        return IPAddress.TryParse(ip, out IPAddress? address) &&
               address.AddressFamily == AddressFamily.InterNetwork &&
               ip.Count(c => c == '.') == 3;
    }

    public static bool IsValidInetPort(int port)
    {
        return port is >= 0 and <= 65535;
    }
}
