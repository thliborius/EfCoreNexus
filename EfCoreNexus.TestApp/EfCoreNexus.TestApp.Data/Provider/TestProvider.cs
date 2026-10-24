using EfCoreNexus.Framework.Provider;
using EfCoreNexus.TestApp.Data.Context;
using EfCoreNexus.TestApp.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace EfCoreNexus.TestApp.Data.Provider;

public sealed class TestProvider(IDbContextFactory<MainContext> factory)
    : ProviderBase<Test, Guid, MainContext>(factory)
{
    public Task<List<Test>> GetActiveOrderedByDateAsync(CancellationToken ct = default)
        => WithContextAsync((db, token) => db.Set<Test>().AsNoTracking()
            .Where(x => x.Active).OrderBy(x => x.CurrentDate).ToListAsync(token), ct);
}
