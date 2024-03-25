using EfCoreNexus.Framework.Context;
using EfCoreNexus.Framework.Helper;
using Microsoft.EntityFrameworkCore;

namespace EfCoreNexus.TestApp.Data.Context;

/// <summary>
/// Factory to create DbContext for Entity Framework Migration. 
/// </summary>
public class MainContextFactory : BaseContextFactory<MainContext>
{
    /// <summary>
    /// Parameterless constructor called by migrations tool
    /// </summary>
    /// <exception cref="InvalidOperationException">Environment variable with Connection string was not set</exception>
    public MainContextFactory()
    {
        var connectionString = Environment.GetEnvironmentVariable("EFCORETOOLSDB");
        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException("The connection string was not set in the 'EFCORETOOLSDB' environment variable.");
        }

        OptionsBuilder = new DbContextOptionsBuilder<MainContext>();
        OptionsBuilder.UseSqlServer(connectionString);
    }

    public MainContextFactory(DataAssemblyConfiguration assemblyConf, DbContextOptionsBuilder<MainContext> optionsBuilder)
        : base(assemblyConf, optionsBuilder)
    {
    }

    public override MainContext CreateDbContext()
    {
        return new MainContext(OptionsBuilder.Options, EntityConfigurations);
    }
}