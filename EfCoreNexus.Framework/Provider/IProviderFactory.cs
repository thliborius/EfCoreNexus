using EfCoreNexus.Framework.Services;
using Microsoft.EntityFrameworkCore;

namespace EfCoreNexus.Framework.Provider;

public interface IProviderFactory<T> where T : DbContext
{
    IEnumerable<IProvider> Create(TransactionService<T> transactionSvc);
}