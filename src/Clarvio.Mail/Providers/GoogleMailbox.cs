using System.Text.Json;
using Clarvio.Mail.Core;
using Microsoft.AspNetCore.WebUtilities;

namespace Clarvio.Mail.Providers;

public sealed partial class GoogleProvider
{
    public async Task<IReadOnlyList<MailFolder>> Folders(string token, CancellationToken ct)
    {
        using var doc = await http.Json(Api + "/labels", token, null, null, ct);
        return [new("inbox", "Inbox"), new("sent", "Sent", false), new("drafts", "Drafts", false), new("archive", "Archive"),
            new("trash", "Trash"), new("all", "All mail", false),
            .. doc.RootElement.Array("labels").Where(x => x.String("type") == "user")
                .OrderBy(x => x.String("name")).Select(x => new MailFolder("custom:" + x.String("id"), x.String("name") ?? "Label"))];
    }
    public async Task SetRead(string token, string id, bool isRead, CancellationToken ct)
    {
        using var response = await http.Json(Api + "/messages/" + Uri.EscapeDataString(id) + "/modify", token, HttpMethod.Post,
            new { addLabelIds = isRead ? Array.Empty<string>() : ["UNREAD"], removeLabelIds = isRead ? ["UNREAD"] : Array.Empty<string>() }, ct);
    }
    public async Task<MailMessage> Move(string token, string id, string destination, string? source, CancellationToken ct)
    {
        using var before = await http.Json(Api + "/messages/" + Uri.EscapeDataString(id) + "?format=minimal", token, null, null, ct);
        var labels = before.RootElement.Array("labelIds").Select(x => x.GetString()).ToHashSet();
        if (labels.Contains("DRAFT")) throw new ApiError(409, "draft_move_unsupported", "Send or edit the draft before moving it.");
        var endpoint = Api + "/messages/" + Uri.EscapeDataString(id);
        if (destination == "trash")
        { using var trash = await http.Json(endpoint + "/trash", token, HttpMethod.Post, new { }, ct); }
        else
        {
            if (labels.Contains("TRASH")) { using var restore = await http.Json(endpoint + "/untrash", token, HttpMethod.Post, new { }, ct); }
            var add = destination == "inbox" ? new[] { "INBOX" } : destination.StartsWith("custom:", StringComparison.Ordinal) ? [destination[7..]] : [];
            var remove = new List<string>();
            if (destination != "inbox") remove.Add("INBOX");
            if (source?.StartsWith("custom:", StringComparison.Ordinal) == true && source != destination) remove.Add(source[7..]);
            using var modify = await http.Json(endpoint + "/modify", token, HttpMethod.Post, new { addLabelIds = add, removeLabelIds = remove }, ct);
        }
        return await Get(token, id, ct);
    }
    private async Task<MessagePage> ListDrafts(string token, string? query, int limit, string? cursor, CancellationToken ct)
    {
        using var doc = await http.Json(QueryHelpers.AddQueryString(Api + "/drafts", new Dictionary<string, string?>
        { ["maxResults"] = limit.ToString(), ["pageToken"] = cursor, ["q"] = query }), token, null, null, ct);
        var rows = doc.RootElement.Array("drafts").Select(x => (Draft: x.String("id")!, Message: x.GetProperty("message").String("id")!)).ToArray();
        using var gate = new SemaphoreSlim(4);
        var messages = await Task.WhenAll(rows.Select(async row =>
        { await gate.WaitAsync(ct); try { return (await GetMetadata(token, row.Message, ct)) with { DraftId = row.Draft }; } finally { gate.Release(); } }));
        return new(messages, doc.RootElement.String("nextPageToken"));
    }
    public async Task<MailMessage> GetDraft(string token, string id, CancellationToken ct)
    {
        using var doc = await http.Json(Api + "/drafts/" + Uri.EscapeDataString(id) + "?format=full", token, null, null, ct);
        return Map(doc.RootElement.GetProperty("message"), true) with { DraftId = doc.RootElement.String("id") };
    }
    public async Task<MailMessage> SaveDraft(string token, string sender, string? id, SaveDraftRequest request, CancellationToken ct)
    {
        var existing = id is null ? null : await GetDraft(token, id, ct);
        var keep = DraftAttachments.Keep(existing, request.KeepAttachmentIds);
        var files = new List<OutgoingAttachment>(request.Message.Attachments ?? []);
        foreach (var attachment in keep)
        {
            var file = await Attachment(token, existing!.Id, attachment.Id, ct);
            files.Add(new(file.Name, file.ContentType, Convert.ToBase64String(file.Bytes)));
        }
        var messageRequest = request.Message with { Attachments = files };
        MessageValidation.Validate(messageRequest, draft: true);
        var reply = id is null && request.ReplyMessageId is not null ? await Get(token, request.ReplyMessageId, ct) : null;
        using var message = MessageValidation.Mime(sender, messageRequest, reply);
        if (!string.IsNullOrWhiteSpace(existing?.InReplyTo)) message.InReplyTo = existing.InReplyTo.Trim('<', '>');
        if (!string.IsNullOrWhiteSpace(existing?.References))
            foreach (var reference in MimeKit.Utils.MimeUtils.EnumerateReferences(existing.References)) message.References.Add(reference);
        using var stream = new MemoryStream(); await message.WriteToAsync(stream, ct);
        var body = new { message = new { raw = Secrets.Base64Url(stream.ToArray()), threadId = existing?.ThreadId ?? reply?.ThreadId } };
        using var response = await http.Json(Api + "/drafts" + (id is null ? "" : "/" + Uri.EscapeDataString(id)), token,
            id is null ? HttpMethod.Post : HttpMethod.Put, body, ct);
        return await GetDraft(token, response.RootElement.String("id") ?? id ?? throw new ProviderError("draft_missing", "The provider did not return a draft ID."), ct);
    }
    public async Task<SendResult> SendDraft(string token, string id, CancellationToken ct)
    {
        using var response = await http.Json(Api + "/drafts/send", token, HttpMethod.Post, new { id }, ct);
        return new(response.RootElement.String("id"));
    }
    public async Task DeleteDraft(string token, string id, CancellationToken ct)
    { using var response = await http.Json(Api + "/drafts/" + Uri.EscapeDataString(id), token, HttpMethod.Delete, null, ct); }
}

public static class DraftAttachments
{
    public static IReadOnlyList<AttachmentInfo> Keep(MailMessage? existing, IReadOnlyList<string>? ids)
    {
        if (ids?.Count > 10 || ids?.Any(string.IsNullOrWhiteSpace) == true || ids?.Distinct().Count() != ids?.Count)
            throw new ApiError(400, "invalid_attachments", "The retained draft attachments are invalid.");
        var keep = ids ?? existing?.Attachments.Select(x => x.Id).ToArray() ?? [];
        var result = existing?.Attachments.Where(x => keep.Contains(x.Id)).ToArray() ?? [];
        if (result.Length != keep.Count) throw new ApiError(400, "invalid_attachments", "An attachment does not belong to this draft.");
        return result;
    }
}
