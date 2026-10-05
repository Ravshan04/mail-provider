using System.Globalization;
using System.Text;
using System.Text.Json;
using Clarvio.Mail.Core;
using Microsoft.AspNetCore.WebUtilities;
using MimeKit;

namespace Clarvio.Mail.Providers;

public sealed partial class GoogleProvider(MailSettings settings, ProviderHttp http) : IEmailProvider
{
    private const string Api = "https://gmail.googleapis.com/gmail/v1/users/me";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string Scopes = "openid email profile https://www.googleapis.com/auth/gmail.modify";
    private string Redirect => settings.PublicUrl + "/auth/gmail/callback";
    public string Name => "gmail";
    public bool Configured => settings.Google.Enabled;

    public string AuthorizationUrl(string state, string challenge) => QueryHelpers.AddQueryString("https://accounts.google.com/o/oauth2/v2/auth", new Dictionary<string, string?>
    {
        ["client_id"] = settings.Google.ClientId, ["redirect_uri"] = Redirect, ["response_type"] = "code",
        ["scope"] = Scopes, ["state"] = state, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
        ["access_type"] = "offline", ["prompt"] = "consent"
    });
    public Task<TokenSet> ExchangeCode(string code, string verifier, CancellationToken ct) => http.Token(TokenEndpoint, Credentials(new()
    { ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = Redirect, ["grant_type"] = "authorization_code" }), ct);
    public Task<TokenSet> Refresh(string refreshToken, CancellationToken ct) => http.Token(TokenEndpoint, Credentials(new()
    { ["refresh_token"] = refreshToken, ["grant_type"] = "refresh_token" }), ct);
    private Dictionary<string, string> Credentials(Dictionary<string, string> data)
    { data["client_id"] = settings.Google.ClientId; data["client_secret"] = settings.Google.ClientSecret; return data; }

    public async Task<ProviderIdentity> Identify(string accessToken, CancellationToken ct)
    {
        using var doc = await http.Json("https://openidconnect.googleapis.com/v1/userinfo", accessToken, null, null, ct);
        var root = doc.RootElement;
        if (!root.TryGetProperty("email_verified", out var verified) || verified.ValueKind != JsonValueKind.True)
            throw new ProviderError("email_unverified", "Google did not verify this email address.");
        return new ProviderIdentity(root.String("sub") ?? throw new ProviderError("identity_missing", "Google did not return an account identity."),
            root.String("email") ?? throw new ProviderError("email_missing", "Google did not return an email address."), root.String("name") ?? "");
    }

    public async Task<MessagePage> List(string accessToken, string folder, string? query, int limit, string? cursor, CancellationToken ct)
    {
        if (folder == "drafts") return await ListDrafts(accessToken, query, limit, cursor, ct);
        var parameters = new Dictionary<string, string?>
        { ["maxResults"] = limit.ToString(), ["pageToken"] = cursor,
            ["q"] = folder == "archive" ? "-in:inbox -in:drafts " + query : query,
            ["includeSpamTrash"] = folder == "trash" ? "true" : null,
            ["labelIds"] = folder switch { "inbox" => "INBOX", "sent" => "SENT", "trash" => "TRASH", _ => folder.StartsWith("custom:", StringComparison.Ordinal) ? folder[7..] : null } };
        using var doc = await http.Json(QueryHelpers.AddQueryString(Api + "/messages", parameters), accessToken, null, null, ct);
        var ids = doc.RootElement.Array("messages").Select(x => x.String("id")!).ToArray();
        using var gate = new SemaphoreSlim(4);
        var messages = await Task.WhenAll(ids.Select(async id =>
        {
            await gate.WaitAsync(ct);
            try { return await GetMetadata(accessToken, id, ct); }
            finally { gate.Release(); }
        }));
        return new MessagePage(messages, doc.RootElement.String("nextPageToken"));
    }

    private async Task<MailMessage> GetMetadata(string accessToken, string id, CancellationToken ct)
    {
        using var doc = await http.Json(Api + "/messages/" + Uri.EscapeDataString(id) + "?format=metadata", accessToken, null, null, ct);
        return Map(doc.RootElement, false);
    }
    public async Task<MailMessage> Get(string accessToken, string id, CancellationToken ct)
    {
        using var doc = await http.Json(Api + "/messages/" + Uri.EscapeDataString(id) + "?format=full", accessToken, null, null, ct);
        return Map(doc.RootElement, true);
    }

