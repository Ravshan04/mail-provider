using System.Security.Cryptography;
using System.Net;

namespace Clarvio.Mail.Core;

public sealed class MailSettings
{
    public string PublicUrl { get; set; } = "http://localhost:8740";
    public string Database { get; set; } = "";
    public string ActiveKey { get; set; } = "v1";
    public Dictionary<string, string> EncryptionKeys { get; set; } = [];
    public bool AllowSignup { get; set; }
    public string[] AllowedLoginEmails { get; set; } = [];
    public string[] WebhookHosts { get; set; } = [];
    public ProviderSettings Google { get; set; } = new();
    public ProviderSettings Microsoft { get; set; } = new();
    public int PollSeconds { get; set; } = 60;
    public bool EnableSwagger { get; set; }
    public string[] TrustedProxies { get; set; } = [];

    public void Validate(bool production)
    {
        if (!Uri.TryCreate(PublicUrl, UriKind.Absolute, out var uri) ||
            uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo) ||
            (production ? uri.Scheme != "https" : uri.Scheme is not ("http" or "https")))
            throw new InvalidOperationException("Mail:PublicUrl must be an origin (HTTPS in production).");
        PublicUrl = PublicUrl.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(Database)) throw new InvalidOperationException("Mail:Database is required.");
        if (!EncryptionKeys.ContainsKey(ActiveKey)) throw new InvalidOperationException("The active encryption key is missing.");
        foreach (var (id, value) in EncryptionKeys)
        {
            if (id.Contains('.') || Convert.FromBase64String(value).Length != 32)
                throw new InvalidOperationException("Encryption keys need a simple id and exactly 32 bytes in base64.");
        }
        if (PollSeconds < 15) throw new InvalidOperationException("PollSeconds must be at least 15.");
        if (TrustedProxies.Any(address => !IPAddress.TryParse(address, out _)))
            throw new InvalidOperationException("TrustedProxies must contain explicit IP addresses.");
        Microsoft.Tenant = Microsoft.Tenant.Trim();
        if (Microsoft.Tenant is not ("common" or "organizations" or "consumers") && !Guid.TryParse(Microsoft.Tenant, out _))
            throw new InvalidOperationException("Microsoft tenant must be common, organizations, consumers, or a tenant GUID.");
        if (production && Microsoft.Enabled && string.IsNullOrWhiteSpace(Microsoft.CertificatePath))
            throw new InvalidOperationException("Production Microsoft OAuth requires a certificate credential.");
    }
}

public sealed class ProviderSettings
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string Tenant { get; set; } = "common";
    public string CertificatePath { get; set; } = "";
    public string CertificatePassword { get; set; } = "";
    public bool Enabled => !string.IsNullOrWhiteSpace(ClientId) &&
        (!string.IsNullOrWhiteSpace(ClientSecret) || !string.IsNullOrWhiteSpace(CertificatePath));
}

public static class Secrets
{
    public static string Random(int bytes = 32) => Base64Url(RandomNumberGenerator.GetBytes(bytes));
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] DecodeBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight((s.Length + 3) / 4 * 4, '='));
    }
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));
    public static bool Equal(string left, string right) => CryptographicOperations.FixedTimeEquals(
        System.Text.Encoding.UTF8.GetBytes(left), System.Text.Encoding.UTF8.GetBytes(right));
}
