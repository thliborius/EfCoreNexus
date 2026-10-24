# EfCoreNexus v2 architecture

**Less boilerplate. More Entity Framework.** Issues #8–#12 implement the selected
contract. The [historical proposal](history/architecture-proposal.md) records
alternatives, inherited defects and intermediate validation. Start with the
[README](../README.md) and [native API guide](native-setup.md).

## Final public surface

| Surface | Why it remains |
| --- | --- |
| `ProviderBase<TEntity,TId,TContext>` | Six ordinary async CRUD calls remove repeated factory/dispose/query/save code. Four context-first write overloads reuse the same semantics in an explicit unit of work. Two protected `WithContextAsync` forms own and dispose custom operations. |
| `IProviderCrud<TEntity,TId>` | Optional substitution boundary for ordinary CRUD; no automatic interface selection. |
| `IEntity` | Optional entity-discovery marker only; providers do not require it. |
| `AddNexusProviders<TContext>` | One bounded assembly scan replaces per-provider registration, using ordinary scoped DI. |
| `RegisterNexusEntities` | One bounded model scan replaces per-entity DbSet/model edits. Native mappings remain authoritative. |
| `TransactionRunner<TContext>` | Optional single callback owns transaction/context cleanup across several immediate saves. |

`ContextOperations` is internal and shares ownership/exception cleanup between
providers and the runner. The public signatures and behavior agreed in #8–#11
are unchanged by #12. No v1 compatibility layer remains.

## Consumer and complexity review (#12)

The sample needs one factory registration, one provider assembly registration,
and two model setup calls. Adding `Memo` and `MemoProvider` required no changes
to these calls, no DbSet, and no configuration wrapper. DI resolution and actual
SQLite persistence are covered by integration tests. Discovery is deterministic,
idempotent and reports conflicting registrations; it deliberately scans only
explicit assemblies and the exact context type. Multi-model boundaries still
require predicates and care with EF's separate navigation/configuration discovery.

A native create normally needs create/dispose, Add and SaveChanges; a provider
call reduces that to `await provider.CreateAsync(entity)`. A custom query still
uses ordinary LINQ; `WithContextAsync` removes ownership cleanup, not query logic.
A new CRUD provider is a constructor and an empty class body. These are concrete,
small savings compared with v1's service/factory/configuration hierarchy.

Native EF is as simple or simpler for a one-off tracked edit, bulk operation,
projection, graph or one-save workflow. The sample's deactivate action uses a
factory context and one save. Wrapping it in the runner would add ceremony.
The runner earns its place only when composing several provider saves; each
borrowed write saves the **whole tracker**. Explicit context arguments prevent
hidden enlistment but require caller discipline. No retry or nested transaction
infrastructure is added.

The metadata-heavy generic update remains the most complex part: full scalar
edits and generated concurrency tokens need precise rules. Application-managed
token renewal stays a short specialized native method (`MemoProvider.RenameAsync`).
Do not extend this into generic graph merge or a form framework. The optional CRUD
interface and four borrowed overloads remain because they are agreed contracts;
removing them would need a separate API decision and real consumer evidence.

## Remaining limitations / technical debt

- SQLite sample concurrency uses a renewed Guid. SQL Server rowversion and retrying
  execution strategies remain unverified; automatic retries are unsupported.
- Assembly scans do not claim trim/Native AOT support; explicit native mapping and
  registration remain the fallback.
- Generic update is a full scalar edit, not a patch/upsert/graph merge. A no-change
  edit does not assert a version. Composite/shadow generic CRUD keys are unsupported.
- The demo uses `EnsureCreated` for a disposable local database. A production app
  needs an explicit migration/deployment policy, authorization and domain validation.
- Browser-side EF/database access is unsupported. WebAssembly needs a server API;
  this sample is Interactive Server only.

See [validation evidence](validation-12.md) for tests and platform limits.
