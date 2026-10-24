using EfCoreNexus.Framework.Helper;
using Microsoft.EntityFrameworkCore;

namespace EfCoreNexus.TestApp.Data.Context;

public sealed class MainContext(DbContextOptions<MainContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.RegisterNexusEntities(typeof(MainContext).Assembly);
        model.ApplyConfigurationsFromAssembly(typeof(MainContext).Assembly);
    }
}
