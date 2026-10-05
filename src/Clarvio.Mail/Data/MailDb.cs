using Microsoft.EntityFrameworkCore;

namespace Clarvio.Mail.Data;

public sealed class MailDb(DbContextOptions<MailDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Identity> Identities => Set<Identity>();
    public DbSet<Mailbox> Mailboxes => Set<Mailbox>();
    public DbSet<OAuthAttempt> OAuthAttempts => Set<OAuthAttempt>();
    public DbSet<LoginSession> Sessions => Set<LoginSession>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<SendOperation> SendOperations => Set<SendOperation>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();
    public DbSet<IncomingEvent> IncomingEvents => Set<IncomingEvent>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Identity>().HasIndex(x => new { x.Provider, x.Subject }).IsUnique();
        m.Entity<Mailbox>().HasIndex(x => new { x.Provider, x.Subject }).IsUnique();
        m.Entity<Mailbox>().HasIndex(x => x.UserId);
        m.Entity<Mailbox>().HasIndex(x => x.NextPollAt);
        m.Entity<OAuthAttempt>().HasKey(x => x.StateHash);
        m.Entity<LoginSession>().HasKey(x => x.TokenHash);
        m.Entity<ApiKey>().HasIndex(x => x.TokenHash).IsUnique();
        m.Entity<SendOperation>().HasIndex(x => new { x.UserId, x.IdempotencyKey }).IsUnique();
        m.Entity<AuditEvent>().HasIndex(x => new { x.UserId, x.CreatedAt });
        m.Entity<IncomingEvent>().HasIndex(x => new { x.MailboxId, x.ProviderMessageId }).IsUnique();
        m.Entity<WebhookDelivery>().HasIndex(x => new { x.SubscriptionId, x.EventId }).IsUnique();
        m.Entity<WebhookDelivery>().HasIndex(x => new { x.Status, x.NextAttemptAt });
        foreach (var type in new[] { typeof(Identity), typeof(Mailbox), typeof(LoginSession), typeof(ApiKey),
            typeof(SendOperation), typeof(AuditEvent), typeof(WebhookSubscription) })
            m.Entity(type).HasOne(typeof(User), null).WithMany().HasForeignKey("UserId").OnDelete(DeleteBehavior.Restrict);
        m.Entity<WebhookSubscription>().HasOne<Mailbox>().WithMany().HasForeignKey(x => x.MailboxId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<IncomingEvent>().HasOne<Mailbox>().WithMany().HasForeignKey(x => x.MailboxId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<WebhookDelivery>().HasOne<WebhookSubscription>().WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<WebhookDelivery>().HasOne<IncomingEvent>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
    }
}
