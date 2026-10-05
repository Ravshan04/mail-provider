using System.Text;
using System.Text.Json;
using Clarvio.Mail.Core;
using Clarvio.Mail.Data;
using Clarvio.Mail.Providers;
using Microsoft.EntityFrameworkCore;

namespace Clarvio.Mail.Services;

public sealed class MailWorker(IServiceScopeFactory scopes, MailSettings settings, ILogger<MailWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Cycle(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning("Mail worker cycle failed ({Type}).", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task Cycle(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MailDb>();
        var now = DateTimeOffset.UtcNow;
        await db.OAuthAttempts.Where(x => x.ExpiresAt < now).ExecuteDeleteAsync(ct);
        await db.Sessions.Where(x => x.ExpiresAt < now).ExecuteDeleteAsync(ct);
        await db.SendOperations.Where(x => x.Status == "sending" && x.UpdatedAt < now.AddMinutes(-5))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "unknown").SetProperty(x => x.ErrorCode, "interrupted_send").SetProperty(x => x.UpdatedAt, now), ct);
        var ids = await db.Mailboxes.Where(x => x.Status == "active" && (x.NextPollAt == null || x.NextPollAt <= now) &&
            db.WebhookSubscriptions.Any(s => s.MailboxId == x.Id && s.Active)).OrderBy(x => x.NextPollAt).Select(x => x.Id).Take(10).ToListAsync(ct);
        foreach (var id in ids)
        {
            try { await Poll(id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { log.LogWarning("Mailbox sync {Id} failed ({Type}).", id, ex.GetType().Name); }
        }
        var deliveries = await db.WebhookDeliveries.Where(x => x.Status == "pending" && x.NextAttemptAt <= now)
            .OrderBy(x => x.NextAttemptAt).Select(x => x.Id).Take(20).ToListAsync(ct);
        foreach (var id in deliveries) await Deliver(id, ct);
    }

    private async Task Poll(Guid id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<MailDb>();
        var mailbox = await db.Mailboxes.SingleAsync(x => x.Id == id, ct);
        try
        {
            var token = await services.GetRequiredService<MailService>().AccessToken(mailbox, ct);
            await using var tx = await DbLocks.Acquire(db, "mailbox:" + id, ct);
            await db.Entry(mailbox).ReloadAsync(ct);
            if (mailbox.Status != "active" || mailbox.NextPollAt > DateTimeOffset.UtcNow) return;
            var subs = await db.WebhookSubscriptions.Where(x => x.MailboxId == id && x.Active).ToListAsync(ct);
            if (subs.Count == 0) return;
            var provider = services.GetRequiredService<ProviderRegistry>().Get(mailbox.Provider);
            for (var page = 0; page < 3; page++)
            {
                var changes = await provider.Changes(token, mailbox.SyncCursor, mailbox.SyncPage, ct);
                if (changes.Reset)
                {
                    mailbox.SyncCursor = null; mailbox.SyncPage = null; mailbox.SyncInitial = true;
                    mailbox.LastSyncError = "sync_reset"; break;
                }
                if (!mailbox.SyncInitial)
                    foreach (var messageId in changes.MessageIds.Distinct())
                    {
                        if (await db.IncomingEvents.AnyAsync(x => x.MailboxId == id && x.ProviderMessageId == messageId, ct)) continue;
                        var incoming = new IncomingEvent { MailboxId = id, ProviderMessageId = messageId };
                        incoming.Payload = JsonSerializer.Serialize(new { eventId = incoming.Id, type = "message.received", mailboxId = id, messageId, createdAt = incoming.CreatedAt });
                        db.IncomingEvents.Add(incoming);
                        foreach (var sub in subs) db.WebhookDeliveries.Add(new WebhookDelivery { SubscriptionId = sub.Id, EventId = incoming.Id, Payload = incoming.Payload });
                        await db.SaveChangesAsync(ct);
                    }
                mailbox.SyncPage = changes.NextPage;
                if (changes.FinalCursor is not null)
                { mailbox.SyncCursor = changes.FinalCursor; mailbox.SyncInitial = false; mailbox.LastSyncError = null; }
                if (changes.NextPage is null) break;
            }
            mailbox.NextPollAt = DateTimeOffset.UtcNow.AddSeconds(mailbox.SyncPage is null ? settings.PollSeconds : 5);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        catch (ProviderError ex)
        {
            db.ChangeTracker.Clear();
            await db.Mailboxes.Where(x => x.Id == id && x.Status == "active").ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSyncError, ex.Code)
                .SetProperty(x => x.NextPollAt, DateTimeOffset.UtcNow.AddMinutes(2))
                .SetProperty(x => x.Status, ex.Reconnect ? "reconnect_required" : "active"), ct);
        }
    }

    private async Task Deliver(Guid id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<MailDb>();
        await using var tx = await DbLocks.Acquire(db, "delivery:" + id, ct);
        var delivery = await db.WebhookDeliveries.SingleAsync(x => x.Id == id, ct);
        if (delivery.Status != "pending" || delivery.NextAttemptAt > DateTimeOffset.UtcNow) return;
        var sub = await db.WebhookSubscriptions.SingleAsync(x => x.Id == delivery.SubscriptionId, ct);
        if (!sub.Active) delivery.Status = "cancelled";
        else
        {
            delivery.Attempts++;
            try
            {
                var target = WebhookTargets.Validate(sub.Url, settings.WebhookHosts);
                var secret = services.GetRequiredService<TokenCipher>().Decrypt(sub.SigningSecret, "webhook:" + sub.Id);
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                using var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = new StringContent(delivery.Payload, Encoding.UTF8, "application/json") };
                request.Headers.Add("X-Clarvio-Mail-Event", delivery.EventId.ToString());
                request.Headers.Add("X-Clarvio-Mail-Signature", "t=" + timestamp + ",v1=" + WebhookTargets.Sign(secret, timestamp, delivery.Payload));
                using var response = await services.GetRequiredService<IHttpClientFactory>().CreateClient("webhook")
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                delivery.LastStatusCode = (int)response.StatusCode;
                if (response.IsSuccessStatusCode) delivery.Status = "delivered";
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ApiError)
            { delivery.LastStatusCode = null; }
            if (delivery.Status == "pending")
            {
                if (delivery.Attempts >= 10) delivery.Status = "dead_letter";
                else delivery.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(3600, 10 * Math.Pow(2, delivery.Attempts)));
            }
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
}
