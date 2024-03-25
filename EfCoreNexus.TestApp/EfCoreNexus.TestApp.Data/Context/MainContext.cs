using EfCoreNexus.Framework.Context;
using EfCoreNexus.Framework.Helper;
using Microsoft.EntityFrameworkCore;

namespace EfCoreNexus.TestApp.Data.Context;

public class MainContext(DbContextOptions<MainContext> options, IEnumerable<EntityTypeConfigurationDependency> configurations) : BaseContext<MainContext>(options, configurations);