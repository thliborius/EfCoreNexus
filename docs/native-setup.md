# Native setup and async CRUD on v2

**Less boilerplate. More Entity Framework.** Configure an assembly/model boundary
once. An eligible new entity or concrete provider inside it needs no per-type DI
registration or `DbSet` property. Discovery is optional; native EF registration
and model building remain available.

This page describes implemented setup, ordinary async CRUD (#10), and custom
operations. Shared-context writes and `TransactionRunner` follow in #11.
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
interface selection. The optional ordinary CRUD interface is mapped explicitly:

```csharp
services.AddScoped<IProviderCrud<Customer, int>>(
    sp => sp.GetRequiredService<CustomerProvider>());
```

`IProviderCrud<TEntity,TId>` has only the six async methods below, with
`TEntity : class` and `TId : notnull`; it requires neither `IEntity` nor `IProvider`.

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

## Ordinary async CRUD

For a simple edit, native EF needs context creation/disposal, key lookup, scalar
assignment and a save. The provider removes that repeated code:

```csharp
var customer = new Customer("Ada");
await Customers.CreateAsync(customer, ct); // Generated Id is available.
var edit = await Customers.GetByIdAsync(customer.Id, ct)
    ?? throw new KeyNotFoundException();
edit.Name = "Grace";
await Customers.UpdateAsync(edit, ct);
await Customers.DeleteAsync(customer.Id, ct);
```

All six methods are virtual and accept `CancellationToken ct = default`. Each
ordinary call uses a fresh context and awaits asynchronous disposal on success,
failure or cancellation. EF queries, inserts and saves use their async APIs;
there is no hidden transaction enlistment or retry.

| Method | Contract |
| --- | --- |
| `GetAllAsync(ct)` | Materialized `List<TEntity>`, no tracking; use specialized LINQ for large sets. |
| `GetByIdAsync(id, ct)` | Native `FindAsync`; returns null when absent. The result has no live operation context. |
| `CreateAsync(entity, ct)` | Native add/save, including reachable new entities and generated keys. Duplicates are database `DbUpdateException`s, without an existence pre-check. |
| `UpdateAsync(entity, ct)` | Full edit of writable mapped CLR scalars; missing at lookup throws `KeyNotFoundException`. |
| `DeleteAsync(id, ct)` | Deletes the current row; missing at lookup is a successful no-op. A later change/delete can raise `DbUpdateConcurrencyException`. |
| `DeleteAsync(entity, ct)` | Deletes by key and expected original CLR tokens without a lookup. Missing or stale rows raise `DbUpdateConcurrencyException`. Without tokens, only existence is checked. |

Every CRUD call validates native primary-key metadata: one CLR key whose type
matches `TId` exactly. Convention and Fluent mappings work, including `Guid`,
`int`, `string`, generated keys and private setters. Unmapped/keyless entities,
composite/shadow/indexer keys and mismatched `TId` produce an
`InvalidOperationException` identifying the entity/key before any CRUD SQL.
Native EF remains available for those models. Null arguments produce
`ArgumentNullException`; EF/database failures and cancellation propagate.

### Full scalar edits and detached concurrency

Update reads the current row, compares eligible values using EF value comparers,
and copies only changed scalars. Metadata excludes all keys, generated
properties, shadow/indexer values and properties whose after-save behavior is
not `Save`. Native backing-field mappings and value converters work. Navigation,
owned and complex graphs are not copied. Omitted/default scalar values are still
supplied values: this is a full edit, so use a specialized provider for partial or
allowed-field updates. Unchanged edits issue no update and do not assert a version.

Keep **every original CLR concurrency token unchanged** on the detached entity
shown to the user. Update transfers these values to EF's `OriginalValue` on the
loaded row, preserving stale-edit detection despite the fresh lookup. It supports
only tokens generated on update with after-save behavior `Ignore`; it never
writes token columns. Shadow/indexer/complex tokens and application-managed token
renewal are explicitly rejected with `InvalidOperationException`. Entity delete
accepts application-managed CLR tokens because it needs no renewal; it rejects
shadow/indexer/complex tokens as well. ID delete uses the currently loaded tokens.

A missing update at lookup is distinct from a stale edit or deletion between
lookup and save, which raises EF's `DbUpdateConcurrencyException`. The application
chooses reload/conflict handling; without tokens, normal last-writer behavior
applies. Update does not refresh the detached input's generated values: read again
before another edit/delete. For SQL Server, `IsRowVersion()` has the intended
metadata shape, but **SQL Server rowversion behavior has not been integration
tested**. SQLite tests use an actual computed concurrency column; they do not
establish SQL Server parity. See [EF concurrency](https://learn.microsoft.com/en-us/ef/core/saving/concurrency).

For application-managed tokens, a specialized operation keeps original and new
values explicit using native EF:

```csharp
public Task RenameAsync(int id, string name, Guid expectedRevision,
    CancellationToken ct = default)
    => WithContextAsync(async (db, token) =>
    {
        var row = await db.Set<Customer>().FindAsync([id], token)
            ?? throw new KeyNotFoundException();
        db.Entry(row).Property(x => x.Revision).OriginalValue = expectedRevision;
        row.Name = name;
        row.Revision = Guid.NewGuid();
        await db.SaveChangesAsync(token);
    }, ct);
```

This example assumes `Customer` also maps a `Guid Revision` as a concurrency
token. There is no generic merge, patch, token factory or graph updater.

## Ownership and temporary legacy boundary

Both protected `WithContextAsync` forms delegate to #8's `ContextOperations`.
Each call creates one native factory context, forwards cancellation, awaits the
callback and asynchronous disposal, and never saves implicitly. Callback failure
or cancellation remains the primary exception; a simultaneous disposal failure is
attached under `Exception.Data["EfCoreNexus.DisposeAsyncException"]`. Disposal-only
failures propagate. Materialize within the callback; do not return queries,
streams or lazy-loading dependencies, or leave background work running.

`ProviderBase<TEntity,TId,TContext>` now accepts only `IDbContextFactory<TContext>`
with `TEntity : class`, `TId : notnull`, `TContext : DbContext`. It offers
the six ordinary CRUD methods and two ownership helpers. It neither depends on
nor participates in `TransactionService`.

The old provider implementation and its CRUD interface are retained in
`EfCoreNexus.Framework.Legacy.Provider`. This is a **source-breaking
namespace/constructor/interface change** for existing providers and callers
explicitly naming the old interface. The sample and inherited
tests explicitly import the legacy namespace. There is no dual-constructor base,
automatic fallback, adapter, or new CRUD/transaction implementation.

| Temporarily retained | Why / removal boundary |
| --- | --- |
| Legacy `ProviderBase`, `IProviderCrud`, `IProvider`, `ProviderFactory` / `IProviderFactory`, `DataService` / `IDataService`, `TransactionService` | Existing sample CRUD and transactions still depend on them. Keep behavior/regression tests until #10/#11 and consumer migration permit removal; do not port new features to this path. |
| `BaseContext`, `BaseContextFactory`, `StartupConfiguration`, `DataAssemblyConfiguration`, `EntityTypeConfigurationDependency` | Still required by that sample/setup and its regression tests. Native consumers bypass the entire chain; remove it when remaining consumers migrate. |
| `System.Linq.Dynamic.Core` | Only the legacy key query still needs it; review removal after #10 and remaining legacy consumers. No dependency was added or changed for #9/#10. |

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


## Validation for issue #10

Based on `origin/v2` at `6cd3471`, after PR #14 merged. Executed on Linux with
.NET SDK 10.0.113 / runtime 10.0.13 and EF Core 10.0.13:

```sh
dotnet restore
dotnet restore -p:NuGetAudit=true -p:NuGetAuditMode=all -warnaserror:NU1901,NU1902,NU1903,NU1904
dotnet build --no-restore -p:GeneratePackageOnBuild=false --verbosity minimal
dotnet test --no-build --no-restore --filter FullyQualifiedName~AsyncCrudTests --verbosity minimal
dotnet test --no-build --no-restore --logger 'trx;LogFileName=issue-10.trx' --results-directory /tmp/efcorenexus-issue-10-results --verbosity minimal
dotnet list package --vulnerable --include-transitive
```

Restore and build passed with zero build warnings/errors. All **116 tests passed**
without failures/skips: 90 existing cases and **26 new CRUD cases**. The focused
CRUD run passed all 26. Coverage includes the six-method interface, metadata keys,
generated IDs, database duplicates, actual changed SQL columns, full-edit defaults,
EF comparers/backing fields/converters, untouched navigation/owned/complex graphs,
missing-row contracts, stale and overlapping updates, multiple generated tokens
(including binary values), lookup/save deletion races, both delete variants,
explicit application-managed renewal, rejected mappings, cancellation and async
context disposal. Existing ownership and DI cases were retained without repetition.
The transitive audit found no known vulnerable packages in all four projects.
No packages were published; SQL Server and Windows were not exercised.
