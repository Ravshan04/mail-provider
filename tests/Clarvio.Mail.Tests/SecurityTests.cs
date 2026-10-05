using System.Net;
using System.Security.Cryptography;
using Clarvio.Mail.Core;
using Clarvio.Mail.Providers;
using Clarvio.Mail.Services;

namespace Clarvio.Mail.Tests;

public sealed class SecurityTests
{
    internal static MailSettings Settings() => new()
    {
        Database = "test", AllowSignup = true,
        EncryptionKeys = new() { ["v1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }
    };

    [Fact]
    public void Encrypted_tokens_are_bound_to_the_mailbox_and_detect_tampering()
    {
        var settings = Settings(); var cipher = new TokenCipher(settings);
        var first = cipher.Encrypt("a provider credential", "refresh:account-one");
        var second = cipher.Encrypt("a provider credential", "refresh:account-one");
        Assert.NotEqual(first, second);
        Assert.Equal("a provider credential", cipher.Decrypt(first, "refresh:account-one"));
        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt(first, "refresh:account-two"));
        var parts = first.Split('.'); var bytes = Secrets.DecodeBase64Url(parts[1]); bytes[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt(parts[0] + "." + Secrets.Base64Url(bytes), "refresh:account-one"));
        settings.EncryptionKeys["v2"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        settings.ActiveKey = "v2";
        Assert.Equal("a provider credential", cipher.Decrypt(first, "refresh:account-one"));
        Assert.StartsWith("v2.", cipher.Encrypt("new credential", "refresh:account-one"));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.2.3.4")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.2")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    public void Webhook_connections_block_private_destinations(string address) => Assert.False(WebhookTargets.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("http://hooks.example/event")]
    [InlineData("https://hooks.example:8443/event")]
    [InlineData("https://hooks.example.attacker.test/event")]
    [InlineData("https://user@hooks.example/event")]
    [InlineData("https://hooks.example/event#fragment")]
    public void Webhook_hosts_require_an_exact_approved_https_origin(string url) => Assert.Throws<ApiError>(() => WebhookTargets.Validate(url, ["hooks.example"]));

    [Theory]
    [InlineData("https://graph.microsoft.com.attacker.test/v1.0/me/messages")]
    [InlineData("http://graph.microsoft.com/v1.0/me/messages")]
    [InlineData("https://graph.microsoft.com/v1.0/users/someone/messages")]
    public void Graph_pagination_cannot_redirect_credentials(string cursor) => Assert.Throws<ApiError>(() => MicrosoftProvider.ValidateCursor(cursor));

    [Fact]
    public void Mime_supports_cc_bcc_attachments_and_reply_headers()
    {
        var request = new SendMailRequest([new("to@example.com")], "Subject", "Hello", [new("cc@example.com")], [new("bcc@example.com")],
            Attachments: [new("note.txt", "text/plain", Convert.ToBase64String("attachment"u8.ToArray()))]);
        MessageValidation.Validate(request);
        var reply = new MailMessage("message", "thread", "Original subject", new("from@example.com"), [], [], null, "", null, null, true, [], "<original@example.com>", "<earlier@example.com>");
        using var message = MessageValidation.Mime("sender@example.com", request, reply);
        Assert.Equal("sender@example.com", message.From.Mailboxes.Single().Address);
        Assert.Equal("cc@example.com", message.Cc.Mailboxes.Single().Address);
        Assert.Equal("bcc@example.com", message.Bcc.Mailboxes.Single().Address);
        Assert.Equal("original@example.com", message.InReplyTo);
        Assert.Contains("earlier@example.com", message.References);
        Assert.Equal("Original subject", message.Subject);
        Assert.Single(message.Attachments);
    }

    [Fact]
    public void Message_validation_rejects_header_injection_and_null_json_values()
    {
        var request = new SendMailRequest([new("to@example.com")], "Hello\r\nBcc: attacker@example.com", "hello");
        Assert.Throws<ApiError>(() => MessageValidation.Validate(request));
        Assert.Throws<ApiError>(() => MessageValidation.Validate(new(null!, "Hello", "hello")));
        Assert.Throws<ApiError>(() => MessageValidation.Validate(new([null!], "Hello", "hello")));
        Assert.Throws<ApiError>(() => MessageValidation.Validate(new([new("to@example.com")], "Hello", null!)));
    }

    [Fact]
    public async Task Failed_send_requests_are_never_automatically_retried()
    {
        var handler = new CountingHandler();
        var provider = new ProviderHttp(new Clients(handler));
        var error = await Assert.ThrowsAsync<ProviderError>(() => provider.Json("https://graph.microsoft.com/v1.0/me/sendMail", "token", HttpMethod.Post, new { message = "body" }, default));
        Assert.True(error.Uncertain); Assert.Equal(1, handler.Count);
    }
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Count++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); }
    }
    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, disposeHandler: false); }
}
