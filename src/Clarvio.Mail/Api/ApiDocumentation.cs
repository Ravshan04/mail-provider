using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Clarvio.Mail.Auth;
using Clarvio.Mail.Core;
using Clarvio.Mail.Data;
using Clarvio.Mail.Providers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Clarvio.Mail.Api;

public static class ApiDocumentation
{
    private const string ResourcePrefix = "Clarvio.Mail.Api.Swagger.";
    private sealed record EndpointDoc(string Method, string Path, string Id, string Tag, string Summary,
        Type? Response = null, string? Scope = null, bool SessionOnly = false, int Status = 200, string? Details = null);
    private static readonly EndpointDoc[] Endpoints =
    [
        new("GET", "health/live", "GetLiveness", "Health", "Check whether the API process is alive", typeof(HealthResponse)),
        new("GET", "health/ready", "GetReadiness", "Health", "Check database connectivity", typeof(HealthResponse)),
        new("GET", "auth/{provider}", "StartOAuth", "Authentication", "Start provider sign-in or mailbox connection", Status: 302,
            Details: "Open this URL in a browser, not through Try it out. provider is gmail or outlook. Set mode=connect while signed in to add a mailbox to the current workspace."),
        new("GET", "auth/{provider}/callback", "CompleteOAuth", "Authentication", "Complete the provider OAuth callback", Status: 302,
            Details: "Called by the provider after consent. Requires the matching browser OAuth cookie and a single-use state. Do not construct callback requests manually."),
        new("GET", "api/v1/providers", "ListProviders", "Authentication", "List provider configuration status", typeof(ProviderResponse[])),
        new("GET", "api/v1/session", "GetSession", "Authentication", "Get the current user and CSRF token", typeof(SessionResponse), SessionOnly: true),
        new("POST", "api/v1/logout", "Logout", "Authentication", "End the current browser session", SessionOnly: true, Status: 204),
        new("GET", "api/v1/mailboxes", "ListMailboxes", "Mailboxes", "List your connected mailboxes", typeof(MailboxResponse[]), "accounts:read"),
        new("DELETE", "api/v1/mailboxes/{mailboxId}", "DisconnectMailbox", "Mailboxes", "Disconnect a mailbox and disable its webhooks", SessionOnly: true, Status: 204),
        new("GET", "api/v1/mailboxes/{mailboxId}/folders", "ListFolders", "Mailboxes", "List folders and Gmail labels", typeof(MailFolder[]), "mail:read"),
        new("GET", "api/v1/mailboxes/{mailboxId}/messages", "ListMessages", "Messages", "Browse or search messages", typeof(MessagePage), "mail:read",
            Details: "Folders: inbox, sent, drafts, archive, trash, all, or a custom ID returned by ListFolders. Gmail uses labels; Microsoft uses folders. A cursor belongs to the same mailbox, folder, query, and limit. Draft rows contain draftId for draft operations."),
        new("GET", "api/v1/mailboxes/{mailboxId}/messages/{messageId}", "GetMessage", "Messages", "Read a message", typeof(MailMessage), "mail:read"),
        new("PATCH", "api/v1/mailboxes/{mailboxId}/messages/{messageId}/read", "SetReadState", "Messages", "Mark a message read or unread", Scope: "mail:write", Status: 204),
        new("POST", "api/v1/mailboxes/{mailboxId}/messages/{messageId}/move", "MoveMessage", "Messages", "Move a message or restore it from Trash", typeof(MailMessage), "mail:write",
            Details: "Choose inbox, archive, trash, or a custom folder ID. Supply sourceFolderId when moving between Gmail labels. Drafts must be edited or sent through draft endpoints."),
        new("DELETE", "api/v1/mailboxes/{mailboxId}/messages/{messageId}", "TrashMessage", "Messages", "Move a regular message to Trash", typeof(MailMessage), "mail:write"),
        new("GET", "api/v1/mailboxes/{mailboxId}/messages/{messageId}/attachments/{attachmentId}", "DownloadAttachment", "Messages", "Download a file attachment", Scope: "mail:read",
            Details: "File download; maximum 10 MiB. Inline previews and non-file Microsoft attachment types are not supported."),
        new("POST", "api/v1/mailboxes/{mailboxId}/messages", "SendMessage", "Sending", "Send a new message", typeof(OperationResponse), "mail:send"),
        new("POST", "api/v1/mailboxes/{mailboxId}/messages/{messageId}/reply", "ReplyToMessage", "Sending", "Send a reply with optional CC, BCC, and attachments", typeof(OperationResponse), "mail:send"),
        new("GET", "api/v1/operations/{id}", "GetSendOperation", "Sending", "Inspect a send outcome", typeof(OperationResponse), "mail:send",
            Details: "Statuses: sending, accepted, failed, unknown. accepted means provider acceptance, not delivery confirmation. Check Sent mail before issuing another send for an unknown outcome."),
        new("GET", "api/v1/mailboxes/{mailboxId}/drafts/{draftId}", "GetDraft", "Drafts", "Read a draft and its retained attachments", typeof(MailMessage), "mail:read"),
        new("POST", "api/v1/mailboxes/{mailboxId}/drafts", "CreateDraft", "Drafts", "Create an unfinished message or reply draft", typeof(MailMessage), "mail:write"),
        new("PUT", "api/v1/mailboxes/{mailboxId}/drafts/{draftId}", "UpdateDraft", "Drafts", "Update draft content and attachments", typeof(MailMessage), "mail:write"),
        new("DELETE", "api/v1/mailboxes/{mailboxId}/drafts/{draftId}", "DeleteDraft", "Drafts", "Delete a draft", Scope: "mail:write", Status: 204,
            Details: "Gmail draft deletion is permanent; Microsoft deletes the draft through Graph. This endpoint refuses regular Microsoft messages."),
        new("POST", "api/v1/mailboxes/{mailboxId}/drafts/{draftId}/send", "SendDraft", "Sending", "Send a saved draft", typeof(OperationResponse), "mail:send"),
        new("GET", "api/v1/keys", "ListKeys", "API keys", "List your API keys without their secrets", typeof(KeyResponse[]), SessionOnly: true),
        new("POST", "api/v1/keys", "CreateKey", "API keys", "Create an expiring API key", typeof(CreatedKeyResponse), SessionOnly: true,
            Details: "The token is returned only once. Scopes: accounts:read, mail:read, mail:write, mail:send. Expiry: 1–365 days. At most 20 active keys."),
        new("DELETE", "api/v1/keys/{id}", "RevokeKey", "API keys", "Revoke an API key", SessionOnly: true, Status: 204),
        new("GET", "api/v1/audit", "ListAuditEvents", "Activity", "List the latest 100 activity events", typeof(AuditEvent[]), SessionOnly: true),
        new("GET", "api/v1/webhooks", "ListWebhooks", "Webhooks", "List your webhook subscriptions", typeof(WebhookResponse[]), SessionOnly: true),
        new("POST", "api/v1/webhooks", "CreateWebhook", "Webhooks", "Subscribe to mailbox change events", typeof(CreatedWebhookResponse), SessionOnly: true,
            Details: "Requires an active mailbox and an HTTPS destination with an exact hostname allowed by Mail:WebhookHosts. The signing secret is returned only once. Events start after initial synchronization."),
        new("DELETE", "api/v1/webhooks/{id}", "DisableWebhook", "Webhooks", "Disable a webhook subscription", SessionOnly: true, Status: 204),
        new("GET", "api/v1/webhook-deliveries", "ListWebhookDeliveries", "Webhooks", "List the latest 100 delivery outcomes", typeof(DeliveryResponse[]), SessionOnly: true)
    ];

