using EfCoreNexus.Framework.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfCoreNexus.Framework.Provider;

/// <summary>Ordinary calls own a fresh context; context-first writes borrow the caller's context.</summary>
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
        return WithContextAsync((db, token) => CreateAsync(db, entity, token), ct);
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
        return WithContextAsync((db, token) => UpdateAsync(db, entity, token), ct);
    }

    /// <summary>Deletes the current row. Missing at lookup is a no-op; later races can conflict.</summary>
    public virtual Task DeleteAsync(TId id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        return WithContextAsync((db, token) => DeleteAsync(db, id, token), ct);
    }

    /// <summary>
    /// Deletes using the key and unchanged original CLR concurrency tokens, without a lookup.
    /// A missing row or stale version throws DbUpdateConcurrencyException.
    /// Application-managed CLR tokens are supported because deletion needs no renewal.
    /// </summary>
    public virtual Task DeleteAsync(TEntity entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return WithContextAsync((db, token) => DeleteAsync(db, entity, token), ct);
    }

    /// <summary>
    /// Adds and immediately saves the entire caller-owned tracker, including other entity types.
    /// Never creates/disposes the context or manages its transaction. Ordinary calls are independent.
    /// </summary>
    public virtual async Task CreateAsync(TContext db, TEntity entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);
        GetKey(db);
        await db.Set<TEntity>().AddAsync(entity, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the ordinary scalar-edit contract and immediately saves the entire caller-owned tracker,
    /// even for an unchanged edit. Reuses tracked identity and preserves already tracked input's
    /// original tokens; detached inputs supply expected tokens. Added/deleted update targets are rejected.
    /// Never owns the context or transaction. Generated values refresh tracked inputs only.
    /// </summary>
    public virtual async Task UpdateAsync(TContext db, TEntity entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);
        var key = GetKey(db);
        var source = db.Entry(entity);
        var concurrency = GetConcurrencyProperties(source.Metadata, forUpdate: true);
        var id = source.Property(key.Name).CurrentValue;
        if (source.State is EntityState.Added or EntityState.Deleted)
            throw new InvalidOperationException($"{typeof(TEntity).Name}: added/deleted entities require a specialized operation.");

        var row = await db.Set<TEntity>().FindAsync([id], ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"{typeof(TEntity).Name} with key '{id}' was not found.");
        var target = db.Entry(row);
        if (target.State is EntityState.Added or EntityState.Deleted)
            throw new InvalidOperationException($"{typeof(TEntity).Name}: added/deleted entities require a specialized operation.");
        if (source.State == EntityState.Detached && source.Metadata != target.Metadata)
            throw new InvalidOperationException($"Update requires the same mapped entity type; input is '{source.Metadata.Name}' and target is '{target.Metadata.Name}'.");
        if (target.State == EntityState.Detached)
            target.State = EntityState.Unchanged;

        if (source.State == EntityState.Detached)
        {
            target.DetectChanges();
            var changed = target.State == EntityState.Modified;
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

            if (changed)
                foreach (var property in concurrency)
                    target.Property(property.Name).OriginalValue = source.Property(property.Name).CurrentValue;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the current row by native FindAsync, including a tracked instance. A missing row is a
    /// no-op for this entity, but still saves the entire caller-owned tracker immediately.
    /// An Added target is rejected before changing its state or saving.
    /// Never owns the context or transaction. Ordinary calls use independent contexts.
    /// </summary>
    public virtual async Task DeleteAsync(TContext db, TId id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(id);
        GetKey(db);
        // FindAsync preserves tracked identity and query filters. Its database query uses
        // the context default, so temporarily enable tracking to retain owned shadow keys.
        var tracking = db.ChangeTracker.QueryTrackingBehavior;
        TEntity? row;
        try
        {
            db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
            row = await db.Set<TEntity>().FindAsync([id], ct).ConfigureAwait(false);
        }
        finally
        {
            db.ChangeTracker.QueryTrackingBehavior = tracking;
        }
        if (row is not null)
        {
            if (db.Entry(row).State == EntityState.Added)
                throw new InvalidOperationException($"{typeof(TEntity).Name}: ID delete cannot delete an Added entity before it has been saved.");
            db.Remove(row);
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes by key and expected CLR tokens without a database lookup, reusing any tracked instance.
    /// Tracked inputs retain original tokens; detached inputs supply expected tokens.
    /// Added targets are rejected with InvalidOperationException before changing state or saving.
    /// Immediately saves the entire caller-owned tracker. Never owns its context or transaction.
    /// </summary>
    public virtual async Task DeleteAsync(TContext db, TEntity entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);
        var key = GetKey(db);
        var source = db.Entry(entity);
        var concurrency = GetConcurrencyProperties(source.Metadata, forUpdate: false);
        var detached = source.State == EntityState.Detached;
        var target = source;
        if (detached)
        {
            var id = source.Property(key.Name).CurrentValue;
            target = db.ChangeTracker.Entries<TEntity>().FirstOrDefault(entry =>
                key.GetKeyValueComparer().Equals(entry.Property(key.Name).CurrentValue, id)) ?? source;
        }
        if (target.State == EntityState.Added)
            throw new InvalidOperationException($"{typeof(TEntity).Name}: entity delete cannot delete an Added entity before it has been saved.");
        target.State = EntityState.Deleted;
        if (detached)
            foreach (var property in concurrency)
                target.Property(property.Name).OriginalValue = source.Property(property.Name).CurrentValue;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
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
