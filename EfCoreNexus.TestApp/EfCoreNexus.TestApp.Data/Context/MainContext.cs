using EfCoreNexus.Framework.Context;
using EfCoreNexus.Framework.Helper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCoreNexus.TestApp.Data.Context;

public class MainContext(DbContextOptions<MainContext> options, IEnumerable<EntityTypeConfigurationDependency> configurations) : BaseContext<MainContext>(options, configurations)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);

        optionsBuilder.ConfigureWarnings(w => w.Ignore(SqlServerEventId.SavepointsDisabledBecauseOfMARS));
        optionsBuilder.UseSqlServer(opt => opt.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
    }
}