    public async Task<DownloadedAttachment> Attachment(string accessToken, string messageId, string attachmentId, CancellationToken ct)
    {
        var message = await Get(accessToken, messageId, ct);
        var meta = message.Attachments.SingleOrDefault(x => x.Id == attachmentId) ?? throw new ApiError(404, "attachment_not_found", "That attachment does not belong to this email.");
        if (meta.Size > MessageValidation.MaxAttachmentBytes) throw new ApiError(413, "attachment_too_large", "This attachment exceeds the 10 MiB download limit.");
        using var doc = await http.Json(Api + "/messages/" + Uri.EscapeDataString(messageId) + "/attachments/" + Uri.EscapeDataString(attachmentId), accessToken, null, null, ct);
        return new DownloadedAttachment(Secrets.DecodeBase64Url(doc.RootElement.String("data") ?? ""), MessageValidation.SafeFilename(meta.Name), meta.ContentType);
    }

    public async Task<SendResult> Send(string accessToken, string sender, SendMailRequest request, MailMessage? reply, CancellationToken ct)
    {
        using var message = MessageValidation.Mime(sender, request, reply);
        using var stream = new MemoryStream();
        await message.WriteToAsync(stream, ct);
        using var doc = await http.Json(Api + "/messages/send", accessToken, HttpMethod.Post, new
        { raw = Secrets.Base64Url(stream.ToArray()), threadId = reply?.ThreadId }, ct);
        return new SendResult(doc.RootElement.String("id"));
    }

    public async Task<ChangePage> Changes(string accessToken, string? cursor, string? page, CancellationToken ct)
    {
        if (cursor is null)
        {
            using var profile = await http.Json(Api + "/profile", accessToken, null, null, ct);
            return new ChangePage([], null, profile.RootElement.String("historyId"));
        }
        try
        {
            using var doc = await http.Json(QueryHelpers.AddQueryString(Api + "/history", new Dictionary<string, string?>
            { ["startHistoryId"] = cursor, ["pageToken"] = page, ["maxResults"] = "100", ["historyTypes"] = "messageAdded", ["labelId"] = "INBOX" }), accessToken, null, null, ct);
            var root = doc.RootElement;
            var ids = root.Array("history").SelectMany(x => x.Array("messagesAdded")).Select(x => x.GetProperty("message").String("id")!)
                .Where(x => x is not null).Distinct().ToArray();
            return new ChangePage(ids, root.String("nextPageToken"), root.String("nextPageToken") is null ? root.String("historyId") : null);
        }
        catch (ProviderError ex) when (ex.Code == "message_not_found") { return new ChangePage([], null, null, true); }
    }

    private static MailMessage Map(JsonElement root, bool full)
    {
        var payload = root.GetProperty("payload");
        var headers = payload.Array("headers").GroupBy(x => x.String("name") ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().String("value") ?? "", StringComparer.OrdinalIgnoreCase);
        string Header(string key) => headers.GetValueOrDefault(key, "");
        MailAddress[] Addresses(string key) => InternetAddressList.TryParse(Header(key), out var list)
            ? list.Mailboxes.Select(x => new MailAddress(x.Address, x.Name)).ToArray() : [];
        var attachments = new List<AttachmentInfo>();
        var text = new List<string>(); var html = new List<string>();
        void Walk(JsonElement part)
        {
            if (part.TryGetProperty("body", out var body))
            {
                var filename = part.String("filename");
                var type = part.String("mimeType") ?? "application/octet-stream";
                var contentId = part.Array("headers").FirstOrDefault(x => x.String("name")?.Equals("Content-ID", StringComparison.OrdinalIgnoreCase) == true);
                if (!string.IsNullOrEmpty(filename) && body.String("attachmentId") is { } attachmentId)
                    attachments.Add(new AttachmentInfo(attachmentId, filename, type, body.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                        contentId.ValueKind == JsonValueKind.Object ? contentId.String("value") : null));
                else if (body.String("data") is { } data && type is "text/plain" or "text/html")
                {
                    var decoded = Encoding.UTF8.GetString(Secrets.DecodeBase64Url(data));
                    (type == "text/plain" ? text : html).Add(decoded);
                }
            }
            foreach (var partChild in part.Array("parts")) Walk(partChild);
        }
        if (full) Walk(payload);
        DateTimeOffset? date = root.String("internalDate") is { } ms && long.TryParse(ms, CultureInfo.InvariantCulture, out var milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : null;
        return new MailMessage(root.String("id") ?? "", root.String("threadId"), Header("Subject"), Addresses("From").FirstOrDefault(),
            Addresses("To"), Addresses("Cc"), date, root.String("snippet") ?? "", text.Count > 0 ? string.Join("\n", text) : null,
            html.Count > 0 ? string.Join("\n", html) : null, !root.Array("labelIds").Any(x => x.GetString() == "UNREAD"),
            attachments, Header("Message-ID"), Header("References"), Addresses("Reply-To"), Addresses("Bcc"), null, Header("In-Reply-To"));
    }
}