    public static IServiceCollection AddMailDocumentation(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new() { Title = "Clarvio Mail API", Version = "v1", Description =
                    "Microsoft Graph and Gmail email workspace. Create a scoped API key in the workspace and enter the cm_sk_ token in Authorize. " +
                    "Session-only endpoints require sign-in at /. Swagger automatically supplies CSRF for an existing browser session. " +
                    "Try it out executes real requests: send, move, delete, and disconnect operations change the connected mailbox or service state." };
                document.Components ??= new();
                document.Tags = Endpoints.Select(x => x.Tag).Distinct().Select(name => new OpenApiTag { Name = name }).ToHashSet();
                document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
                {
                    ["ApiKey"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "cm_sk_ API key",
                        Description = "Paste the full cm_sk_ API key without the Bearer prefix. The key needs the scope documented on the operation." },
                    ["BrowserSession"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Cookie, Name = IdentityMiddleware.SessionCookie,
                        Description = "Sign in at / in this browser. The HttpOnly cookie is sent automatically; Swagger cannot set it manually." }
                };
                return Task.CompletedTask;
            });
            options.AddOperationTransformer(DescribeOperation);
        });
        return services;
    }

    public static void MapMailDocumentation(this WebApplication app, MailSettings settings)
    {
        if (!app.Environment.IsDevelopment() && !settings.EnableSwagger) return;
        app.MapOpenApi();
        app.MapGet("/swagger/clarvio-init.js", () => Results.Stream(Resource("clarvio-init.js"), "text/javascript"))
            .ExcludeFromDescription();
        app.UseSwaggerUI(options =>
        {
            options.RoutePrefix = "swagger";
            options.IndexStream = () => Resource("index.html");
        });
    }
    private static Stream Resource(string name) => typeof(ApiDocumentation).Assembly.GetManifestResourceStream(ResourcePrefix + name)
        ?? throw new InvalidOperationException("Swagger resource missing.");

    private static async Task DescribeOperation(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken ct)
    {
        var path = Regex.Replace((context.Description.RelativePath ?? "").TrimStart('/'), @"\{([^}:]+):[^}]+\}", "{$1}");
        var doc = Endpoints.SingleOrDefault(x => x.Method == context.Description.HttpMethod && x.Path == path)
            ?? throw new InvalidOperationException("Missing API documentation for " + context.Description.HttpMethod + " " + path);
        operation.OperationId = doc.Id; operation.Summary = doc.Summary;
        operation.Tags = new HashSet<OpenApiTagReference> { new(doc.Tag, context.Document) };
        operation.Description = doc.Details ?? doc.Summary + ".";
        var authenticated = doc.Scope is not null || doc.SessionOnly;
        if (authenticated)
        {
            operation.Description += doc.SessionOnly ? " Browser session required; API keys cannot access this endpoint." : " API key scope: " + doc.Scope + ". A browser session is also accepted.";
            operation.Security = [new() { [new OpenApiSecuritySchemeReference("BrowserSession", context.Document)] = [] }];
            if (!doc.SessionOnly) operation.Security.Add(new() { [new OpenApiSecuritySchemeReference("ApiKey", context.Document)] = [] });
        }
        else operation.Security = [];

        operation.Parameters ??= [];
        if (authenticated && doc.Method is not ("GET" or "HEAD"))
            operation.Parameters.Add(Header("X-CSRF-Token", "Required only for browser-session requests. Swagger fetches it automatically from /api/v1/session; API key requests do not need it.", false));
        if (doc.Scope == "mail:send" && doc.Method == "POST")
        {
            var header = Header("Idempotency-Key", "Required: 16–128 characters. Use a unique key for a new send intent, and reuse it when retrying that same send. Do not issue a new key for an unknown outcome until checking Sent mail.", true);
            header.Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 16, MaxLength = 128 };
            header.Example = JsonValue.Create("clarvio-demo-send-0001"); operation.Parameters.Add(header);
            operation.Description += " Response status is sending, accepted, failed, or unknown. HTTP 200 alone does not indicate provider acceptance.";
        }
        foreach (var parameter in operation.Parameters.OfType<OpenApiParameter>())
        {
            parameter.Description ??= parameter.Name switch
            {
                "mailboxId" => "Mailbox UUID from GET /api/v1/mailboxes, owned by the signed-in user.",
                "messageId" => "Provider message ID from the message list.",
                "draftId" => "draftId returned by draft creation or listing. Gmail draft IDs differ from message IDs.",
                "attachmentId" => "Attachment ID from the parent message.",
                "folder" => "inbox (default), sent, drafts, archive, trash, all, or a custom ID from ListFolders.",
                "limit" => "Messages per page: 1–50, default 25.",
                "query" => "Provider search query, up to 300 characters.",
                "cursor" => "Opaque nextCursor from the previous page; expires after 30 minutes. Keep the same folder, query, and limit.",
                "provider" => "gmail or outlook.",
                _ => null
            };
        }
        if (doc.Id is "SendMessage" or "ReplyToMessage")
            operation.Description += " Up to 50 recipients, 10 attachments, and 10 MiB of attachment content combined. Attachment content is base64. A To address and subject are required.";
        if (doc.Id is "CreateDraft" or "UpdateDraft")
            operation.Description += " Empty To and subject are allowed for drafts. Omit keepAttachmentIds to retain existing files; use [] to remove all; supply IDs to keep selected files. New attachments go in message.attachments. replyMessageId is for new reply drafts.";

        var example = doc.Id switch
        {
            "SendMessage" or "ReplyToMessage" => """{"to":[{"email":"recipient@example.com","name":"Recipient"}],"subject":"Example message","textBody":"Hello from Clarvio Mail","cc":[],"bcc":[],"attachments":[]}""",
            "CreateDraft" => """{"message":{"to":[],"subject":"","textBody":"Unfinished message","cc":[],"bcc":[],"attachments":[]},"keepAttachmentIds":[]}""",
            "UpdateDraft" => """{"message":{"to":[{"email":"recipient@example.com"}],"subject":"Draft subject","textBody":"Updated draft","cc":[],"bcc":[],"attachments":[]}}""",
            "SetReadState" => """{"isRead":true}""",
            "MoveMessage" => """{"destinationFolderId":"archive","sourceFolderId":"inbox"}""",
            "CreateKey" => """{"name":"Read-only integration","scopes":["accounts:read","mail:read"],"expiresInDays":90}""",
            "CreateWebhook" => """{"mailboxId":"00000000-0000-0000-0000-000000000001","url":"https://hooks.example.com/clarvio-mail"}""",
            _ => null
        };
        if (example is not null && operation.RequestBody is OpenApiRequestBody { Content: { } content })
            foreach (var media in content.Values) media.Example = JsonNode.Parse(example);

        operation.Responses = new();
        var success = new OpenApiResponse { Description = doc.Status switch { 204 => "Completed; no response body.", 302 => "Browser redirect.", _ => "Request completed. Check operation.status for send outcomes." } };
        if (doc.Response is not null)
            success.Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = await context.GetOrCreateSchemaAsync(doc.Response, null, ct) } };
        if (doc.Id == "DownloadAttachment")
            success.Content = new Dictionary<string, OpenApiMediaType> { ["application/octet-stream"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" } } };
        if (doc.Status == 302)
            success.Headers = new Dictionary<string, IOpenApiHeader> { ["Location"] = new OpenApiHeader { Description = "Browser redirect target.", Schema = new OpenApiSchema { Type = JsonSchemaType.String } } };
        operation.Responses[doc.Status.ToString()] = success;
        if (doc.Id == "GetReadiness")
        { operation.Responses["503"] = new OpenApiResponse { Description = "Database unavailable; returns {\"status\":\"unavailable\"}." }; return; }
        if (doc.Tag == "Health") return;
        var errorSchema = await context.GetOrCreateSchemaAsync(typeof(ErrorResponse), null, ct);
        foreach (var (status, description) in new[] { (400, "Invalid request or validation error."), (401, "Sign in or provide a valid API key."),
            (403, "Missing scope, browser session, CSRF, or account admission permission."), (404, "Resource not found or not owned by this user."),
            (409, "Reconnect required, idempotency conflict, or resource state conflict."), (413, "Request or attachment too large."),
            (422, "Unsupported provider operation or resource."), (429, "Rate limit reached; inspect Retry-After."), (500, "Internal error; retain traceId for diagnostics."), (502, "Provider could not complete the request.") })
            operation.Responses[status.ToString()] = new OpenApiResponse { Description = description,
                Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = errorSchema } } };
    }
    private static OpenApiParameter Header(string name, string description, bool required) => new()
    { Name = name, In = ParameterLocation.Header, Required = required, Description = description, Schema = new OpenApiSchema { Type = JsonSchemaType.String } };
}
