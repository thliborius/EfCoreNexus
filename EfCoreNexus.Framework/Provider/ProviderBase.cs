using EfCoreNexus.Framework.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfCoreNexus.Framework.Provider;

/// <summary>A provider using a fresh, asynchronously disposed context for each operation.</summary>
public abstract class ProviderBase<TEntity, TId, TContext> : IProviderCrud<TEntity, TId>
    where TEntity : class
    where TId : notnull
    where TContext : DbContext
{
    private readonly IDbContextFactory<TContext> _factory;

    protected ProviderBase(IDbContextFactory<TContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <summary>Materializes a no-tracking list. Use specialized queries for large sets.</summary>
    public virtual Task<List<TEntity>> GetAllAsync(CancellationToken ct = default)
        => WithContextAsync((db, token) =>
        {
            GetKey(db);
            return db.Set<TEntity>().AsNoTracking().ToListAsync(token);
        }, ct);

    /// <summary>Finds the mapped primary key; returns null if absent.</summary>
    public virtual Task<TEntity?> GetByIdAsync(TId id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        return WithContextAsync((db, token) =>
        {
            GetKey(db);
            return db.Set<TEntity>().FindAsync([id], token).AsTask();
        }, ct);
    }

    /// <summary>Adds using native EF graph semantics and saves; EF generates keys.</summary>
    public virtual Task CreateAsync(TEntity entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return WithContextAsync(async (db, token) =>
        {
            GetKey(db);
            await db.Set<TEntity>().AddAsync(entity, token).ConfigureAwait(false);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>
    /// Applies a full scalar edit, excluding keys, generated values and graphs.
    /// Supply unchanged original store-generated CLR concurrency tokens. Missing at
    /// lookup throws KeyNotFoundException; a later conflict throws DbUpdateConcurrencyException.
    /// A no-change edit does not issue an update or assert the supplied version.
    /// Read again to refresh generated values on the detached input.
    /// </summary>
    public virtual Task UpdateAsync(TEntity entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return WithContextAsync(async (db, token) =>
        {
            var key = GetKey(db);
            var source = db.Entry(entity);
            var concurrency = GetConcurrencyProperties(source.Metadata, forUpdate: true);
            var id = source.Property(key.Name).CurrentValue;
            var row = await db.Set<TEntity>().FindAsync([id], token).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"{typeof(TEntity).Name} with key '{id}' was not found.");
            var target = db.Entry(row);
            var changed = false;
            foreach (var property in target.Metadata.GetProperties())
            {
                if (property.IsKey() || property.IsShadowProperty() || property.IsIndexerProperty()
                    || property.ValueGenerated != ValueGenerated.Never
                    || property.GetAfterSaveBehavior() != PropertySaveBehavior.Save)
                    continue;

                var value = source.Property(property.Name).CurrentValue;
                var destination = target.Property(property.Name);
                if (property.GetValueComparer().Equals(destination.CurrentValue, value)) continue;
                destination.CurrentValue = value;
                changed = true;
            }

            if (!changed) return;
            foreach (var property in concurrency)
                target.Property(property.Name).OriginalValue = source.Property(property.Name).CurrentValue;
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>Deletes the current row. Missing at lookup is a no-op; later races can conflict.</summary>
    public virtual Task DeleteAsync(TId id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        return WithContextAsync(async (db, token) =>
        {
            GetKey(db);
            var row = await db.Set<TEntity>().FindAsync([id], token).ConfigureAwait(false);
            if (row is null) return;
            db.Remove(row);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>
    /// Deletes using the key and unchanged original CLR concurrency tokens, without a lookup.
    /// A missing row or stale version throws DbUpdateConcurrencyException.
    /// Application-managed CLR tokens are supported because deletion needs no renewal.
    /// </summary>
    public virtual Task DeleteAsync(TEntity entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return WithContextAsync(async (db, token) =>
        {
            GetKey(db);
            var entry = db.Entry(entity);
            var concurrency = GetConcurrencyProperties(entry.Metadata, forUpdate: false);
            entry.State = EntityState.Deleted;
            foreach (var property in concurrency)
                entry.Property(property.Name).OriginalValue = entry.Property(property.Name).CurrentValue;
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, ct);
    }

    private static IProperty GetKey(TContext db)
    {
        var type = db.Model.FindEntityType(typeof(TEntity));
        var key = type?.FindPrimaryKey();
        if (key is null || key.Properties.Count != 1)
            throw new InvalidOperationException($"{typeof(TEntity).Name}: CRUD requires one mapped CLR primary key; "
                + "unmapped, keyless and composite-key entities need a specialized provider.");
        var property = key.Properties[0];
        if (property.IsShadowProperty() || property.IsIndexerProperty() || property.ClrType != typeof(TId))
            throw new InvalidOperationException($"{typeof(TEntity).Name}.{property.Name}: CRUD requires a CLR key "
                + $"of type {typeof(TId).Name}; mapped key type is {property.ClrType.Name}. "
                + "Shadow/indexer keys and mismatched TId are unsupported.");
        return property;
    }

    private static List<IProperty> GetConcurrencyProperties(IEntityType type, bool forUpdate)
    {
        var properties = type.GetFlattenedProperties().Where(p => p.IsConcurrencyToken).ToList();
        foreach (var property in properties)
        {
            if (property.DeclaringType is not IEntityType
                || property.IsShadowProperty() || property.IsIndexerProperty()
                || forUpdate && ((property.ValueGenerated & ValueGenerated.OnUpdate) == 0
                    || property.GetAfterSaveBehavior() != PropertySaveBehavior.Ignore))
                throw new InvalidOperationException($"{type.ClrType.Name}.{property.Name}: unsupported concurrency mapping. "
                    + (forUpdate ? "Generic update requires store-generated CLR tokens with after-save behavior Ignore. "
                        : "Entity delete requires CLR tokens on the entity. ")
                    + "Use a specialized native EF operation for shadow/complex tokens or application-managed token renewal.");
        }
        return properties;
    }

    /// <summary>
    /// Awaits the operation and async context disposal, including on failure/cancellation.
    /// Does not save or enlist in a shared transaction. Await all work inside the callback.
    /// </summary>
    protected Task WithContextAsync(Func<TContext, CancellationToken, Task> operation,
        CancellationToken ct = default)
        => ContextOperations.WithContextAsync(_factory, operation, ct);

    /// <summary>
    /// Awaits the operation and async context disposal. Does not save implicitly.
    /// Materialize results inside the callback; do not return deferred queries, streams,
    /// lazy-loading dependencies, or other objects requiring the live context.
    /// </summary>
    protected Task<TResult> WithContextAsync<TResult>(
        Func<TContext, CancellationToken, Task<TResult>> operation,
        CancellationToken ct = default)
        => ContextOperations.WithContextAsync(_factory, operation, ct);
}
