using EfCoreNexus.Framework.Provider;
using EfCoreNexus.Framework.Services;
using EfCoreNexus.TestApp.Data.Context;
using EfCoreNexus.TestApp.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace EfCoreNexus.TestApp.Data.Provider;

public class TestProvider : ProviderBase<Test, Guid, MainContext>
{
    public TestProvider(TransactionService<MainContext> transactionSvc) : base(transactionSvc)
    {
    }

    protected override IQueryable<Test> GetAllQuery(DbContext ctx)
    {
        return base.GetAllQuery(ctx).Where(x => x.Active == true);
    }
}