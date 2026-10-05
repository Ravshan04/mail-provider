using System.Text.Json.Serialization;

namespace Clarvio.Mail.Providers;

public sealed record MailAddress(string Email, string? Name = null);
public sealed record AttachmentInfo(string Id, string Name, string ContentType, long Size, string? ContentId = null);
public sealed record MailMessage(string Id, string? ThreadId, string Subject, MailAddress? From,
    IReadOnlyList<MailAddress> To, IReadOnlyList<MailAddress> Cc, DateTimeOffset? ReceivedAt,
    string Preview, string? TextBody, string? HtmlBody, bool? IsRead,
    IReadOnlyList<AttachmentInfo> Attachments, string? InternetMessageId = null, string? References = null,
    IReadOnlyList<MailAddress>? ReplyTo = null, IReadOnlyList<MailAddress>? Bcc = null, string? DraftId = null, string? InReplyTo = null);
public sealed record MailFolder(string Id, string Name, bool CanMove = true);
public sealed record SaveDraftRequest(SendMailRequest Message, IReadOnlyList<string>? KeepAttachmentIds = null, string? ReplyMessageId = null);
public sealed record MessagePage(IReadOnlyList<MailMessage> Messages, string? NextCursor);
public sealed record DownloadedAttachment(byte[] Bytes, string Name, string ContentType);
public sealed record OutgoingAttachment(string Name, string ContentType, string ContentBase64);
public sealed record SendMailRequest(IReadOnlyList<MailAddress> To, string Subject, string TextBody,
    IReadOnlyList<MailAddress>? Cc = null, IReadOnlyList<MailAddress>? Bcc = null,
    string? HtmlBody = null, IReadOnlyList<OutgoingAttachment>? Attachments = null);
public sealed record TokenSet(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string Scopes);
public sealed record ProviderIdentity(string Subject, string Email, string Name);
public sealed record ChangePage(IReadOnlyList<string> MessageIds, string? NextPage, string? FinalCursor, bool Reset = false);
public sealed record SendResult(string? MessageId);

public interface IEmailProvider
{
    string Name { get; }
    bool Configured { get; }
    string AuthorizationUrl(string state, string challenge);
    Task<TokenSet> ExchangeCode(string code, string verifier, CancellationToken ct);
    Task<TokenSet> Refresh(string refreshToken, CancellationToken ct);
    Task<ProviderIdentity> Identify(string accessToken, CancellationToken ct);
    Task<MessagePage> List(string accessToken, string folder, string? query, int limit, string? cursor, CancellationToken ct);
    Task<MailMessage> Get(string accessToken, string id, CancellationToken ct);
    Task<DownloadedAttachment> Attachment(string accessToken, string messageId, string attachmentId, CancellationToken ct);
    Task<SendResult> Send(string accessToken, string sender, SendMailRequest request, MailMessage? reply, CancellationToken ct);
    Task<ChangePage> Changes(string accessToken, string? cursor, string? page, CancellationToken ct);
    Task<IReadOnlyList<MailFolder>> Folders(string accessToken, CancellationToken ct);
    Task SetRead(string accessToken, string id, bool isRead, CancellationToken ct);
    Task<MailMessage> Move(string accessToken, string id, string destination, string? source, CancellationToken ct);
    Task<MailMessage> GetDraft(string accessToken, string id, CancellationToken ct);
    Task<MailMessage> SaveDraft(string accessToken, string sender, string? id, SaveDraftRequest request, CancellationToken ct);
    Task<SendResult> SendDraft(string accessToken, string id, CancellationToken ct);
    Task DeleteDraft(string accessToken, string id, CancellationToken ct);
}

public sealed class ProviderRegistry(IEnumerable<IEmailProvider> providers)
{
    private readonly Dictionary<string, IEmailProvider> _providers = providers.ToDictionary(x => x.Name);
    public IEnumerable<IEmailProvider> All => _providers.Values;
    public IEmailProvider Get(string name) => _providers.TryGetValue(name, out var provider) ? provider
        : throw new Core.ApiError(404, "provider_not_found", "That email provider is not supported.");
}
