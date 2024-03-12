using EfCoreNexus.Framework.Helper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EfCoreNexus.Framework.Context;

/// <summary>
/// Base Factory to create DbContext for Entity Framework Migration. 
/// </summary>
public abstract class BaseContextFactory<TContext> : IDesignTimeDbContextFactory<TContext>, IDbContextFactory<TContext> where TContext : BaseContext<TContext>
{
    public DataAssemblyConfiguration AssemblyConfiguration { get; } = null!;
    protected readonly List<EntityTypeConfigurationDependency> EntityConfigurations = new();
    protected string? ConnectionString { get; set; }

    protected BaseContextFactory()
    {
        ConnectionString = Environment.GetEnvironmentVariable("EFCORETOOLSDB");
        if (string.IsNullOrEmpty(ConnectionString))
        {
            throw new InvalidOperationException("The connection string was not set in the 'EFCORETOOLSDB' environment variable.");
        }
    }

    protected BaseContextFactory(DataAssemblyConfiguration assemblyConf, string connectionString)
    {
        ConnectionString = connectionString;

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