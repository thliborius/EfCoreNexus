using EfCoreNexus.Framework.Context;
using EfCoreNexus.Framework.Provider;
using EfCoreNexus.Framework.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EfCoreNexus.Framework.Helper;

public class StartupConfiguration<TContext>(BaseContextFactory<TContext> dbFactory, DbContextOptionsBuilder<TContext> optionsBuilder)
    where TContext : BaseContext<TContext>
{
    public void ConfigureDataservice(IServiceCollection services)
    {
        services.AddScoped(x => optionsBuilder.Options);
        services.AddScoped(x => (IDbContextFactory<TContext>)dbFactory);
        services.AddScoped(x => dbFactory.AssemblyConfiguration);
        services.AddScoped<IProviderFactory<TContext>, ProviderFactory<TContext>>();
        services.AddScoped<TContext>();
        services.AddScoped<DataService<TContext>>();

        // Add configurations for DbContext to services
        foreach (var type in dbFactory.AssemblyConfiguration.AssemblyData.DefinedTypes
                     .Where(t => !t.IsAbstract && !t.IsGenericTypeDefinition && typeof(EntityTypeConfigurationDependency).IsAssignableFrom(t)))
        {
            services.AddSingleton(typeof(EntityTypeConfigurationDependency), type);
        }
    }
}