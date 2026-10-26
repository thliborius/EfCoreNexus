# EfCoreNexus.Framework 2.0.1

Patch release for .NET 10 / EF Core 10. This release fixes several reproducible CRUD, tracking, concurrency, and model-discovery edge cases found after 2.0.0. The public API and EF Core 10 dependency remain unchanged.

```sh
dotnet add package EfCoreNexus.Framework --version 2.0.1
```

Select your EF Core 10 database provider separately (SQLite, SQL Server, etc.).

## Fixes

- **NoTracking updates:** `UpdateAsync` now tracks a detached row returned by the lookup, preventing silent loss of edits under global `NoTracking` or `NoTrackingWithIdentityResolution`, without changing the context's global setting.
- **NoTracking ID deletes:** `DeleteAsync(id)` preserves owned-collection shadow keys by using a tracked lookup and restoring the original tracking setting, including for caller-owned contexts.
- **Added-entity deletion:** `DeleteAsync(db, id)` rejects an `Added` target before it is detached or any pending writes are saved.
- **Concurrency with pending Complex-Type changes:** The update path detects pending Complex-Type modifications when applying a detached input's expected concurrency token.
- **Discovery:** `RegisterNexusEntities` respects pre-convention Complex-Type classification instead of registering the type as an entity root.
- **Mapped subtype safety:** Detached updates now reject inputs whose mapped EF entity type differs from the loaded target, preventing cross-subtype scalar copying. **This is an intentional behavior change** for such mismatched inputs.

Related work: [#26](https://github.com/thliborius/EfCoreNexus/issues/26) / [PR #27](https://github.com/thliborius/EfCoreNexus/pull/27), and [#28](https://github.com/thliborius/EfCoreNexus/issues/28) / [PR #29](https://github.com/thliborius/EfCoreNexus/pull/29).

## Scope and compatibility

- No new public API, framework abstraction, or dependency upgrade.
- Generic updates remain **full scalar edits**, not partial patches or graph merges.
- Borrowed-context writes still save the entire caller-owned tracker. Arbitrary pending owned-entity edits are not guaranteed to be protected by the owner's detached concurrency token; use a specialized native EF operation when required.
- Deletes using only a base-type instance cannot promise to check subtype-specific concurrency tokens; use a concrete typed provider or native EF for those hierarchies.

See [native setup and contracts](native-setup.md) and the [v2 migration guide](migration-v2.md). For v1 users, **2.0.0** introduced major breaking changes; review the [2.0.0 release notes](release-notes-2.0.0.md).

## Validation of the fixes

The final hardening PR reported **172 passing SQLite tests** and **7 passing SQL Server 2025 integration tests**, with both GitHub CI jobs passing and no known vulnerable dependencies in its audit. Before publishing 2.0.1, validate the final release commit, inspect the packed NuGet artifact (metadata, files and version), and test a fresh local-package consumer. Publication is a separate step.
