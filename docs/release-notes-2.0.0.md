# EfCoreNexus.Framework 2.0.0

Stable v2 for .NET 10 / EF Core 10. The package ID remains
`EfCoreNexus.Framework`. This release provides typed async CRUD, explicit
assembly discovery, per-operation context ownership and optional multi-provider
transactions while keeping native EF APIs available.

```sh
dotnet add package EfCoreNexus.Framework --version 2.0.0
```

Add a compatible EF Core 10 database provider separately; see
[installation and setup](../README.md#install).

## Breaking changes from 1.0.5

- **Runtime:** .NET 10 / EF Core 10 is required. Applications on the v1 runtime
  baseline must upgrade before using this package.
- **Setup and DI:** native `DbContext`, `IDbContextFactory<TContext>`,
  `IEntityTypeConfiguration<T>` and concrete provider injection replace
  `BaseContext`, `BaseContextFactory`, `EntityTypeConfigurationDependency`,
  `StartupConfiguration`, `DataAssemblyConfiguration`, `DataService` /
  `IDataService` and `ProviderFactory` / `IProviderFactory` / `IProvider`.
  Discovery uses explicit assembly boundaries.
- **CRUD signatures and keys:** asynchronous methods with cancellation replace
  the old signatures and synchronous query hooks. Create/update take the
  entity's mapped key, without a separate ID argument. ID and entity delete
  are distinct overloads. Native EF metadata replaces Dynamic LINQ and
  attribute-only key discovery; generic CRUD requires a single CLR key.
- **Context and transaction ownership:** ordinary calls create and asynchronously
  dispose their own context. `TransactionService` and implicit shared state are
  removed. Use native EF for a single save or explicitly pass the optional
  `TransactionRunner<TContext>` context to participating writes. Borrowed writes
  save the entire supplied tracker immediately. Rollback does not undo in-memory
  keys or entity state; discard entities from failed workflows.
- **Update, delete and concurrency:** updates are full scalar edits, not graph
  merges or patches. Preserve original concurrency tokens; generic updates
  support store-generated CLR tokens, while application-managed token renewal
  requires a specialized native operation. A no-change update asserts no
  version. Missing reads return `null`, missing updates throw
  `KeyNotFoundException`, missing ID deletes are a no-op, and stale/missing
  entity deletes throw `DbUpdateConcurrencyException`.
- **Sample database:** the sample now uses a fresh disposable SQLite schema
  with a Memo table. It does not migrate an existing SQL Server database;
  production mappings, schema and data migrations remain application-owned.

See the [migration guide](migration-v2.md) for replacements and before/after
examples, and the [API contracts](native-setup.md) before porting overrides.
Server-side authorization, tenant isolation and allowed-field validation remain
the application's responsibility; query filters and concurrency tokens are not
access checks.

The stable release preparation changes package metadata and documentation only.
It adds no framework API, implementation, dependencies, tests or release automation
to the source validated in PR #24. See the
[release guide](release-readiness.md) for validation and the manual release commands.
