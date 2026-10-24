# Native setup on v2 (issue #9)

**Less boilerplate. More Entity Framework.** Configure an assembly/model boundary
once. An eligible new entity or concrete provider inside it needs no per-type DI
registration or `DbSet` property. Discovery is optional; native EF registration
and model building remain available.

This page describes implemented setup and custom operations. Ordinary v2 CRUD
follows in #10; shared-context writes and `TransactionRunner` follow in #11.
The complete future API remains in [architecture-v2.md](architecture-v2.md).

## Before and after

The inherited setup needs a `BaseContext` subclass, a `BaseContextFactory`
subclass, configuration wrapper classes, and startup/provider lookup plumbing:

```csharp
var options = new DbContextOptionsBuilder<MainContext>().UseSqlServer(connection);
var assembly = new DataAssemblyConfiguration(typeof(Customer).Assembly.GetName().Name!);
var factory = new MainContextFactory(assembly, options);
new StartupConfiguration<MainContext>(factory, options).ConfigureDataservice(services);
// Consumers retrieve providers through DataService.
```

The native path uses `EfCoreNexus.Framework.Helper` and
`EfCoreNexus.Framework.Provider` alongside normal EF/DI namespaces:

```csharp
builder.Services.AddDbContextFactory<MainContext>(o => o.UseSqlServer(connection));
builder.Services.AddNexusProviders<MainContext>(typeof(CustomerProvider).Assembly);
// Consumers inject CustomerProvider directly.

public sealed class MainContext(DbContextOptions<MainContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        model.RegisterNexusEntities(typeof(Customer).Assembly);
        model.ApplyConfigurationsFromAssembly(typeof(Customer).Assembly);
        // Explicit application overrides, if needed, go last.
    }
}

public sealed class Customer(string name) : IEntity
{
    public int Id { get; private set; }
    public string Name { get; set; } = name;
}

public sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
        => builder.Property(x => x.Name).HasMaxLength(200);
}

public sealed class CustomerProvider(IDbContextFactory<MainContext> factory,
    ILogger<CustomerProvider> logger) : ProviderBase<Customer, int, MainContext>(factory)
{
    public Task<List<Customer>> GetCustomersAsync(CancellationToken ct = default)
        => WithContextAsync((db, token) => db.Set<Customer>()
            .AsNoTracking().OrderBy(x => x.Name).ToListAsync(token), ct);

    public Task AddCustomerAsync(string name, CancellationToken ct = default)
        => WithContextAsync(async (db, token) =>
        {
            db.Add(new Customer(name));
            await db.SaveChangesAsync(token); // The helper never saves implicitly.
            logger.LogInformation("Customer created");
        }, ct);
}
```

The connection and logging setup belong to the application. Use a normal
`IDesignTimeDbContextFactory<MainContext>` only if EF tooling needs one; it must
use the same provider/options/model as runtime. Neither helper registers a
runtime factory, design-time factory, configuration object, or database provider.
The runtime/tooling model parity test uses native factories and SQLite.

## Discovery rules

`AddNexusProviders<TContext>(params Assembly[] assemblies)` scans only the supplied
assemblies. Concrete, closed subclasses of `ProviderBase<,,>`, including indirect
subclasses, are included for the **exact** `TContext`. Abstract/open generic and
unrelated-context providers are excluded. Providers need no `IEntity` constraint.
Normal .NET DI chooses constructors, validates dependencies and disposes scoped
providers/dependencies; the scan never constructs them.

Duplicate assemblies and repeated scans do not add descriptors. A single explicit
`AddScoped<CustomerProvider>()` is accepted. Other unkeyed descriptors for that
concrete service (another lifetime/implementation, factory, instance, or multiple
descriptors) throw `InvalidOperationException` naming the provider, context and
registrations. The scan validates all conflicts before adding anything. Keyed
services are separate and do not collide with unkeyed discovery. Later manual DI
changes remain the application's responsibility; register overrides deliberately
instead of relying on the container's last-registration behavior.

Two providers for one entity are legal concrete services. There is no automatic
interface selection. Ordinary v2 `IProviderCrud` mappings arrive with #10; the
currently retained interface still belongs to the legacy CRUD contract.

`RegisterNexusEntities(Assembly assembly, Func<Type,bool>? predicate = null)`
includes concrete, closed `IEntity` classes without constructing them or requiring
a parameterless constructor. It excludes `[NotMapped]`, `[Owned]`, `[ComplexType]`,
and types already ignored or mapped as owned/complex (including nested complex
properties). Repeated discovery preserves existing mappings. Conflicting owned
and complex annotations and shared-type roots are diagnosed; use explicit EF
mapping for shared-type entities. Native EF still validates keys and mappings.

The predicate limits this helper's roots, not EF's navigation discovery or
configuration scanning. For two contexts in one assembly, choose a stable model
boundary for **both** discovery calls, for example:

