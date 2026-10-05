using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Clarvio.Mail.Core;

namespace Clarvio.Mail.Providers;

public sealed class ProviderHttp(IHttpClientFactory clients)
{
    private const int MaxBytes = 16 * 1024 * 1024;

    // Outlook's upload URL is pre-authenticated. Never send the Graph bearer token to it.
    public async Task UploadOutlookAttachment(string url, byte[] bytes, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort ||
            uri.Host != "outlook.office.com" || !uri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ProviderError("invalid_upload_url", "Microsoft returned an unsupported attachment upload URL.");
        const int chunkSize = 2 * 1024 * 1024;
        for (var offset = 0; offset < bytes.Length;)
        {
            var count = Math.Min(chunkSize, bytes.Length - offset);
            using var request = new HttpRequestMessage(HttpMethod.Put, uri) { Content = new ByteArrayContent(bytes, offset, count) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + count - 1, bytes.Length);
            HttpResponseMessage response;
            try { response = await clients.CreateClient("provider").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            { throw new ProviderError("attachment_upload_unknown", "Attachment upload was interrupted. Read the draft before retrying.", uncertain: true); }
            using (response)
            {
                var last = offset + count == bytes.Length;
                if (response.StatusCode != (last ? HttpStatusCode.Created : HttpStatusCode.OK))
                    throw new ProviderError("attachment_upload_failed", "Microsoft did not confirm the attachment upload. Read the draft before retrying.", uncertain: true);
                if (!last)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(await BoundedBytes(response, ct));
                        var ranges = doc.RootElement.Array("nextExpectedRanges").Select(x => x.GetString()).ToArray();
                        var next = (offset + count).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        if (ranges.Length != 1 || ranges[0] is null || ranges[0]!.TrimEnd('-') != next)
                            throw new JsonException();
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException or ProviderError or HttpRequestException or OperationCanceledException)
                    { throw new ProviderError("attachment_upload_unknown", "Microsoft did not confirm the next attachment range. Read the draft before retrying.", uncertain: true); }
                }
            }
            offset += count;
        }
    }
    public async Task<JsonDocument> Json(string url, string? token, HttpMethod? method, object? body, CancellationToken ct)
    {
        using var response = await Request(url, token, method ?? HttpMethod.Get, body, ct, true);
        try
        {
            var bytes = await BoundedBytes(response, ct);
            return bytes.Length == 0 ? JsonDocument.Parse("{}") : JsonDocument.Parse(bytes);
        }
        catch (ProviderError ex) when (method is not null && method != HttpMethod.Get)
        { throw new ProviderError(ex.Code, ex.Message, uncertain: true); }
        catch (JsonException)
        { throw new ProviderError("invalid_provider_response", "The provider returned an invalid response.", uncertain: method is not null && method != HttpMethod.Get); }
    }

    public async Task<TokenSet> Token(string url, Dictionary<string, string> fields, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(fields) };
        using var response = await clients.CreateClient("provider").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var bytes = await BoundedBytes(response, ct);
        if (!response.IsSuccessStatusCode)
        {
            var code = "";
            try { using var json = JsonDocument.Parse(bytes); code = json.RootElement.GetProperty("error").GetString() ?? ""; }
            catch (JsonException) { }
            catch (KeyNotFoundException) { }
            var reconnect = code is "invalid_grant";
            throw new ProviderError(reconnect ? "reconnect_required" : "oauth_failed",
                reconnect ? "The mailbox authorization expired or was revoked. Connect it again."
                    : "The email provider could not complete authentication. Check the application configuration.", reconnect);
        }
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        var access = root.String("access_token");
        if (string.IsNullOrEmpty(access)) throw new ProviderError("oauth_failed", "No access token was returned.");
        var seconds = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var n) ? n : 3600;
        return new TokenSet(access, root.String("refresh_token"), DateTimeOffset.UtcNow.AddSeconds(seconds), root.String("scope") ?? "");
    }

    public async Task<HttpResponseMessage> Request(string url, string token, HttpMethod method, object? body, CancellationToken ct)
        => await Request(url, (string?)token, method, body, ct, true);

    private async Task<HttpResponseMessage> Request(string url, string? token, HttpMethod method, object? body, CancellationToken ct, bool unused = true)
    {
        var read = method == HttpMethod.Get;
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (url.StartsWith("https://graph.microsoft.com/", StringComparison.Ordinal))
                request.Headers.TryAddWithoutValidation("Prefer", "IdType=\"ImmutableId\"");
            if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            HttpResponseMessage response;
            try { response = await clients.CreateClient("provider").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            { throw new ProviderError("provider_unavailable", "The email provider did not respond. Try again later.", uncertain: !read); }
            if (response.IsSuccessStatusCode) return response;
            var retry = response.Headers.RetryAfter?.Delta is { } delta ? (int)Math.Ceiling(delta.TotalSeconds) : 2;
            var status = response.StatusCode;
            response.Dispose();
            if (read && status == HttpStatusCode.TooManyRequests && attempt == 0 && retry <= 5)
            { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, retry)), ct); continue; }
            throw status switch
            {
                HttpStatusCode.Unauthorized => new ProviderError("reconnect_required", "The mailbox authorization is no longer valid. Connect it again.", true),
                HttpStatusCode.Forbidden => new ProviderError("permission_denied", "The provider denied this operation. Check the granted mailbox permissions."),
                HttpStatusCode.NotFound => new ProviderError("message_not_found", "That email or attachment is no longer available."),
                HttpStatusCode.Gone => new ProviderError("sync_expired", "The synchronization cursor expired."),
                HttpStatusCode.TooManyRequests => new ProviderError("rate_limited", "The provider is limiting requests. Try again later.", retryAfter: Math.Clamp(retry, 1, 3600)),
                _ => new ProviderError("provider_error", "The provider could not complete this operation.", uncertain: !read && (int)status >= 500)
            };
        }
    }

    public static async Task<byte[]> BoundedBytes(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > MaxBytes)
            throw new ProviderError("response_too_large", "The provider response exceeds the supported size.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;
            if (memory.Length + read > MaxBytes) throw new ProviderError("response_too_large", "The provider response exceeds the supported size.");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }
}

public static class JsonValues
{
    public static string? String(this JsonElement root, string name) => root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    public static IEnumerable<JsonElement> Array(this JsonElement root, string name) => root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
}
