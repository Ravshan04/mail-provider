using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Clarvio.Mail.Core;
using Microsoft.AspNetCore.WebUtilities;

namespace Clarvio.Mail.Providers;

public sealed partial class MicrosoftProvider(MailSettings settings, ProviderHttp http) : IEmailProvider
{
    private const string Graph = "https://graph.microsoft.com/v1.0";
    private const string Scopes = "https://graph.microsoft.com/User.Read https://graph.microsoft.com/Mail.ReadWrite https://graph.microsoft.com/Mail.Send offline_access";
    private string Authority => $"https://login.microsoftonline.com/{settings.Microsoft.Tenant}/oauth2/v2.0";
    private string Redirect => settings.PublicUrl + "/auth/outlook/callback";
    public string Name => "outlook";
    public bool Configured => settings.Microsoft.Enabled;

    public string AuthorizationUrl(string state, string challenge) => QueryHelpers.AddQueryString(Authority + "/authorize", new Dictionary<string, string?>
    {
        ["client_id"] = settings.Microsoft.ClientId, ["redirect_uri"] = Redirect,
        ["response_type"] = "code", ["response_mode"] = "query", ["scope"] = Scopes,
        ["state"] = state, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["prompt"] = "select_account"
    });

    public Task<TokenSet> ExchangeCode(string code, string verifier, CancellationToken ct) => http.Token(Authority + "/token", Credentials(new()
    {
        ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = Redirect,
        ["scope"] = Scopes, ["grant_type"] = "authorization_code"
    }), ct);
    public Task<TokenSet> Refresh(string refreshToken, CancellationToken ct) => http.Token(Authority + "/token", Credentials(new()
    {
        ["refresh_token"] = refreshToken, ["grant_type"] = "refresh_token", ["scope"] = Scopes
    }), ct);

