using EfCoreNexus.Framework.Entities;
using EfCoreNexus.Framework.Provider;
using Microsoft.EntityFrameworkCore;

namespace EfCoreNexus.Framework.Services;

public class DataService<TContext> : IDataService<TContext> where TContext : DbContext
{
    private readonly List<IProvider> _provider = new();

    public DataService(TContext context, IDbContextFactory<TContext> ctxFactory, IProviderFactory<TContext> providerFactory)
    {
        CtxFactory = ctxFactory;
        // context can be null for unit testing
        if (context != null)
        {
            TransactionSvc = new TransactionService<TContext>(context, CtxFactory);
        }

        if (TransactionSvc != null)
        {
            _provider.AddRange(providerFactory.Create(TransactionSvc));
        }
    }

    private TransactionService<TContext>? TransactionSvc { get; }
    public IDbContextFactory<TContext> CtxFactory { get; }

    public TProviderBase GetProvider<TProviderBase>() where TProviderBase : IProvider
    {
        foreach (var p in _provider)
        {
            if (p is TProviderBase typedProvider)
            {
                return typedProvider;
            }
        }

        throw new ArgumentException($"No repository found for provider {typeof(TProviderBase)}");
    }

    public IProviderCrud<TEntity, TId> GetProviderCrud<TEntity, TId>()
        where TEntity : IEntity
        where TId : struct
    {
        foreach (var p in _provider)
        {
            if (p is IProviderCrud<TEntity, TId> typedProvider)
            {
                return typedProvider;
            }
        }

        throw new ArgumentException($"No repository found for entity type {typeof(TEntity)}");
    }

    public void BeginTransaction()
    {
        TransactionSvc?.BeginTransaction();
    }

    public async Task CommitTransaction()
    {
        if (TransactionSvc != null)
        {
            await TransactionSvc.CommitTransaction().ConfigureAwait(false);
        }
    }

    public async Task DisposeTransaction()
    {
        if (TransactionSvc != null)
        {
            await TransactionSvc.DisposeTransaction().ConfigureAwait(false);
        }
    }

    public void Reset()
    {
        TransactionSvc?.StandardContext.ChangeTracker.Entries().ToList().ForEach(e => e.State = EntityState.Detached);
    }
}