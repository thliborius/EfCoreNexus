using EfCoreNexus.Framework.Provider;
using EfCoreNexus.Framework.Services;
using EfCoreNexus.TestApp.Data.Context;
using EfCoreNexus.TestApp.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace EfCoreNexus.TestApp.Data.Provider;

public class TestProvider : ProviderBase<Test, Guid, MainContext>
{
    public TestProvider(TransactionService<MainContext> transactionSvc) : base(transactionSvc)
    {
    }

    /// <summary>
    /// How to write your own query
    /// </summary>
    public async Task<IList<Test>> GetActiveOrderedByDate()
    {
        var ctx = await GetContextAsync().ConfigureAwait(false);

        try
        {
            var items = GetAllQuery(ctx);
            return await items.Where(x => x.Active == true).OrderBy(x => x.CurrentDate).ToListAsync().ConfigureAwait(false);
        }
        finally
        {
            if (TransactionSvc is { CtxTransaction: null })
            {
                await ctx.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///  How to use transactions: set all entries to active=false and create a new one
    /// </summary>
    /// <param name="newEntity">A new entity to add</param>
    public async Task DeactivateAndCreate(Test newEntity)
    {
        try
        {
            TransactionSvc.BeginTransaction();

            var ctx = await GetContextAsync().ConfigureAwait(false);
            var itemsToDeactivate = GetDbSet(ctx).Where(x => x.Active == true);
            foreach (var item in itemsToDeactivate)
            {
                item.Active = false;
            }
            ctx.Set<Test>().UpdateRange(itemsToDeactivate);

            GetDbSet(ctx).Add(newEntity);

            await ctx.SaveChangesAsync().ConfigureAwait(false);

            await TransactionSvc.CommitTransaction().ConfigureAwait(false);
        }
        finally
        {
            await TransactionSvc.DisposeTransaction().ConfigureAwait(false);
        }
    }
}