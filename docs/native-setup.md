# Native setup and async CRUD on v2

**Less boilerplate. More Entity Framework.** Configure an assembly/model boundary
once. An eligible new entity or concrete provider inside it needs no per-type DI
registration or `DbSet` property. Discovery is optional; native EF registration
and model building remain available.

This page describes implemented setup, ordinary async CRUD (#10), custom
operations, and optional shared-context transactions (#11). The design and
trade-offs remain in [architecture-v2.md](architecture-v2.md).

## Native setup

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

## Security boundaries: filters, authorization and full edits

**Query filters do not replace authorization.** The application must authorize
user and tenant access to each operation and validate changes on the server.
EfCoreNexus has no user/tenant permission context and provides no authorization
or tenant-isolation guarantee. Concurrency tokens detect stale data, not access
rights. A soft-delete query filter alone does not turn deletes into soft deletes.
See [EF query filters](https://learn.microsoft.com/en-us/ef/core/querying/filters).

SQLite integration tests in `AsyncCrudTests` use tenant IDs 1/2, a soft-delete
filter and a generated concurrency token. They verify actual persisted rows and
captured SQL, with these results:

| Operation | Filter behavior |
| --- | --- |
| `GetAllAsync`, `GetByIdAsync` | Both hide foreign-tenant and soft-deleted rows with fresh factory contexts. `GetByIdAsync` uses `FindAsync`; it is not intrinsically an authorization check. |
| `UpdateAsync(entity)` / `UpdateAsync(db, entity)`, target not tracked | Filtered lookup cannot find the row: `KeyNotFoundException`, no target UPDATE. A detached foreign-tenant snapshot alone does not bypass this lookup. |
| `DeleteAsync(id)` / `DeleteAsync(db, id)`, target not tracked | Filtered lookup cannot find the row: no target DELETE. |
| Borrowed update or ID delete, target already tracked | `FindAsync` reuses tracked identity without a query/filter recheck. A hidden row loaded using `IgnoreQueryFilters` can be updated or physically deleted, including when the update input is a detached snapshot. |
| `DeleteAsync(entity)` / `DeleteAsync(db, entity)` | No lookup: even hidden rows can be physically deleted with correct key/tokens. This also applies to tracked targets and detached snapshots matching tracked identity. Stale tokens still raise `DbUpdateConcurrencyException`. |

There are no borrowed read overloads; reads on a shared context use native EF.
Native [FindAsync checks tracked identity first](https://learn.microsoft.com/en-us/ef/core/change-tracking/entity-entries#find-and-findasync);
a normal filtered LINQ query still goes to the database. Merely tracking an entity
in another context does not make it tracked in an ordinary provider call's fresh
context. Do not reuse a context across authorization/tenant boundaries.

In the tested model, UPDATE/DELETE predicates contain the primary key and original
concurrency token, **not** `TenantId` or `IsDeleted`. Filters affect the lookup,
not subsequent `SaveChanges` predicates; they cannot prevent an intervening change
to tenant/visibility unless a separate application/database rule covers it. These
are expected EF behaviors, not reproduced defects in EfCoreNexus. Borrowed writes
also save all other pending tracker changes, even when the target lookup is a no-op.

Never pass unchecked user-controlled entities into generic full-scalar updates.
Writable scalars such as `TenantId`, `IsDeleted` and `IsAdmin` are ordinary fields:
a visible row can be moved to another tenant, hidden or granted privileges by such
an edit. Prefer DTOs and explicit allowed assignments, or a specialized provider
operation that loads and authorizes the row, applies permitted changes and saves.
An unchecked full edit such as `await Customers.UpdateAsync(boundEntity)` should
instead become an application-specific operation such as
`await Customers.RenameAsync(id, renameDto.Name, expectedRevision, ct)`, with
server-side permission checks inside that operation. DTOs alone are not authorization.

### Factory lifetime for tenant-dependent configuration

`AddDbContextFactory` defaults to singleton and caches options. If options or
factory dependencies depend on the current user/tenant, choose a compatible
lifetime; in Blazor Server, `ServiceLifetime.Scoped` normally follows the circuit.
A scoped factory still caches its options: switching tenant/database within a
circuit requires configuration to be reevaluated, and existing providers/contexts
must not retain the old configuration. Simply registering a transient factory
does not refresh one already captured by a scoped provider. Keep tenant state on
each context instance when used in filters, and review scope/switching behavior in
the application. See Microsoft's [multi-tenancy and factory lifetime guidance](https://learn.microsoft.com/en-us/ef/core/miscellaneous/multitenancy).

## Optional multi-provider transactions

For operations that need only one save, use a native factory context and
`SaveChangesAsync`: EF already supplies the transaction. For several immediate
provider saves, the optional `EfCoreNexus.Framework.Services.TransactionRunner<TContext>`
removes repeated begin/rollback/cleanup code. Register it explicitly if needed:

```csharp
services.AddScoped<TransactionRunner<MainContext>>();

// Customers, Orders and Transactions are injected concrete services.
await Transactions.ExecuteAsync(async (db, token) =>
{
    var customer = new Customer("Ada");
    await Customers.CreateAsync(db, customer, token); // Saves now; generated Id is available.
    await Orders.CreateAsync(db, new Order { CustomerId = customer.Id }, token);
    db.Add(new AuditEntry { Message = "Customer and order created" }); // Runner's final save.
}, ct);
```

The runner accepts `IDbContextFactory<TContext>` and exposes only
`Task ExecuteAsync(Func<TContext, CancellationToken, Task> operation, CancellationToken ct = default)`.
Each invocation owns a fresh context and native async transaction, awaits the
callback, calls `SaveChangesAsync`, commits, then asynchronously disposes the
transaction and context. The factory must supply independently usable contexts
and connections. No transaction state is stored in DI. Overlapping invocations
are independent; SQLite still permits only one writer at a time.

Four virtual overloads on `ProviderBase<TEntity,TId,TContext>` borrow the exact
caller-owned context; they are not added to `IProviderCrud<TEntity,TId>`:

```csharp
Task CreateAsync(TContext db, TEntity entity, CancellationToken ct = default);
Task UpdateAsync(TContext db, TEntity entity, CancellationToken ct = default);
Task DeleteAsync(TContext db, TId id, CancellationToken ct = default);
Task DeleteAsync(TContext db, TEntity entity, CancellationToken ct = default);
```

They never create/dispose that context or begin/commit/roll back its transaction.
They work with caller-managed native transactions too. **Each call immediately
saves the entire tracker**, including other entity types and direct EF changes.
Even an unchanged update or a missing ID delete saves other pending changes.
Only supply a context whose whole pending state belongs to the operation. For
precise staging, use native EF and explicit saves instead. Ordinary write methods
delegate to these virtual overloads through their ownership helper, so a borrowed
write override serves both paths. Ordinary no-change updates and missing ID
deletes now also invoke the save lifecycle (including EF save interceptors),
while still issuing no update/delete SQL for the target row.

Reads on the borrowed context stay native: `db.Set<Customer>().FindAsync([id], token)`
or ordinary LINQ. An ordinary provider call without `db`, even inside the runner
callback, creates and disposes an **independent** context and does not join this
transaction. The runner cannot roll that separate operation back. Independent
calls may contend for database locks; use the explicit context for participating
work. Await all callback work sequentially; do not use the same context concurrently,
return live queries or retain it after the callback. A second runner call is
another independent transaction, not nested transaction support.

### Tracking and concurrency

The #10 key/scalar/token restrictions still apply. Update uses native `FindAsync`
and reuses tracked identity; detached entity delete searches the local tracker
without a database existence lookup. Neither attaches a replacement instance for
an already tracked key. Tracked inputs retain EF's existing original concurrency
values, even if current token values changed. Detached inputs supply expected
tokens to the tracked target, including an already pending scalar update. A
no-change detached edit of an unchanged row still asserts no version. Added or
deleted tracked update inputs/targets are rejected instead of becoming upserts.
Entity delete also rejects an `Added` target with `InvalidOperationException`
before changing its state or saving, including detached input matching its key.

Tracked entities receive generated keys/tokens after each successful save and can
be edited again within the same context. Detached edits are not refreshed; reload
before reusing their tokens. Application-managed tokens still require a specialized
native update with explicit original/new values; generic entity delete supports
these tokens. Conflicts propagate as `DbUpdateConcurrencyException`. Rollback
reverts database writes, **not** in-memory keys, refreshed tokens or tracker state;
discard failed workflow entities. Let provider failures escape the callback;
swallowing them and continuing is not a supported recovery pattern.

### Failure and cleanup

Creation failures propagate; the factory owns resources it never returned. Begin
failures still dispose the acquired context. Callback, final-save, commit and
cancellation failures attempt rollback using a separate **five-second** token,
independent of request cancellation. This is a cooperative cancellation budget;
a provider that ignores cancellation cannot be forcibly stopped. Transaction and
context disposal are always awaited in that order, even after rollback/disposal
failure. Disposal APIs have no cancellation token.

The original exception and stack survive. Secondary cleanup errors are attached
using standard `Exception.Data`, without a new exception or diagnostics service:

| Data key | Value |
| --- | --- |
| `EfCoreNexus.RollbackAsyncException` | Secondary rollback exception, including cleanup timeout. |
| `EfCoreNexus.TransactionDisposeAsyncException` | Secondary transaction disposal exception. |
| `EfCoreNexus.DisposeAsyncException` | Secondary context disposal exception (same as the ownership helper). |
| `EfCoreNexus.CommitOutcome` | `"Committed"` for cleanup failure after acknowledged commit; `"Unknown"` if commit threw. Absent if commit was never attempted. |

A cleanup-only exception propagates directly. After acknowledged commit the runner
never attempts rollback; a cleanup exception does not mean the changes were lost.
If commit throws, even on cancellation or after a successful rollback attempt,
its outcome may be unknown. No verification or exactly-once guarantee is supplied.
Do not blindly replay the operation.

This is a single-attempt runner: no retry, savepoint, ambient transaction, nesting
or execution-strategy wrapper is added. **Retrying execution strategies are not
validated/supported by this implementation.** EF can reject a user transaction
under such a strategy. Native composition would have to wrap the whole invocation
with fresh contexts and mutable entities per attempt, replay-safe callbacks and
application-specific handling of uncertain commits; this has not been tested.
See [EF transaction limitations](https://learn.microsoft.com/en-us/ef/core/saving/transactions)
and [execution strategies and commit uncertainty](https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency).
SQL Server rowversion, SQL Server transaction behavior and Windows execution
remain unverified; SQLite tests establish only the covered SQLite behavior.

## Context ownership

Both protected `WithContextAsync` forms delegate to #8's `ContextOperations`.
Each call creates one native factory context, forwards cancellation, awaits the
callback and asynchronous disposal, and never saves implicitly. Callback failure
or cancellation remains the primary exception; a simultaneous disposal failure is
attached under `Exception.Data["EfCoreNexus.DisposeAsyncException"]`. Disposal-only
failures propagate. Materialize within the callback; do not return queries,
streams or lazy-loading dependencies, or leave background work running.

`ProviderBase<TEntity,TId,TContext>` now accepts only `IDbContextFactory<TContext>`
with `TEntity : class`, `TId : notnull`, `TContext : DbContext`. It offers
the six ordinary CRUD methods, four borrowed write overloads and two ownership
helpers. It neither depends on nor participates in `TransactionService`.

Legacy components were removed in #12. See [migration from v1](migration-v2.md) and
[current validation](validation-12.md); earlier ticket evidence is retained in the
[historical architecture record](history/architecture-proposal.md).
