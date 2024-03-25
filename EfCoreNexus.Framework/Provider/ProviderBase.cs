using EfCoreNexus.Framework.Entities;
using EfCoreNexus.Framework.Services;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Linq.Dynamic.Core;

namespace EfCoreNexus.Framework.Provider;

public abstract class ProviderBase<TEntity, TId, TContext>(TransactionService<TContext> transactionSvc) : IProviderCrud<TEntity, TId>
    where TEntity : class, IEntity
    where TContext : DbContext
{
    protected readonly TransactionService<TContext> TransactionSvc = transactionSvc;

    public virtual async Task Delete(TId id)
    {
        var ctx = await GetContextAsync().ConfigureAwait(false);

        try
        {
            var itemToDelete = await GetByIdQuery(ctx, id).FirstOrDefaultAsync().ConfigureAwait(false);
            if (itemToDelete != null)
            {
                GetDbSet(ctx).Remove(itemToDelete);

                try
                {
                    await ctx.SaveChangesAsync().ConfigureAwait(false);
                }
                catch
                {
                    ctx.Entry(itemToDelete).State = EntityState.Unchanged;
                    throw;
                }
            }
        }
        finally
        {
            if (TransactionSvc is { CtxTransaction: null })
            {
                await ctx.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public virtual IList<TEntity> GetAll()
    {
        var ctx = GetContext();

        try
        {
            var items = GetAllQuery(ctx);
            return items.ToList();
        }
        finally
        {
            if (TransactionSvc is { CtxTransaction: null })
            {
                ctx.Dispose();
            }
        }
    }

    public virtual async Task<IList<TEntity>> GetAllAsync()
    {
        var ctx = await GetContextAsync().ConfigureAwait(false);

        try
        {
            var items = GetAllQuery(ctx);
            return await items.ToListAsync().ConfigureAwait(false);
        }
        finally
        {
            if (TransactionSvc is { CtxTransaction: null })
            {
                await ctx.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public virtual async Task<TEntity?> GetById(TId id)
    {
        var ctx = await GetContextAsync().ConfigureAwait(false);

        try
        {
            var items = GetByIdQuery(ctx, id);

            var itemToReturn = items.FirstOrDefault();

            return await Task.FromResult(itemToReturn).ConfigureAwait(false);
        }
        finally
        {
            if (TransactionSvc is { CtxTransaction: null })
            {
                await ctx.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public virtual async Task Update(TEntity item, TId id)
    {
        var ctx = await GetContextAsync().ConfigureAwait(false);

        try
        {
            var itemToUpdate = await GetByIdQuery(ctx, id).FirstOrDefaultAsync().ConfigureAwait(false);
            if (itemToUpdate == null)
            {
                throw new Exception("Item no longer available");
            }

            var entryToUpdate = ctx.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(item);
            entryToUpdate.State = EntityState.Modified;
            await ctx.SaveChangesAsync().ConfigureAwait(false);
        }
        finally
        {
            if (TransactionSvc is { CtxTransaction: null })
            {
                await ctx.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public virtual async Task Create(TEntity item, TId id)
    {
        var ctx = await GetContextAsync().ConfigureAwait(false);

        try
        {
            var existingItem = await GetByIdQuery(ctx, id).FirstOrDefaultAsync().ConfigureAwait(false);

            if (existingItem != null)
            {
                throw new Exception("Item already available");
            }

            try
            {
                GetDbSet(ctx).Add(item);
                await ctx.SaveChangesAsync().ConfigureAwait(false);
            }
            catch
            {
                ctx.Entry(item).State = EntityState.Detached;
                throw;
            }
        }
        finally
        {
            if (TransactionSvc is { CtxTransaction: null })
            {
                await ctx.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    protected virtual IQueryable<TEntity> GetAllQuery(DbContext ctx)
    {
        return GetDbSet(ctx).AsNoTracking();
    }

    protected virtual IQueryable<TEntity> GetByIdQuery(DbContext ctx, TId id)
    {
        var type = typeof(TEntity);
        var key = type.GetProperties().FirstOrDefault(p => p.CustomAttributes.Any(attr => attr.AttributeType == typeof(KeyAttribute)));
        if (key == null)
        {
            throw new NotImplementedException($"No primary key defined in Entity {type.Name}");
        }

        return GetDbSet(ctx).Where($"{key.Name} == @0", id);
    }

    protected DbSet<TEntity> GetDbSet(DbContext ctx)
    {
        return ctx.Set<TEntity>();
    }

    protected async Task<DbContext> GetContextAsync()
    {
        return TransactionSvc.CtxTransaction ?? await TransactionSvc.CtxFactory.CreateDbContextAsync().ConfigureAwait(false);
    }

    private DbContext GetContext()
    {
        return TransactionSvc.CtxTransaction ?? TransactionSvc.CtxFactory.CreateDbContext();
    }
}