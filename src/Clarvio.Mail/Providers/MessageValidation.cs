using Clarvio.Mail.Core;
using MimeKit;

namespace Clarvio.Mail.Providers;

public static class MessageValidation
{
    public const int MaxAttachmentBytes = 2 * 1024 * 1024;
    public static void Validate(SendMailRequest request, bool draft = false)
    {
        if (request.To is null || request.TextBody is null) Fail("To recipients and a text body are required.");
        var recipients = request.To.Concat(request.Cc ?? []).Concat(request.Bcc ?? []).ToArray();
        if ((!draft && request.To.Count == 0) || recipients.Length > 50) Fail("Use at most 50 recipients, including at least one To address when sending.");
        foreach (var address in recipients)
        {
            if (address is null || string.IsNullOrWhiteSpace(address.Email) || address.Email.Length > 254 || !MailboxAddress.TryParse(address.Email, out var parsed) ||
                !string.Equals(parsed.Address, address.Email, StringComparison.Ordinal) ||
                address.Email.Any(char.IsControl) || (address.Name?.Any(char.IsControl) ?? false)) Fail("A recipient address is invalid.");
        }
        if (request.Subject is null || (!draft && string.IsNullOrWhiteSpace(request.Subject)) || request.Subject.Length > 500 || request.Subject.Any(char.IsControl))
            Fail("Use a subject of 1–500 characters without line breaks.");
        if (request.TextBody.Length > 100_000 || request.HtmlBody?.Length > 100_000) Fail("The email body is too long.");
        long total = 0;
        if (request.Attachments?.Count > 10) Fail("Use at most 10 attachments.");
        foreach (var attachment in request.Attachments ?? [])
        {
            if (attachment is null || string.IsNullOrWhiteSpace(attachment.Name) || attachment.Name.Length > 255 ||
                attachment.Name.Any(char.IsControl) || attachment.Name.IndexOfAny(['/', '\\']) >= 0) Fail("An attachment filename is invalid.");
            if (string.IsNullOrEmpty(attachment.ContentType) || !ContentType.TryParse(attachment.ContentType, out _) || attachment.ContentType.Any(char.IsControl)) Fail("An attachment content type is invalid.");
            if (attachment.ContentBase64 is null) Fail("An attachment is not valid base64.");
            if (attachment.ContentBase64.Length > (MaxAttachmentBytes + 2) / 3 * 4) Fail("Attachments exceed the 2 MiB combined limit.");
            try { total += Convert.FromBase64String(attachment.ContentBase64).Length; }
            catch (FormatException) { Fail("An attachment is not valid base64."); }
            if (total > MaxAttachmentBytes) Fail("Attachments exceed the 2 MiB combined limit.");
        }
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string message) => throw new ApiError(400, "invalid_message", message);

    public static string SafeFilename(string name) => string.Concat(name.Where(c => !char.IsControl(c) && c is not '/' and not '\\'))
        is { Length: > 0 } safe ? safe : "attachment";

    public static MimeMessage Mime(string sender, SendMailRequest request, MailMessage? reply)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("", sender));
        foreach (var address in request.To) message.To.Add(new MailboxAddress(address.Name ?? "", address.Email));
        foreach (var address in request.Cc ?? []) message.Cc.Add(new MailboxAddress(address.Name ?? "", address.Email));
        foreach (var address in request.Bcc ?? []) message.Bcc.Add(new MailboxAddress(address.Name ?? "", address.Email));
        message.Subject = reply is null ? request.Subject : reply.Subject;
        if (reply?.InternetMessageId is { Length: > 0 } id)
        {
            message.InReplyTo = id.Trim('<', '>');
            if (!string.IsNullOrWhiteSpace(reply.References))
                foreach (var reference in MimeKit.Utils.MimeUtils.EnumerateReferences(reply.References)) message.References.Add(reference);
            message.References.Add(message.InReplyTo);
        }
        var builder = new BodyBuilder { TextBody = request.TextBody, HtmlBody = request.HtmlBody };
        foreach (var file in request.Attachments ?? [])
            builder.Attachments.Add(file.Name, Convert.FromBase64String(file.ContentBase64), ContentType.Parse(file.ContentType));
        message.Body = builder.ToMessageBody();
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
        return message;
    }
}
