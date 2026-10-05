using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Clarvio.Mail.Core;

namespace Clarvio.Mail.Services;

public static class WebhookTargets
{
    public static Uri Validate(string url, IReadOnlyCollection<string> hosts)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            !hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) || IPAddress.TryParse(uri.Host, out _))
            throw new ApiError(400, "invalid_webhook_url", "This webhook destination is not available.");
        return uri;
    }
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224 || b[0] == 169 && b[1] == 254 ||
                b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 ||
                b[0] == 100 && b[1] is >= 64 and <= 127 || b[0] == 198 && b[1] is 18 or 19);
        return address.AddressFamily == AddressFamily.InterNetworkV6 && !address.Equals(IPAddress.IPv6Any) &&
            !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !address.IsIPv6Multicast && (b[0] & 0xFE) != 0xFC;
    }
    public static async ValueTask<Stream> Connect(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        if (addresses.Length == 0 || addresses.Any(a => !IsPublic(a))) throw new HttpRequestException("Webhook DNS is not publicly routable.");
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(address, context.DnsEndPoint.Port, ct); return new NetworkStream(socket, ownsSocket: true); }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException) { socket.Dispose(); last = ex; }
        }
        throw new HttpRequestException("Webhook connection failed.", last);
    }
    public static string Sign(string secret, string timestamp, string payload) => Convert.ToHexString(
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(timestamp + "." + payload))).ToLowerInvariant();
}
