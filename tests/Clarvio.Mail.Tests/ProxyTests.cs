using System.Net;
using Clarvio.Mail.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Clarvio.Mail.Tests;

public sealed class ProxyTests
{
    [Theory]
    [InlineData("172.30.87.2", true)]
    [InlineData("::ffff:172.30.87.2", true)]
    [InlineData("172.30.87.3", false)]
    [InlineData("127.0.0.1", false)]
    public async Task Only_explicitly_trusted_proxies_can_change_the_client_ip_and_scheme(string remote, bool trusted)
    {
        var options = new ForwardedHeadersOptions();
        ProxyConfiguration.Configure(options, new MailSettings { TrustedProxies = ["172.30.87.2"] });
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("mail.example.com");
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.10";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-Host"] = "attacker.example";
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(options));
        await middleware.Invoke(context);
        Assert.Equal(trusted ? "https" : "http", context.Request.Scheme);
        Assert.Equal(trusted ? "198.51.100.10" : remote, context.Connection.RemoteIpAddress!.ToString());
        Assert.Equal("mail.example.com", context.Request.Host.Value);
    }

    [Fact]
    public void Proxy_addresses_are_validated_before_startup()
    {
        var settings = SecurityTests.Settings();
        settings.TrustedProxies = ["0.0.0.0/0"];
        Assert.Throws<InvalidOperationException>(() => settings.Validate(false));
    }
}
