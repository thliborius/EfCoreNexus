using EfCoreNexus.Framework.Entities;
using EfCoreNexus.Framework.Helper;
using Microsoft.EntityFrameworkCore;
using System.Reflection;

namespace EfCoreNexus.Framework.Context;

public abstract class BaseContext<TContext>(DbContextOptions<TContext> options, IEnumerable<EntityTypeConfigurationDependency> configurations) : DbContext(options)
    where TContext : DbContext
{
    private readonly Assembly _assemblyEntities = Assembly.GetCallingAssembly();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Register configurations of all entities in the modelBuilder
        foreach (var entityTypeConfiguration in configurations)
        {
            entityTypeConfiguration.Configure(modelBuilder);
        }

        // Register all entities of the type IEntity in the modelbuilder
        // With ctx.Set<TEntity>() the DbSet of the entity could automatically retrieved
        // and mustn't be registered manually as property in the dbContext class
        var entityBuilderTypes = _assemblyEntities.GetTypes().Where(asyType => asyType.GetInterfaces().Contains(typeof(IEntity)));
        var typesWithConstructors = entityBuilderTypes.Where(ebType => ebType.GetConstructor(Type.EmptyTypes) != null);

        foreach (var type in typesWithConstructors)
        {
            modelBuilder.Entity(type);
        }
    }
}