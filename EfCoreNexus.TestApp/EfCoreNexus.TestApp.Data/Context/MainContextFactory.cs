using EfCoreNexus.Framework.Context;
using EfCoreNexus.Framework.Helper;
using Microsoft.EntityFrameworkCore;

namespace EfCoreNexus.TestApp.Data.Context;

/// <summary>
/// Factory to create DbContext for Entity Framework Migration. 
/// </summary>
public class MainContextFactory : BaseContextFactory<MainContext>
{
    public MainContextFactory()
    {
    }

    public MainContextFactory(DataAssemblyConfiguration assemblyConf, string connectionString) : base(assemblyConf)
    {
        ConnectionString = connectionString;
    }

    public override MainContext CreateDbContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<MainContext>();
        optionsBuilder.UseSqlServer(ConnectionString);

        return new MainContext(optionsBuilder.Options, EntityConfigurations);
    }
}