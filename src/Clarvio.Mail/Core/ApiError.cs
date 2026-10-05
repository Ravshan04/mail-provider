namespace Clarvio.Mail.Core;

public sealed class ApiError(int status, string code, string message, int? retryAfter = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public int? RetryAfter { get; } = retryAfter;
}

public sealed class ProviderError(string code, string message, bool reconnect = false, bool uncertain = false, int? retryAfter = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public bool Reconnect { get; } = reconnect;
    public bool Uncertain { get; } = uncertain;
    public int? RetryAfter { get; } = retryAfter;
}
