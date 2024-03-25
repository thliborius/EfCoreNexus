using EfCoreNexus.Framework.Helper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EfCoreNexus.Framework.Context;

/// <summary>
/// Base Factory to create DbContext for Entity Framework Migration. 
/// </summary>
public abstract class BaseContextFactory<TContext> : IDesignTimeDbContextFactory<TContext>, IDbContextFactory<TContext> where TContext : BaseContext<TContext>
{
    public DataAssemblyConfiguration AssemblyConfiguration { get; }
    protected readonly List<EntityTypeConfigurationDependency> EntityConfigurations;
    protected DbContextOptionsBuilder<TContext> OptionsBuilder { get; set; }

    /// <summary>
    /// Parameterless constructor called by migrations tool
    /// </summary>
    protected BaseContextFactory()
    {
    }

    protected BaseContextFactory(DataAssemblyConfiguration assemblyConf, DbContextOptionsBuilder<TContext> optionsBuilder)
    {
        OptionsBuilder = optionsBuilder;

        AssemblyConfiguration = assemblyConf;
        EntityConfigurations = new List<EntityTypeConfigurationDependency>();
        foreach (var type in assemblyConf.AssemblyData.DefinedTypes.Where(t => !t.IsAbstract && !t.IsGenericTypeDefinition && typeof(EntityTypeConfigurationDependency).IsAssignableFrom(t)))
        {
            var item = (EntityTypeConfigurationDependency?)Activator.CreateInstance(type);
            if (item != null)
            {
                EntityConfigurations.Add(item);
            }
        }
    }

    public TContext CreateDbContext(string[] args)
    {
        return CreateDbContext();
    }

    public abstract TContext CreateDbContext();

}