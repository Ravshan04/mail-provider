using Clarvio.Mail.Auth;
using Clarvio.Mail.Core;
using Clarvio.Mail.Data;
using Clarvio.Mail.Providers;
using Clarvio.Mail.Services;
using Microsoft.EntityFrameworkCore;

namespace Clarvio.Mail.Api;

public sealed record CreateKeyRequest(string Name, string[] Scopes, int ExpiresInDays = 90);
public sealed record CreateWebhookRequest(Guid MailboxId, string Url);
public sealed record ReadStateRequest(bool IsRead);
public sealed record MoveMessageRequest(string DestinationFolderId, string? SourceFolderId = null);

public static class Endpoints
{
    public static void MapMailApi(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { service = "clarvio-mail", status = "alive" }));
        app.MapGet("/health/ready", async (MailDb db, CancellationToken ct) =>
        {
            try { await db.Users.AnyAsync(ct); return Results.Ok(new { service = "clarvio-mail", status = "ready" }); }
            catch { return Results.Json(new { status = "unavailable" }, statusCode: 503); }
        });
        app.MapGet("/auth/{provider}", async (HttpContext ctx, string provider, string? mode, OAuthService oauth, CancellationToken ct) =>
            Results.Redirect(await oauth.Start(ctx, provider, mode == "connect", ct))).RequireRateLimiting("auth");
        app.MapGet("/auth/{provider}/callback", async (HttpContext ctx, string provider, string? state, string? code, string? error, OAuthService oauth, CancellationToken ct) =>
        {
            await oauth.Complete(ctx, provider, state ?? "", code, error, ct);
            return Results.Redirect("/");
        }).RequireRateLimiting("auth");
        var api = app.MapGroup("/api/v1").RequireRateLimiting("api");
        api.MapGet("/providers", (ProviderRegistry providers) => providers.All.Select(x => new { provider = x.Name, configured = x.Configured }));
        api.MapGet("/session", async (HttpContext ctx, MailDb db, CancellationToken ct) =>
        {
            var identity = RequestIdentity.Require(ctx, sessionOnly: true);
            var user = await db.Users.SingleAsync(x => x.Id == identity.UserId, ct);
            return Results.Ok(new { user = new { user.Id, user.Name, user.Email }, csrfToken = RequestIdentity.Csrf(identity.SessionToken!) });
        });
        api.MapPost("/logout", async (HttpContext ctx, MailDb db, CancellationToken ct) =>
        {
            var identity = RequestIdentity.Require(ctx, sessionOnly: true);
            await db.Sessions.Where(x => x.TokenHash == Secrets.Hash(identity.SessionToken!)).ExecuteDeleteAsync(ct);
            ctx.Response.Cookies.Delete(IdentityMiddleware.SessionCookie, new CookieOptions { Path = "/" });
            return Results.NoContent();
        });
        api.MapGet("/mailboxes", async (HttpContext ctx, MailDb db, CancellationToken ct) =>
        {
            var user = RequestIdentity.Require(ctx, "accounts:read").UserId;
            return await db.Mailboxes.Where(x => x.UserId == user).OrderBy(x => x.CreatedAt)
                .Select(x => new { x.Id, x.Provider, x.Email, x.Name, x.Status, x.CreatedAt, x.UpdatedAt, x.LastSyncError, x.SyncInitial }).ToListAsync(ct);
        });
        api.MapDelete("/mailboxes/{mailboxId:guid}", async (HttpContext ctx, Guid mailboxId, MailService mail, CancellationToken ct) =>
        { await mail.Disconnect(RequestIdentity.Require(ctx, sessionOnly: true).UserId, mailboxId, ctx.TraceIdentifier, ct); return Results.NoContent(); });
        api.MapGet("/mailboxes/{mailboxId:guid}/messages", async (HttpContext ctx, Guid mailboxId, string? folder, string? query, int? limit, string? cursor, MailService mail, CancellationToken ct) =>
            await mail.List(RequestIdentity.Require(ctx, "mail:read").UserId, mailboxId, folder ?? "inbox", query, limit ?? 25, cursor, ct));
        api.MapGet("/mailboxes/{mailboxId:guid}/folders", async (HttpContext ctx, Guid mailboxId, MailService mail, CancellationToken ct) =>
            await mail.Folders(RequestIdentity.Require(ctx, "mail:read").UserId, mailboxId, ct));
        api.MapPatch("/mailboxes/{mailboxId:guid}/messages/{messageId}/read", async (HttpContext ctx, Guid mailboxId, string messageId, ReadStateRequest request, MailService mail, CancellationToken ct) =>
        { await mail.SetRead(RequestIdentity.Require(ctx, "mail:write").UserId, mailboxId, messageId, request.IsRead, ctx.TraceIdentifier, ct); return Results.NoContent(); });
        api.MapPost("/mailboxes/{mailboxId:guid}/messages/{messageId}/move", async (HttpContext ctx, Guid mailboxId, string messageId, MoveMessageRequest request, MailService mail, CancellationToken ct) =>
            await mail.Move(RequestIdentity.Require(ctx, "mail:write").UserId, mailboxId, messageId, request.DestinationFolderId, request.SourceFolderId, ctx.TraceIdentifier, ct));
        api.MapDelete("/mailboxes/{mailboxId:guid}/messages/{messageId}", async (HttpContext ctx, Guid mailboxId, string messageId, MailService mail, CancellationToken ct) =>
            await mail.Move(RequestIdentity.Require(ctx, "mail:write").UserId, mailboxId, messageId, "trash", null, ctx.TraceIdentifier, ct));
        api.MapGet("/mailboxes/{mailboxId:guid}/drafts/{draftId}", async (HttpContext ctx, Guid mailboxId, string draftId, MailService mail, CancellationToken ct) =>
            await mail.GetDraft(RequestIdentity.Require(ctx, "mail:read").UserId, mailboxId, draftId, ct));
        api.MapPost("/mailboxes/{mailboxId:guid}/drafts", async (HttpContext ctx, Guid mailboxId, SaveDraftRequest request, MailService mail, CancellationToken ct) =>
            await mail.SaveDraft(RequestIdentity.Require(ctx, "mail:write").UserId, mailboxId, null, request, ctx.TraceIdentifier, ct));
        api.MapPut("/mailboxes/{mailboxId:guid}/drafts/{draftId}", async (HttpContext ctx, Guid mailboxId, string draftId, SaveDraftRequest request, MailService mail, CancellationToken ct) =>
            await mail.SaveDraft(RequestIdentity.Require(ctx, "mail:write").UserId, mailboxId, draftId, request, ctx.TraceIdentifier, ct));
        api.MapDelete("/mailboxes/{mailboxId:guid}/drafts/{draftId}", async (HttpContext ctx, Guid mailboxId, string draftId, MailService mail, CancellationToken ct) =>
        { await mail.DeleteDraft(RequestIdentity.Require(ctx, "mail:write").UserId, mailboxId, draftId, ctx.TraceIdentifier, ct); return Results.NoContent(); });
        api.MapPost("/mailboxes/{mailboxId:guid}/drafts/{draftId}/send", async (HttpContext ctx, Guid mailboxId, string draftId, MailService mail, CancellationToken ct) =>
            Operation(await mail.SendDraft(RequestIdentity.Require(ctx, "mail:send").UserId, mailboxId, draftId, ctx.Request.Headers["Idempotency-Key"].ToString(), ctx.TraceIdentifier, ct))).RequireRateLimiting("send");
        api.MapGet("/mailboxes/{mailboxId:guid}/messages/{messageId}", async (HttpContext ctx, Guid mailboxId, string messageId, MailService mail, CancellationToken ct) =>
            await mail.Get(RequestIdentity.Require(ctx, "mail:read").UserId, mailboxId, messageId, ct));
        api.MapGet("/mailboxes/{mailboxId:guid}/messages/{messageId}/attachments/{attachmentId}", async (HttpContext ctx, Guid mailboxId, string messageId, string attachmentId, MailService mail, CancellationToken ct) =>
        {
            var file = await mail.Attachment(RequestIdentity.Require(ctx, "mail:read").UserId, mailboxId, messageId, attachmentId, ct);
            if (file.Bytes.Length > MessageValidation.MaxAttachmentBytes) throw new ApiError(413, "attachment_too_large", "Downloads support attachments up to 10 MiB.");
            return Results.File(file.Bytes, "application/octet-stream", MessageValidation.SafeFilename(file.Name));
        });
        api.MapPost("/mailboxes/{mailboxId:guid}/messages", async (HttpContext ctx, Guid mailboxId, SendMailRequest request, MailService mail, CancellationToken ct) =>
            Operation(await mail.Send(RequestIdentity.Require(ctx, "mail:send").UserId, mailboxId, request, ctx.Request.Headers["Idempotency-Key"].ToString(), null, ctx.TraceIdentifier, ct)))
            .RequireRateLimiting("send");
        api.MapPost("/mailboxes/{mailboxId:guid}/messages/{messageId}/reply", async (HttpContext ctx, Guid mailboxId, string messageId, SendMailRequest request, MailService mail, CancellationToken ct) =>
            Operation(await mail.Send(RequestIdentity.Require(ctx, "mail:send").UserId, mailboxId, request, ctx.Request.Headers["Idempotency-Key"].ToString(), messageId, ctx.TraceIdentifier, ct)))
            .RequireRateLimiting("send");
        api.MapGet("/operations/{id:guid}", async (HttpContext ctx, Guid id, MailDb db, CancellationToken ct) =>
        {
            var user = RequestIdentity.Require(ctx, "mail:send").UserId;
            return Operation(await db.SendOperations.SingleOrDefaultAsync(x => x.Id == id && x.UserId == user, ct)
                ?? throw new ApiError(404, "operation_not_found", "That operation is not available."));
        });
        api.MapGet("/keys", async (HttpContext ctx, MailDb db, CancellationToken ct) =>
        {
            var user = RequestIdentity.Require(ctx, sessionOnly: true).UserId;
            return await db.ApiKeys.Where(x => x.UserId == user).OrderByDescending(x => x.CreatedAt)
                .Select(x => new { x.Id, x.Name, x.Prefix, x.Scopes, x.CreatedAt, x.ExpiresAt, x.LastUsedAt, x.RevokedAt }).Take(100).ToListAsync(ct);
        });
        api.MapPost("/keys", async (HttpContext ctx, CreateKeyRequest request, MailDb db, CancellationToken ct) =>
        {
            var user = RequestIdentity.Require(ctx, sessionOnly: true).UserId;
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 80 || request.Scopes is not { Length: > 0 } ||
                request.Scopes.Any(x => !RequestIdentity.KeyScopes.Contains(x)) || request.ExpiresInDays is < 1 or > 365)
                throw new ApiError(400, "invalid_key", "Choose a name, valid scopes and an expiry of 1–365 days.");
            if (await db.ApiKeys.CountAsync(x => x.UserId == user && x.RevokedAt == null && x.ExpiresAt > DateTimeOffset.UtcNow, ct) >= 20)
                throw new ApiError(409, "key_limit", "Revoke an existing key before creating another.");
            var token = "cm_sk_" + Secrets.Random();
            var key = new ApiKey { UserId = user, Name = request.Name.Trim(), TokenHash = Secrets.Hash(token), Prefix = token[..14], Scopes = request.Scopes.Distinct().ToArray(), ExpiresAt = DateTimeOffset.UtcNow.AddDays(request.ExpiresInDays) };
            db.ApiKeys.Add(key); Audit(db, user, "key.create", ctx);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { key.Id, key.Name, key.Scopes, key.ExpiresAt, token });
        });
        api.MapDelete("/keys/{id:guid}", async (HttpContext ctx, Guid id, MailDb db, CancellationToken ct) =>
        {
            var user = RequestIdentity.Require(ctx, sessionOnly: true).UserId;
            if (await db.ApiKeys.Where(x => x.Id == id && x.UserId == user).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow), ct) == 0)
                throw new ApiError(404, "key_not_found", "That key is not available.");
            Audit(db, user, "key.revoke", ctx); await db.SaveChangesAsync(ct); return Results.NoContent();
        });
        api.MapGet("/audit", async (HttpContext ctx, MailDb db, CancellationToken ct) =>
        {
            var user = RequestIdentity.Require(ctx, sessionOnly: true).UserId;
            return await db.AuditEvents.Where(x => x.UserId == user).OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct);
        });
        api.MapGet("/webhooks", async (HttpContext ctx, MailDb db, CancellationToken ct) =>
        {
            var user = RequestIdentity.Require(ctx, sessionOnly: true).UserId;
            return await db.WebhookSubscriptions.Where(x => x.UserId == user).OrderByDescending(x => x.CreatedAt)
                .Select(x => new { x.Id, x.MailboxId, x.Url, x.Active, x.CreatedAt }).Take(100).ToListAsync(ct);
        });
        api.MapPost("/webhooks", async (HttpContext ctx, CreateWebhookRequest request, MailDb db, MailService mail, MailSettings settings, TokenCipher cipher, CancellationToken ct) =>
        {
            var user = RequestIdentity.Require(ctx, sessionOnly: true).UserId;
            var mailbox = await mail.Owned(user, request.MailboxId, ct);
            WebhookTargets.Validate(request.Url, settings.WebhookHosts);
            if (await db.WebhookSubscriptions.CountAsync(x => x.UserId == user && x.Active, ct) >= 20)
                throw new ApiError(409, "webhook_limit", "Remove an existing subscription before creating another.");
            var secret = "cm_wh_" + Secrets.Random();
            var sub = new WebhookSubscription { UserId = user, MailboxId = mailbox.Id, Url = request.Url };
            sub.SigningSecret = cipher.Encrypt(secret, "webhook:" + sub.Id);
            db.WebhookSubscriptions.Add(sub); mailbox.NextPollAt = DateTimeOffset.UtcNow;
            Audit(db, user, "webhook.create", ctx); await db.SaveChangesAsync(ct);
            return Results.Ok(new { sub.Id, sub.MailboxId, sub.Url, signingSecret = secret });
        });
        api.MapDelete("/webhooks/{id:guid}", async (HttpContext ctx, Guid id, MailDb db, CancellationToken ct) =>
        {
            var user = RequestIdentity.Require(ctx, sessionOnly: true).UserId;
            if (await db.WebhookSubscriptions.Where(x => x.Id == id && x.UserId == user).ExecuteUpdateAsync(s => s.SetProperty(x => x.Active, false), ct) == 0)
                throw new ApiError(404, "webhook_not_found", "That subscription is not available.");
            Audit(db, user, "webhook.disable", ctx); await db.SaveChangesAsync(ct); return Results.NoContent();
        });
        api.MapGet("/webhook-deliveries", async (HttpContext ctx, MailDb db, CancellationToken ct) =>
        {
            var user = RequestIdentity.Require(ctx, sessionOnly: true).UserId;
            return await db.WebhookDeliveries.Where(x => db.WebhookSubscriptions.Any(s => s.Id == x.SubscriptionId && s.UserId == user))
                .OrderByDescending(x => x.NextAttemptAt).Select(x => new { x.Id, x.EventId, x.SubscriptionId, x.Status, x.Attempts, x.NextAttemptAt, x.LastStatusCode }).Take(100).ToListAsync(ct);
        });
    }
    private static object Operation(SendOperation operation) => new { operation.Id, operation.Status, operation.ProviderMessageId, operation.ErrorCode, operation.CreatedAt, operation.UpdatedAt };
    private static void Audit(MailDb db, Guid user, string action, HttpContext ctx) => db.AuditEvents.Add(new AuditEvent { UserId = user, Action = action, Outcome = "success", TraceId = ctx.TraceIdentifier });
}
