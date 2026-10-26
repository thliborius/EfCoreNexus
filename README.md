# EfCoreNexus

**Less boilerplate. More Entity Framework.**

EfCoreNexus is a small, optional helper for Blazor applications that organize
Entity Framework Core access into typed providers. It eliminates repetitive
CRUD implementations, per-provider registrations and DbContext lifecycle code
without replacing native EF Core.

## Why EfCoreNexus?

### 1. Write a provider once, not the same CRUD methods six times

If you use a provider per entity, the usual handwritten code can look like this:

```csharp
public sealed class CustomerProvider(IDbContextFactory<AppDb> factory)
{
    public async Task<List<Customer>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Set<Customer>().AsNoTracking().ToListAsync(ct);
    }

    public async Task CreateAsync(Customer customer, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Set<Customer>().Add(customer);
        await db.SaveChangesAsync(ct);
    }

    // And again for GetByIdAsync, UpdateAsync and both DeleteAsync overloads...
}
```

With EfCoreNexus, the provider is just:

```csharp
public sealed class CustomerProvider(IDbContextFactory<AppDb> factory)
    : ProviderBase<Customer, int, AppDb>(factory);
```

The inherited provider already implements `GetAllAsync`, `GetByIdAsync`,
`CreateAsync`, `UpdateAsync` and both `DeleteAsync` overloads.
Every call supports cancellation and owns a fresh, asynchronously disposed
DbContext. For example, creating a customer becomes one line:

```csharp
await customers.CreateAsync(customer);
```

This saves the repeated implementations for each entity, **not a fixed
percentage of your application's total code**.

### 2. Fewer types to declare and less setup per entity

