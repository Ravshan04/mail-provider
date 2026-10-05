using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Clarvio.Mail.Core;

public static class ProxyConfiguration
{
    public static void Configure(ForwardedHeadersOptions options, MailSettings settings)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var value in settings.TrustedProxies)
        {
            var address = IPAddress.Parse(value);
            options.KnownProxies.Add(address);
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                options.KnownProxies.Add(address.MapToIPv6());
        }
    }
}
