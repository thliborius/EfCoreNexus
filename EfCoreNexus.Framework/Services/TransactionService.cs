using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EfCoreNexus.Framework.Services;

public class TransactionService<T>(IDbContextFactory<T> ctxFactory)
    where T : DbContext
{
    public IDbContextFactory<T> CtxFactory { get; } = ctxFactory;
    private IDbContextTransaction? Transaction { get; set; }
    public DbContext CtxTransaction { get; private set; } = null!;

    public void BeginTransaction()
    {
        if (Transaction != null)
        {
            throw new Exception("Transaction open, has to be closed before starting a new one.");
        }

        CtxTransaction = CtxFactory.CreateDbContext();
        Transaction = CtxTransaction.Database.BeginTransaction();
    }

    public async Task CommitTransaction()
    {
        if (Transaction == null)
        {
            throw new Exception("No transaction found, start it first.");
        }

        await Transaction.CommitAsync().ConfigureAwait(false);
    }

    public async Task DisposeTransaction()
    {
        if (Transaction != null)
        {
            await Transaction.DisposeAsync().ConfigureAwait(false);
            Transaction = null;
        }
    }
}