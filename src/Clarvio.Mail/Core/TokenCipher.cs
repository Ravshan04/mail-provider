using System.Security.Cryptography;
using System.Text;

namespace Clarvio.Mail.Core;

public sealed class TokenCipher(MailSettings settings)
{
    public string Encrypt(string plain, string purpose)
    {
        var key = Convert.FromBase64String(settings.EncryptionKeys[settings.ActiveKey]);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var ciphertext = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, data, ciphertext, tag, Encoding.UTF8.GetBytes(settings.ActiveKey + ":" + purpose));
        return settings.ActiveKey + "." + Secrets.Base64Url([.. nonce, .. tag, .. ciphertext]);
    }

    public string Decrypt(string encoded, string purpose)
    {
        var split = encoded.Split('.', 2);
        if (split.Length != 2 || !settings.EncryptionKeys.TryGetValue(split[0], out var value))
            throw new CryptographicException("Unknown token encryption key.");
        var data = Secrets.DecodeBase64Url(split[1]);
        if (data.Length < 28) throw new CryptographicException("Invalid encrypted token.");
        var plaintext = new byte[data.Length - 28];
        using var aes = new AesGcm(Convert.FromBase64String(value), 16);
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plaintext,
            Encoding.UTF8.GetBytes(split[0] + ":" + purpose));
        return Encoding.UTF8.GetString(plaintext);
    }
}
