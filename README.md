# EfCoreNexus

**Less boilerplate. More Entity Framework.** A small optional helper for EF Core
in Blazor: typed async CRUD, safe per-operation contexts and opt-in discovery.
Native EF stays available. No service locator, mandatory repository or unit of work.

This branch contains **v2 source for .NET 10 / EF Core 10**. The published 1.0.5
package has a different API; this work does not publish or bump a package version.
Until a v2 package is released, add a project reference to
`EfCoreNexus.Framework/EfCoreNexus.Framework.csproj` and reference an EF Core 10
provider such as `Microsoft.EntityFrameworkCore.Sqlite` in your application.

## Setup once

```csharp
using EfCoreNexus.Framework.Entities;
using EfCoreNexus.Framework.Helper;
using EfCoreNexus.Framework.Provider;
using Microsoft.EntityFrameworkCore;

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

## Use CRUD directly

Inject `CustomerProvider` into your component (`@inject CustomerProvider Customers`).
Call writes from interactive event handlers, never prerender initialization:

```csharp
var customer = new Customer { Name = "Ada" };
await Customers.CreateAsync(customer);
var all = await Customers.GetAllAsync();
var edit = await Customers.GetByIdAsync(customer.Id); // null when missing
edit!.Name = "Ada Lovelace";
await Customers.UpdateAsync(edit);
await Customers.DeleteAsync(customer.Id); // Current-row delete; missing is a no-op.
// Alternative: DeleteAsync(edit) checks supplied concurrency tokens, if mapped.
```

Each call creates and asynchronously disposes a context. Generic update is a full
scalar edit, not a graph merge or patch. Entity delete carries the original tokens;
ID delete does not assert a caller's version. Application-managed token renewal
needs a specialized native operation; see the sample's memo editor and
[precise CRUD/concurrency contracts](docs/native-setup.md).

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
or nested transaction support. See [transaction contracts](docs/native-setup.md#optional-multi-provider-transactions).

## Run the sample

From a fresh checkout with the .NET 10 SDK:

```sh
dotnet restore
dotnet tool restore
dotnet build -p:GeneratePackageOnBuild=false
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
For tooling, configuration and schema lifecycle, see [sample setup](docs/sample.md).
EF runs on the server; browser-side EF/DB secrets in WebAssembly are unsupported.

- [Migration / breaking changes from 1.0.5](docs/migration-v2.md)
- [Detailed native setup and API contracts](docs/native-setup.md)
- [Architecture and complexity review](docs/architecture-v2.md)
- [Executed checks and platform limits](docs/validation-12.md)
- [Optional SQL Server integration tests with Docker](docs/sql-server-tests.md)
