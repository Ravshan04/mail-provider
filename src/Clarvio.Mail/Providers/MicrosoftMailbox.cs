using System.Text.Json;
using Clarvio.Mail.Core;

namespace Clarvio.Mail.Providers;

public sealed partial class MicrosoftProvider
{
    private static string NativeFolder(string folder) => folder switch
    {
        "inbox" => "inbox", "sent" => "sentitems", "drafts" => "drafts", "trash" => "deleteditems", "archive" => "archive",
        _ when folder.StartsWith("custom:", StringComparison.Ordinal) => folder[7..],
        _ => throw new ApiError(400, "invalid_folder", "That folder is not supported.")
    };
    public async Task<IReadOnlyList<MailFolder>> Folders(string token, CancellationToken ct)
    {
        var standard = new[] { new MailFolder("inbox", "Inbox"), new("sent", "Sent", false), new("drafts", "Drafts", false), new("trash", "Trash"), new("archive", "Archive") };
        var nativeIds = new Dictionary<string, MailFolder>();
        foreach (var folder in standard)
        {
            try
            { using var doc = await http.Json(Graph + "/me/mailFolders/" + NativeFolder(folder.Id) + "?$select=id", token, null, null, ct); nativeIds[doc.RootElement.String("id")!] = folder; }
            catch (ProviderError ex) when (ex.Code == "message_not_found" && folder.Id == "archive") { }
        }
        var result = new List<MailFolder>(standard.Where(folder => folder.Id != "archive" || nativeIds.Values.Any(x => x.Id == "archive"))) { new("all", "All mail", false) };
        var queue = new Queue<(string Path, string Prefix)>(); queue.Enqueue(("/me/mailFolders", ""));
        var seen = new HashSet<string>();
        while (queue.TryDequeue(out var item))
        {
            string? url = Graph + item.Path + "?$top=100&$select=id,displayName,childFolderCount";
            while (url is not null)
            {
                using var doc = await http.Json(ValidateCursor(url), token, null, null, ct);
                foreach (var value in doc.RootElement.Array("value"))
                {
                    var id = value.String("id")!; if (!seen.Add(id)) continue;
                    if (seen.Count > 500) throw new ApiError(422, "folder_limit", "This mailbox exceeds the supported 500-folder limit.");
                    var name = item.Prefix + (value.String("displayName") ?? "Folder");
                    if (!nativeIds.ContainsKey(id)) result.Add(new("custom:" + id, name));
                    if (value.TryGetProperty("childFolderCount", out var count) && count.GetInt32() > 0)
                        queue.Enqueue(("/me/mailFolders/" + Uri.EscapeDataString(id) + "/childFolders", name + " / "));
                }
                url = doc.RootElement.String("@odata.nextLink");
            }
        }
        return result;
    }
    public async Task SetRead(string token, string id, bool isRead, CancellationToken ct)
    { using var response = await http.Json(Graph + "/me/messages/" + Uri.EscapeDataString(id), token, HttpMethod.Patch, new { isRead }, ct); }
    public async Task<MailMessage> Move(string token, string id, string destination, string? source, CancellationToken ct)
    {
        if ((await Get(token, id, ct)).DraftId is not null) throw new ApiError(409, "draft_move_unsupported", "Send or edit the draft before moving it.");
        using var response = await http.Json(Graph + "/me/messages/" + Uri.EscapeDataString(id) + "/move", token, HttpMethod.Post, new { destinationId = NativeFolder(destination) }, ct);
        return Map(response.RootElement, []);
    }
    public async Task<MailMessage> GetDraft(string token, string id, CancellationToken ct)
    {
        var message = await Get(token, id, ct);
        if (message.DraftId is null) throw new ApiError(409, "not_a_draft", "This message is no longer a draft.");
        return message;
    }
    private static object GraphAttachment(OutgoingAttachment file) => new Dictionary<string, object>
    { ["@odata.type"] = "#microsoft.graph.fileAttachment", ["name"] = file.Name, ["contentType"] = file.ContentType, ["contentBytes"] = file.ContentBase64 };
    private static Dictionary<string, object> DraftBody(SendMailRequest message) => new()
    {
        ["subject"] = message.Subject,
        ["body"] = new { contentType = message.HtmlBody is null ? "Text" : "HTML", content = message.HtmlBody ?? message.TextBody },
        ["toRecipients"] = message.To.Select(x => new { emailAddress = new { address = x.Email, name = x.Name } }).ToArray(),
        ["ccRecipients"] = (message.Cc ?? []).Select(x => new { emailAddress = new { address = x.Email, name = x.Name } }).ToArray(),
        ["bccRecipients"] = (message.Bcc ?? []).Select(x => new { emailAddress = new { address = x.Email, name = x.Name } }).ToArray()
    };
    public async Task<MailMessage> SaveDraft(string token, string sender, string? id, SaveDraftRequest request, CancellationToken ct)
    {
        var body = DraftBody(request.Message);
        if (id is null)
        {
            DraftAttachments.Keep(null, request.KeepAttachmentIds);
            body["attachments"] = (request.Message.Attachments ?? []).Select(GraphAttachment).ToArray();
            var url = request.ReplyMessageId is null ? Graph + "/me/messages" : Graph + "/me/messages/" + Uri.EscapeDataString(request.ReplyMessageId) + "/createReply";
            using var response = await http.Json(url, token, HttpMethod.Post, request.ReplyMessageId is null ? body : new { message = body }, ct);
            return await GetDraft(token, response.RootElement.String("id") ?? throw new ProviderError("draft_missing", "Microsoft did not return a draft ID."), ct);
        }
        var existing = await GetDraft(token, id, ct); var keep = DraftAttachments.Keep(existing, request.KeepAttachmentIds);
        if (keep.Sum(x => x.Size) + (request.Message.Attachments ?? []).Sum(x => (long)Convert.FromBase64String(x.ContentBase64).Length) > MessageValidation.MaxAttachmentBytes || keep.Count + (request.Message.Attachments?.Count ?? 0) > 10)
            throw new ApiError(400, "invalid_attachments", "Use at most 10 attachments, up to 2 MiB combined.");
        var path = Graph + "/me/messages/" + Uri.EscapeDataString(id);
        using var update = await http.Json(path, token, HttpMethod.Patch, body, ct);
        foreach (var file in existing.Attachments.Where(x => !keep.Any(k => k.Id == x.Id)))
        { using var delete = await http.Json(path + "/attachments/" + Uri.EscapeDataString(file.Id), token, HttpMethod.Delete, null, ct); }
        foreach (var file in request.Message.Attachments ?? [])
        { using var add = await http.Json(path + "/attachments", token, HttpMethod.Post, GraphAttachment(file), ct); }
        return await GetDraft(token, id, ct);
    }
    public async Task<SendResult> SendDraft(string token, string id, CancellationToken ct)
    { using var response = await http.Json(Graph + "/me/messages/" + Uri.EscapeDataString(id) + "/send", token, HttpMethod.Post, new { }, ct); return new(null); }
    public async Task DeleteDraft(string token, string id, CancellationToken ct)
    { await GetDraft(token, id, ct); using var response = await http.Json(Graph + "/me/messages/" + Uri.EscapeDataString(id), token, HttpMethod.Delete, null, ct); }
}
