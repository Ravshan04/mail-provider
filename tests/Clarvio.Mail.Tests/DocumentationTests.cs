using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Clarvio.Mail.Api;
using Clarvio.Mail.Core;
using Clarvio.Mail.Data;
using Clarvio.Mail.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Clarvio.Mail.Tests;

public sealed class DocumentationTests(TestDatabase fixture) : IClassFixture<TestDatabase>
{
    private Factory App(string environment = "Development", bool enabled = false) => new(fixture, environment, enabled);

    [Fact]
    public async Task Openapi_documents_every_endpoint_with_models_authentication_and_send_headers()
    {
        using var app = App(); using var client = app.CreateClient();
        // Generate directly as well, so schema failures retain their diagnostic exception.
        await app.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1").GetOpenApiDocumentAsync();
        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var root = document.RootElement;
        Assert.Equal("Clarvio Mail API", root.GetProperty("info").GetProperty("title").GetString());
        var paths = root.GetProperty("paths");
        var ids = new HashSet<string>();
        foreach (var path in paths.EnumerateObject())
            foreach (var method in path.Value.EnumerateObject())
            {
                var operation = method.Value;
                Assert.True(ids.Add(operation.GetProperty("operationId").GetString()!));
                Assert.False(string.IsNullOrWhiteSpace(operation.GetProperty("summary").GetString()));
                Assert.True(operation.GetProperty("tags").GetArrayLength() > 0);
            }
        Assert.Equal(32, ids.Count);
        var send = paths.GetProperty("/api/v1/mailboxes/{mailboxId}/messages").GetProperty("post");
        Assert.Contains(send.GetProperty("parameters").EnumerateArray(), x => x.GetProperty("name").GetString() == "Idempotency-Key" && x.GetProperty("required").GetBoolean());
        Assert.Contains(send.GetProperty("security").EnumerateArray(), x => x.TryGetProperty("ApiKey", out _));
        Assert.Equal("recipient@example.com", send.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("example").GetProperty("to")[0].GetProperty("email").GetString());
        Assert.True(send.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").TryGetProperty("schema", out _));
        Assert.True(send.GetProperty("responses").TryGetProperty("401", out _));
        var keys = paths.GetProperty("/api/v1/keys").GetProperty("post");
        Assert.Single(keys.GetProperty("security").EnumerateArray());
        Assert.True(keys.GetProperty("security")[0].TryGetProperty("BrowserSession", out _));
        Assert.True(paths.GetProperty("/api/v1/logout").GetProperty("post").GetProperty("responses").TryGetProperty("204", out _));
        Assert.True(paths.GetProperty("/auth/{provider}").GetProperty("get").GetProperty("responses").TryGetProperty("302", out _));
        Assert.True(paths.GetProperty("/health/ready").GetProperty("get").GetProperty("responses").TryGetProperty("503", out _));
        var schemas = root.GetProperty("components").GetProperty("schemas").GetRawText();
        Assert.DoesNotContain("tokenHash", schemas); Assert.DoesNotContain("accessToken", schemas); Assert.DoesNotContain("refreshToken", schemas);
    }

    [Theory]
    [InlineData("Development", false, true)]
    [InlineData("Production", false, false)]
    [InlineData("Production", true, true)]
    public async Task Swagger_and_assets_follow_the_environment_setting(string environment, bool enabled, bool visible)
    {
        using var app = App(environment, enabled); using var client = app.CreateClient();
        foreach (var path in new[] { "/swagger/index.html", "/swagger/clarvio-init.js", "/swagger/swagger-ui-bundle.js", "/openapi/v1.json" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(visible ? HttpStatusCode.OK : HttpStatusCode.NotFound, response.StatusCode);
            if (visible && path == "/swagger/index.html")
            {
                var html = await response.Content.ReadAsStringAsync();
                Assert.Contains("./clarvio-init.js", html);
                Assert.DoesNotContain("unsafe-inline", response.Headers.GetValues("Content-Security-Policy").Single().Split("script-src")[1].Split(';')[0]);
            }
        }
    }

    [Fact]
    public async Task Documented_api_key_authentication_works_but_does_not_grant_session_management()
    {
        var token = "cm_sk_" + Secrets.Random();
        await using (var db = fixture.Open())
        {
            var user = new User { Email = "swagger-test@example.com" };
            db.Users.Add(user); db.ApiKeys.Add(new() { UserId = user.Id, Name = "Docs test", TokenHash = Secrets.Hash(token), Scopes = ["accounts:read"], ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
            await db.SaveChangesAsync();
        }
        using var app = App(); using var client = app.CreateClient();
        using var anonymous = await client.GetAsync("/api/v1/mailboxes"); Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var mailboxes = await client.GetAsync("/api/v1/mailboxes"); Assert.Equal(HttpStatusCode.OK, mailboxes.StatusCode);
        using var keys = await client.GetAsync("/api/v1/keys"); Assert.Equal(HttpStatusCode.Forbidden, keys.StatusCode);
    }

    private sealed class Factory(TestDatabase fixture, string environment, bool enabled) : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            // Supply startup values before Program validates configuration, using the deferred host's arguments.
            builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mail:PublicUrl"] = "https://mail.example.com", ["Mail:Database"] = fixture.Settings.Database,
                ["Mail:EncryptionKeys:v1"] = fixture.Settings.EncryptionKeys["v1"], ["Mail:ActiveKey"] = "v1",
                ["Mail:EnableSwagger"] = enabled.ToString(), ["Mail:Google:ClientId"] = "", ["Mail:Microsoft:ClientId"] = ""
            }));
            return base.CreateHost(builder);
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.ConfigureServices(services =>
            {
                foreach (var service in services.Where(x => x.ImplementationType == typeof(MailWorker)).ToArray()) services.Remove(service);
            });
        }
    }
}
