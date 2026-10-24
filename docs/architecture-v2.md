# EfCoreNexus 2.0: architecture and migration proposal

**Status: selected v2 API contract, pending human review in
[issue #6](https://github.com/thliborius/EfCoreNexus/issues/6).** This decision pass
builds on the proposal from [PR #5](https://github.com/thliborius/EfCoreNexus/pull/5)
(merged into `v2` at `964a9d1`) and
[issue #1](https://github.com/thliborius/EfCoreNexus/issues/1), under the
[design principles](design-principles.md). Selected means the recommendation to
approve, not an already approved or implemented API. This documentation change
closes neither issue; human review comes before implementation.

Review baseline: `origin/v2` at
[`64a63ba`](https://github.com/thliborius/EfCoreNexus/tree/64a63ba780e57259d45415f8ee5de4ea7359c06e),
including the .NET 10 migration. “v1 behavior” below means the inherited API on
that branch, not a claim that the published v1.0.5 package targets .NET 10.
All framework classes, sample source/components, project files, and three test
fixtures were inspected for that original proposal. Section 1 retains its
historical findings and test evidence; sections 2–7 now align with the selected
contract below. Code links refer to the unchanged source baseline.

Issue #6 starts from current `origin/v2` at `964a9d1`, which merged PR #5.
GitHub contains no separate submitted reviews or inline comments for PR #5;
its proposal and PR description provide the recorded review questions. This pass
resolves those questions into the recommendations below and keeps issue #6 open
for human review.

## 0. v2 API decisions

**Less boilerplate. More Entity Framework.** The initial v2 scope is typed CRUD
providers, opt-in registration, and an optional transaction callback. Native EF
remains usable on its own. No mandatory repository, session, or application layer
is introduced. The table records the final recommendations from this decision
pass; implementation details still needing validation are isolated in section 7.

| Decision | Keep / remove / add | Trade-off and migration impact | Implementation order |
| --- | --- | --- | --- |
| Typed, stateless providers | Keep `ProviderBase<TEntity,TId,TContext>`; inject concrete providers through normal DI | Dependencies become visible; migrate constructors to `IDbContextFactory<TContext>`. Keep `IProviderCrud<TEntity,TId>` as an optional ordinary-CRUD boundary; no interface per custom provider is required. | 1: ownership baseline; 2: DI |
| Service/factory chain | Remove `DataService` / `IDataService`, `ProviderFactory` / `IProviderFactory`, `IProvider`, `TransactionService`, `StartupConfiguration`, `DataAssemblyConfiguration` | Breaking source change; replace lookups and begin/commit calls. No initial compatibility adapter. | 2: DI; 4: transactions |
| Automatic registration | Add two opt-in helpers, `AddNexusProviders<TContext>` and `RegisterNexusEntities`, in the initial scope | Configure assemblies/model boundaries once; no DI or `DbSet` edits for each new type within them. Small bounded reflection cost; no new scanning package. | 2: DI/model |
| Context/model/tooling | Use native factories, `DbContext`, `IEntityTypeConfiguration<T>`, and configuration scanning; remove required custom base/factory/configuration hierarchy | Port model setup and preserve mappings. `IEntity` survives only as an optional discovery marker; no provider constraint. Design-time factory only when EF tooling needs one. | 2: DI/model |
| CRUD and keys | Async/cancelable reads, create, entity update, **ID delete**, and entity delete; single CLR `Guid`/`int`/`string` key via EF metadata | Generated keys work; no separate create/update ID. ID delete does not require a UI entity. Entity delete carries expected concurrency values. No Dynamic LINQ or generic graph merge. | 3: CRUD/concurrency |
| Transactions | Add optional stateless `TransactionRunner<TContext>` and four context-first write overloads | All writes save immediately; runner also saves once before commit. Four extra write signatures; reads on an existing context stay native EF. No ambient enlistment; native EF is preferable for one save. | 4: runner |
| Deferred conveniences | Exclude compatibility facade, generalized keys, paging DSL, pooling helper, source generator, retry/nesting/savepoint infrastructure | Native EF remains the route for advanced requirements. No speculative public surface or dependencies. | Later only with a concrete use case |

### Exact proposed surface

`ProviderBase<TEntity,TId,TContext>` takes `IDbContextFactory<TContext>` and uses
`where TEntity : class`, `where TId : notnull`, `where TContext : DbContext`.
These six virtual methods are the ordinary `IProviderCrud<TEntity,TId>` contract:

```csharp
Task<List<TEntity>> GetAllAsync(CancellationToken ct = default);
Task<TEntity?> GetByIdAsync(TId id, CancellationToken ct = default);
Task CreateAsync(TEntity entity, CancellationToken ct = default);
Task UpdateAsync(TEntity entity, CancellationToken ct = default);
Task DeleteAsync(TId id, CancellationToken ct = default);
Task DeleteAsync(TEntity entity, CancellationToken ct = default);
```

Each ordinary call creates and asynchronously disposes its own context. The base
also exposes exactly these four context-first virtual write overloads; they are **not**
on the context-independent CRUD interface:

```csharp
Task CreateAsync(TContext db, TEntity entity, CancellationToken ct = default);
Task UpdateAsync(TContext db, TEntity entity, CancellationToken ct = default);
Task DeleteAsync(TContext db, TId id, CancellationToken ct = default);
Task DeleteAsync(TContext db, TEntity entity, CancellationToken ct = default);
```

Borrowed calls never create/dispose a context or begin/commit/roll back a
transaction. Writes call `SaveChangesAsync` on **the entire supplied tracker**.
The caller owns that unit of work. For specialized methods the
base supplies only two protected ownership helpers:

```csharp
Task WithContextAsync(Func<TContext, CancellationToken, Task> operation,
    CancellationToken ct = default);
Task<TResult> WithContextAsync<TResult>(
    Func<TContext, CancellationToken, Task<TResult>> operation,
    CancellationToken ct = default);
```

They create/await/dispose, forward the token, and do **not** save implicitly.
Dispose on callback failure/cancellation too; preserve the initiating exception
if disposal also fails, as in section 4's cleanup rules. No deferred query may
outlive the callback. The runner takes the same native
factory and exposes one method, with no result/session/options hierarchy:

```csharp
Task ExecuteAsync(Func<TContext, CancellationToken, Task> operation,
    CancellationToken ct = default);
```

The runner owns a fresh context and async transaction per invocation, awaits the
callback, saves remaining changes, commits, and disposes both resources. Section 4
defines rollback/failure handling and the limits of immediate saves. Sections 2
and 3 specify registration signatures, CRUD semantics, and consumer examples.

### Why each retained member earns its place

| Member / abstraction | Recurring benefit and boundary |
| --- | --- |
| `GetAllAsync` / `GetByIdAsync` | Materialize common reads and close the operation context. Keep lists for small sets only; no paging/query DSL. |
| `CreateAsync` | Remove repeated add/save/dispose code; EF still generates keys and enforces constraints. |
| `UpdateAsync` | Remove repeated scalar read/compare/save work for simple edit forms. Limited to the documented scalar/token contract, not an aggregate editor. |
| `DeleteAsync(id)` | A list/delete button needs only its key; callers need no entity or tracker. |
| `DeleteAsync(entity)` | A caller already holding an edit snapshot can request token-aware deletion with one call. Retain rather than requiring repeated attach/original-value code. |
| Four borrowed write overloads | Reuse those write contracts in one explicit transaction; they save on the entire shared tracker. No borrowed read overloads: `db.Set<T>()` already removes the context-ownership boilerplate. |
| `ProviderBase` / optional `IProviderCrud` | Share the six ordinary operations and allow substitution without requiring a custom interface per provider. No automatic interface-to-provider selection. |
| Two protected `WithContextAsync` forms | Close owned contexts around custom commands/queries without repeating conditional disposal. No implicit save or deferred results. |
| Two discovery helpers / optional `IEntity` marker | Remove per-type registration edits within one explicit boundary; native configuration remains native. |
| Optional `TransactionRunner` with one callback | Centralize multi-save cleanup; use native EF when one save suffices. No session, result, transaction handle, or options hierarchy. |

Remove borrowed read methods, native configuration/factory wrappers, extra key
APIs, patch/graph methods, and compatibility lookup methods from the proposed
core: their native alternatives are already short or their recurring value has
not been demonstrated. No public API is added solely for future flexibility.

## 1. Assessment of the current architecture

The useful core is small: extensible CRUD providers, ordinary LINQ, automatic
registration, and factory-created contexts. Keep these benefits. The main
problem is ownership: a circuit-scoped service implicitly switches every
provider between an operation context and a shared transaction context.

| Current surface | Responsibility and assessment |
| --- | --- |
| [`DataService<TContext>` / `IDataService<TContext>`](../EfCoreNexus.Framework/Services/DataService.cs#L7) | Provider lookup, exposed `CtxFactory`, and begin/commit/dispose forwarding. Constructs its own transaction service and eagerly creates all providers. `GetProvider<T>()` and `GetProviderCrud<TEntity,TId>()` hide dependencies and choose the first match. `IDataService` exists but startup registers only the concrete service. |
| [`ProviderBase<TEntity,TId,TContext>`](../EfCoreNexus.Framework/Provider/ProviderBase.cs#L9) | Useful CRUD defaults and overridable query hooks. Also repeats context/disposal logic and builds key predicates from attributes and dynamic strings. |
| [`IProviderCrud<TEntity,TId>` / `IProvider`](../EfCoreNexus.Framework/Provider/IProviderCrud.cs#L5) | CRUD contract plus empty discovery marker. Current methods: `Create(item,id)`, `GetAll()`, `GetAllAsync()`, `GetById(id)`, `Update(item,id)`, `Delete(id)`; no cancellation tokens. |
| [`ProviderFactory<TContext>` / `IProviderFactory<TContext>`](../EfCoreNexus.Framework/Provider/ProviderFactory.cs#L7) | Scans an assembly and invokes a constructor with exactly one `TransactionService<TContext>`. Bypasses DI constructor injection, dependency validation, and provider disposal by the container. |
| [`TransactionService<TContext>`](../EfCoreNexus.Framework/Services/TransactionService.cs#L6) | Owns mutable context/transaction fields, with synchronous begin, async commit, and transaction-only cleanup. The context is neither disposed nor cleared. |
| [`BaseContext<TContext>`](../EfCoreNexus.Framework/Context/BaseContext.cs#L8) | Applies custom configurations and scans `Assembly.GetCallingAssembly()` for `IEntity` implementations with a public parameterless constructor. Convenient absence of `DbSet` properties, but assembly choice and constructor restriction are unnecessary. |
| [`BaseContextFactory<TContext>`](../EfCoreNexus.Framework/Context/BaseContextFactory.cs#L10) | Combines runtime and design-time factories, holds a mutable options builder, and instantiates configuration objects. Its parameterless constructor is already initialized correctly on this branch. |
| [`EntityTypeConfigurationDependency<T>`](../EfCoreNexus.Framework/Helper/EntityTypeConfigurationDependency.cs#L6) | Two custom base classes wrapping EF's existing `IEntityTypeConfiguration<T>`. |
| [`StartupConfiguration<TContext>`](../EfCoreNexus.Framework/Helper/StartupConfiguration.cs#L9) | Registers scoped DataService/provider factory and scoped delegates returning pre-created factory/options/assembly objects; also registers configuration singletons already instantiated by the factory. Providers themselves are absent from DI. |
| [`DataAssemblyConfiguration`](../EfCoreNexus.Framework/Helper/DataAssemblyConfiguration.cs#L5) / [`IEntity`](../EfCoreNexus.Framework/Entities/IEntity.cs#L3) | Name-based lookup of an already loaded assembly; optional automation marker currently mandatory for providers. Explicit assembly references would remove load-order ambiguity. |

Strengths to preserve: no prescribed application architecture, no separate query
language for custom queries, one entity plus one short provider for CRUD, optional
mapping classes, and SQLite tests independent of SQL Server. Ordinary calls before
any transaction generally do create and dispose their own context correctly.

### Confirmed defects and technical risks

These are source-derived findings. The passing tests below do not reproduce or
disprove every failure scenario.

| ID / category | Evidence and concrete consequence |
| --- | --- |
| F1 — **defect, critical** | [`DisposeTransaction`, lines 34–40](../EfCoreNexus.Framework/Services/TransactionService.cs#L34) disposes only the transaction. `CtxTransaction` stays referenced and undisposed. [`GetContextAsync`, lines 184–191](../EfCoreNexus.Framework/Provider/ProviderBase.cs#L184) subsequently returns it; provider cleanup also skips it. Reproduce: begin → write → commit → dispose → read/write. The later call reuses the old tracker/context outside the intended transaction. A new begin overwrites and leaks the old context. Commit itself does not save pending changes. |
| F2 — **defect, critical under overlap** | [`DataService` construction](../EfCoreNexus.Framework/Services/DataService.cs#L11) gives every provider the same transaction service. An unrelated component's call during a transaction implicitly joins it. Two begins can both pass the unprotected null check; overlapping operations can use one context concurrently. Even a single circuit's reentrant async execution can interleave ownership changes. |
| F3 — **defect, failure cleanup** | [`BeginTransaction`, lines 13–21](../EfCoreNexus.Framework/Services/TransactionService.cs#L13) stores the context before beginning the transaction. If begin throws, it leaks and remains selected. If transaction disposal throws, the field reset is skipped. Neither service implements disposal, so DI scope disposal cannot clean up an abandoned transaction/context. A provider's `finally` checks current shared state rather than the ownership established when its operation started. |
| F4 — **defect, nested sample operation** | [`TestProvider.DeactivateAndCreate`, lines 38–58](../EfCoreNexus.TestApp/EfCoreNexus.TestApp.Data/Provider/TestProvider.cs#L38) calls begin inside a try and unconditionally disposes in finally. If begin rejects an already active outer transaction, finally disposes that outer transaction. Its synchronous enumeration and second enumeration in `UpdateRange` also add unnecessary database work. |
| F5 — **mapping limitation** | [`GetByIdQuery`, lines 167–176](../EfCoreNexus.Framework/Provider/ProviderBase.cs#L167) recognizes only the first CLR `[Key]`. Conventional `Id`, Fluent API keys, shadow keys, and composite keys are unsupported despite valid EF models. `GetProviderCrud` additionally restricts IDs to structs although other provider APIs do not. |
| F6 — **defect, async contract** | [`GetById`, lines 82–92](../EfCoreNexus.Framework/Provider/ProviderBase.cs#L82) executes synchronous `FirstOrDefault()` and wraps its result in a task. Database I/O blocks despite the task-returning API. Begin is also synchronous; no public operation propagates cancellation. |
| R1 — **CRUD design risk** | [`Create`, lines 129–145](../EfCoreNexus.Framework/Provider/ProviderBase.cs#L129) queries before insertion. Two callers can both pass this check; only a database constraint guarantees uniqueness. The separate `id` need not match the entity and complicates generated keys. Generic exceptions obscure duplicate/missing-row contracts. |
| R2 — **update/concurrency design risk** | [`Update`, lines 103–118](../EfCoreNexus.Framework/Provider/ProviderBase.cs#L103) reloads the row, copies scalar values, then marks the entire entry modified. This can overwrite unrelated columns. With a detached stale UI entity, EF's original concurrency token comes from the fresh reload unless explicitly replaced; stale edits can therefore escape intended conflict detection. No current sample token or test exercises this. `Delete(id)` similarly expresses deletion of the currently loaded version, not the UI's version. |
| R3 — **tracking/failure design risk** | [`GetAllQuery`](../EfCoreNexus.Framework/Provider/ProviderBase.cs#L162) uses no tracking, while key reads track. After F1 those tracked reads persist across operations. Create/Delete reset one entry on save failure, Update does not; none of these resets restores an entire shared tracker or transaction after failure. A later provider's save can flush another provider's pending changes. |
| R4 — **discovery/DI limitation** | [`ProviderFactory`](../EfCoreNexus.Framework/Provider/ProviderFactory.cs#L14) scans all `IProvider` types without context filtering. Mixed-context assemblies or additional constructor dependencies fail at runtime. [`BaseContext`](../EfCoreNexus.Framework/Context/BaseContext.cs#L11) infers its assembly independently of the explicitly configured provider/configuration assembly; it can silently miss entities or include unsuitable abstract/owned types. Configuration objects are built twice through separate paths. |
| R5 — **sample/setup risk** | [`Program`, lines 48–61](../EfCoreNexus.TestApp/EfCoreNexus.TestApp/Program.cs#L48) hard-codes Windows-style SQL Server settings, enables MARS, and suppresses the savepoint warning. [`MainContextFactory`](../EfCoreNexus.TestApp/EfCoreNexus.TestApp.Data/Context/MainContextFactory.cs#L16) uses a separate environment variable. Runtime/tooling databases can diverge. |

F1–F4 and the blocking I/O in F6 need corrections regardless of broader architecture
choices. F5 and R1–R5 require explicit contracts or simplification; they do not
imply every existing app fails. Cancellation is a recommended API improvement.
Pooling, paging helpers, a compatibility facade, and composite/shadow generic key
support are optional extensions, not fixes required to make the core safe.

### Sample and test coverage

[`HomeComponent`](../EfCoreNexus.TestApp/EfCoreNexus.TestApp/Components/Pages/Home.razor.cs#L16)
inserts data during initialization and then demonstrates a transaction and custom
query. `Program` registers server and WebAssembly render modes, but
[`App`](../EfCoreNexus.TestApp/EfCoreNexus.TestApp/Components/App.razor) /
[`Routes`](../EfCoreNexus.TestApp/EfCoreNexus.TestApp/Components/Routes.razor) /
[`Home`](../EfCoreNexus.TestApp/EfCoreNexus.TestApp/Components/Pages/Home.razor)
do not select an interactive render mode. This is not evidence of working
Interactive Server or browser database access. Enabling interaction with
prerendering also requires avoiding duplicate initialization writes.

The 17 NUnit tests pass on this baseline with
`dotnet test --no-restore -p:GeneratePackageOnBuild=false --verbosity minimal`
(.NET SDK 10.0.113; 17 passed, 0 failed/skipped). This builds the test dependency
graph, not the running Blazor application. No new tests or dependencies are added.

- [`TestProviderTests`](../EfCoreNexus.TestApp/EfCoreNexus.TestApp.Data.Tests/TestProviderTests.cs): CRUD, lookup, one custom query, successful transaction, double begin, and commit without a transaction. Transaction tests do not check context disposal, rollback after failure, reuse afterward, or two providers. A single shared SQLite connection cannot establish independent concurrent-operation behavior.
- [`BaseContextFactoryTests`](../EfCoreNexus.TestApp/EfCoreNexus.TestApp.Data.Tests/BaseContextFactoryTests.cs): parameterless initialization, configuration discovery, and one SQLite insert. The old uninitialized-factory issue is already fixed; do not reopen it as a v2 finding.
- [`FrameworkTests`](../EfCoreNexus.TestApp/EfCoreNexus.TestApp.Data.Tests/FrameworkTests.cs): missing assembly and a brittle service-count assertion. It never resolves a complete DI graph or verifies lifetimes.

Missing coverage includes F1–F6, resource disposal on every failure path,
independent overlapping calls, rollback across providers, conventional/Fluent/string
keys, detached optimistic concurrency, cancellation, and tracking contracts.

## 2. Recommended minimal architecture

**Prefer direct DI of stateless typed providers, native context factories and
configurations, and an optional transaction callback with an explicit context.**
Keep EF Core visible. `DbContext` already supplies the change tracker and unit of
work; a second repository/session hierarchy is unnecessary.

| Recommendation | Reason / alternative |
| --- | --- |
| Retain one extensible `ProviderBase<TEntity,TId,TContext>` | Shared CRUD and safe operation ownership earn their place. Keep `IProviderCrud` for ordinary CRUD substitutions and custom providers for specialized queries/business operations; direct EF remains equally valid. No mandatory interface per provider. |
| Use `AddDbContextFactory<TContext>` and `IDbContextFactory<TContext>` | Already native EF APIs; no custom runtime factory or options container. Factory-created contexts remain caller-owned. |
| Remove `ProviderFactory` / `IProviderFactory`, `StartupConfiguration`, and `DataAssemblyConfiguration` | One optional registration extension scans explicit `Assembly` objects and adds ordinary DI descriptors. DI constructs/disposes providers and resolves additional dependencies. Base-type discovery also makes the empty `IProvider` marker unnecessary. |
| Prefer ordinary `DbContext` over mandatory `BaseContext` | Replace configuration inheritance with `IEntityTypeConfiguration<T>` and `ApplyConfigurationsFromAssembly`. Keep a narrowly scoped entity-discovery extension only for the automation EF does not supply. |
| Remove stateful `TransactionService` and `DataService` / `IDataService` | An optional stateless `TransactionRunner<TContext>` owns resources per callback. No ambient circuit transaction or provider cache. No compatibility facade in the initial scope. |
| Separate runtime from design time | Use ordinary `IDesignTimeDbContextFactory<TContext>` only when EF tooling cannot create the context from the app. No mandatory derived factory for every app. |

The framework already references EF Core 10.0.13 on `v2`; recommend retaining
`net10.0` / EF Core 10 as the major-version baseline. EF10 requires .NET 10.
Most facilities used here predate EF10; upgrading alone does not fix ownership.
EF10-specific query features remain available through native LINQ without adding
wrappers. See [EF Core 10](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew).

### DI and model discovery

Recommend scoped providers and a scoped, stateless runner. Their circuit lifetime
is safe only because they never store contexts, transactions, or per-call entity
state. Additional dependencies must also support the intended overlapping usage.
Transient providers are a viable alternative but do not by themselves fix a
captured context. Singleton providers would unnecessarily constrain dependencies.

`AddDbContextFactory` defaults to a singleton factory/options. This is appropriate
for fixed configuration, not a singleton context. Avoid injecting the scoped
`TContext` registration that this API also supplies into components/providers.
For per-user configuration use a scoped factory and validate dependency lifetimes;
changing tenants within a circuit requires a separate explicit design. See
[factory registration](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.dependencyinjection.entityframeworkservicecollectionextensions.adddbcontextfactory?view=efcore-10.0)
and [Blazor factory lifetimes](https://learn.microsoft.com/en-us/ef/core/miscellaneous/multitenancy#blazor-server-apps-and-the-life-of-the-factory).

Automation is opt-in but **part of the initial v2 core convenience**, with these
proposed extension signatures (not an options/registration framework):

```csharp
IServiceCollection AddNexusProviders<TContext>(this IServiceCollection services,
    params Assembly[] assemblies) where TContext : DbContext;
ModelBuilder RegisterNexusEntities(this ModelBuilder model, Assembly assembly,
    Func<Type, bool>? predicate = null);
```

Automation has these explicit boundaries:

- `AddNexusProviders<TContext>(assemblies)` scans once at registration for concrete,
  closed providers derived, directly or indirectly, from the base for **that exact
  context**. Use ordinary scoped DI descriptors, avoid eager activation, make
  repeated identical registration idempotent. Register concrete types only: two
  specialized providers for the same entity are valid because no default is
  selected. An optional `IProviderCrud` mapping is explicit, e.g.
  `services.AddScoped<IProviderCrud<Customer, Guid>>(sp => sp.GetRequiredService<CustomerProvider>())`.
  Reject conflicting descriptors for a scanned concrete service rather than
  choosing the first/last. Ordinary DI reports ambiguous constructors or missing
  dependencies when resolving/validating; scanning must not instantiate providers.
  Explicit `AddScoped<CustomerProvider>()` remains supported. Do not add a scanning
  dependency such as Scrutor just for this operation.
- `RegisterNexusEntities(assembly, predicate)` registers concrete, closed `IEntity` root
  classes without requiring a parameterless constructor. Retain this marker as
  an **optional opt-in to discovery**, not a base-provider constraint. Exclude
  `[NotMapped]`, `[Owned]`, `[ComplexType]`, and types already mapped as
  owned/complex; configure owned/complex types through native EF mapping, without
  marking them as discoverable roots.
  The assembly is a deliberate inclusion boundary for this model; all eligible
  roots are included unless a predicate narrows it. For two contexts in one
  assembly, configure a stable namespace/type predicate per model once, e.g.
  `type => type.Namespace == "App.Sales.Entities"`. Alternatively separate data
  assemblies. Adding a type inside that boundary needs no registration edit.
  Never scan the AppDomain/calling assembly or infer entities from providers.
- Apply native configurations after discovery, then explicit context overrides.
  Use explicit assemblies/predicates for multiple models. Configuration scanning
  has no defined ordering and uses parameterless constructors; configurations
  needing dependencies/order should use explicit `ApplyConfiguration` instead.
  Configurations need no separate DI registration. See
  [EF model configuration](https://learn.microsoft.com/en-us/ef/core/modeling/).

Deduplicate input assemblies/types; fail with `ArgumentException` for invalid
arguments and `InvalidOperationException` naming the type/context for descriptor
conflicts or incompatible entity classifications. Do not silently skip assembly
load failures or use reflection order to resolve conflicts. Native EF model
validation still diagnoses incompatible keys/mappings. Conflicting native
configuration classes must be narrowed with the native predicate or applied
explicitly; the helper cannot infer the intent of configuration method bodies.
Marker discovery does not resolve DI services or demand entity constructors.

`ApplyConfigurationsFromAssembly` is **not** arbitrary entity scanning: it finds
configuration types. Unconfigured, unrelated entities still need marker discovery,
a `DbSet`, or `modelBuilder.Entity<T>()`. Navigation discovery remains native EF
behavior. See [entity inclusion](https://learn.microsoft.com/en-us/ef/core/modeling/entity-types).
Reflection-free explicit registration is the fallback for trimming/AOT; broad
Native AOT support and a source generator are not proposed for the first v2.

## 3. Public API and consumer examples

All v2 names/signatures below are proposed, not currently available. `ct` denotes
an application-provided `CancellationToken`; ordinary EF/Blazor namespaces are
omitted for brevity.

### Alternatives

| Choice | Consumer experience | Trade-off / recommendation |
| --- | --- | --- |
| A: `DataService.GetProvider<CustomerProvider>()` | One injected gateway, familiar to v1 users | Hides dependencies and delays missing-provider errors. Removed from initial v2; no compatibility adapter. |
| B: inject `CustomerProvider` | Explicit dependency, ordinary constructor injection and DI validation; specialized methods directly visible | More injected members when a component uses many providers. **Recommended default.** Use optional CRUD/application interfaces for substitutions; concrete DI alone does not make classes mockable. |
| C: inject `IDbContextFactory<MainContext>` | Native EF with full control; shortest path for one-off queries or tracked workflows | Caller writes creation/disposal and materialization. **Supported escape hatch**, not a competing mandatory layer. |

Actual v1 baseline, matching the sample's method names:

```csharp
[Inject] public DataService<MainContext> MainSvc { get; set; } = default!;

// Inside a component method:
var provider = MainSvc.GetProvider<TestProvider>();
var item = new Test { TestId = Guid.NewGuid(), Active = true };
await provider.Create(item, item.TestId);
var rows = await provider.GetAllAsync();
item.Content = "Edited";
await provider.Update(item, item.TestId);
await provider.Delete(item.TestId);
```

### Proposed setup and adding an entity

Before, the v1 setup requires custom context/factory classes and this chain
(`services` and `connection` come from the application):

```csharp
var options = new DbContextOptionsBuilder<MainContext>().UseSqlServer(connection);
var assembly = new DataAssemblyConfiguration(typeof(Test).Assembly.GetName().Name!);
var factory = new MainContextFactory(assembly, options);
new StartupConfiguration<MainContext>(factory, options).ConfigureDataservice(services);
```

A new v1 table needs an `IEntity` class with a key recognized by the provider and
a provider taking `TransactionService`. A v1 custom query manually borrows the
context and conditionally disposes it:

```csharp
public class Customer : IEntity
{
    [Key] public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool Active { get; set; }
}

public class CustomerProvider(TransactionService<MainContext> transactions)
    : ProviderBase<Customer, Guid, MainContext>(transactions)
{
    public async Task<List<Customer>> GetActiveAsync()
    {
        var db = await GetContextAsync();
        try
        {
            return await GetAllQuery(db).Where(x => x.Active)
                .OrderBy(x => x.Name).ToListAsync();
        }
        finally
        {
            if (TransactionSvc.CtxTransaction is null) await db.DisposeAsync();
        }
    }
}
```

After, keep the entity shape above, remove `[Key]` if using the `Id` convention,
and use native context/configuration APIs. The marker is needed only when using
the optional discovery helper:

```csharp
public sealed class Customer : IEntity // Marker only for optional discovery
{
    public Guid Id { get; set; } // EF key convention; no [Key] required
    public string Name { get; set; } = "";
    public bool Active { get; set; }
}

public sealed class MainContext(DbContextOptions<MainContext> options)
    : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        model.RegisterNexusEntities(typeof(Customer).Assembly); // Proposed helper
        model.ApplyConfigurationsFromAssembly(typeof(Customer).Assembly);
    }
}

// Enough for standard CRUD; custom query shown below goes inside this class.
public class CustomerProvider(IDbContextFactory<MainContext> factory)
    : ProviderBase<Customer, Guid, MainContext>(factory) { }

// Program.cs, once per application:
var connection = builder.Configuration.GetConnectionString("Main")
    ?? throw new InvalidOperationException("ConnectionStrings:Main is required.");
builder.Services.AddDbContextFactory<MainContext>(o => o.UseSqlServer(connection));
builder.Services.AddNexusProviders<MainContext>(typeof(CustomerProvider).Assembly);
// Optional, only when explicit multi-save transactions are needed:
builder.Services.AddScoped<TransactionRunner<MainContext>>();
```

After setup, a new ordinary table needs its entity and, if CRUD convenience is
wanted, one short provider. No per-entity DI or `DbSet` edit is required. With
native EF usage alone the provider can be omitted. A mapping class is optional:

```csharp
public sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
        => builder.Property(x => x.Name).HasMaxLength(200);
}

[Inject] public CustomerProvider Customers { get; set; } = default!;

// Inside a component method:
var customer = new Customer { Name = "Ada", Active = true };
await Customers.CreateAsync(customer, ct); // EF-generated Id is available here.
var customers = await Customers.GetAllAsync(ct);
var current = await Customers.GetByIdAsync(customer.Id, ct); // null if absent
customer.Name = "Ada Lovelace";
await Customers.UpdateAsync(customer, ct);
await Customers.DeleteAsync(customer.Id, ct); // No entity required by the UI.
// Alternative when holding expected concurrency values:
// await Customers.DeleteAsync(customer, ct);
```

A proposed protected `WithContextAsync<TResult>` helper owns one factory-created
context and awaits the callback before async disposal. It accepts native EF code,
not a query specification language. A specialized method becomes:

```csharp
public Task<List<Customer>> GetActiveAsync(CancellationToken ct = default)
    => WithContextAsync((db, token) => db.Set<Customer>()
        .AsNoTracking().Where(x => x.Active).OrderBy(x => x.Name)
        .ToListAsync(token), ct);
```

This replaces the v1 manual try/finally. To enlist the custom query in a shared
operation, use that LINQ directly on the supplied context. A custom provider may
offer its own context-taking method if the query is reused there; the base class
does not add borrowed read overloads for this. No separate query abstraction is needed.
Never return an `IQueryable`, lazy
loading dependency, or unconsumed stream from an owned context. Materialize within
its lifetime; use native factory ownership explicitly for streaming.

### Recommended CRUD contracts

Retain the three generic parameters for familiar, typed single-key calls, with
`TEntity : class`, `TContext : DbContext`, and `TId : notnull` (including strings).
Recommend these ordinary operations, all accepting `CancellationToken = default`:

| Proposed method | Behavior |
| --- | --- |
| `Task<List<TEntity>> GetAllAsync(ct)` | Materialized, no tracking. Keep for small sets; paging/filtering is ordinary specialized LINQ. |
| `Task<TEntity?> GetByIdAsync(TId id, ct)` | Native `FindAsync` on a fresh context; null when missing. Its internal tracking ends with that context, so the result has no live tracker. |
| `Task CreateAsync(TEntity entity, ct)` | Add and save once. Let EF generate keys. No existence pre-check; unique violations remain `DbUpdateException` with provider details. |
| `Task UpdateAsync(TEntity entity, ct)` | Locate by the entity's mapped key, copy allowed writable scalar values into a tracked row, preserve original concurrency tokens supplied by the caller, and save. Missing row: `KeyNotFoundException`. No graph merge, key mutation, upsert, or blanket `Modified` assignment. |
| `Task DeleteAsync(TId id, ct)` | Resolve the row by key, delete and save if found. Missing at lookup: successful no-op, preserving v1's ID-delete behavior. No caller version is asserted; a change/delete between lookup and save can still raise `DbUpdateConcurrencyException`. A borrowed context may resolve an already tracked row, just as native `FindAsync` does. |
| `Task DeleteAsync(TEntity entity, ct)` | Delete by the mapped key and caller's expected concurrency tokens, without a preliminary existence check. Missing row **or** stale token: `DbUpdateConcurrencyException`. Without a configured token, this checks existence, not a historical version. |

Use `context.Model.FindEntityType(typeof(TEntity))?.FindPrimaryKey()` for key
shape/type validation and key extraction, rather than CLR attributes or a string
parser. A typed `TId` API should initially require one CLR key property; reject
composite/shadow/keyless mappings clearly before a write. Native
`FindAsync(new object?[] { orderId, lineId }, ct)` and specialized providers remain
available for composite keys in EF metadata order. An `object[]` public generic
key API would sacrifice type safety for an unproven requirement. See
[primary-key metadata](https://learn.microsoft.com/en-us/dotnet/api/microsoft.entityframeworkcore.metadata.ireadonlyentitytype.findprimarykey?view=efcore-10.0)
and [FindAsync / composite keys](https://learn.microsoft.com/en-us/ef/core/change-tracking/entity-entries#find-and-findasync).
This removes the framework's demonstrated need for `System.Linq.Dynamic.Core`;
recommend removing that package in a later implementation PR after checking
consumer overrides. No dependency changes belong in this PR.

For detached edits, set EF **original** concurrency values to the client's
expected values, not the freshly loaded row's values. Do not copy keys or
store-generated values into writable current values. Copy only ordinary writable
mapped scalar properties, compare values, and let EF detect actual changes;
do not copy navigations/owned/complex graphs or mark every column modified.
The key identifies the target row and is never copied into a different row. This
is a full scalar edit, not a patch: omitted/default fields are still supplied
values and may overwrite current data. Use a specialized method for allowed-field
updates. A no-change update need not issue SQL, so it is not a stale-version check.

Create uses native `Add` semantics, including reachable new entities; delete
follows EF/database cascade configuration. The scalar-only restriction above is
the generic update contract, not a replacement for native relationship behavior.
Handle aggregate graphs explicitly in specialized providers when their rules
require more than those native operations.

The detached entity must carry every configured original CLR concurrency token
from the read shown to the user, **unchanged** by the UI. This applies equally to
entity delete. Reloading the latest token first defeats stale-edit detection.
SQL Server `rowversion` can
be preserved this way; application-managed token regeneration or shadow tokens
need an explicit specialized operation with original/new values. A stale-token
test must prove the contract. Applications without tokens still accept ordinary
last-writer behavior. For a borrowed context, reuse its tracked row rather than
attaching a second instance with the same key. If the supplied entity is already
tracked, retain EF's existing `OriginalValues`; if detached, transfer its expected
tokens as above. New/deleted tracked rows or unsupported token shapes require a
specialized operation; do not silently reinterpret them as upserts.
Propagate `DbUpdateConcurrencyException`; the UI/application
chooses reload, merge, or retry. See
[EF optimistic concurrency](https://learn.microsoft.com/en-us/ef/core/saving/concurrency).

For example, with SQL Server `public byte[] Version { get; set; } = [];` mapped by
`builder.Property(x => x.Version).IsRowVersion()`, keep `Version` from the original
`GetByIdAsync` response on the UI edit model and send it back on update/delete.
After updating a detached entity, read it again for refreshed store-generated
values before another edit/delete; `UpdateAsync` does not promise to refresh the
detached input object. Generic update supports store-generated CLR tokens;
application-managed token renewal needs a custom operation with separate
original/new values. Entity delete can use application-managed CLR tokens as
expected values because deletion needs no renewal. Reject unsupported shadow
tokens (and application-managed tokens on generic update) with
`InvalidOperationException` before writing, rather than inventing token handling.

For SQLite or an application-managed token, an allowed-field operation stays
short with native EF. For example, add a `Guid Revision` initialized for inserts
to `Customer` and map it with `.IsConcurrencyToken()`. This custom method belongs
inside `CustomerProvider`; the caller passes the revision from its original read:

```csharp
public Task RenameAsync(Guid id, string name, Guid expectedRevision,
    CancellationToken ct = default)
    => WithContextAsync(async (db, token) =>
    {
        var customer = await db.Set<Customer>()
            .SingleOrDefaultAsync(x => x.Id == id, token)
            ?? throw new KeyNotFoundException();
        db.Entry(customer).Property(x => x.Revision).OriginalValue = expectedRevision;
        customer.Name = name;
        customer.Revision = Guid.NewGuid();
        await db.SaveChangesAsync(token);
    }, ct);
```

This explicitly distinguishes the expected and new token without adding a
generic updater, token factory, patch object, or merge strategy. Simple entities
without tokens can use the ordinary scalar update; SQL Server rowversion uses
the store-generated-token contract above.

Standard failures: null arguments → `ArgumentNullException`; unsupported key or
token mapping / mismatched `TId` → `InvalidOperationException` naming the mapping;
missing update at lookup → `KeyNotFoundException`; later update/delete races →
`DbUpdateConcurrencyException`; database constraint/save failures → EF's
`DbUpdateException` with provider details; cancellation → `OperationCanceledException`.
Do not wrap these in a library exception hierarchy or silently retry. In
particular, a duplicate create is a database constraint failure, not a reliable
pre-insert existence test. Native provider-specific failures still propagate.

Do not hide authorization or aggregate rules in generic CRUD; specialized methods
should select allowed fields. Remove the sync CRUD surface; await EF async I/O
and forward cancellation to creation, queries, saves, and transaction operations.
Avoid `Task.Run` / `Task.FromResult` around sync database calls. Providers may
honor cancellation differently; cancellation does not establish commit outcome.
See [EF async guidance](https://learn.microsoft.com/en-us/ef/core/miscellaneous/async).

### Blazor list/delete example

For a small administration list in an app configured for Interactive Server,
the component can inject its provider directly. Initialization only reads, so
prerendering cannot duplicate writes. The component owns cancellation, while the
provider owns each operation's context:

```razor
@page "/customers"
@using Microsoft.EntityFrameworkCore
@using static Microsoft.AspNetCore.Components.Web.RenderMode
@rendermode InteractiveServer
@implements IDisposable
@inject CustomerProvider Customers

@foreach (var customer in customers)
{
    <p @key="customer.Id">@customer.Name
        <button disabled="@busy" @onclick="() => DeleteAsync(customer.Id)">Delete</button>
    </p>
}
<p>@message</p>

@code {
    private readonly CancellationTokenSource lifetime = new();
    private List<Customer> customers = [];
    private bool busy;
    private string message = "";

    protected override async Task OnInitializedAsync()
    {
        try { customers = await Customers.GetAllAsync(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task DeleteAsync(Guid id)
    {
        if (busy) return;
        busy = true;
        message = "";
        try
        {
            await Customers.DeleteAsync(id, lifetime.Token);
            customers = await Customers.GetAllAsync(lifetime.Token);
        }
        catch (DbUpdateConcurrencyException) { message = "Changed while deleting. Reload the list."; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { busy = false; }
    }

    public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); }
}
```

This button deliberately deletes the current row by ID. An edit form that must
assert the version displayed to the user instead passes its unchanged token via
`DeleteAsync(customer, ct)` or `UpdateAsync(customer, ct)` and catches
`DbUpdateConcurrencyException`. After a successful update, reload the entity to
refresh store-generated values before another edit. The loading flag suppresses
duplicate clicks; correctness across components comes from separate operation
contexts and the database's concurrency checks.

## 4. Transaction ownership and Blazor concurrency

### Recommended model

Use normal per-operation saves for ordinary CRUD. For a business operation whose
changes can be saved once, use one factory-created context and one
`SaveChangesAsync`; EF already supplies the database transaction. An optional
runner is justified for workflows spanning several saves/providers: it centralizes
begin/commit/rollback/disposal without storing current state in DI.

Before, a v1 two-provider operation relies on the service's hidden shared state
(using v1-shaped `CustomerProvider` / `OrderProvider` counterparts):

```csharp
var customer = new Customer { Id = Guid.NewGuid(), Name = "Ada" };
var order = new Order { Id = Guid.NewGuid(), CustomerId = customer.Id };
try
{
    MainSvc.BeginTransaction();
    await MainSvc.GetProvider<CustomerProvider>().Create(customer, customer.Id);
    await MainSvc.GetProvider<OrderProvider>().Create(order, order.Id);
    await MainSvc.CommitTransaction();
}
finally
{
    await MainSvc.DisposeTransaction();
}
```

This illustrates inherited ceremony and the defects in section 1, not a safe
template to retain. After, the additional v2 types are ordinary EF types and one
short provider, automatically discovered inside the configured boundary:

```csharp
public sealed class Order : IEntity
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = default!;
}

public class OrderProvider(IDbContextFactory<MainContext> factory)
    : ProviderBase<Order, Guid, MainContext>(factory) { }

[Inject] public OrderProvider Orders { get; set; } = default!;
[Inject] public TransactionRunner<MainContext> Transactions { get; set; } = default!;
```

```csharp
// Customers, Orders, and Transactions are directly injected services.
await Transactions.ExecuteAsync(async (db, token) =>
{
    var customer = new Customer { Name = "Ada" };
    await Customers.CreateAsync(db, customer, token);
    await Orders.CreateAsync(db, new Order { CustomerId = customer.Id }, token);
}, ct);
```

Recommend four **explicit-context write overloads**, with `TContext`
as the first argument. They borrow the context: they never create, dispose,
commit, or roll it back. Writes save immediately, as the ordinary methods do,
but those saves are enclosed by the runner's transaction. They can flush any
other pending changes on that same tracker. Specialized business methods need
the same explicit-context path when they participate in a shared operation;
the ordinary base write methods delegate to their borrowed overloads through
`WithContextAsync`, so overrides do not require two implementations.

This choice preserves one save contract for ordinary and borrowed writes and
makes generated IDs available to the next call. It does **not** isolate each
provider's changes: if raw EF stages an unrelated audit row before
`Customers.CreateAsync(db, ...)`, that save also persists the audit row inside
the transaction. Only pass a context whose entire pending state belongs to the
same business operation. For precise staging/save boundaries, use native EF
on that context and call `SaveChangesAsync` explicitly. Do not add an auto-save
switch, per-entry save emulation, or differently timed overloads with the same
name. Native EF remains preferable when explicit staging is simpler.

The runner's proposed core signature is
`Task ExecuteAsync(Func<TContext, CancellationToken, Task> operation, CancellationToken ct = default)`.
It performs a final `SaveChangesAsync` for any remaining direct EF changes before
committing. This deliberately differs from v1's commit-only method. Calling an
ordinary provider overload in the callback creates an independent operation;
it does **not** join the transaction. This explicit-context requirement is the
main usability cost and must be visible in XML documentation and examples.

A successful provider save inside the callback is not a committed business
operation. If the transaction later rolls back, discard its in-memory entities;
rollback does not undo assigned keys, refreshed tokens, or tracker state.

There are no borrowed-context read methods on the base. Use
`db.Set<Customer>().AsNoTracking().Where(...).ToListAsync(token)` or
`db.Set<Customer>().FindAsync(new object?[] { id }, token)` directly. Native
`FindAsync` may return a tracked instance, including local changes; it does not
promise a database-fresh read. Read convenience is earned when the provider
creates/disposes the context, not when the caller already holds one.

### Required lifetime and failure behavior

The following is the recommended contract for implementation, including paths
that a plain `await using` sketch can obscure:

1. Create a fresh `TContext` into a local owned variable for **each invocation**.
   Begin a local transaction asynchronously. If creation/begin fails, dispose
   everything successfully acquired, including the context after begin failure.
2. Await the callback sequentially, perform the final save, then await commit.
   The callback may use several providers and raw EF on this exact context; all
   enlisted database changes use the same transaction. Use the provider's default
   isolation level initially; transactions alone do not prevent lost updates or
   read/modify/write races. Domain invariants may need tokens, constraints, or a
   deliberate native isolation-level choice.
3. On callback/save/cancellation/commit failure, attempt rollback with a separate,
   bounded cleanup token, even if the request token is canceled. Always attempt
   transaction disposal and then context disposal, even if rollback/disposal fails.
   Preserve the initiating exception and stack; attach secondary cleanup failures
   diagnostically rather than masking it. Surface cleanup failure if it is the
   only error. Never reuse a failed context.
4. After successful commit, dispose transaction and context too. A cleanup failure
   after successful commit must not suggest that database changes rolled back;
   report the known committed state diagnostically. If commit throws due to a
   lost connection/cancellation, outcome can be **unknown**, even after attempted
   rollback. Do not claim exactly-once behavior or invite blind retries.
5. No context/transaction is stored on the runner/provider or escapes the callback.
   No fire-and-forget work and no `Task.WhenAll` on that shared context. Parallel
   independent runner invocations get different contexts. Helpers cannot make
   arbitrary concurrent raw EF calls on a borrowed context safe; EF's own checks
   are diagnostic, not synchronization.

Nested calls must pass the existing context to participating methods. A second
runner invocation is a separate transaction, never a nested/enlisted one, even
if called lexically inside a callback. No automatic nesting, savepoint API,
`AsyncLocal`, or circuit semaphore is recommended. Likewise no distributed or
cross-`TContext` atomicity is promised; native connection/transaction sharing is
an advanced application concern. A failed workflow should unwind; swallowing a
provider exception inside a callback and continuing is not a supported recovery
pattern. See [native transaction behavior and limitations](https://learn.microsoft.com/en-us/ef/core/saving/transactions).

Recommend a **single-attempt runner**, without hidden delegate replay. A retrying
EF execution strategy can reject a user transaction unless the application wraps
the **whole runner invocation** in the native strategy, with fresh contexts and
mutable entities per attempt. Callbacks must be replay-safe; external effects and
unknown commit outcomes need application-specific handling. No retry integration
or exactly-once promise is part of this API. See
[EF execution strategies](https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency#execution-strategies-and-transactions).

### Alternatives considered

| Alternative | Assessment |
| --- | --- |
| Keep ambient scoped `TransactionService`, add reset/dispose | Repairs F1/F3 but leaves accidental enlistment and overlapping components. Locking an entire circuit would serialize unrelated operations and still hide ownership. Not recommended. |
| Explicit disposable UnitOfWork with `GetProvider<T>()` | Clear lifetime, but needs a provider rebinding/factory/resolver API and explicit commit/disposal ceremony. Consider only if passing contexts proves harder for real consumers. |
| Callback plus four explicit `TContext` write overloads | Reuses typed DI providers and EF, makes shared ownership visible, and centralizes cleanup. Recommended for multi-save work despite extra overloads and the need to pass the context. Borrowed reads stay native. |
| Native factory/context/transaction only | Fewest library concepts and full control. Always supported; for one save, usually preferable. The runner should survive only if it removes recurring failure-handling code. |

### Consumer ceremony compared with native EF

Native setup already needs just `AddDbContextFactory<MainContext>(...)`.
In the `OnModelCreating` example, it can replace discovery with
`model.Entity<Customer>(); model.Entity<Order>();`, or use `DbSet` properties or
configuration classes. A new unrelated entity then needs native model inclusion;
no provider is needed. V2 adds two once-per-boundary scanning calls to remove
those per-type edits when that convenience is wanted. Native configuration
scanning is shared by both approaches; no helper is earned by wrapping it.

For an isolated ID delete, direct EF remains short (factory injected as
`IDbContextFactory<MainContext> Factory`, `id` supplied by the UI):

```csharp
await using var db = await Factory.CreateDbContextAsync(ct);
var customer = await db.Set<Customer>().FindAsync(new object?[] { id }, ct);
if (customer is not null)
{
    db.Remove(customer);
    await db.SaveChangesAsync(ct);
}
```

The equivalent v2 call is `await Customers.DeleteAsync(id, ct)`. Likewise,
a direct custom query only needs context ownership plus native LINQ:

```csharp
await using var db = await Factory.CreateDbContextAsync(ct);
var active = await db.Set<Customer>().AsNoTracking().Where(x => x.Active)
    .OrderBy(x => x.Name).ToListAsync(ct);
```

The provider earns its place when that operation recurs; it does not shorten an
isolated query enough to justify creating a provider just for it.

For customer + order that can be saved once, native EF is simpler than the
two-provider runner example and is the recommendation:

```csharp
await using var db = await Factory.CreateDbContextAsync(ct);
var customer = new Customer { Name = "Ada" };
db.Add(new Order { Customer = customer }); // EF fixes up generated keys.
await db.SaveChangesAsync(ct); // Atomic through native EF transaction behavior.
```

| Task | v1 | Native EF | Selected v2 helper benefit / cost |
| --- | --- | --- | --- |
| App setup | Context + custom runtime/design factory + assembly/options/startup chain | Context + factory registration; ordinary model setup | Native setup + opt-in scanning; deletes custom hierarchy |
| New entity/provider | Marker/key + provider tied to transaction service; no DI edit | Entity + explicit/native discovery; no provider necessary | Marker + optional one-line provider; no per-type DI/`DbSet` edit within configured boundary |
| CRUD | Gateway lookup, separate create/update ID; manual transaction state | Ownership + native find/add/edit/remove/save | Six async methods hide repeated ownership; keep ID delete; entity token contract costs explanation |
| Custom LINQ | Manual conditional try/finally | Short ownership + awaited query | `WithContextAsync` removes ownership boilerplate; same LINQ |
| Two related inserts, one save | Shared mutable transaction gateway | Shortest: tracked graph + one save | Choose native EF; no runner required |
| Multi-provider / multi-save | Implicit enlistment; unsafe cleanup | Explicit transaction + error/cleanup handling | Callback centralizes cleanup; context must be passed, writes flush tracker; four extra write overloads |

No throughput or line-count benchmark is claimed. The examples demonstrate
where convenience removes recurring code and where ordinary EF wins. See
[native single-save transaction behavior](https://learn.microsoft.com/en-us/ef/core/saving/transactions#default-transaction-behavior).

### Hosting boundaries

In Blazor Server / Interactive Server, scoped services live across components in
one circuit. Reentrancy after `await` is sufficient to expose shared mutable
state; a single UI synchronization context is not a context-lifetime guarantee.
Separate components may issue independent operations concurrently because every
ordinary call owns a separate context. UI loading flags can suppress accidental
double clicks but cannot provide cross-component correctness. Long-lived tracked
edit contexts remain an explicit native EF choice with component ownership,
serialized access, and disposal. See
[Blazor EF guidance](https://learn.microsoft.com/en-us/aspnet/core/blazor/blazor-ef-core?view=aspnetcore-10.0).

For WebAssembly, recommend browser DTOs and `HttpClient` to an authenticated,
authorized server API. Providers/EF and connection secrets stay on the server;
one server request/business operation owns a multi-provider transaction. A
transaction must not span browser requests or user think time. Interactive Auto
needs that boundary for its client execution phase. Specialized browser-local
database providers are outside the initial support promise. Registering a
WebAssembly render mode does not make server DI/providers run in the browser.

## 5. Breaking changes and migration from v1.0.5

Recommend a SemVer 2.0 major of the existing package, with review/prerelease
validation before a stable release. Published v1 remains unchanged on `main`;
v2 targets .NET/EF10 without multi-targeting .NET 8. This document schedules no
release and changes no versions. Keep database-specific providers in consuming
apps; remove Dynamic LINQ only with the key-query implementation. Keep Design
tooling where migrations need it; do not remove the baseline's security override
without a separate dependency audit.

| v1 API / behavior | Proposed migration |
| --- | --- |
| `BaseContext<TContext>(options, configurations)` | Ordinary `DbContext(options)`; move model setup to explicit discovery/native configuration calls. Preserve table/schema/key/relationship mappings. |
| `BaseContextFactory<TContext>` | Runtime `AddDbContextFactory`; optional separate native design-time factory. Remove dual factory/setup constructors. |
| `StartupConfiguration`, named data assembly | Native registration plus explicit assembly references in optional helpers. |
| `EntityTypeConfigurationDependency<T>` | Implement `IEntityTypeConfiguration<T>`; replace override with ordinary `Configure`. |
| `IEntity` required everywhere | Keep only for optional marker discovery, or use native explicit model inclusion. No public parameterless constructor requirement. |
| `DataService.GetProvider<T>()` / `GetProviderCrud` | Inject typed provider or explicitly map the ordinary CRUD interface; remove gateway/provider factory. No initial lookup adapter. |
| Provider constructor takes `TransactionService` | Take `IDbContextFactory<TContext>` plus normal DI dependencies. Keep the existing three generic parameters initially. |
| `Create(item,id)` / `Update(item,id)` / `Delete(id)` | `CreateAsync(item,ct)` / `UpdateAsync(item,ct)` / **`DeleteAsync(id,ct)`**. ID-delete missing lookup remains a no-op; it asserts no client version. Opt into `DeleteAsync(item,ct)` for expected-token checks; missing rows then raise a concurrency exception. Preserve original tokens in detached edits. |
| `GetAll()` / `GetById(id)` | Await `GetAllAsync(ct)` / `GetByIdAsync(id,ct)`; string and Fluent/convention single keys become usable. Composite/shadow/keyless generic CRUD remains explicitly unsupported. |
| `GetContextAsync`, query hooks, manual conditional disposal | `WithContextAsync` for owned work; native LINQ with borrowed context for shared work. Port overrides individually; never leak deferred queries. |
| Begin/commit/dispose on service or provider | Callback with shared context passed to every participating provider; remove caller-controlled ambient state. Review final-save semantics and all exception paths. |
| Generic missing/duplicate exceptions, mixed tracking | Documented standard/EF exceptions and explicit tracking contracts. Update callers relying on old exception text, synchronous methods, or leaked tracking state. |

Suggested migration sequence: inventory custom overrides and transaction callers;
capture model/behavior regression tests; migrate context/configuration/setup;
port providers/CRUD and concurrency tokens; migrate components and multi-provider
operations; verify model equivalence and failure/concurrency tests before rollout.
The helper redesign should not require a schema change by itself. Compare model
snapshots/generated migrations: discovery changes or EF upgrades can alter the
model, so inspect scripts against a database copy rather than applying an
unreviewed migration automatically.

Use `ConnectionStrings:Main` consistently for runtime and design time (environment
name `ConnectionStrings__Main`, app configuration, or development user secrets).
Avoid hard-coded credentials/server paths. Remove the sample's MARS setting and
warning suppression unless there is a measured requirement. Prefer the existing
platform-neutral `dotnet ef` commands; `.bat` files can remain optional. An
explicit design-time factory, when needed, must construct the same model/provider
from that configuration and fail clearly when missing. See
[EF design-time creation](https://learn.microsoft.com/en-us/ef/core/cli/dbcontext-creation).
Linux/macOS users can use an accessible SQL Server host or a separately documented
SQLite sample option; selecting SQLite requires deliberate provider/migration
changes, not just replacing a connection string. SQLite remains the default test
database, not proof of SQL Server rowversion/savepoint behavior.

## 6. Prioritized follow-up work and acceptance criteria

Propose the following five implementation issues, in dependency order, **after
human approval of this contract**. They are not created by this ticket. Keep
each implementation PR focused; split a test/implementation pair if needed.
All retain the .NET/EF10 baseline and platform-neutral SQLite default tests.

| Order / proposed issue | Verifiable acceptance criteria |
| --- | --- |
| 1 — P0: Capture ownership regressions and add owned-operation helper | Reproduce F1/F3/F4 before replacement: post-commit reuse/leak, begin failure, rejected nested begin, cleanup exceptions. Count created/disposed resources and preserve initiating exceptions. Test both `WithContextAsync` forms: fresh context per call, callback failure/cancellation disposal, no implicit save, no deferred results in examples. Controlled overlap uses distinct connections to a shared SQLite database. |
| 2 — P1: Native model/factory setup and opt-in DI discovery (after 1) | Replace the custom factory/configuration chain. Test constructor dependencies, DI validation/disposal, indirect inheritance, idempotent scans, conflicting descriptors, two contexts in one assembly with predicates, all exclusions, marker-only entities, configuration-only entities, and native configuration overrides. Adding an entity/provider within the configured boundary needs no startup edit. Runtime/design-time models match. |
| 3 — P1: Async CRUD, metadata keys, and concurrency (after 1–2) | Compile consumer calls for all six ordinary methods/interface mappings. Test convention/Fluent `Guid`/`int`/`string` and generated keys, cancellation, unsupported keys/tokens/type mismatch, database duplicate constraints, null reads, missing updates, ID-delete no-op, entity-delete missing/stale conflict, and lookup/save races. Verify scalar-only changes, no key/generated-column overwrite, tracked identity reuse, no-change update behavior, and full-edit defaults. SQLite uses explicit original/new application-managed token operations for stale updates and generic entity-delete token tests; add focused SQL Server `rowversion` coverage before claiming that provider behavior. Remove Dynamic LINQ only when no framework use remains. |
| 4 — P0: Optional runner and four borrowed write overloads (after 3) | Verify the four borrowed write signatures; shared reads stay native EF. Two providers commit/roll back together; ordinary calls inside the callback remain independent. Assert immediate tracker-wide saves, generated keys between calls, final-save behavior, and borrowed resources never disposed by providers. Cover create/begin/callback/save/commit/cancellation/rollback/disposal failures and preserve initiating exceptions. Distinguish known commit success from unknown outcome. Overlapping runner calls use fresh contexts; a later call after failure works. Explicit execution-strategy composition must prove fresh resources/entities per attempt before retry-enabled use is advertised. |
| 5 — P1: Migration and consumer proof (after 2–4) | Port sample to actual Interactive Server without write-on-prerender initialization; compile/run the setup, CRUD including ID delete, custom LINQ, native single-save, and two-provider examples. Use one runtime/tooling configuration, no hard-coded secrets or MARS suppression. Verify build/test and model creation on Windows/Linux; document macOS CLI requirements. Keep WebAssembly behind a server API and mark it unproven until demonstrated. Publish no package as part of these issues without separate authorization. |

Compatibility adapters, generalized keys, pooling/paging helpers and source
generation remain outside this sequence. They need a separate concrete consumer
case; none blocks the initial v2 contract.

## 7. Value test and decisions for review

V2 offers real value if it fixes context leaks/accidental enlistment, removes the
custom runtime/configuration factory chain, accepts ordinary DI dependencies,
and reduces a custom query from manual ownership logic to one awaited EF callback.
Adding a table remains two small classes when a provider is wanted; the benefit
is safer defaults and less setup, not a claim of fewer classes in every case.

Costs are a major source break, explicit-context overloads, migration of overrides,
and still-limited reflection for opt-in automation. Passing a context can be
forgotten; callback lifetime cannot prevent all misuse of raw EF. Native EF is
already simpler for isolated queries and single-save tracked workflows. If the
runner/helper surface grows beyond that benefit, ship fewer helpers instead.
No CQRS, MediatR, event bus, generic cache, custom query language, or new service
hierarchy is recommended.

### Recommendation settled for API review

Retain entity delete and the optional runner with **four borrowed write
overloads**. Entity delete removes repeated token-aware attach/remove code;
the runner removes repeated multi-save cleanup. Drop the two proposed borrowed
read overloads: native reads on a context are already straightforward. Keep
immediate whole-tracker provider saves and the runner's final save explicit.
Native EF only for shared workflows remains the smaller alternative if reviewers
judge the runner's limited benefit insufficient; do not replace it with a session
framework or save-mode switches. Omitting entity delete would save two signatures
but push its repeated concurrency handling into every consuming provider.

Direct DI, native context/configuration primitives, initial opt-in automation,
metadata single-key CRUD, and no compatibility adapter are also selected
recommendations. No additional feature is implied by a future use case.

### Open technical details for implementation

These do not add methods or change the selected ownership/save contract:

1. **Scalar eligibility tests.** Pin down the minimal EF metadata checks for
   writable CLR scalars, including save behavior, converters, backing fields and
   store-generated properties. Prefer EF entry/property metadata to custom
   reflection rules. Restrict unsupported mappings explicitly instead of adding
   graph traversal, custom mapping annotations, or a general update engine.
   The default must not overwrite keys/generated values or silently ignore an
   unsupported concurrency token. Validate this before implementing generic update.
2. **Cleanup diagnostics and budget.** Choose a small internal rollback timeout
   and how secondary cleanup errors/known commit success are attached to the
   initiating exception, e.g. standard `Exception.Data` versus existing logging.
   Prefer no new dependency or public diagnostics/options/exception hierarchy.
   Test cancellation and multiple failures before finalizing the internal policy.

The complete contract requires human approval in the PR for issue #6. No API is
claimed approved merely by writing it here. The implementation plan proposes
follow-up work only; this document authorizes no issue creation, merge, release,
or deployment.
