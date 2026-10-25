# Run and inspect the sample

Use the [README commands](../README.md#run-the-sample) from the repository root.
.NET 10 and SQLite are sufficient. `--launch-profile http` selects Development
and serves build-time static assets. The sample is local, unauthenticated and
intended for disposable demonstration data.

## Configuration and tooling

`ConnectionStrings:Main` in the web project's appsettings is the single connection
contract. Environment (`ConnectionStrings__Main`) and CLI overrides work for both
runtime and EF tooling. No `EFCORETOOLSDB`, custom factory, credentials or SQL Server
is required. Relative SQLite paths are relative to the process working directory;
use an absolute `Data Source` when running commands from different directories.
`dotnet run --project` starts in the web project directory.

The checked-in `.config/dotnet-tools.json` pins EF tools to the EF package
version. Run these commands from the repository root after `dotnet restore`:
`dotnet tool restore` discovers the manifest automatically, and `dotnet ef` uses
the restored local tool. No global `dotnet-ef` installation is required.

```sh
dotnet tool restore
dotnet ef dbcontext info --project EfCoreNexus.TestApp/EfCoreNexus.TestApp.Data --startup-project EfCoreNexus.TestApp/EfCoreNexus.TestApp
dotnet ef dbcontext script --project EfCoreNexus.TestApp/EfCoreNexus.TestApp.Data --startup-project EfCoreNexus.TestApp/EfCoreNexus.TestApp --output sample-schema.sql
```

For an explicit connection append, for example:
`-- --ConnectionStrings:Main "Data Source=/absolute/path/sample.db"` (use a native
absolute path on your OS). EF builds the application host and obtains exactly the
same factory/options/model registration. No duplicate design-time configuration.
The tools stop host execution before the startup schema initialization.

The demo calls `EnsureCreatedAsync` once at startup and **never seeds rows**.
Prerender and interactive initialization only read. Controls are disabled until
interactive rendering completes and while an operation runs. Each write requires
an event; components retain detached edit data, never a DbContext.

`EnsureCreated` is deliberately not a migration strategy: after model changes,
stop the demo and remove its disposable database (including journal sidecars),
or choose a new filename. Do not delete a database containing needed data. For a
real application's schema evolution, replace startup `EnsureCreated` with an
explicit migration policy. Standard commands then apply:

```sh
dotnet ef migrations add Initial --project EfCoreNexus.TestApp/EfCoreNexus.TestApp.Data --startup-project EfCoreNexus.TestApp/EfCoreNexus.TestApp
dotnet ef database update --project EfCoreNexus.TestApp/EfCoreNexus.TestApp.Data --startup-project EfCoreNexus.TestApp/EfCoreNexus.TestApp
```

Do not mix migrations with an existing EnsureCreated database. No SQL Server data
migration is provided or implied.

## Walk through the API

1. **Create**, **Edit / Save entry**, **Reload all** and **Delete by ID** use the
   ordinary CRUD methods. Simple entries have last-writer semantics, without a token.
2. **Active only** invokes a materialized LINQ query inside `WithContextAsync`.
3. **Deactivate all** uses a native factory context and one `SaveChangesAsync`.
4. **Create entry + memo** passes one runner context to both providers. Integration
   tests also force an exception after both saves and verify rollback of both rows.
5. Load the same memo in two tabs. Save in tab A, then save or delete the loaded
   snapshot in tab B: both report a conflict. **Reload all** explicitly discards the
   stale edit; loading the fresh memo allows deletion. The specialized rename
   renews a Guid token; generic entity delete checks the original token.

To run a local Production build, publish first (no deployment is needed):

```sh
dotnet publish EfCoreNexus.TestApp/EfCoreNexus.TestApp -o artifacts/sample
cd artifacts/sample
dotnet EfCoreNexus.TestApp.dll --urls http://localhost:5031
```

Production uses the published assets; running an unpublished build with the
Development profile disabled is not the documented startup path. Windows/macOS
CLI execution remains untested. The sample uses SQLite; SQL Server behavior is
covered separately by the [container suite](sql-server-tests.md). See
[release-readiness validation](release-readiness.md) and [earlier sample validation](validation-12.md).
