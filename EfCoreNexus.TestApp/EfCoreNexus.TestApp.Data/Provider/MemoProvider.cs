using EfCoreNexus.Framework.Provider;
using EfCoreNexus.TestApp.Data.Context;
using EfCoreNexus.TestApp.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace EfCoreNexus.TestApp.Data.Provider;

public sealed class MemoProvider(IDbContextFactory<MainContext> factory)
    : ProviderBase<Memo, int, MainContext>(factory)
{
    // Application-managed tokens require explicit original/new values, not generic UpdateAsync.
    public Task RenameAsync(int id, string text, Guid expectedRevision, CancellationToken ct = default)
        => WithContextAsync(async (db, token) =>
        {
            var memo = await db.Set<Memo>().FindAsync([id], token)
                ?? throw new KeyNotFoundException("The memo was deleted.");
            db.Entry(memo).Property(x => x.Revision).OriginalValue = expectedRevision;
            memo.Text = text;
            memo.Revision = Guid.NewGuid();
            await db.SaveChangesAsync(token);
        }, ct);
}
