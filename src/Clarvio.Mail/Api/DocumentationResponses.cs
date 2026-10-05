namespace Clarvio.Mail.Api;

// Public response shapes only. Provider tokens, hashes, and internal send fields are excluded.
public sealed record ProviderResponse(string Provider, bool Configured);
public sealed record HealthResponse(string Service, string Status);
public sealed record UserResponse(Guid Id, string Name, string Email);
public sealed record SessionResponse(UserResponse User, string CsrfToken);
public sealed record MailboxResponse(Guid Id, string Provider, string Email, string Name, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? LastSyncError, bool SyncInitial);
public sealed record OperationResponse(Guid Id, string Status, string? ProviderMessageId, string? ErrorCode,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record KeyResponse(Guid Id, string Name, string Prefix, string[] Scopes, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt);
public sealed record CreatedKeyResponse(Guid Id, string Name, string[] Scopes, DateTimeOffset ExpiresAt, string Token);
public sealed record WebhookResponse(Guid Id, Guid MailboxId, string Url, bool Active, DateTimeOffset CreatedAt);
public sealed record CreatedWebhookResponse(Guid Id, Guid MailboxId, string Url, string SigningSecret);
public sealed record DeliveryResponse(Guid Id, Guid EventId, Guid SubscriptionId, string Status, int Attempts,
    DateTimeOffset NextAttemptAt, int? LastStatusCode);
public sealed record ErrorResponse(ErrorDetail Error, string TraceId);
public sealed record ErrorDetail(string Code, string Message);
