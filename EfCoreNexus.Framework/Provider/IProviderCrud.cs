using EfCoreNexus.Framework.Entities;
using Microsoft.EntityFrameworkCore;

namespace EfCoreNexus.Framework.Provider;

public interface IProviderCrud<TEntity, in TId, TContext> : IProvider
    where TEntity : IEntity
    where TContext : DbContext
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