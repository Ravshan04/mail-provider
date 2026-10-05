using System.Text.Json;
using Clarvio.Mail.Auth;
using Clarvio.Mail.Core;
using Clarvio.Mail.Data;
using Clarvio.Mail.Providers;
using Clarvio.Mail.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Npgsql;

namespace Clarvio.Mail.Tests;

public sealed class TestDatabase : IAsyncLifetime
{
    private readonly string name = "clarvio_mail_test_" + Guid.NewGuid().ToString("N");
    private string admin = "";
    private string connection = "";
    public readonly MailSettings Settings = SecurityTests.Settings();
    public MailDb Open() => new(new DbContextOptionsBuilder<MailDb>().UseNpgsql(connection).Options);
    public async Task InitializeAsync()
    {
        var source = Environment.GetEnvironmentVariable("CLARVIO_MAIL_TEST_ADMIN_DATABASE");
        if (string.IsNullOrWhiteSpace(source))
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            using var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, ".local/settings.json")));
            source = config.RootElement.GetProperty("Mail").GetProperty("Database").GetString()!;
        }
        var cs = new NpgsqlConnectionStringBuilder(source) { Database = "postgres", Pooling = false };
        admin = cs.ConnectionString;
        await using var conn = new NpgsqlConnection(admin); await conn.OpenAsync();
        await using var create = new NpgsqlCommand("CREATE DATABASE \"" + name + "\"", conn); await create.ExecuteNonQueryAsync();
        cs.Database = name; connection = cs.ConnectionString; Settings.Database = connection;
        await using var db = Open(); await db.Database.MigrateAsync();
    }
    public async Task DisposeAsync()
    {
        // Only the uniquely named test database can be removed.
        if (!name.StartsWith("clarvio_mail_test_", StringComparison.Ordinal) || string.IsNullOrEmpty(admin)) return;
        await using var conn = new NpgsqlConnection(admin); await conn.OpenAsync();
        await using var drop = new NpgsqlCommand("DROP DATABASE IF EXISTS \"" + name + "\" WITH (FORCE)", conn); await drop.ExecuteNonQueryAsync();
    }
}

