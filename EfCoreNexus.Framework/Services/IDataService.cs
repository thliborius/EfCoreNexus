using EfCoreNexus.Framework.Entities;
using EfCoreNexus.Framework.Provider;
using Microsoft.EntityFrameworkCore;

namespace EfCoreNexus.Framework.Services;

public interface IDataService<TContext> where TContext : DbContext
{
    IDbContextFactory<TContext> CtxFactory { get; }

    TProviderBase GetProvider<TProviderBase>() where TProviderBase : IProvider;

    IProviderCrud<TEntity, TId> GetProviderCrud<TEntity, TId>()
        where TEntity : IEntity
        where TId : struct;

    void BeginTransaction();
    Task CommitTransaction();
    Task DisposeTransaction();
}