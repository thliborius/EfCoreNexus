# SQL Server integration tests (#21)

The optional `EfCoreNexus.SqlServer.Tests` project references the framework directly.
It is deliberately **outside `EfCoreNexus.sln`**: the normal root `dotnet test`
still runs the 133 SQLite tests without Docker. SQL Server, SqlClient and
Testcontainers dependencies belong only to this test project.

## Run explicitly

Install the .NET 10 SDK and provide a reachable Docker Engine running Linux
containers. No local SQL Server, connection string, fixed port or credentials
are needed. From the repository root, in Bash or PowerShell:

```sh
dotnet restore EfCoreNexus.SqlServer.Tests/EfCoreNexus.SqlServer.Tests.csproj -p:NuGetAudit=true -p:NuGetAuditMode=all -warnaserror:NU1901,NU1902,NU1903,NU1904
dotnet build EfCoreNexus.SqlServer.Tests/EfCoreNexus.SqlServer.Tests.csproj --no-restore
dotnet test EfCoreNexus.SqlServer.Tests/EfCoreNexus.SqlServer.Tests.csproj --no-build --logger "console;verbosity=normal"
dotnet list EfCoreNexus.SqlServer.Tests/EfCoreNexus.SqlServer.Tests.csproj package --vulnerable --include-transitive
```

