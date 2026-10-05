using System.Text.Json;
using Clarvio.Mail.Auth;
using Clarvio.Mail.Core;
using Clarvio.Mail.Data;
using Clarvio.Mail.Providers;
using Microsoft.EntityFrameworkCore;

namespace Clarvio.Mail.Services;

public sealed record PageCursor(Guid MailboxId, string Folder, string? Query, int Limit, string ProviderCursor, DateTimeOffset ExpiresAt);

public sealed class MailService(MailDb db, TokenCipher cipher, ProviderRegistry providers)
{
    public async Task<Mailbox> Owned(Guid user, Guid mailboxId, CancellationToken ct, bool active = true)
    {
        var mailbox = await db.Mailboxes.SingleOrDefaultAsync(x => x.Id == mailboxId && x.UserId == user, ct)
            ?? throw new ApiError(404, "mailbox_not_found", "That mailbox is not available.");
        if (active && mailbox.Status != "active") throw new ApiError(409, "reconnect_required", "Connect this mailbox again before using it.");
        return mailbox;
    }

    public async Task<string> AccessToken(Mailbox mailbox, CancellationToken ct)
    {
        await using var tx = await DbLocks.Acquire(db, "mailbox:" + mailbox.Id, ct);
        await db.Entry(mailbox).ReloadAsync(ct);
        if (mailbox.Status != "active") throw new ApiError(409, "reconnect_required", "Connect this mailbox again.");
        if (mailbox.TokenExpiresAt > DateTimeOffset.UtcNow.AddSeconds(60))
            return cipher.Decrypt(mailbox.AccessToken, "access:" + mailbox.Id);
        if (mailbox.RefreshToken is null) throw new ApiError(409, "reconnect_required", "This mailbox needs to be connected again.");
        try
        {
            var tokens = await providers.Get(mailbox.Provider).Refresh(cipher.Decrypt(mailbox.RefreshToken, "refresh:" + mailbox.Id), ct);
            mailbox.AccessToken = cipher.Encrypt(tokens.AccessToken, "access:" + mailbox.Id);
            if (!string.IsNullOrEmpty(tokens.RefreshToken)) mailbox.RefreshToken = cipher.Encrypt(tokens.RefreshToken, "refresh:" + mailbox.Id);
            mailbox.TokenExpiresAt = tokens.ExpiresAt; mailbox.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return tokens.AccessToken;
        }
        catch (ProviderError ex) when (ex.Reconnect)
        {
            mailbox.Status = "reconnect_required"; mailbox.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); throw;
        }
    }

    public async Task<MessagePage> List(Guid user, Guid mailboxId, string folder, string? query, int limit, string? cursor, CancellationToken ct)
    {
        var mailbox = await Owned(user, mailboxId, ct);
        ValidateFolder(folder);
        if (query?.Length > 300 || limit is < 1 or > 50)
            throw new ApiError(400, "invalid_query", "Use a query up to 300 characters and a limit of 1–50.");
        string? providerCursor = null;
        if (cursor is not null)
        {
            try
            {
                if (cursor.Length > 12_000) throw new FormatException();
                var value = JsonSerializer.Deserialize<PageCursor>(cipher.Decrypt(cursor, "cursor:" + user + ":" + mailboxId))!;
                if (value.MailboxId != mailboxId || value.Folder != folder || value.Query != query || value.Limit != limit || value.ExpiresAt < DateTimeOffset.UtcNow) throw new FormatException();
                providerCursor = value.ProviderCursor;
            }
            catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException or JsonException or NullReferenceException)
            { throw new ApiError(400, "invalid_cursor", "The page link expired or does not match this mailbox and query."); }
        }
        var page = await providers.Get(mailbox.Provider).List(await AccessToken(mailbox, ct), folder, query, limit, providerCursor, ct);
        return page with { NextCursor = page.NextCursor is null ? null : cipher.Encrypt(JsonSerializer.Serialize(
            new PageCursor(mailboxId, folder, query, limit, page.NextCursor, DateTimeOffset.UtcNow.AddMinutes(30))), "cursor:" + user + ":" + mailboxId) };
    }

    public async Task<MailMessage> Get(Guid user, Guid mailboxId, string id, CancellationToken ct)
    { var mailbox = await Owned(user, mailboxId, ct); return await providers.Get(mailbox.Provider).Get(await AccessToken(mailbox, ct), id, ct); }
    public async Task<DownloadedAttachment> Attachment(Guid user, Guid mailboxId, string id, string attachment, CancellationToken ct)
    { var mailbox = await Owned(user, mailboxId, ct); return await providers.Get(mailbox.Provider).Attachment(await AccessToken(mailbox, ct), id, attachment, ct); }

    public Task<SendOperation> Send(Guid user, Guid mailboxId, SendMailRequest request, string key, string? replyId, string trace, CancellationToken ct)
    {
        MessageValidation.Validate(request);
        return SendInternal(user, mailboxId, request, key, replyId, null, trace, ct);
    }
    public Task<SendOperation> SendDraft(Guid user, Guid mailboxId, string draftId, string key, string trace, CancellationToken ct)
        => SendInternal(user, mailboxId, null, key, null, draftId, trace, ct);

    private async Task<SendOperation> SendInternal(Guid user, Guid mailboxId, SendMailRequest? request, string key, string? replyId, string? draftId, string trace, CancellationToken ct)
    {
        if (key.Length is < 16 or > 128 || key.Any(char.IsControl))
            throw new ApiError(400, "idempotency_required", "Provide an Idempotency-Key of 16–128 characters.");
        var mailbox = await Owned(user, mailboxId, ct);
        var fingerprint = Secrets.Hash(draftId is null ? JsonSerializer.Serialize(new { mailboxId, request, replyId }) : JsonSerializer.Serialize(new { mailboxId, draftId }));
        SendOperation operation;
        await using (var tx = await DbLocks.Acquire(db, "send:" + user + ":" + key, ct))
        {
            var existing = await db.SendOperations.SingleOrDefaultAsync(x => x.UserId == user && x.IdempotencyKey == key, ct);
            if (existing is not null)
            {
                if (existing.Fingerprint != fingerprint) throw new ApiError(409, "idempotency_conflict", "This idempotency key was already used for a different email.");
                return existing;
            }
            operation = new SendOperation { UserId = user, MailboxId = mailboxId, IdempotencyKey = key, Fingerprint = fingerprint };
            db.SendOperations.Add(operation); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        var providerCalled = false;
        try
        {
            var token = await AccessToken(mailbox, ct);
            var provider = providers.Get(mailbox.Provider);
            var reply = replyId is null ? null : await provider.Get(token, replyId, ct);
            if (draftId is not null)
            {
                var draft = await provider.GetDraft(token, draftId, ct);
                MessageValidation.Validate(new(draft.To, draft.Subject, draft.TextBody ?? "", draft.Cc, draft.Bcc, draft.HtmlBody));
            }
            providerCalled = true;
            var result = draftId is null ? await provider.Send(token, mailbox.Email, request!, reply, ct) : await provider.SendDraft(token, draftId, ct);
            operation.Status = "accepted"; operation.ProviderMessageId = result.MessageId;
        }
        catch (ProviderError ex)
        {
            operation.Status = ex.Uncertain && providerCalled ? "unknown" : "failed"; operation.ErrorCode = ex.Code;
            if (ex.Reconnect) mailbox.Status = "reconnect_required";
        }
        catch (ApiError ex) { operation.Status = "failed"; operation.ErrorCode = ex.Code; }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        { operation.Status = providerCalled ? "unknown" : "failed"; operation.ErrorCode = "provider_unavailable"; }
        catch
        { operation.Status = providerCalled ? "unknown" : "failed"; operation.ErrorCode = "internal_error"; }
        operation.UpdatedAt = DateTimeOffset.UtcNow;
        db.AuditEvents.Add(new AuditEvent { UserId = user, MailboxId = mailboxId, Action = draftId is not null ? "draft.send" : replyId is null ? "mail.send" : "mail.reply", Outcome = operation.Status, TraceId = trace });
        // Persist the outcome even when the requesting browser disconnected after provider acceptance.
        using var saveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await db.SaveChangesAsync(saveTimeout.Token);
        return operation;
    }

    public static void ValidateFolder(string? folder)
    {
        if (folder is null || folder.Length > 1000 || folder.Any(char.IsControl) ||
            folder is not ("inbox" or "sent" or "all" or "drafts" or "archive" or "trash") &&
            !(folder.StartsWith("custom:", StringComparison.Ordinal) && folder.Length > 7))
            throw new ApiError(400, "invalid_folder", "Choose a supported mailbox folder.");
    }
    public async Task<IReadOnlyList<MailFolder>> Folders(Guid user, Guid mailboxId, CancellationToken ct)
    { var mailbox = await Owned(user, mailboxId, ct); return await providers.Get(mailbox.Provider).Folders(await AccessToken(mailbox, ct), ct); }
    public async Task SetRead(Guid user, Guid mailboxId, string id, bool isRead, string trace, CancellationToken ct)
    {
        var mailbox = await Owned(user, mailboxId, ct);
        await providers.Get(mailbox.Provider).SetRead(await AccessToken(mailbox, ct), id, isRead, ct);
        await AuditMutation(user, mailboxId, isRead ? "mail.mark_read" : "mail.mark_unread", trace, ct);
    }
    public async Task<MailMessage> Move(Guid user, Guid mailboxId, string id, string destination, string? source, string trace, CancellationToken ct)
    {
        ValidateFolder(destination); if (source is not null) ValidateFolder(source);
        if (destination is "all" or "drafts" or "sent") throw new ApiError(400, "invalid_destination", "Choose Inbox, Archive, Trash, or a custom folder.");
        var mailbox = await Owned(user, mailboxId, ct);
        var result = await providers.Get(mailbox.Provider).Move(await AccessToken(mailbox, ct), id, destination, source, ct);
        await AuditMutation(user, mailboxId, destination == "trash" ? "mail.trash" : "mail.move", trace, ct); return result;
    }
    public async Task<MailMessage> GetDraft(Guid user, Guid mailboxId, string id, CancellationToken ct)
    { var mailbox = await Owned(user, mailboxId, ct); return await providers.Get(mailbox.Provider).GetDraft(await AccessToken(mailbox, ct), id, ct); }
    public async Task<MailMessage> SaveDraft(Guid user, Guid mailboxId, string? id, SaveDraftRequest request, string trace, CancellationToken ct)
    {
        if (request.Message is null) throw new ApiError(400, "invalid_message", "Draft message content is required.");
        MessageValidation.Validate(request.Message, draft: true);
        var mailbox = await Owned(user, mailboxId, ct);
        var result = await providers.Get(mailbox.Provider).SaveDraft(await AccessToken(mailbox, ct), mailbox.Email, id, request, ct);
        await AuditMutation(user, mailboxId, id is null ? "draft.create" : "draft.update", trace, ct); return result;
    }
    public async Task DeleteDraft(Guid user, Guid mailboxId, string id, string trace, CancellationToken ct)
    {
        var mailbox = await Owned(user, mailboxId, ct);
        await providers.Get(mailbox.Provider).DeleteDraft(await AccessToken(mailbox, ct), id, ct);
        await AuditMutation(user, mailboxId, "draft.delete", trace, ct);
    }
    private async Task AuditMutation(Guid user, Guid mailboxId, string action, string trace, CancellationToken ct)
    {
        db.AuditEvents.Add(new AuditEvent { UserId = user, MailboxId = mailboxId, Action = action, Outcome = "success", TraceId = trace });
        await db.SaveChangesAsync(ct);
    }

    public async Task Disconnect(Guid user, Guid mailboxId, string trace, CancellationToken ct)
    {
        var mailbox = await Owned(user, mailboxId, ct, false);
        await using var tx = await DbLocks.Acquire(db, "mailbox:" + mailbox.Id, ct);
        await db.Entry(mailbox).ReloadAsync(ct);
        mailbox.Status = "disconnected"; mailbox.AccessToken = ""; mailbox.RefreshToken = null; mailbox.SyncCursor = null;
        mailbox.SyncPage = null; mailbox.SyncInitial = true;
        mailbox.UpdatedAt = DateTimeOffset.UtcNow;
        await db.WebhookSubscriptions.Where(x => x.MailboxId == mailboxId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Active, false), ct);
        db.AuditEvents.Add(new AuditEvent { UserId = user, MailboxId = mailboxId, Action = "mailbox.disconnect", Outcome = "success", TraceId = trace });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
}
