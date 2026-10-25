# v2 preview release readiness (#23)

Local validation on **2026-10-25**, based on `origin/v2` at `5a3c466` (merged
PR #22). Host: Linux Mint 22.3 x64, .NET SDK `10.0.113`, runtime `10.0.13`,
EF Core `10.0.13`, Docker Engine `29.9.2`. No framework implementation, public
API, test semantics or CI workflow was changed for this preparation.

## Package and installation

The published 1.0.5 nuspec was checked directly on nuget.org: its ID is
`EfCoreNexus.Framework`. That identity is now explicit and unchanged.
`2.0.0-preview.1` identifies the incompatible .NET 10 / EF Core 10 API as a
prerelease candidate; it has only been packed locally. Normal builds have
`GeneratePackageOnBuild=false`. Packaging requires an explicit `dotnet pack`.

```sh
dotnet pack EfCoreNexus.Framework/EfCoreNexus.Framework.csproj --no-restore -c Release -o artifacts/packages
unzip -l artifacts/packages/EfCoreNexus.Framework.2.0.0-preview.1.nupkg
unzip -p artifacts/packages/EfCoreNexus.Framework.2.0.0-preview.1.nupkg EfCoreNexus.Framework.nuspec
```

The inspected package is approximately **396 KiB**, with eight ZIP entries:
`lib/net10.0/EfCoreNexus.Framework.dll`, root `README.md`, `license.txt` (MIT),
`logo.png`, the nuspec and three NuGet infrastructure entries. Packaged README,
license, icon and assembly were compared byte for byte with the build inputs.
There are no demo/test assemblies, configuration/secrets, patches, historical
documents, source files or build artifacts beyond the library DLL.

The only direct package dependency is `Microsoft.EntityFrameworkCore >= 10.0.13`
for `net10.0`; there is no database provider, SqlClient, Testcontainers or legacy
Dynamic LINQ dependency. A plain console consumer needs only the framework and
its chosen EF provider: DI/logging dependencies arrive transitively through EF.
Description, tags, breaking-change release notes, project/repository URLs,
license, icon and README metadata were inspected in the generated nuspec.
All 36 links between the current documentation pages had valid local paths and
anchors. The five existing GitHub documentation targets also returned HTTP 200
via their raw-content URLs. README documentation links use absolute `v2` GitHub
URLs so NuGet rendering does not depend on unpacked documentation files.

Assembly and file versions are `2.0.0.0`; informational version contains
`2.0.0-preview.1` and the source commit. The SDK emits a portable PDB beside the
Release DLL and GitHub SourceLink metadata without an added package/toolchain.
The SourceLink mapping and nuspec repository commit were inspected after packing
the committed source. The nupkg does not include symbols; no snupkg was created
and source stepping through a symbol server was not tested.

## Independent consumer, using the nupkg

A new console application was created **outside the repository/solution**, at
`/tmp/efcorenexus-issue23-consumer`. It has no ProjectReference or ASP.NET
FrameworkReference. Its only PackageReferences are `EfCoreNexus.Framework
2.0.0-preview.1` and `Microsoft.EntityFrameworkCore.Sqlite 10.0.13`.
Installation started with an empty, isolated package cache; NuGet's installed
`.nupkg.metadata` confirms EfCoreNexus came from the local artifact directory.
Both the local source and nuget.org were configured, so transitive dependencies
could restore without relying on the developer's existing cache.

The commands below were executed in Bash; paths identify the local evidence,
not checked-in infrastructure. `Program.cs` combined the README's C# examples
with a ServiceCollection host, temporary SQLite schema and assertions.

```sh
dotnet new console -f net10.0 -n NexusConsumer -o /tmp/efcorenexus-issue23-consumer --no-restore
dotnet new nugetconfig -o /tmp/efcorenexus-issue23-consumer
cd /tmp/efcorenexus-issue23-consumer
export NUGET_PACKAGES=/tmp/efcorenexus-issue23-packages
dotnet nuget add source /home/thorsten/projects/EfCoreNexus/artifacts/packages --name EfCoreNexusPreview --configfile nuget.config
dotnet add package EfCoreNexus.Framework --version 2.0.0-preview.1
dotnet add package Microsoft.EntityFrameworkCore.Sqlite --version 10.0.13
dotnet restore -p:NuGetAudit=true -p:NuGetAuditMode=all -warnaserror:NU1901,NU1902,NU1903,NU1904
dotnet build --no-restore -c Release
dotnet run --no-build --no-restore -c Release
dotnet list package --vulnerable --include-transitive
```

Restore/build/run passed with zero build warnings/errors and no known vulnerable
packages. Assertions verified concrete scoped provider DI, entity discovery
without DbSet properties, the native context factory, generated keys, all six
CRUD methods, persisted scalar edits/deletes, null reads, missing ID-delete
no-op, materialized custom LINQ, direct native EF and runner commit/rollback.
Verification reads used fresh contexts. The temporary database was deleted.

A second temporary console consumer at `/tmp/efcorenexus-issue23-api-consumer`
compiled and ran the API guide's setup with its documented SQLite substitution.
It supplied the stated Revision property/concurrency mapping for the rename
example. Audited restore, Release build and run passed: constructor/logger DI,
native configuration scanning, both WithContextAsync forms, explicit original/new
tokens, stale rename rejection and fresh entity deletion worked. Neither consumer
requires a new permanent sample or test project in this repository.

## Solution, SQL Server and sample checks

Executed from the repository root:

```sh
dotnet restore -p:NuGetAudit=true -p:NuGetAuditMode=all -warnaserror:NU1901,NU1902,NU1903,NU1904
dotnet build --no-restore -c Release
dotnet test --no-build --no-restore -c Release --logger "trx;LogFileName=sqlite.trx" --results-directory /tmp/efcorenexus-issue23-results
dotnet list package --vulnerable --include-transitive
dotnet restore EfCoreNexus.SqlServer.Tests/EfCoreNexus.SqlServer.Tests.csproj -p:NuGetAudit=true -p:NuGetAuditMode=all -warnaserror:NU1901,NU1902,NU1903,NU1904
dotnet build EfCoreNexus.SqlServer.Tests/EfCoreNexus.SqlServer.Tests.csproj --no-restore -c Release
dotnet test EfCoreNexus.SqlServer.Tests/EfCoreNexus.SqlServer.Tests.csproj --no-build --no-restore -c Release --logger "trx;LogFileName=sql-server.trx" --logger "console;verbosity=normal" --results-directory /tmp/efcorenexus-issue23-results
dotnet list EfCoreNexus.SqlServer.Tests/EfCoreNexus.SqlServer.Tests.csproj package --vulnerable --include-transitive
dotnet tool restore
dotnet ef dbcontext info --project EfCoreNexus.TestApp/EfCoreNexus.TestApp.Data --startup-project EfCoreNexus.TestApp/EfCoreNexus.TestApp --configuration Release --no-build -- --ConnectionStrings:Main "Data Source=/tmp/efcorenexus-issue23-sample.db"
dotnet ef dbcontext script --project EfCoreNexus.TestApp/EfCoreNexus.TestApp.Data --startup-project EfCoreNexus.TestApp/EfCoreNexus.TestApp --configuration Release --no-build --output /tmp/efcorenexus-issue23-schema.sql
dotnet run --no-build --no-restore -c Release --project EfCoreNexus.TestApp/EfCoreNexus.TestApp --launch-profile http -- --ConnectionStrings:Main "Data Source=/tmp/efcorenexus-issue23-sample.db"
curl -fsS http://localhost:5031/ -o /tmp/efcorenexus-issue23-sample.html
git diff --check
```

Solution and SQL test project builds passed without warnings/errors. **133/133
SQLite tests** and **7/7 SQL Server tests** passed, with zero failures/skips.
SQL Server reported **2025 CU10, 17.0.5035.5** from the existing pinned image;
the suite verified rowversion, CRUD and transaction commit/rollback. Container
cleanup was checked. Both transitive dependency audits were clean; the SQL
test-only patched `System.Security.Cryptography.Pkcs 9.0.21` pin is unchanged.
No security warnings were suppressed.

EF tool restore, context info and schema scripting passed. The sample returned
HTTP 200, referenced Blazor assets and left both tables empty after prerendering.
Its component/CRUD/concurrency behavior is also covered by the existing SQLite
tests. Interactive browser scenarios and Production publish were not rerun for
#23; their earlier evidence remains in [#12 validation](validation-12.md).

Both CI jobs already passed in [PR #22](https://github.com/thliborius/EfCoreNexus/actions/runs/40188489945)
and on [merged v2](https://github.com/thliborius/EfCoreNexus/actions/runs/40188713643).
The workflow is unchanged. See [SQL Server coverage and platform limits](sql-server-tests.md).

## Before public publication

The candidate is locally installable and ready for prerelease review. Remaining
release actions are separate decisions:

- Review and merge the preparation PR into `v2`, then rebuild/audit/retest the
  exact clean commit selected for publication. Confirm both PR CI jobs pass.
- Confirm the public version and release notes; verify that preview.1 is still
  available. Replace local-only installation/status wording when publishing.
- Check the final README in NuGet's rendering and verify its absolute GitHub
  links against the merged `v2` documents. Paths/anchors were checked locally;
  this does not claim a live nuget.org preview. The new readiness page becomes
  available at its `v2` URL after merge. Decide whether symbols are wanted.
- Authorize and perform publication separately. No package push, release, tag,
  deployment or merge was performed here.

Windows/macOS, WebAssembly database access, trimming/AOT, retrying execution
strategies and production database migrations were not validated. The sample
uses disposable EnsureCreated data. Query filters do not authorize writes;
server-side permission checks, allowed fields and tenant isolation remain the
consumer's responsibility, as documented in the [API security contracts](native-setup.md#security-boundaries-filters-authorization-and-full-edits).
