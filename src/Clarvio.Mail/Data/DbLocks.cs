using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Clarvio.Mail.Data;

public static class DbLocks
{
    // Transaction-scoped locks coordinate refresh/sync/send across API replicas without a global in-memory lock.
    public static async Task<IDbContextTransaction> Acquire(MailDb db, string key, CancellationToken ct)
    {
        var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
            return tx;
        }
        catch { await tx.DisposeAsync(); throw; }
    }
}