public sealed class DatabaseTests(TestDatabase fixture) : IClassFixture<TestDatabase>
{
    private readonly TokenCipher cipher = new(fixture.Settings);
    private static readonly SendMailRequest Message = new([new("recipient@example.com")], "Subject", "Body");
    private async Task<Mailbox> Seed(MailDb db, bool expired = false)
    {
        var user = new User { Email = "test@example.com" };
        var mailbox = new Mailbox { UserId = user.Id, Email = user.Email, Provider = "outlook", Subject = Guid.NewGuid().ToString(), TokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 10) };
        mailbox.AccessToken = cipher.Encrypt("access", "access:" + mailbox.Id);
        mailbox.RefreshToken = cipher.Encrypt("refresh", "refresh:" + mailbox.Id);
        db.Users.Add(user); db.Mailboxes.Add(mailbox); await db.SaveChangesAsync(); return mailbox;
    }
    private MailService Service(MailDb db, FakeProvider provider) => new(db, cipher, new([provider]));

    [Fact]
    public async Task Ownership_is_enforced_before_provider_access()
    {
        await using var db = fixture.Open(); var mailbox = await Seed(db); var provider = new FakeProvider();
        var error = await Assert.ThrowsAsync<ApiError>(() => Service(db, provider).Get(Guid.NewGuid(), mailbox.Id, "message", default));
        Assert.Equal(404, error.Status); Assert.Equal(0, provider.Reads);
    }

    [Fact]
    public async Task Idempotency_survives_new_requests_and_rejects_changed_content()
    {
        var provider = new FakeProvider(); Guid user, mailboxId;
        await using (var db = fixture.Open())
        {
            var mailbox = await Seed(db); user = mailbox.UserId; mailboxId = mailbox.Id;
            var first = await Service(db, provider).Send(user, mailboxId, Message, "stable-key-123456789", null, "test", default);
            Assert.Equal("accepted", first.Status);
        }
        await using (var db = fixture.Open())
        {
            var again = await Service(db, provider).Send(user, mailboxId, Message, "stable-key-123456789", null, "test", default);
            Assert.Equal("accepted", again.Status); Assert.Equal(1, provider.Sends);
            var error = await Assert.ThrowsAsync<ApiError>(() => Service(db, provider).Send(user, mailboxId, Message with { TextBody = "different" }, "stable-key-123456789", null, "test", default));
            Assert.Equal("idempotency_conflict", error.Code); Assert.Equal(1, provider.Sends);
        }
    }

    [Fact]
    public async Task Concurrent_sends_with_one_key_call_the_provider_once()
    {
        var provider = new FakeProvider { SendDelay = 100 }; Guid user, mailboxId;
        await using (var db = fixture.Open()) { var mailbox = await Seed(db); user = mailbox.UserId; mailboxId = mailbox.Id; }
        async Task<SendOperation> Send()
        { await using var db = fixture.Open(); return await Service(db, provider).Send(user, mailboxId, Message, "concurrent-key-123456", null, "test", default); }
        var results = await Task.WhenAll(Send(), Send());
        Assert.Equal(results[0].Id, results[1].Id); Assert.Equal(1, provider.Sends);
    }

    [Fact]
    public async Task Uncertain_send_is_recorded_and_never_repeated()
    {
        var provider = new FakeProvider { Uncertain = true }; Guid user, mailboxId;
        await using (var db = fixture.Open())
        {
            var mailbox = await Seed(db); user = mailbox.UserId; mailboxId = mailbox.Id;
            var result = await Service(db, provider).Send(user, mailboxId, Message, "uncertain-key-123456", null, "test", default);
            Assert.Equal("unknown", result.Status);
        }
        await using (var db = fixture.Open())
        {
            var result = await Service(db, provider).Send(user, mailboxId, Message, "uncertain-key-123456", null, "test", default);
            Assert.Equal("unknown", result.Status); Assert.Equal(1, provider.Sends);
        }
    }

    [Theory]
    [InlineData(false, "accepted")]
    [InlineData(true, "unknown")]
    public async Task Draft_send_replays_the_operation_without_refetching_or_resending_a_consumed_draft(bool uncertain, string status)
    {
        var provider = new FakeProvider { Uncertain = uncertain }; Guid user, mailboxId;
        await using (var db = fixture.Open())
        {
            var mailbox = await Seed(db); user = mailbox.UserId; mailboxId = mailbox.Id;
            var result = await Service(db, provider).SendDraft(user, mailboxId, "draft-one", "draft-stable-key-12345", "test", default);
            Assert.Equal(status, result.Status);
        }
        provider.DraftGone = true;
        await using (var db = fixture.Open())
        {
            var result = await Service(db, provider).SendDraft(user, mailboxId, "draft-one", "draft-stable-key-12345", "test", default);
            Assert.Equal(status, result.Status); Assert.Equal(1, provider.Sends); Assert.Equal(1, provider.DraftReads);
            var error = await Assert.ThrowsAsync<ApiError>(() => Service(db, provider).SendDraft(user, mailboxId, "draft-two", "draft-stable-key-12345", "test", default));
            Assert.Equal("idempotency_conflict", error.Code);
        }
    }

    [Fact]
    public async Task Mailbox_mutations_enforce_ownership_and_audit_successful_changes()
    {
        await using var db = fixture.Open(); var mailbox = await Seed(db); var provider = new FakeProvider(); var service = Service(db, provider);
        var stranger = Guid.NewGuid(); var draft = new SaveDraftRequest(new([], "", "unfinished"));
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiError>(() => service.SetRead(stranger, mailbox.Id, "message", true, "test", default))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiError>(() => service.Move(stranger, mailbox.Id, "message", "trash", "inbox", "test", default))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiError>(() => service.SaveDraft(stranger, mailbox.Id, null, draft, "test", default))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiError>(() => service.DeleteDraft(stranger, mailbox.Id, "draft", "test", default))).Status);
        Assert.Equal(0, provider.Writes);
        await service.SetRead(mailbox.UserId, mailbox.Id, "message", false, "test", default);
        await service.Move(mailbox.UserId, mailbox.Id, "message", "trash", "inbox", "test", default);
        await service.SaveDraft(mailbox.UserId, mailbox.Id, null, draft, "test", default);
        await service.DeleteDraft(mailbox.UserId, mailbox.Id, "draft", "test", default);
        Assert.Equal(4, provider.Writes);
        var actions = await db.AuditEvents.Where(x => x.MailboxId == mailbox.Id).Select(x => x.Action).ToArrayAsync();
        Assert.Contains("mail.mark_unread", actions); Assert.Contains("mail.trash", actions);
        Assert.Contains("draft.create", actions); Assert.Contains("draft.delete", actions);
        Assert.Equal(0, provider.Sends);
    }

    [Fact]
    public async Task Concurrent_refreshes_rotate_credentials_once()
    {
        var provider = new FakeProvider(); Guid mailboxId;
        await using (var db = fixture.Open()) { mailboxId = (await Seed(db, expired: true)).Id; }
        async Task<string> Refresh()
        { await using var db = fixture.Open(); var mailbox = await db.Mailboxes.SingleAsync(x => x.Id == mailboxId); return await Service(db, provider).AccessToken(mailbox, default); }
        var tokens = await Task.WhenAll(Refresh(), Refresh());
        Assert.All(tokens, token => Assert.Equal("new-access", token)); Assert.Equal(1, provider.Refreshes);
    }

    [Fact]
    public async Task OAuth_is_bound_to_the_browser_and_cannot_be_replayed()
    {
        await using var db = fixture.Open(); var provider = new FakeProvider();
        var oauth = new OAuthService(db, fixture.Settings, cipher, new([provider]), new Development());
        var start = new DefaultHttpContext(); var url = await oauth.Start(start, "outlook", false, default);
        var state = QueryHelpers.ParseQuery(new Uri(url).Query)["state"].ToString();
        var browserCookie = start.Response.Headers.SetCookie.ToString().Split(';')[0];
        var foreign = new DefaultHttpContext(); foreign.Request.Headers.Cookie = "cm_oauth=different-browser";
        var error = await Assert.ThrowsAsync<ApiError>(() => oauth.Complete(foreign, "outlook", state, "new-person", null, default));
        Assert.Equal("invalid_state", error.Code); Assert.Equal(0, provider.Exchanges);
        var callback = new DefaultHttpContext(); callback.Request.Headers.Cookie = browserCookie;
        await oauth.Complete(callback, "outlook", state, "new-person", null, default);
        Assert.Contains("cm_session=", callback.Response.Headers.SetCookie.ToString()); Assert.Equal(1, provider.Exchanges);
        var replay = await Assert.ThrowsAsync<ApiError>(() => oauth.Complete(callback, "outlook", state, "new-person", null, default));
        Assert.Equal("invalid_state", replay.Code); Assert.Equal(1, provider.Exchanges);
    }

    [Fact]
    public async Task Api_key_scopes_revocation_and_session_csrf_are_enforced()
    {
        await using var db = fixture.Open(); var mailbox = await Seed(db); var token = "cm_sk_" + Secrets.Random();
        var key = new ApiKey { UserId = mailbox.UserId, TokenHash = Secrets.Hash(token), Scopes = ["mail:read"], ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) };
        db.ApiKeys.Add(key); await db.SaveChangesAsync();
        var middleware = new IdentityMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext(); context.Request.Headers.Authorization = "Bearer " + token;
        await middleware.Invoke(context, db);
        Assert.Equal(mailbox.UserId, RequestIdentity.Require(context, "mail:read").UserId);
        Assert.Throws<ApiError>(() => RequestIdentity.Require(context, "mail:send"));
        Assert.Throws<ApiError>(() => RequestIdentity.Require(context, "mail:write"));
        key.RevokedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync();
        var revoked = new DefaultHttpContext(); revoked.Request.Headers.Authorization = "Bearer " + token;
        await middleware.Invoke(revoked, db); Assert.Throws<ApiError>(() => RequestIdentity.Require(revoked));
        var session = Secrets.Random(); db.Sessions.Add(new() { UserId = mailbox.UserId, TokenHash = Secrets.Hash(session), ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) }); await db.SaveChangesAsync();
        var mutating = new DefaultHttpContext(); mutating.Request.Path = "/api/v1/keys"; mutating.Request.Method = "POST"; mutating.Request.Headers.Cookie = "cm_session=" + session;
        var missingCsrf = await Assert.ThrowsAsync<ApiError>(() => middleware.Invoke(mutating, db)); Assert.Equal("invalid_csrf", missingCsrf.Code);
        mutating.Request.Headers["X-CSRF-Token"] = RequestIdentity.Csrf(session); await middleware.Invoke(mutating, db);
        Assert.True(RequestIdentity.Require(mutating, sessionOnly: true).IsSession);
    }

    private sealed class Development : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = ".";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}

