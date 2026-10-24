# Migrating from v1.0.5 to v2

v2 is source-breaking and requires .NET 10 / EF Core 10. No compatibility facade
is retained. Package publication/version selection is separate from this migration.

| v1 | v2 replacement |
| --- | --- |
| `DataService` / `IDataService`, `GetProvider` | Inject the concrete provider directly. |
| `ProviderFactory` / `IProviderFactory`, `IProvider` | `AddNexusProviders<TContext>(assembly)` and ordinary DI. |
| Legacy `ProviderBase`, old CRUD interface | `EfCoreNexus.Framework.Provider.ProviderBase` and optional `IProviderCrud`; constructor takes native `IDbContextFactory<TContext>`. |
| `Create(entity,id)`, `Update(entity,id)`, `GetById`, `Delete` | Async methods with cancellation; create/update use the entity's mapped key, not a separate ID. ID and entity delete are separate overloads. |
| Synchronous `GetAll` / query hooks | Async materialized reads; custom native LINQ inside `WithContextAsync`. |
| `TransactionService` begin/commit/dispose | Native EF for one save, or optional `TransactionRunner<TContext>.ExecuteAsync` with explicit context-first writes. |
| `StartupConfiguration`, `DataAssemblyConfiguration` | Native factory registration plus explicit assembly discovery. |
| `BaseContext` / `BaseContextFactory` | Native DbContext and factory; EF tooling can use the application's host configuration. |
| `EntityTypeConfigurationDependency` wrappers | `IEntityTypeConfiguration<T>` / `ApplyConfigurationsFromAssembly`. |
| Dynamic LINQ / attribute-only keys | Native EF metadata and typed single CLR keys. |

Before: `MainSvc.GetProvider<TestProvider>().Update(item, item.TestId)`.
After: inject `TestProvider Tests`, then `await Tests.UpdateAsync(item)`.

Behavior changes are deliberate: no circuit-scoped mutable transaction/context,
no implicit enlistment, no pre-insert existence query, and scalar-only full edits
instead of blanket graph updates. Missing reads return null; missing updates throw
`KeyNotFoundException`; missing ID deletes are a no-op; stale/missing entity deletes
throw `DbUpdateConcurrencyException`. Preserve original concurrency tokens.
Generic updates support store-generated CLR tokens; application-managed renewal
uses native EF in a specialized operation. A no-change update is not a version assertion.

Borrowed writes save the entire supplied tracker immediately. Failed transactions
do not undo generated IDs or in-memory state; discard failed workflow entities.
See [full contracts](native-setup.md) before migrating overrides.

The demo switches from SQL Server to SQLite and has a new Memo table. It is a
fresh disposable demo schema, not an automatic migration of an existing SQL Server
database. Existing applications must retain/review their own mappings, migrations,
connection configuration and data migration policy.
