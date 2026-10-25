# Issue #12 validation

Historical evidence for #12. See [stable release readiness](release-readiness.md)
for the current package version, test counts and SQL Server coverage.

Baseline: `origin/v2` at `61e040a`. Executed on Linux, .NET SDK 10.0.113,
runtime 10.0.13, EF Core / local dotnet-ef 10.0.13, SQLite.

## Automated checks

```sh
dotnet restore -p:NuGetAudit=true -p:NuGetAuditMode=all -warnaserror:NU1901,NU1902,NU1903,NU1904
dotnet build --no-restore -p:GeneratePackageOnBuild=false
dotnet test --no-build --no-restore --logger "trx;LogFileName=issue-12.trx" --results-directory /tmp/nexus-tests
dotnet list package --vulnerable --include-transitive
```

Restore and build passed, with zero build warnings/errors. **122 tests pass,
none failed/skipped**. Compared with 162 baseline cases: 21 v1-only cases removed
(four fixtures, including intentional defect characterizations), 24 duplicate
internal ownership runs removed, five sample integration cases added. The same
ownership assertions continue through the real public provider surface; the
framework no longer exposes internals to the test assembly.

The new cases prove real sample assembly discovery/DI, all ordinary CRUD paths,
custom LINQ, stale memo update/delete rejection and fresh deletion, two-provider
commit/rollback, repeated read-only component initialization and native EF writes.
Existing detailed CRUD, lifetime, registration and transaction failure tests stay.
One existing discovery assertion now includes both sample entities.

NuGet's nuget.org audit found no known vulnerabilities in any of the four projects,
including transitives. Removed direct packages: Dynamic LINQ, SQL Server provider,
its Pkcs security override and the unused WebAssembly Server package. No version
bump, release, package publication or merge is performed.

## Tooling, HTTP and browser

- Local EF tool restore, `dbcontext info` and `dbcontext script` succeeded using
  the web startup project and data target project, without a design-time factory.
- CLI connection override was reflected in `dbcontext info`. Generated SQL was
  applied to an independent SQLite database; `PRAGMA table_info` matched both
  runtime-created tables. Both paths execute the same MainContext model setup.
- Playwright/Chromium exercised actual Interactive Server (connected `_blazor`
  WebSocket): create, read/edit/save, current-row ID delete, active-only LINQ,
  native deactivate, two-provider create, two-tab stale memo edit and snapshot
  delete conflicts, explicit reload and successful fresh snapshot deletion.
- Initial page loads created no data. The explicit `InteractiveServer` render modes
  on Routes and HeadOutlet include prerendering; initialization only reads.
- Local publish and Production execution verified the framework JavaScript assets.
  Development browser console had no errors after proper startup. The browser
  check found and corrected missing static asset endpoint mapping.
- Source scan found no remaining legacy framework consumers, Dynamic LINQ,
  SQL Server configuration, warning suppression, credentials or Windows batch paths.

## Limits

Only Linux/SQLite and Chromium were executed. No Windows, macOS, SQL Server,
WebAssembly, AOT, retry strategy or production deployment claim is made. The
sample uses disposable `EnsureCreated` schema setup, not an upgrade of an existing
v1 database. Runtime/SQL schema comparison does not establish cross-provider parity.
The local browser workflow is manual tool-driven evidence, not a new CI browser
infrastructure. Existing CI runs build/tests/audit. EF model scripting was verified locally;
the available GitHub credential lacks workflow scope, so the proposed CI tooling
step could not be pushed and the existing workflow is unchanged. CI's final
result is reported in the PR/completion report.

## Local tool manifest follow-up

The unchanged EF tool manifest is stored at `.config/dotnet-tools.json`, the
standard local discovery location. README and sample tooling commands use
`dotnet tool restore` from the repository root without an explicit manifest path
or a global `dotnet-ef` installation.

Verified on Linux / SDK 10.0.113 in a fresh temporary Git clone with an isolated
`DOTNET_CLI_HOME` and no global tools: `dotnet ef` was unavailable before restore;
`dotnet tool restore`, `dotnet ef --version` (10.0.13), and the documented
`dbcontext info`, `dbcontext script`, `migrations add Initial` and `database update`
commands succeeded. Migration files and the SQLite database were created only in
the temporary checkout. Restore/build passed with zero build warnings/errors,
all 122 tests passed, and the transitive dependency audit found no known
vulnerabilities. The manifest is also discovered from a nested project directory.

Open release point: `EfCoreNexus.Framework.csproj` still declares version `1.0.5`
and `GeneratePackageOnBuild=True`. Review both before releasing v2; this tooling
correction changes neither setting nor any package/tool version.
