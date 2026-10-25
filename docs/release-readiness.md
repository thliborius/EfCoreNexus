# Stable 2.0.0 release preparation

Prepared on **2026-10-25** from `origin/v2` at
`e6e42cce827675d5dd5e3567dc9ff8f6938f7295` (merged PR #24).
Host: Linux x64, .NET SDK `10.0.113`, runtime / EF Core `10.0.13`,
Docker Engine `29.9.2`. This preparation changes only package metadata and
release/installation documentation. Framework API, implementation, dependencies,
tests and CI workflows are unchanged.

See [2.0.0 release notes](release-notes-2.0.0.md) and
[breaking changes from 1.0.5](migration-v2.md).

## Package inspection

`EfCoreNexus.Framework.2.0.0.nupkg` is a stable package with the same package ID
as 1.0.5. Normal builds still do not generate packages. The explicit Release pack
and inspection commands are:

```sh
dotnet pack EfCoreNexus.Framework/EfCoreNexus.Framework.csproj -c Release -o artifacts/packages
unzip -l artifacts/packages/EfCoreNexus.Framework.2.0.0.nupkg
unzip -p artifacts/packages/EfCoreNexus.Framework.2.0.0.nupkg EfCoreNexus.Framework.nuspec
sha256sum artifacts/packages/EfCoreNexus.Framework.2.0.0.nupkg
```

The package contains eight ZIP entries: `lib/net10.0/EfCoreNexus.Framework.dll`,
root `README.md`, MIT `license.txt`, `logo.png`, the nuspec and three NuGet
infrastructure entries. README, license, icon and DLL match their inputs byte for
byte. No demo/test assemblies, source, configuration, secrets or historical
documents are included. The only direct dependency is
`Microsoft.EntityFrameworkCore >= 10.0.13` for `net10.0`; a concrete database
provider remains the consumer's choice.

The inspected nuspec has stable version `2.0.0`, unchanged ID/authorship,
stable release notes with breaking changes, README/icon/license entries,
project/repository URLs and the packing commit's repository SHA. Assembly and
file versions are `2.0.0.0`; informational version is `2.0.0+<source commit>`.
No symbol package is produced. Package hashes, nuspec/entry inspection output
and validation logs are local evidence under `artifacts/release-2.0.0/validation/`;
these generated files are not committed.

Package-facing documentation links and the project URL use the planned
**`v2.0.0` tag**, rather than the moving `v2` branch. Local target paths and
anchors are checked before release. The tag does not exist during preparation;
these public URLs become reachable only after the separately authorized tag
push below. Other documentation uses relative links so it stays within the
selected checkout/tag. A live NuGet README rendering was not checked; no package
was uploaded to the NuGet web portal.

## Independent local-package consumer

The final stable nupkg was installed into a new **net10.0 console application
outside this repository and solution**, with an initially empty isolated package
cache. It has no ProjectReference or ASP.NET FrameworkReference, only
`EfCoreNexus.Framework 2.0.0` and `Microsoft.EntityFrameworkCore.Sqlite 10.0.13`.
NuGet source mapping restricts EfCoreNexus to `LocalRelease`; nuget.org supplies
other packages. Installed `.nupkg.metadata` confirms the local source, and the
installed nupkg hash matches the inspected artifact.

The exact temporary consumer location is recorded in
`artifacts/release-2.0.0/validation/consumer-location.txt`. Its `Program.cs`
combines the README setup/CRUD/custom-LINQ examples with a ServiceCollection
host, SQLite in-memory database and assertions. Executed in that directory,
with `NUGET_PACKAGES` pointing to its new sibling cache:

```sh
dotnet add package EfCoreNexus.Framework --version 2.0.0
dotnet add package Microsoft.EntityFrameworkCore.Sqlite --version 10.0.13
dotnet restore -p:NuGetAudit=true -p:NuGetAuditMode=all -warnaserror:NU1901,NU1902,NU1903,NU1904
dotnet build --no-restore -c Release
dotnet run --no-build --no-restore -c Release
dotnet list package --vulnerable --include-transitive
```

Restore, Release build and run passed with zero build warnings/errors and no
known vulnerable dependencies. Assertions verify stable assembly/source-commit
metadata, scoped concrete provider DI, entity discovery without DbSet properties,
the native factory, generated keys, all six CRUD methods, persisted scalar edits,
null reads and missing ID-delete no-op, custom materialized LINQ, direct native
EF, and two-provider commit/rollback. Database verification uses fresh contexts.
The in-memory database is disposed when the consumer exits.

For developer-only installation from another checkout, first pack as above, add
its absolute `artifacts/packages` path as a local NuGet source and leave nuget.org
enabled for dependencies. Use a fresh `NUGET_PACKAGES` cache when validating a
rebuilt package with the same version. Do not commit machine-specific sources.
Public consumers use the [normal NuGet installation](../README.md#install).

## Quality gates and CI

Executed from the repository root, with audit vulnerability warnings treated as
errors, and no warning suppression:

```sh
dotnet restore -p:NuGetAudit=true -p:NuGetAuditMode=all -warnaserror:NU1901,NU1902,NU1903,NU1904
dotnet build --no-restore -c Release
dotnet test --no-build --no-restore -c Release
dotnet list package --vulnerable --include-transitive
dotnet restore EfCoreNexus.SqlServer.Tests/EfCoreNexus.SqlServer.Tests.csproj -p:NuGetAudit=true -p:NuGetAuditMode=all -warnaserror:NU1901,NU1902,NU1903,NU1904
dotnet build EfCoreNexus.SqlServer.Tests/EfCoreNexus.SqlServer.Tests.csproj --no-restore -c Release
dotnet test EfCoreNexus.SqlServer.Tests/EfCoreNexus.SqlServer.Tests.csproj --no-build --no-restore -c Release --logger "console;verbosity=normal"
dotnet list EfCoreNexus.SqlServer.Tests/EfCoreNexus.SqlServer.Tests.csproj package --vulnerable --include-transitive
git diff --check
```

Solution and SQL Server project restore/build passed without warnings/errors.
**133/133 SQLite tests and 7/7 SQL Server tests passed, zero failures/skips.**
SQL Server used the existing pinned 2025 CU10 image. All three transitive package
audits (solution, SQL Server tests, independent consumer) reported no known
vulnerabilities. The existing test-only Pkcs servicing pin is unchanged.

Both existing CI jobs, `build` and `sql-server`, including their audited restores
and audit-report steps, passed on the
[PR #24 merge commit](https://github.com/thliborius/EfCoreNexus/actions/runs/40200608876).
The release-preparation PR must also pass both jobs on its final head; inspect
its checks and audit logs before merging. The workflow is unchanged.

Windows/macOS, trimming/AOT, retrying execution strategies and production
migrations were not revalidated. Interactive sample/browser scenarios were not
rerun for this metadata/documentation change; earlier evidence remains in
[#12 validation](validation-12.md) and the [SQL Server coverage](sql-server-tests.md).

## Manual release commands — after separate approval

**Recommendation:** approve stable 2.0.0 once this preparation PR is merged into
`v2`, both CI jobs are green and the exact clean merge commit and rebuilt package
pass the checks above, including the independent consumer. No further framework
changes or release infrastructure are needed for this preparation.

The commands below are a manual handoff, **not actions performed here**. They use
Bash from the repository root. First select the merged commit and require a clean
checkout; if the cleanliness check fails, stop and preserve the local changes.

```sh
set -euo pipefail
git fetch origin --prune
git switch --detach origin/v2
test -z "$(git status --porcelain)"
NEXUS_RELEASE_COMMIT="$(git rev-parse HEAD)"
NEXUS_CI_RUN="$(gh run list --repo thliborius/EfCoreNexus --workflow dotnet.yml --branch v2 --commit "$NEXUS_RELEASE_COMMIT" --limit 1 --json databaseId --jq '.[0].databaseId')"
test -n "$NEXUS_CI_RUN" && test "$NEXUS_CI_RUN" != null
gh run watch "$NEXUS_CI_RUN" --repo thliborius/EfCoreNexus --exit-status
```

Run all quality-gate commands above, then pack/inspect again and rerun the
independent local-package consumer with a fresh cache. Do not reuse this branch's
artifact after a merge: its repository SHA must match `NEXUS_RELEASE_COMMIT`.
Confirm `2.0.0` is still absent from the
[NuGet version index](https://api.nuget.org/v3-flatcontainer/efcorenexus.framework/index.json)
and `v2.0.0` is absent from remote tags before executing the authorized release:

```sh
git ls-remote --tags origin refs/tags/v2.0.0
curl -fsS https://api.nuget.org/v3-flatcontainer/efcorenexus.framework/index.json
git tag -a v2.0.0 "$NEXUS_RELEASE_COMMIT" -m "EfCoreNexus.Framework 2.0.0"
git push origin refs/tags/v2.0.0
curl -fsS https://raw.githubusercontent.com/thliborius/EfCoreNexus/v2.0.0/docs/release-notes-2.0.0.md
curl -fsS https://raw.githubusercontent.com/thliborius/EfCoreNexus/v2.0.0/docs/migration-v2.md
dotnet nuget push artifacts/packages/EfCoreNexus.Framework.2.0.0.nupkg --source https://api.nuget.org/v3/index.json --api-key "$NUGET_API_KEY"
```

Supply `NUGET_API_KEY` through the release operator's secret environment; never
commit or print it. Stop on any failed command. Verify all packaged README links
against the tag before the NuGet push; then verify public installation in a fresh
consumer after NuGet indexing. The
[official push reference](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-push)
describes the publication command. A GitHub release, if desired, can use the
[release notes](release-notes-2.0.0.md) and the same tag after separate approval.

**Preparation status:** no merge, tag, package push, GitHub release or deployment
was performed. At preparation time, nuget.org lists versions through 1.0.5 only;
2.0.0 remains a locally validated release artifact until publication is authorized.
