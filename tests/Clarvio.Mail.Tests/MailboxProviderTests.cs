using System.Net;
using System.Text;
using System.Text.Json;
using Clarvio.Mail.Core;
using Clarvio.Mail.Providers;
using MimeKit;

namespace Clarvio.Mail.Tests;

public sealed class MailboxProviderTests
{
    private const string GoogleMessage = """
        {"id":"message-one","threadId":"thread-one","labelIds":["DRAFT"],"payload":{"mimeType":"text/plain","headers":[
          {"name":"Subject","value":"Subject"},{"name":"To","value":"to@example.com"},
          {"name":"Bcc","value":"hidden@example.com"},{"name":"Message-ID","value":"<message@example.com>"}],"body":{"data":"SGVsbG8"}}}
        """;
    private const string GraphDraft = """
        {"id":"draft-one","isDraft":true,"subject":"Subject","body":{"contentType":"text","content":"Hello"},
         "toRecipients":[{"emailAddress":{"address":"to@example.com"}}],
         "bccRecipients":[{"emailAddress":{"address":"hidden@example.com"}}],"attachments":[]}
        """;

    [Fact]
    public async Task Gmail_restore_uses_untrash_and_changes_only_the_source_label()
    {
        var handler = new ScriptedHandler([
            new(HttpMethod.Get, "/messages/message-one", "{\"labelIds\":[\"TRASH\",\"Label_old\",\"Label_unrelated\"]}"),
            new(HttpMethod.Post, "/messages/message-one/untrash", "{}"),
            new(HttpMethod.Post, "/messages/message-one/modify", "{}"),
            new(HttpMethod.Get, "/messages/message-one", GoogleMessage)
        ]);
        var provider = new GoogleProvider(SecurityTests.Settings(), new(new Clients(handler)));
        await provider.Move("fake-token", "message-one", "custom:Label_new", "custom:Label_old", default);
        using var body = JsonDocument.Parse(handler.Requests[2].Body!);
        Assert.Equal(new[] { "Label_new" }, body.RootElement.GetProperty("addLabelIds").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal(new[] { "INBOX", "Label_old" }, body.RootElement.GetProperty("removeLabelIds").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.DoesNotContain(handler.Requests, x => x.Method == HttpMethod.Delete);
        handler.AssertComplete();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Read_status_is_translated_for_both_providers(bool isRead)
    {
        var google = new ScriptedHandler([new(HttpMethod.Post, "/messages/m/modify", "{}")]);
        await new GoogleProvider(SecurityTests.Settings(), new(new Clients(google))).SetRead("fake", "m", isRead, default);
        using var g = JsonDocument.Parse(google.Requests.Single().Body!);
        Assert.Equal(isRead ? 0 : 1, g.RootElement.GetProperty("addLabelIds").GetArrayLength());
        Assert.Equal(isRead ? 1 : 0, g.RootElement.GetProperty("removeLabelIds").GetArrayLength());
        var microsoft = new ScriptedHandler([new(HttpMethod.Patch, "/me/messages/m", "{}")]);
        await new MicrosoftProvider(SecurityTests.Settings(), new(new Clients(microsoft))).SetRead("fake", "m", isRead, default);
        using var m = JsonDocument.Parse(microsoft.Requests.Single().Body!);
        Assert.Equal(isRead, m.RootElement.GetProperty("isRead").GetBoolean());
    }

    [Fact]
    public async Task Gmail_draft_preserves_reply_thread_and_bcc_without_sending()
    {
        var source = GoogleMessage.Replace("\"DRAFT\"", "\"INBOX\"");
        var handler = new ScriptedHandler([
            new(HttpMethod.Get, "/messages/message-one", source),
            new(HttpMethod.Post, "/drafts", "{\"id\":\"stable-draft\"}"),
            new(HttpMethod.Get, "/drafts/stable-draft", "{\"id\":\"stable-draft\",\"message\":" + GoogleMessage + "}")
        ]);
        var provider = new GoogleProvider(SecurityTests.Settings(), new(new Clients(handler)));
        var request = new SendMailRequest([new("to@example.com")], "Subject", "Hello", Bcc: [new("hidden@example.com")]);
        var result = await provider.SaveDraft("fake", "sender@example.com", null, new(request, ReplyMessageId: "message-one"), default);
        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        var message = body.RootElement.GetProperty("message");
        Assert.Equal("thread-one", message.GetProperty("threadId").GetString());
        using var mime = MimeMessage.Load(new MemoryStream(Secrets.DecodeBase64Url(message.GetProperty("raw").GetString()!)));
        Assert.Equal("message@example.com", mime.InReplyTo);
        Assert.Equal("hidden@example.com", mime.Bcc.Mailboxes.Single().Address);
        Assert.Equal("stable-draft", result.DraftId); Assert.Equal("message-one", result.Id);
        Assert.Equal("hidden@example.com", result.Bcc!.Single().Email);
        handler.AssertComplete();
    }

    [Fact]
    public async Task Gmail_replacing_a_draft_downloads_and_retains_selected_attachments()
    {
        var existing = """
            {"id":"message-before","threadId":"thread-one","payload":{"mimeType":"multipart/mixed","headers":[
              {"name":"In-Reply-To","value":"<original@example.com>"},{"name":"References","value":"<earlier@example.com> <original@example.com>"}],
              "parts":[{"filename":"retained.txt","mimeType":"text/plain","body":{"attachmentId":"attachment-one","size":4}},
                       {"filename":"removed.txt","mimeType":"text/plain","body":{"attachmentId":"attachment-two","size":4}}]}}
            """;
        var handler = new ScriptedHandler([
            new(HttpMethod.Get, "/drafts/stable-draft", "{\"id\":\"stable-draft\",\"message\":" + existing + "}"),
            new(HttpMethod.Get, "/messages/message-before", existing),
            new(HttpMethod.Get, "/messages/message-before/attachments/attachment-one", "{\"data\":\"a2VlcA\"}"),
            new(HttpMethod.Put, "/drafts/stable-draft", "{\"id\":\"stable-draft\"}"),
            new(HttpMethod.Get, "/drafts/stable-draft", "{\"id\":\"stable-draft\",\"message\":" + GoogleMessage + "}")
        ]);
        var request = new SaveDraftRequest(new([], "", "unfinished", Attachments: [new("new.txt", "text/plain", "bmV3")]), ["attachment-one"]);
        await new GoogleProvider(SecurityTests.Settings(), new(new Clients(handler))).SaveDraft("fake", "sender@example.com", "stable-draft", request, default);
        using var body = JsonDocument.Parse(handler.Requests[3].Body!);
        using var mime = MimeMessage.Load(new MemoryStream(Secrets.DecodeBase64Url(body.RootElement.GetProperty("message").GetProperty("raw").GetString()!)));
        Assert.Equal("original@example.com", mime.InReplyTo); Assert.Contains("earlier@example.com", mime.References);
        var files = mime.Attachments.Cast<MimePart>().ToArray();
        Assert.Equal(2, files.Length); Assert.DoesNotContain(files, x => x.FileName == "removed.txt");
        using var content = new MemoryStream(); files.Single(x => x.FileName == "retained.txt").Content!.DecodeTo(content);
        Assert.Equal("keep", Encoding.UTF8.GetString(content.ToArray())); handler.AssertComplete();
    }

    [Fact]
    public async Task Microsoft_draft_update_preserves_existing_files_and_adds_new_files_separately()
    {
        var existing = GraphDraft.Replace("\"attachments\":[]", "\"attachments\":[{\"@odata.type\":\"#microsoft.graph.fileAttachment\",\"id\":\"keep\",\"name\":\"keep.txt\",\"size\":4},{\"@odata.type\":\"#microsoft.graph.fileAttachment\",\"id\":\"remove\",\"name\":\"remove.txt\",\"size\":4}]");
        var handler = new ScriptedHandler([
            new(HttpMethod.Get, "/me/messages/draft-one", existing),
            new(HttpMethod.Patch, "/me/messages/draft-one", GraphDraft),
            new(HttpMethod.Delete, "/me/messages/draft-one/attachments/remove", "", HttpStatusCode.NoContent),
            new(HttpMethod.Post, "/me/messages/draft-one/attachments", "{\"id\":\"new-file\"}"),
            new(HttpMethod.Get, "/me/messages/draft-one", GraphDraft)
        ]);
        var request = new SendMailRequest([], "", "unfinished", Bcc: [new("hidden@example.com")], Attachments: [new("new.txt", "text/plain", "bmV3")]);
        await new MicrosoftProvider(SecurityTests.Settings(), new(new Clients(handler))).SaveDraft("fake", "sender@example.com", "draft-one", new(request, ["keep"]), default);
        using var patch = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.False(patch.RootElement.TryGetProperty("attachments", out _));
        Assert.Equal(0, patch.RootElement.GetProperty("toRecipients").GetArrayLength());
        Assert.Equal("hidden@example.com", patch.RootElement.GetProperty("bccRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString());
        using var attachment = JsonDocument.Parse(handler.Requests[3].Body!);
        Assert.Equal("bmV3", attachment.RootElement.GetProperty("contentBytes").GetString()); handler.AssertComplete();
    }

    [Fact]
    public async Task Microsoft_draft_endpoints_refuse_to_delete_a_regular_message()
    {
        var handler = new ScriptedHandler([new(HttpMethod.Get, "/me/messages/message-one", GraphDraft.Replace("\"isDraft\":true", "\"isDraft\":false"))]);
        var error = await Assert.ThrowsAsync<ApiError>(() => new MicrosoftProvider(SecurityTests.Settings(), new(new Clients(handler))).DeleteDraft("fake", "message-one", default));
        Assert.Equal("not_a_draft", error.Code); handler.AssertComplete();
    }

    [Fact]
    public void Retained_attachment_ids_cannot_refer_to_another_draft_or_duplicate_files()
    {
        var draft = new MailMessage("m", null, "", null, [], [], null, "", "", null, true, [new("valid", "file.txt", "text/plain", 1)]);
        Assert.Single(DraftAttachments.Keep(draft, null)); Assert.Empty(DraftAttachments.Keep(draft, []));
        Assert.Throws<ApiError>(() => DraftAttachments.Keep(draft, ["other"]));
        Assert.Throws<ApiError>(() => DraftAttachments.Keep(draft, ["valid", "valid"]));
        Assert.Throws<ApiError>(() => DraftAttachments.Keep(null, ["valid"]));
    }

    [Fact]
    public async Task Malformed_success_response_after_a_write_has_an_uncertain_outcome()
    {
        var handler = new ScriptedHandler([new(HttpMethod.Post, "/send", "not-json")]);
        var error = await Assert.ThrowsAsync<ProviderError>(() => new ProviderHttp(new Clients(handler)).Json("https://provider.example/send", "fake", HttpMethod.Post, new { }, default));
        Assert.True(error.Uncertain); handler.AssertComplete();
    }

    private sealed record Step(HttpMethod Method, string Path, string Response, HttpStatusCode Status = HttpStatusCode.OK);
    private sealed record Captured(HttpMethod Method, string Path, string? Body);
    private sealed class ScriptedHandler(IEnumerable<Step> steps) : HttpMessageHandler
    {
        private readonly Queue<Step> remaining = new(steps);
        public readonly List<Captured> Requests = [];
        public void AssertComplete() => Assert.Empty(remaining);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var step = remaining.Dequeue(); var path = request.RequestUri!.AbsolutePath;
            Assert.Equal(step.Method, request.Method); Assert.EndsWith(step.Path, path);
            Requests.Add(new(request.Method, path, request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return new(step.Status) { Content = new StringContent(step.Response, Encoding.UTF8, "application/json") };
        }
    }
    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, disposeHandler: false); }
}