public sealed class FakeProvider : IEmailProvider
{
    public Task<IReadOnlyList<MailFolder>> Folders(string token, CancellationToken ct) => Task.FromResult<IReadOnlyList<MailFolder>>([new("inbox", "Inbox"), new("trash", "Trash")]);
    public Task SetRead(string token, string id, bool isRead, CancellationToken ct) { Interlocked.Increment(ref Writes); return Task.CompletedTask; }
    public Task<MailMessage> Move(string token, string id, string destination, string? source, CancellationToken ct) { Interlocked.Increment(ref Writes); return Get(token, id, ct); }
    public Task<MailMessage> GetDraft(string token, string id, CancellationToken ct)
    { Interlocked.Increment(ref DraftReads); if (DraftGone) throw new ProviderError("message_not_found", "Draft consumed"); return Task.FromResult(new MailMessage("draft-message", null, "Draft subject", null, [new("to@example.com")], [], null, "", "Draft body", null, true, [], DraftId: id)); }
    public async Task<MailMessage> SaveDraft(string token, string sender, string? id, SaveDraftRequest request, CancellationToken ct) { Interlocked.Increment(ref Writes); return await GetDraft(token, id ?? "created", ct); }
    public Task<SendResult> SendDraft(string token, string id, CancellationToken ct) => Send(token, "", new([], "", ""), null, ct);
    public Task DeleteDraft(string token, string id, CancellationToken ct) { Interlocked.Increment(ref Writes); return Task.CompletedTask; }
    public string Name => "outlook";
    public bool Configured => true;
    public int Sends, Reads, Refreshes, Exchanges, Writes, DraftReads;
    public bool Uncertain, DraftGone;
    public int SendDelay;
    public string AuthorizationUrl(string state, string challenge) => "https://provider.example/authorize?state=" + state;
    public Task<TokenSet> ExchangeCode(string code, string verifier, CancellationToken ct)
    { Interlocked.Increment(ref Exchanges); return Task.FromResult(new TokenSet(code, "refresh", DateTimeOffset.UtcNow.AddHours(1), "mail")); }
    public Task<TokenSet> Refresh(string token, CancellationToken ct)
    { Interlocked.Increment(ref Refreshes); return Task.FromResult(new TokenSet("new-access", "new-refresh", DateTimeOffset.UtcNow.AddHours(1), "mail")); }
    public Task<ProviderIdentity> Identify(string token, CancellationToken ct) => Task.FromResult(new ProviderIdentity(token, token + "@example.com", token));
    public Task<MessagePage> List(string token, string folder, string? query, int limit, string? cursor, CancellationToken ct) => Task.FromResult(new MessagePage([], null));
    public Task<MailMessage> Get(string token, string id, CancellationToken ct)
    { Interlocked.Increment(ref Reads); return Task.FromResult(new MailMessage(id, null, "Subject", null, [], [], null, "", "Body", null, true, [])); }
    public Task<DownloadedAttachment> Attachment(string token, string message, string attachment, CancellationToken ct) => throw new NotSupportedException();
    public async Task<SendResult> Send(string token, string sender, SendMailRequest request, MailMessage? reply, CancellationToken ct)
    { Interlocked.Increment(ref Sends); await Task.Delay(SendDelay, ct); if (Uncertain) throw new ProviderError("provider_unavailable", "Uncertain", uncertain: true); return new SendResult("accepted-id"); }
    public Task<ChangePage> Changes(string token, string? cursor, string? page, CancellationToken ct) => Task.FromResult(new ChangePage([], null, "checkpoint"));
}
