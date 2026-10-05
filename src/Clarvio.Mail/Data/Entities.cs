namespace Clarvio.Mail.Data;

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class Identity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Provider { get; set; } = "";
    public string Subject { get; set; } = "";
}
public sealed class Mailbox
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Provider { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public string Scopes { get; set; } = "";
    public DateTimeOffset TokenExpiresAt { get; set; }
    public string Status { get; set; } = "active";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? SyncCursor { get; set; }
    public string? SyncPage { get; set; }
    public bool SyncInitial { get; set; } = true;
    public DateTimeOffset? NextPollAt { get; set; }
    public string? LastSyncError { get; set; }
}
public sealed class OAuthAttempt
{
    public string StateHash { get; set; } = "";
    public string BrowserHash { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Verifier { get; set; } = "";
    public Guid? UserId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
public sealed class LoginSession
{
    public string TokenHash { get; set; } = "";
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
}
public sealed class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string[] Scopes { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
public sealed class SendOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid MailboxId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Status { get; set; } = "sending";
    public string? ProviderMessageId { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class AuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid? MailboxId { get; set; }
    public string Action { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string TraceId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class WebhookSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid MailboxId { get; set; }
    public string Url { get; set; } = "";
    public string SigningSecret { get; set; } = "";
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class IncomingEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MailboxId { get; set; }
    public string ProviderMessageId { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class WebhookDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SubscriptionId { get; set; }
    public Guid EventId { get; set; }
    public string Payload { get; set; } = "";
    public int Attempts { get; set; }
    public string Status { get; set; } = "pending";
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public int? LastStatusCode { get; set; }
}