```csharp
model.RegisterNexusEntities(typeof(Customer).Assembly,
    type => type.Namespace == "App.Sales.Entities");
model.ApplyConfigurationsFromAssembly(typeof(CustomerConfiguration).Assembly,
    type => type.Namespace == "App.Sales.Configurations");
```

Configure unannotated owned/complex types **before** discovery, or omit `IEntity`
on those value types. A scanner cannot predict future Fluent calls. Apply normal
root configurations after discovery, then final context overrides. Configuration
classes can introduce entities without the marker. Native configuration scanning
has no defined application order; use explicit `ApplyConfiguration` for ordering
or constructor dependencies. No configuration services need registering in DI.
See [native EF model configuration](https://learn.microsoft.com/en-us/ef/core/modeling/).

Arguments are validated; reflection/type-load failures propagate rather than
silently producing a partial list. No calling-assembly/AppDomain scan, reflection
cache, extra scanning dependency, or registration framework is used. For
trimming/AOT, use explicit native DI/model registration; these scans do not claim
trim safety or Native AOT support.

## Ownership and temporary legacy boundary

Both protected `WithContextAsync` forms delegate to #8's `ContextOperations`.
Each call creates one native factory context, forwards cancellation, awaits the
callback and asynchronous disposal, and never saves implicitly. Callback failure
or cancellation remains the primary exception; a simultaneous disposal failure is
attached under `Exception.Data["EfCoreNexus.DisposeAsyncException"]`. Disposal-only
failures propagate. Materialize within the callback; do not return queries,
streams or lazy-loading dependencies, or leave background work running.

`ProviderBase<TEntity,TId,TContext>` now accepts only `IDbContextFactory<TContext>`
with `TEntity : class`, `TId : notnull`, `TContext : DbContext`. It currently offers
the two ownership helpers; #10 supplies its ordinary CRUD contract. It neither
depends on nor participates in `TransactionService`.

The old provider implementation moved unchanged to
`EfCoreNexus.Framework.Legacy.Provider.ProviderBase`. This is a **source-breaking
namespace/constructor change** for existing providers. The sample and inherited
tests explicitly import the legacy namespace. There is no dual-constructor base,
automatic fallback, adapter, or new CRUD/transaction implementation.

| Temporarily retained | Why / removal boundary |
| --- | --- |
| Legacy `ProviderBase`, `IProviderCrud`, `IProvider`, `ProviderFactory` / `IProviderFactory`, `DataService` / `IDataService`, `TransactionService` | Existing sample CRUD and transactions still depend on them. Keep behavior/regression tests until #10/#11 and consumer migration permit removal; do not port new features to this path. |
| `BaseContext`, `BaseContextFactory`, `StartupConfiguration`, `DataAssemblyConfiguration`, `EntityTypeConfigurationDependency` | Still required by that sample/setup and its regression tests. Native consumers bypass the entire chain; remove it when remaining consumers migrate. |
| `System.Linq.Dynamic.Core` | Only the legacy key query still needs it; review removal after #10 and remaining legacy consumers. No package/dependency was added for #9. |

No legacy CRUD, tracking or transaction semantics were changed. Its characterized
ownership defects remain visible in `LegacyContextOwnershipTests`; the native
provider path has no ambient transaction state. The Blazor sample still shows
the legacy API pending its planned migration in #12. No schema migration or
SQL Server/Windows execution is claimed by the SQLite/Linux tests.

## Validation for issue #9

Based on `origin/v2` at `9f9d6ff` (merged #8). Executed on Linux with .NET SDK
10.0.113 / runtime 10.0.13 and EF Core 10.0.13:

```sh
dotnet restore
dotnet restore -p:NuGetAudit=true -p:NuGetAuditMode=all -warnaserror:NU1901,NU1902,NU1903,NU1904
dotnet build --no-restore -p:GeneratePackageOnBuild=false --verbosity minimal
dotnet test --no-build --no-restore --logger 'trx;LogFileName=issue-9.trx' --results-directory /tmp/efcorenexus-issue-9-results --verbosity minimal
dotnet list package --vulnerable --include-transitive
```

Restore succeeded; the final build had zero warnings/errors. **90 tests passed**
with zero failures/skips: all 45 existing cases, 24 additional runs of the #8
ownership contract through the new provider surface, and 21 DI/model cases.
Coverage includes scoped constructor dependencies/disposal, exact context matching,
indirect inheritance, idempotency/conflicts, marker-only and configuration-only
roots, constructor binding, excluded classifications, Fluent configuration order,
two models in one assembly and independently built runtime/design-time models.
The audit found no known vulnerable packages in all four projects, including
transitive dependencies, using nuget.org. No package versions/dependencies changed;
local package generation was disabled and nothing was published.