    private Dictionary<string, string> Credentials(Dictionary<string, string> fields)
    {
        fields["client_id"] = settings.Microsoft.ClientId;
        if (string.IsNullOrWhiteSpace(settings.Microsoft.CertificatePath)) fields["client_secret"] = settings.Microsoft.ClientSecret;
        else
        {
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(settings.Microsoft.CertificatePath,
                settings.Microsoft.CertificatePassword, X509KeyStorageFlags.EphemeralKeySet);
            using var rsa = certificate.GetRSAPrivateKey() ?? throw new InvalidOperationException("Microsoft OAuth certificate has no RSA private key.");
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var header = Secrets.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            { ["alg"] = "PS256", ["typ"] = "JWT", ["x5t#S256"] = Secrets.Base64Url(certificate.GetCertHash(HashAlgorithmName.SHA256)) }));
            var payload = Secrets.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
            { aud = Authority + "/token", iss = settings.Microsoft.ClientId, sub = settings.Microsoft.ClientId,
                jti = Guid.NewGuid().ToString(), nbf = now - 30, iat = now, exp = now + 300 }));
            var unsigned = header + "." + payload;
            fields["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
            fields["client_assertion"] = unsigned + "." + Secrets.Base64Url(rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        }
        return fields;
    }

    public async Task<ProviderIdentity> Identify(string accessToken, CancellationToken ct)
    {
        using var doc = await http.Json(Graph + "/me?$select=id,mail,userPrincipalName,displayName", accessToken, null, null, ct);
        var root = doc.RootElement;
        return new ProviderIdentity(root.String("id") ?? throw new ProviderError("identity_missing", "Microsoft did not return an account identity."),
            root.String("mail") ?? root.String("userPrincipalName") ?? throw new ProviderError("email_missing", "Microsoft did not return a mailbox address."),
            root.String("displayName") ?? "");
    }

    public async Task<MessagePage> List(string accessToken, string folder, string? query, int limit, string? cursor, CancellationToken ct)
    {
        var path = folder == "all" ? "/me/messages" : "/me/mailFolders/" + Uri.EscapeDataString(NativeFolder(folder)) + "/messages";
        var url = cursor is null ? QueryHelpers.AddQueryString(Graph + path, new Dictionary<string, string?>
        {
            ["$top"] = limit.ToString(), ["$select"] = "id,conversationId,subject,from,toRecipients,ccRecipients,bccRecipients,receivedDateTime,bodyPreview,isRead,isDraft,hasAttachments,internetMessageId",
            ["$orderby"] = string.IsNullOrEmpty(query) ? "receivedDateTime desc" : null,
            ["$search"] = string.IsNullOrEmpty(query) ? null : "\"" + query.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
        }) : ValidateCursor(cursor);
        using var doc = await http.Json(url, accessToken, null, null, ct);
        return new MessagePage(doc.RootElement.Array("value").Select(x => Map(x, [])).ToArray(), doc.RootElement.String("@odata.nextLink"));
    }

    public async Task<MailMessage> Get(string accessToken, string id, CancellationToken ct)
    {
        using var doc = await http.Json(Graph + "/me/messages/" + Uri.EscapeDataString(id) + "?$expand=attachments($select=id,name,contentType,size)&$select=id,conversationId,subject,from,toRecipients,ccRecipients,bccRecipients,replyTo,receivedDateTime,bodyPreview,body,isRead,isDraft,internetMessageId", accessToken, null, null, ct);
        var root = doc.RootElement;
        var attachments = root.Array("attachments").Where(x => x.String("@odata.type") == "#microsoft.graph.fileAttachment")
            .Select(x => new AttachmentInfo(x.String("id") ?? "", x.String("name") ?? "attachment", x.String("contentType") ?? "application/octet-stream",
                x.TryGetProperty("size", out var s) ? s.GetInt64() : 0, x.String("contentId"))).ToArray();
        return Map(root, attachments);
    }

    public async Task<DownloadedAttachment> Attachment(string accessToken, string messageId, string attachmentId, CancellationToken ct)
    {
        using var doc = await http.Json(Graph + "/me/messages/" + Uri.EscapeDataString(messageId) + "/attachments/" + Uri.EscapeDataString(attachmentId), accessToken, null, null, ct);
        var root = doc.RootElement;
        if (root.String("@odata.type") != "#microsoft.graph.fileAttachment")
            throw new ApiError(422, "unsupported_attachment", "This attachment type is not supported.");
        return new DownloadedAttachment(Convert.FromBase64String(root.String("contentBytes") ?? ""),
            MessageValidation.SafeFilename(root.String("name") ?? "attachment"), root.String("contentType") ?? "application/octet-stream");
    }

    public async Task<SendResult> Send(string accessToken, string sender, SendMailRequest request, MailMessage? reply, CancellationToken ct)
    {
        object Addresses(IEnumerable<MailAddress> addresses) => addresses.Select(a => new { emailAddress = new { address = a.Email, name = a.Name } }).ToArray();
        var message = new
        {
            subject = reply?.Subject ?? request.Subject,
            body = new { contentType = request.HtmlBody is null ? "Text" : "HTML", content = request.HtmlBody ?? request.TextBody },
            toRecipients = Addresses(request.To), ccRecipients = Addresses(request.Cc ?? []), bccRecipients = Addresses(request.Bcc ?? []),
            attachments = (request.Attachments ?? []).Select(a => new Dictionary<string, object>
            { ["@odata.type"] = "#microsoft.graph.fileAttachment", ["name"] = a.Name, ["contentType"] = a.ContentType, ["contentBytes"] = a.ContentBase64 }).ToArray()
        };
        var url = reply is null ? Graph + "/me/sendMail" : Graph + "/me/messages/" + Uri.EscapeDataString(reply.Id) + "/reply";
        object body = reply is null ? new { message, saveToSentItems = true } : new { message };
        using var result = await http.Json(url, accessToken, HttpMethod.Post, body, ct);
        return new SendResult(null); // Graph 202 is acceptance, not delivery, and does not return a message id.
    }

    public async Task<ChangePage> Changes(string accessToken, string? cursor, string? page, CancellationToken ct)
    {
        var url = page ?? cursor ?? Graph + "/me/mailFolders/inbox/messages/delta?$select=id&$top=100";
        try
        {
            using var doc = await http.Json(ValidateCursor(url), accessToken, null, null, ct);
            var root = doc.RootElement;
            return new ChangePage(root.Array("value").Where(x => !x.TryGetProperty("@removed", out _))
                .Select(x => x.String("id")!).Where(x => x is not null).ToArray(), root.String("@odata.nextLink"), root.String("@odata.deltaLink"));
        }
        catch (ProviderError ex) when (ex.Code == "sync_expired") { return new ChangePage([], null, null, true); }
    }

    public static string ValidateCursor(string cursor)
    {
        if (!Uri.TryCreate(cursor, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort ||
            !string.Equals(uri.Host, "graph.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith("/v1.0/me/", StringComparison.Ordinal) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ApiError(400, "invalid_cursor", "That pagination cursor is invalid.");
        return cursor;
    }

    private static MailMessage Map(JsonElement root, AttachmentInfo[] attachments)
    {
        string? text = null, html = null;
        if (root.TryGetProperty("body", out var body))
        { if (body.String("contentType")?.Equals("html", StringComparison.OrdinalIgnoreCase) == true) html = body.String("content"); else text = body.String("content"); }
        var from = root.TryGetProperty("from", out var fromJson) ? Address(fromJson) : null;
        DateTimeOffset? at = DateTimeOffset.TryParse(root.String("receivedDateTime"), out var date) ? date : null;
        return new MailMessage(root.String("id") ?? "", root.String("conversationId"), root.String("subject") ?? "", from,
            root.Array("toRecipients").Select(Address).OfType<MailAddress>().ToArray(), root.Array("ccRecipients").Select(Address).OfType<MailAddress>().ToArray(),
            at, root.String("bodyPreview") ?? "", text, html, root.TryGetProperty("isRead", out var isRead) ? isRead.GetBoolean() : null,
            attachments, root.String("internetMessageId"), null, root.Array("replyTo").Select(Address).OfType<MailAddress>().ToArray(),
            root.Array("bccRecipients").Select(Address).OfType<MailAddress>().ToArray(), root.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True ? root.String("id") : null);
    }
    private static MailAddress? Address(JsonElement value) => value.TryGetProperty("emailAddress", out var a) && a.String("address") is { } email
        ? new MailAddress(email, a.String("name")) : null;
}
