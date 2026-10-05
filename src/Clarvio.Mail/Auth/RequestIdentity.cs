using Clarvio.Mail.Core;
using Clarvio.Mail.Data;
using Microsoft.EntityFrameworkCore;

namespace Clarvio.Mail.Auth;

public sealed record RequestIdentity(Guid UserId, bool IsSession, IReadOnlySet<string> Scopes, string? SessionToken = null)
{
    public static readonly string[] KeyScopes = ["accounts:read", "mail:read", "mail:write", "mail:send"];
    public static RequestIdentity Require(HttpContext ctx, string? scope = null, bool sessionOnly = false)
    {
        if (ctx.Items[typeof(RequestIdentity)] is not RequestIdentity identity)
            throw new ApiError(401, "unauthorized", "Sign in or provide a valid API key.");
        if (sessionOnly && !identity.IsSession) throw new ApiError(403, "session_required", "Sign in to manage this setting.");
        if (scope is not null && !identity.IsSession && !identity.Scopes.Contains(scope))
            throw new ApiError(403, "scope_required", "This API key does not have permission for this operation.");
        return identity;
    }
    public static string Csrf(string token) => Secrets.Hash(token + ":csrf");
}

public sealed class IdentityMiddleware(RequestDelegate next)
{
    public const string SessionCookie = "cm_session";
    public async Task Invoke(HttpContext ctx, MailDb db)
    {
        RequestIdentity? identity = null;
        var bearer = ctx.Request.Headers.Authorization.ToString();
        if (bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = bearer[7..];
            if (token.StartsWith("cm_sk_", StringComparison.Ordinal) && token.Length < 200)
            {
                var key = await db.ApiKeys.SingleOrDefaultAsync(x => x.TokenHash == Secrets.Hash(token) && x.RevokedAt == null && x.ExpiresAt > DateTimeOffset.UtcNow, ctx.RequestAborted);
                if (key is not null)
                {
                    identity = new RequestIdentity(key.UserId, false, key.Scopes.ToHashSet());
                    if (key.LastUsedAt is null || key.LastUsedAt < DateTimeOffset.UtcNow.AddMinutes(-5))
                    { key.LastUsedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ctx.RequestAborted); }
                }
            }
        }
        else if (ctx.Request.Cookies[SessionCookie] is { Length: > 20 and < 200 } sessionToken)
        {
            var row = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == Secrets.Hash(sessionToken) && x.ExpiresAt > DateTimeOffset.UtcNow, ctx.RequestAborted);
            if (row is not null) identity = new RequestIdentity(row.UserId, true, new HashSet<string>(), sessionToken);
        }
        ctx.Items[typeof(RequestIdentity)] = identity;
        if (identity?.IsSession == true && ctx.Request.Path.StartsWithSegments("/api") &&
            ctx.Request.Method is not ("GET" or "HEAD" or "OPTIONS"))
        {
            var csrf = ctx.Request.Headers["X-CSRF-Token"].ToString();
            if (!Secrets.Equal(csrf, RequestIdentity.Csrf(identity.SessionToken!)))
                throw new ApiError(403, "invalid_csrf", "Reload the page and try again.");
        }
        await next(ctx);
    }
}
