using EfCoreNexus.Framework.Helper;
using EfCoreNexus.Framework.Services;
using Microsoft.EntityFrameworkCore;

namespace EfCoreNexus.Framework.Provider;

public class ProviderFactory<TContext>(DataAssemblyConfiguration configuration) : IProviderFactory<TContext>
    where TContext : DbContext
{
    public IEnumerable<IProvider> Create(TransactionService<TContext> transactionSvc)
    {
        var lst = new List<IProvider>();

        foreach (var type in configuration.AssemblyData.DefinedTypes.Where(t => !t.IsAbstract && !t.IsGenericTypeDefinition && typeof(IProvider).IsAssignableFrom(t)))
        {
            var obj = Activator.CreateInstance(type, transactionSvc);
            if (obj != null)
            {
                lst.Add((IProvider)obj);
            }
        }

        return lst;
    }
}