using Clarvio.Mail.Core;
using Clarvio.Mail.Data;
using Clarvio.Mail.Providers;
using Microsoft.EntityFrameworkCore;

namespace Clarvio.Mail.Auth;

public sealed class OAuthService(MailDb db, MailSettings settings, TokenCipher cipher, ProviderRegistry providers, IWebHostEnvironment environment)
{
    private const string BrowserCookie = "cm_oauth";
    private CookieOptions Cookie(string path = "/") => new()
    { HttpOnly = true, Secure = !environment.IsDevelopment(), SameSite = SameSiteMode.Lax, Path = path, IsEssential = true };

    public async Task<string> Start(HttpContext ctx, string providerName, bool connect, CancellationToken ct)
    {
        var provider = providers.Get(providerName);
        if (!provider.Configured) throw new ApiError(503, "provider_unconfigured", "This email connection is temporarily unavailable.");
        Guid? userId = connect ? RequestIdentity.Require(ctx, sessionOnly: true).UserId : null;
        var browser = ctx.Request.Cookies[BrowserCookie] is { Length: > 20 and < 200 } existing ? existing : Secrets.Random();
        var state = Secrets.Random(); var verifier = Secrets.Random();
        db.OAuthAttempts.Add(new OAuthAttempt
        {
            StateHash = Secrets.Hash(state), BrowserHash = Secrets.Hash(browser), Provider = providerName,
            UserId = userId, Verifier = cipher.Encrypt(verifier, "oauth:" + Secrets.Hash(state)), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        });
        await db.SaveChangesAsync(ct);
        var cookie = Cookie("/auth"); cookie.MaxAge = TimeSpan.FromMinutes(10);
        ctx.Response.Cookies.Append(BrowserCookie, browser, cookie);
        return provider.AuthorizationUrl(state, Secrets.Challenge(verifier));
    }

    public async Task Complete(HttpContext ctx, string providerName, string state, string? code, string? error, CancellationToken ct)
    {
        if (state.Length > 200 || ctx.Request.Cookies[BrowserCookie] is not { } browser)
            throw new ApiError(400, "invalid_state", "The sign-in session expired. Start again.");
        var hash = Secrets.Hash(state);
        var attempt = await db.OAuthAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.StateHash == hash, ct);
        if (attempt is null || attempt.ExpiresAt < DateTimeOffset.UtcNow || attempt.Provider != providerName ||
            !Secrets.Equal(attempt.BrowserHash, Secrets.Hash(browser)))
            throw new ApiError(400, "invalid_state", "The sign-in session expired. Start again.");
        if (attempt.UserId is { } owner && RequestIdentity.Require(ctx, sessionOnly: true).UserId != owner)
            throw new ApiError(403, "session_changed", "Your signed-in account changed. Start the connection again.");
        // Consume before contacting the provider. A callback cannot be replayed, including provider-error callbacks.
        if (await db.OAuthAttempts.Where(x => x.StateHash == hash).ExecuteDeleteAsync(ct) != 1)
            throw new ApiError(400, "invalid_state", "This sign-in link has already been used.");
        if (!string.IsNullOrEmpty(error)) throw new ApiError(400, "consent_denied", "The email connection was not authorized.");
        if (string.IsNullOrEmpty(code) || code.Length > 4096) throw new ApiError(400, "missing_code", "The provider did not return an authorization code.");
        var provider = providers.Get(providerName);
        var tokens = await provider.ExchangeCode(code, cipher.Decrypt(attempt.Verifier, "oauth:" + hash), ct);
        var account = await provider.Identify(tokens.AccessToken, ct);
        if (string.IsNullOrEmpty(tokens.RefreshToken)) throw new ApiError(422, "offline_access_missing", "Authorize offline access so the mailbox stays connected.");
        await using var tx = await DbLocks.Acquire(db, "identity:" + providerName + ":" + account.Subject, ct);
        var identity = await db.Identities.SingleOrDefaultAsync(x => x.Provider == providerName && x.Subject == account.Subject, ct);
        Guid userId;
        if (attempt.UserId is { } connectedUser) userId = connectedUser;
        else if (identity is not null) userId = identity.UserId;
        else
        {
            if (!settings.AllowSignup && !settings.AllowedLoginEmails.Contains(account.Email, StringComparer.OrdinalIgnoreCase))
                throw new ApiError(403, "signup_closed", "This service is currently available to invited accounts only.");
            var user = new User { Email = account.Email, Name = account.Name };
            db.Users.Add(user); userId = user.Id;
            db.Identities.Add(new Identity { UserId = userId, Provider = providerName, Subject = account.Subject });
        }
        var mailbox = await db.Mailboxes.SingleOrDefaultAsync(x => x.Provider == providerName && x.Subject == account.Subject, ct);
        if (mailbox is not null && mailbox.UserId != userId)
            throw new ApiError(409, "mailbox_owned", "This mailbox belongs to another service account. Sign in with its original account.");
        if (mailbox is null)
        { mailbox = new Mailbox { UserId = userId, Provider = providerName, Subject = account.Subject }; db.Mailboxes.Add(mailbox); }
        else
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"mailbox:" + mailbox.Id}, 0))", ct);
            await db.Entry(mailbox).ReloadAsync(ct);
        }
        mailbox.Email = account.Email; mailbox.Name = account.Name; mailbox.Status = "active";
        mailbox.AccessToken = cipher.Encrypt(tokens.AccessToken, "access:" + mailbox.Id);
        mailbox.RefreshToken = cipher.Encrypt(tokens.RefreshToken, "refresh:" + mailbox.Id);
        mailbox.TokenExpiresAt = tokens.ExpiresAt; mailbox.Scopes = tokens.Scopes; mailbox.UpdatedAt = DateTimeOffset.UtcNow;
        db.AuditEvents.Add(new AuditEvent { UserId = userId, MailboxId = mailbox.Id, Action = "mailbox.connect", Outcome = "success", TraceId = ctx.TraceIdentifier });
        string? sessionToken = null;
        if (attempt.UserId is null)
        {
            sessionToken = Secrets.Random();
            db.Sessions.Add(new LoginSession { TokenHash = Secrets.Hash(sessionToken), UserId = userId, ExpiresAt = DateTimeOffset.UtcNow.AddDays(14) });
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        if (sessionToken is not null)
        {
            var cookie = Cookie(); cookie.MaxAge = TimeSpan.FromDays(14);
            ctx.Response.Cookies.Append(IdentityMiddleware.SessionCookie, sessionToken, cookie);
        }
    }
}
