using System.Threading.RateLimiting;
using Clarvio.Mail.Api;
using Clarvio.Mail.Auth;
using Clarvio.Mail.Core;
using Clarvio.Mail.Data;
using Clarvio.Mail.Providers;
using Clarvio.Mail.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;

if (args.Contains("--healthcheck"))
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try { Environment.Exit((await client.GetAsync("http://localhost:8740/health/ready")).IsSuccessStatusCode ? 0 : 1); }
    catch { Environment.Exit(1); }
    return;
}
var builder = WebApplication.CreateBuilder(args);
// This private file is for local development. Environment variables still take precedence.
if (builder.Environment.IsDevelopment() && Environment.GetEnvironmentVariable("CLARVIO_MAIL_CONFIG") is { Length: > 0 } localConfig)
    builder.Configuration.AddJsonFile(Path.GetFullPath(localConfig), optional: false, reloadOnChange: false).AddEnvironmentVariables();
var settings = builder.Configuration.GetSection("Mail").Get<MailSettings>() ?? new();
settings.Validate(!builder.Environment.IsDevelopment());
builder.WebHost.ConfigureKestrel(o => { o.AddServerHeader = false; o.Limits.MaxRequestBodySize = 16 * 1024 * 1024; });
builder.Services.AddSingleton(settings);
if (settings.TrustedProxies.Length > 0)
    builder.Services.Configure<ForwardedHeadersOptions>(options => ProxyConfiguration.Configure(options, settings));
builder.Services.AddSingleton<TokenCipher>();
builder.Services.AddDbContext<MailDb>(o => o.UseNpgsql(settings.Database));
builder.Services.AddScoped<OAuthService>();
builder.Services.AddScoped<MailService>();
builder.Services.AddScoped<ProviderHttp>();
builder.Services.AddScoped<IEmailProvider, GoogleProvider>();
builder.Services.AddScoped<IEmailProvider, MicrosoftProvider>();
builder.Services.AddScoped<ProviderRegistry>();
builder.Services.AddHttpClient("provider", c => c.Timeout = TimeSpan.FromSeconds(25))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    .RemoveAllLoggers(); // Outlook upload URLs contain pre-authenticated tokens; never log their query strings.
builder.Services.AddHttpClient("webhook", c => c.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectCallback = WebhookTargets.Connect, PooledConnectionLifetime = TimeSpan.FromMinutes(1) });
builder.Services.AddHostedService<MailWorker>();
builder.Services.AddMailDocumentation();
builder.Services.AddRateLimiter(o =>
{
    foreach (var (name, count) in new[] { ("api", 200), ("send", 20), ("auth", 20) })
        o.AddPolicy(name, ctx => RateLimitPartition.GetFixedWindowLimiter(
            ctx.Items[typeof(RequestIdentity)] is RequestIdentity identity ? identity.UserId.ToString() : ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = count, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.StatusCode = 429; ctx.HttpContext.Response.Headers.RetryAfter = "60";
        await ctx.HttpContext.Response.WriteAsJsonAsync(new { error = new { code = "rate_limited", message = "Try again in a minute." }, traceId = ctx.HttpContext.TraceIdentifier }, ct);
    };
});
var app = builder.Build();
if (args.Contains("--migrate"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<MailDb>().Database.MigrateAsync();
    return;
}
if (settings.TrustedProxies.Length > 0) app.UseForwardedHeaders();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-src 'self' about:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/auth")) ctx.Response.Headers.CacheControl = "no-store";
    try { await next(ctx); }
    catch (Exception ex) when (!ctx.Response.HasStarted)
    {
        if (ex is OperationCanceledException && ctx.RequestAborted.IsCancellationRequested) return;
        var (status, code, message, retry) = ex switch
        {
            ApiError e => (e.Status, e.Code, e.Message, e.RetryAfter),
            ProviderError e => (e.Reconnect ? 409 : 502, e.Code, e.Message, e.RetryAfter),
            BadHttpRequestException e => (e.StatusCode, "invalid_request", "The request is invalid.", (int?)null),
            _ => (500, "internal_error", "The service could not complete this request.", (int?)null)
        };
        if (status == 500) app.Logger.LogError("Request {Trace} failed ({Type}).", ctx.TraceIdentifier, ex.GetType().Name);
        ctx.Response.StatusCode = status;
        if (retry is not null) ctx.Response.Headers.RetryAfter = retry.ToString();
        await ctx.Response.WriteAsJsonAsync(new { error = new { code, message }, traceId = ctx.TraceIdentifier });
    }
});
app.UseMiddleware<IdentityMiddleware>();
app.UseRateLimiter();
app.MapMailDocumentation(settings);
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapMailApi();
app.MapFallback((HttpContext ctx, IWebHostEnvironment env) =>
    ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/auth") || ctx.Request.Path.StartsWithSegments("/health") ||
        ctx.Request.Path.StartsWithSegments("/swagger") || ctx.Request.Path.StartsWithSegments("/openapi")
        ? Results.NotFound() : Results.File(Path.Combine(env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot"), "index.html"), "text/html"));
app.Run();
public partial class Program;
