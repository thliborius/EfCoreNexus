using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EfCoreNexus.Framework.Services;

public class TransactionService<T> where T : DbContext
{
    public TransactionService(T stdContext, IDbContextFactory<T> ctxFactory)
    {
        StandardContext = stdContext;
        CtxFactory = ctxFactory;
    }

    public T StandardContext { get; }
    public IDbContextFactory<T> CtxFactory { get; }

    private IDbContextTransaction Transaction { get; set; }
    public DbContext CtxTransaction { get; private set; }

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
        await Transaction.DisposeAsync().ConfigureAwait(false);

        await CtxTransaction.DisposeAsync().ConfigureAwait(false);
    }
}