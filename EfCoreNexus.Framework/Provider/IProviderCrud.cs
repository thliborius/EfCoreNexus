namespace EfCoreNexus.Framework.Provider;

/// <summary>Optional ordinary CRUD contract for single CLR primary keys.</summary>
public interface IProviderCrud<TEntity, in TId>
    where TEntity : class
    where TId : notnull
{
    Task<List<TEntity>> GetAllAsync(CancellationToken ct = default);
    Task<TEntity?> GetByIdAsync(TId id, CancellationToken ct = default);
    Task CreateAsync(TEntity entity, CancellationToken ct = default);
    Task UpdateAsync(TEntity entity, CancellationToken ct = default);
    Task DeleteAsync(TId id, CancellationToken ct = default);
    Task DeleteAsync(TEntity entity, CancellationToken ct = default);
}