The first run downloads the image. Budget at least 2 GB RAM for SQL Server plus
memory for Docker/.NET and several GB of disk for the image and databases.
[Microsoft supports these images on Linux x86-64 hosts](https://learn.microsoft.com/en-us/sql/linux/sql-server-linux-docker-container-deployment?view=sql-server-ver17);
ARM emulation is not a supported substitute. Windows users need Docker Desktop
in Linux-container mode or a compatible Docker host. Windows/macOS execution
has not been tested here; Apple Silicon is not a validated target.

One NUnit fixture starts one container for all seven cases. Each case gets a
unique database, deleted during teardown; connection pooling is disabled for
these disposable databases. `MsSqlBuilder` selects a random host port and
`GetConnectionString()` supplies the actual endpoint. The password is generated
per run, never stored or printed. The module sets `ACCEPT_EULA=Y` only on this
container; `MSSQL_PID=Developer` selects the development/test edition and
`MSSQL_MEMORY_LIMIT_MB=2048` bounds SQL Server's memory budget. See the
[module documentation](https://dotnet.testcontainers.org/modules/mssql/) and
[Microsoft container license/setup instructions](https://learn.microsoft.com/en-us/sql/linux/quickstart-install-connect-docker?view=sql-server-ver17).

Startup has a five-minute cancellation budget. Missing Docker, failed image
pulls and startup errors fail fixture setup with the original exception attached;
there are no environment-based skips. Startup failure disposes any constructed
container after printing available server logs; fixture teardown awaits `DisposeAsync()` after normal execution or
test failure. Testcontainers' resource reaper stays enabled for abnormal process
termination. There is no persistent container reuse across test runs.

## Pinned versions

Verified directly against NuGet and Microsoft Container Registry on **2026-10-25**:

| Component | Selection |
| --- | --- |
| EF Core SQL Server provider | `10.0.13`, matching the framework/SQLite baseline |
| Testcontainers.MsSql | `4.16.0`, [stable release](https://github.com/testcontainers/testcontainers-dotnet/releases/tag/4.16.0), published October 2026 |
| SQL Server | 2025 CU10, Enterprise Developer Edition, Ubuntu 24.04, Linux amd64 |
| Image tag | `mcr.microsoft.com/mssql/server:2025-CU10-ubuntu-24.04` |
| Immutable manifest digest | `sha256:245f6b1c8903edae52cb20fa31419cefcde1a49938bb8c3447099011fc871ca3` |

The fixture uses **tag plus digest**, avoiding a moving `latest` tag. MCR's
[`tags/list`](https://mcr.microsoft.com/v2/mssql/server/tags/list) confirmed CU10
was available; the manifest's `Docker-Content-Digest` was verified by pulling it.
The container image labels identify build `17.0.5035.5`; the fixture also prints
the running server's `@@VERSION` into test output. On updates, recheck the
[NuGet version index](https://api.nuget.org/v3-flatcontainer/testcontainers.mssql/index.json)
and MCR manifest, change both pins, rerun the cases and update this evidence.

The initial audited restore detected vulnerable transitive
`System.Security.Cryptography.Pkcs 9.0.11` through
`Microsoft.EntityFrameworkCore.SqlServer 10.0.13 → Microsoft.Data.SqlClient 6.1.7`.
The test project explicitly pins the patched servicing version `9.0.21` for
[GHSA-2fmv-rv2c-59x7](https://github.com/advisories/GHSA-2fmv-rv2c-59x7) and
[GHSA-846f-f764-76mx](https://github.com/advisories/GHSA-846f-f764-76mx).
No audit warning is suppressed and no framework/demo dependency is changed.

## Executed validation (2026-10-25)

Host: Linux Mint 22.3 x64 in VMware, .NET SDK `10.0.113`, runtime `10.0.13`,
Docker Engine `29.9.2`. Solution and SQL Server project restore/build completed
without warnings. Both `dotnet list package --vulnerable --include-transitive`
audits completed without known vulnerable packages after the Pkcs pin.

The pinned **SQL Server 2025 CU10, build `17.0.5035.5`, passed 7/7 cases with
zero skips**, including real rowversion checks and commit/rollback verification.
The complete run took about 4 minutes 18 seconds. The running `@@VERSION`
confirmed Enterprise Developer Edition on Ubuntu 24.04.5 LTS.

The normal solution passed **133/133 SQLite tests, zero skips**. This also passed
with the Docker socket hidden in a separate Linux mount namespace (a local
validation using `bwrap`, not a test/runtime dependency). In that same isolated
environment, an explicit SQL Server CRUD case exited **1**, with
`DockerUnavailableException`, **0 passed, 1 failed, 0 skipped**. Merely setting
an invalid `DOCKER_HOST` was insufficient isolation: Testcontainers can discover
the default Unix socket as a fallback.

A comparison run passed **7/7 cases, zero skips**, on SQL Server **2022 CU27-GDR**,
build `16.0.4305.5`, using
`mcr.microsoft.com/mssql/server:2022-CU27-GDR1-ubuntu-22.04` at digest
`sha256:cf8f0ab2ebe61feec2eed2629061a07d247f791c2a8ddda74a4ac9f06a291718`.
The total run took about 3 minutes 23 seconds, mostly cold startup.

Initial SQL Server 2025 runs exceeded the original three-minute startup budget
on this VM and correctly reported setup failures, without skips. Native server
logs showed slow initialization before accepting connections. The fixture now
allows five minutes for cold startup; this is a test-host limit, not a framework
change. Both rowversion and transaction semantics remain native EF/SQL Server.
Docker inspection after the runs confirmed that the SQL Server containers were
removed. `git diff --check` passed. No framework implementation change was needed.

## Coverage and contracts

| Test | Evidence checked against SQL Server |
| --- | --- |
| `CrudReturnsIdentityKeysAndIdDeleteUsesCurrentVersionOrNoOpsWhenMissing` | Database-generated integer key, detached lookup, ID delete with the current token after an update, missing read and missing ID-delete no-op, actual empty table. |
| `RowversionGuardsDetachedUpdatesAndEntityDeletesWithOriginalBinaryParameters` | `IsRowVersion()` generates an eight-byte token on insert and a different token on update. Detached update succeeds with the original token; stale update and stale entity delete throw `DbUpdateConcurrencyException`. A fresh entity delete succeeds without a SELECT. Fresh contexts confirm conflicts did not alter the winner. |
| `UnchangedUpdateIssuesNoUpdateAndDoesNotAssertEvenAStaleRowversion` | Both current and stale snapshots whose writable scalars equal the stored row cause no UPDATE and leave the database token unchanged. This deliberately does not assert the supplied version. |
| `RunnerCommitsTwoProvidersImmediateSavesAndTheFinalSaveOnOneNativeTransaction` | Two providers receive the exact runner context; native `SqlTransaction` and `@@TRANCOUNT = 1` confirm the real transaction. Identity keys and tracked rowversions refresh during immediate saves; explicit and final runner saves commit. A fresh context verifies all three tables. |
| `RunnerRollsBackAlreadySavedChangesAfterCallbackOrDatabaseFailure` (three cases) | Callback exception, immediate provider FK failure and final-save FK failure each undo earlier provider updates/inserts and an explicit `SaveChangesAsync`. Reads inside the transaction confirm actual writes occurred. Fresh contexts confirm the original row/token survived and both dependent tables are empty. SQL error 547 confirms real constraint failures. |

A passive command interceptor records completed async reader/nonquery commands
and copies their bound values. Assertions inspect UPDATE/DELETE key predicates,
the **original binary** rowversion parameter (including stale values), generated
token OUTPUT and exclusion of key/token columns from SET. Captured SQL and the
expected token parameter are included in test output. No SQL execution or
transaction is intercepted/simulated.

Generic detached update does not refresh its input's generated values: reload
before the next edit/delete. A tracked borrowed update does refresh them. The
tests preserve both contracts and the no-change behavior. They do not validate
execution-strategy retries, production migrations, other SQL Server versions,
or all EF model shapes. No framework implementation or public API change is needed.

## Separate GitHub Actions job

The available GitHub OAuth credential has `repo` but **no `workflow` scope**
(`gh auth status`). GitHub requires that scope to add/update workflow files; see
[GitHub's scope documentation](https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/scopes-for-oauth-apps#available-scopes).
Consequently this PR leaves `.github/workflows/dotnet.yml` unchanged. The SQL
Server job below has **not run in GitHub Actions**. During the PR #22 CI follow-up,
an actual push of this addition to the existing PR branch was rejected by GitHub:

> refusing to allow an OAuth App to create or update workflow `.github/workflows/dotnet.yml` without `workflow` scope

The ready-to-apply [workflow patch](patches/sql-server-ci.patch) adds this separate
job while preserving the existing regular CI job byte for byte. Its 15-minute
timeout allows for image download and the fixture's five-minute cold-start budget.
There are no fixed credentials, secrets or additional actions.

Use a clean checkout and your own GitHub identity with permission to update
workflow files (OAuth/classic PAT authentication needs the `workflow` scope).
Run these commands from the repository root, stopping if any command fails:

```sh
git fetch origin
git switch feature/issue-21-sql-server-integration
git pull --ff-only
git apply --check docs/patches/sql-server-ci.patch
git apply docs/patches/sql-server-ci.patch
git diff --check
git diff -- .github/workflows/dotnet.yml
git add .github/workflows/dotnet.yml
git commit -m "ci: run SQL Server integration tests in a separate job"
git push origin HEAD
gh pr checks 22 --watch
```

The push updates **PR #22 against `v2`** and triggers the existing pull-request
workflow with both jobs. Check the `sql-server` job log for seven passed tests
and zero skips; Docker/startup errors must fail it. No CI success is claimed for
the unapplied patch. The patch appends this job under the existing `jobs:`:

```yaml
  sql-server:
    runs-on: ubuntu-24.04
    timeout-minutes: 15
    permissions:
      contents: read
    steps:
      - uses: actions/checkout@v7.0.1
      - uses: actions/setup-dotnet@v6.0.0
        with:
          dotnet-version: 10.0.x
      - name: Check Docker
        run: docker info
      - name: Restore and audit
        run: dotnet restore EfCoreNexus.SqlServer.Tests/EfCoreNexus.SqlServer.Tests.csproj -p:NuGetAudit=true -p:NuGetAuditMode=all -warnaserror:NU1901,NU1902,NU1903,NU1904
      - name: Build
        run: dotnet build EfCoreNexus.SqlServer.Tests/EfCoreNexus.SqlServer.Tests.csproj --no-restore
      - name: Test SQL Server
        run: dotnet test EfCoreNexus.SqlServer.Tests/EfCoreNexus.SqlServer.Tests.csproj --no-build --logger "console;verbosity=normal"
      - name: Report dependency audit
        run: dotnet list EfCoreNexus.SqlServer.Tests/EfCoreNexus.SqlServer.Tests.csproj package --vulnerable --include-transitive
```

The existing PR job continues to validate the normal solution/SQLite tests.
No service container, SQL Server secrets or extra orchestration are required;
Testcontainers owns startup, readiness and cleanup inside the separate job.

Follow-up validation: solution restore/build and **133/133 SQLite tests** passed
again without warnings or skips. The SQL test project's restore/build and both
transitive NuGet audits passed. An isolated copy of the project restored without
the explicit Pkcs reference still resolved vulnerable `9.0.11`, failing with
`NU1902`/`NU1903`; therefore the existing `9.0.21` reference is unchanged. SQL test
sources and framework code are unchanged. The workflow YAML parsed successfully,
the regular job was verified unchanged, and `git apply --check` confirmed the
patch applies cleanly. This follow-up did not rerun the SQL container tests.