After the one-time [setup](#setup-once), adding a conventionally mapped entity
and provider can be as small as:

```csharp
public sealed class Order : IEntity
{
    public int Id { get; set; }
    public string Description { get; set; } = "";
}

public sealed class OrderProvider(IDbContextFactory<AppDb> factory)
    : ProviderBase<Order, int, AppDb>(factory);
```

No separate `IOrderProvider` interface, repository implementation, per-provider
`AddScoped<OrderProvider>()`, `DbSet<Order>` property or mandatory mapping
configuration class is needed. Assembly discovery picks up eligible entities
and concrete providers from the assemblies you explicitly configure once.

**EF Core itself does not require a repository or provider layer.** If direct
DbContext access is simpler for your application, use it. EfCoreNexus is most
useful when you *want* typed providers but not all their boilerplate.
Custom LINQ, complex operations and native EF Core APIs remain available.

**2.0.1** is a patch release for **.NET 10 / EF Core 10** with CRUD tracking,
concurrency and discovery fixes, but no new public API. Version **2.0.0** introduced
breaking changes from **1.0.5**. Review the
[migration guide](https://github.com/thliborius/EfCoreNexus/blob/v2.0.1/docs/migration-v2.md)
before upgrading.

## Install

In your .NET 10 application, install from NuGet:

```sh
dotnet add package EfCoreNexus.Framework --version 2.0.1
dotnet add package Microsoft.EntityFrameworkCore.Sqlite --version 10.0.13
```

EfCoreNexus depends only on `Microsoft.EntityFrameworkCore >= 10.0.13`; your
application selects a compatible concrete database provider. For SQL Server,
use `Microsoft.EntityFrameworkCore.SqlServer` instead. Local package builds and
installation are described separately in the
[2.0.0 preparation guide (historical)](https://github.com/thliborius/EfCoreNexus/blob/v2.0.0/docs/release-readiness.md).

## Setup once

```csharp
using EfCoreNexus.Framework.Entities;
using EfCoreNexus.Framework.Helper;
using EfCoreNexus.Framework.Provider;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// Program.cs: connection comes from configuration, not source-code secrets.
var connection = builder.Configuration.GetConnectionString("Main")
    ?? throw new InvalidOperationException("ConnectionStrings:Main is required.");
builder.Services.AddDbContextFactory<AppDb>(o => o.UseSqlite(connection));
builder.Services.AddNexusProviders<AppDb>(typeof(CustomerProvider).Assembly);

public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.RegisterNexusEntities(typeof(Customer).Assembly);
        model.ApplyConfigurationsFromAssembly(typeof(Customer).Assembly);
    }
}

public sealed class Customer : IEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class CustomerProvider(IDbContextFactory<AppDb> factory)
    : ProviderBase<Customer, int, AppDb>(factory);
```

`IEntity` opts into entity discovery. Providers are discovered as concrete scoped
services for the exact context. Add another eligible entity/provider in these
assemblies without editing DI or adding a DbSet. Native `IEntityTypeConfiguration<T>`
classes are optional. Use explicit boundaries/predicates for multiple models.

Create the database schema before calling CRUD, using your application's
migration/deployment policy. For a disposable local experiment, the
[sample](https://github.com/thliborius/EfCoreNexus/blob/v2.0.1/docs/sample.md#configuration-and-tooling)
uses native `EnsureCreatedAsync` once at startup. This is not a migration strategy;
schema setup does not belong in component rendering.

## Use CRUD directly

Inject `CustomerProvider` into your component (`@inject CustomerProvider Customers`).
Call writes from interactive event handlers, never prerender initialization:

```csharp
var customer = new Customer { Name = "Ada" };
await Customers.CreateAsync(customer);
var all = await Customers.GetAllAsync();
var edit = await Customers.GetByIdAsync(customer.Id)
    ?? throw new KeyNotFoundException(); // Reads return null when missing.
edit.Name = "Ada Lovelace";
await Customers.UpdateAsync(edit);
await Customers.DeleteAsync(customer.Id); // Current-row delete; missing is a no-op.
// Alternative: DeleteAsync(edit) checks supplied concurrency tokens, if mapped.
```

Each call creates and asynchronously disposes a context. Generic update is a full
scalar edit, not a graph merge or patch. Entity delete carries the original tokens;
ID delete does not assert a caller's version. Application-managed token renewal
needs a specialized native operation; see the sample's memo editor and
[precise CRUD/concurrency contracts](https://github.com/thliborius/EfCoreNexus/blob/v2.0.1/docs/native-setup.md).

**Authorize and validate writes on the server.** Query filters are not an
authorization or tenant-isolation boundary. Full edits can change every writable
scalar, including tenant/privilege fields. `DeleteAsync(entity)` performs no
lookup, and borrowed writes can reuse an already tracked row without rechecking
filters. Use explicit allowed-field operations and permission checks; see the
[security contracts](https://github.com/thliborius/EfCoreNexus/blob/v2.0.1/docs/native-setup.md#security-boundaries-filters-authorization-and-full-edits).

Custom queries remain LINQ inside your provider:

```csharp
public Task<List<Customer>> SearchAsync(string prefix, CancellationToken ct = default)
    => WithContextAsync((db, token) => db.Set<Customer>().AsNoTracking()
        .Where(x => x.Name.StartsWith(prefix)).ToListAsync(token), ct);
```

Materialize inside the callback. For large data sets, use a filtered/paged query
instead of `GetAllAsync`.

## Prefer native EF when it is simpler

Inject `IDbContextFactory<AppDb>` for one-off operations, graphs, projections,
advanced keys or one-save workflows. There is no requirement to use providers:

```csharp
await using var db = await factory.CreateDbContextAsync();
var customer = await db.Set<Customer>().FindAsync(id);
customer!.Name = "Grace";
await db.SaveChangesAsync(); // Native EF already transacts this save.
```

For **several immediate saves across providers**, optionally register
`services.AddScoped<TransactionRunner<AppDb>>()` from
`EfCoreNexus.Framework.Services` and inject it:

```csharp
await runner.ExecuteAsync(async (db, ct) =>
{
    await customers.CreateAsync(db, customer, ct);
    await orders.CreateAsync(db, new Order { CustomerId = customer.Id }, ct);
});
```

The runner owns commit/rollback/disposal. Pass its context explicitly to every
participating write: ordinary calls are independent. Each borrowed write saves
**all pending changes in that context**. No automatic retries, ambient enlistment
or nested transaction support. See [transaction contracts](https://github.com/thliborius/EfCoreNexus/blob/v2.0.1/docs/native-setup.md#optional-multi-provider-transactions).

## Run the sample

From a fresh checkout with the .NET 10 SDK:

```sh
dotnet restore
dotnet tool restore
dotnet build
dotnet test --no-build
dotnet run --project EfCoreNexus.TestApp/EfCoreNexus.TestApp --launch-profile http
```

`dotnet tool restore` uses `.config/dotnet-tools.json` for the pinned local
`dotnet-ef`; no global installation is needed. Run the commands from the repository root.

Open http://localhost:5031. No SQL Server, credentials or extra UI framework is
needed. SQLite creates `efcorenexus.db` in the web project's content directory.
The app uses Interactive Server with read-only prerendering. Buttons demonstrate
all six CRUD calls, custom LINQ, native EF and a two-provider transaction. Two tabs
can demonstrate stale memo edit/delete conflicts.

Override `ConnectionStrings:Main` in configuration, the environment variable
`ConnectionStrings__Main`, or CLI `--ConnectionStrings:Main "Data Source=..."`.
For tooling, configuration and schema lifecycle, see [sample setup](https://github.com/thliborius/EfCoreNexus/blob/v2.0.1/docs/sample.md).
EF runs on the server; browser-side EF/DB secrets in WebAssembly are unsupported.

- [Migration / breaking changes from 1.0.5](https://github.com/thliborius/EfCoreNexus/blob/v2.0.1/docs/migration-v2.md)
- [Detailed native setup and API contracts](https://github.com/thliborius/EfCoreNexus/blob/v2.0.1/docs/native-setup.md)
- [Architecture and complexity review](https://github.com/thliborius/EfCoreNexus/blob/v2.0.1/docs/architecture-v2.md)
- [2.0.0 release preparation (historical)](https://github.com/thliborius/EfCoreNexus/blob/v2.0.0/docs/release-readiness.md)
- [Optional SQL Server integration tests with Docker](https://github.com/thliborius/EfCoreNexus/blob/v2.0.1/docs/sql-server-tests.md)
- [2.0.1 release notes](https://github.com/thliborius/EfCoreNexus/blob/v2.0.1/docs/release-notes-2.0.1.md)
- [2.0.0 release notes](https://github.com/thliborius/EfCoreNexus/blob/v2.0.0/docs/release-notes-2.0.0.md)
