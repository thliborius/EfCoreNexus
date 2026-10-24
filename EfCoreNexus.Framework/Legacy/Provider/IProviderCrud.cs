using EfCoreNexus.Framework.Entities;
using EfCoreNexus.Framework.Provider;

namespace EfCoreNexus.Framework.Legacy.Provider;

public interface IProviderCrud<TEntity, in TId> : IProvider
    where TEntity : IEntity
{
    // CREATE
    Task Create(TEntity item, TId id);

    // READ
    IList<TEntity> GetAll();
    Task<IList<TEntity>> GetAllAsync();
    Task<TEntity?> GetById(TId id);

    // UPDATE
    Task Update(TEntity item, TId id);

    // DELETE
    Task Delete(TId id);
}
