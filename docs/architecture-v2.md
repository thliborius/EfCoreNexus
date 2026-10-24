# EfCoreNexus 2.0: architecture and migration proposal

**Status: recommendations for review, not approved API decisions.** Addresses
[issue #1](https://github.com/thliborius/EfCoreNexus/issues/1) under the
[design principles](design-principles.md). This document proposes changes; it
implements none and does not close the issue.

Review baseline: `origin/v2` at
[`64a63ba`](https://github.com/thliborius/EfCoreNexus/tree/64a63ba780e57259d45415f8ee5de4ea7359c06e),
including the .NET 10 migration. “v1 behavior” below means the inherited API on
that branch, not a claim that the published v1.0.5 package targets .NET 10.
All framework classes, sample source/components, project files, and three test
fixtures were inspected. Code links refer to this baseline.

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
| Retain one extensible `ProviderBase<TEntity,TId,TContext>` | Shared CRUD and safe operation ownership earn their place. Keep custom providers for specialized queries/business operations; direct EF remains equally valid. Retain `IProviderCrud` only as an injectable/test boundary if consumers need it; no mandatory interface per provider. |
| Use `AddDbContextFactory<TContext>` and `IDbContextFactory<TContext>` | Already native EF APIs; no custom runtime factory or options container. Factory-created contexts remain caller-owned. |
| Remove `ProviderFactory` / `IProviderFactory`, `StartupConfiguration`, and `DataAssemblyConfiguration` | One optional registration extension scans explicit `Assembly` objects and adds ordinary DI descriptors. DI constructs/disposes providers and resolves additional dependencies. Base-type discovery also makes the empty `IProvider` marker unnecessary. |
| Prefer ordinary `DbContext` over mandatory `BaseContext` | Replace configuration inheritance with `IEntityTypeConfiguration<T>` and `ApplyConfigurationsFromAssembly`. Keep a narrowly scoped entity-discovery extension only for the automation EF does not supply. |
| Remove stateful `TransactionService` and default `DataService` / `IDataService` | An optional stateless `TransactionRunner<TContext>` owns resources per callback. No ambient circuit transaction and no provider cache. A lookup facade is a compatibility option only if migration demand justifies it. |
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

Automation should be limited and opt-in:

- `AddNexusProviders<TContext>(assemblies)` scans once at registration for concrete,
  closed providers derived, directly or indirectly, from the base for **that exact
  context**. Use ordinary scoped DI descriptors, avoid eager activation, make
  repeated registration idempotent, and reject ambiguous default CRUD mappings.
  Explicit `AddScoped<CustomerProvider>()` remains supported. Do not add a scanning
  dependency such as Scrutor just for this operation.
- `RegisterNexusEntities(assemblies)` registers concrete, closed `IEntity` root
  classes without requiring a parameterless constructor. Retain this marker as
  an **optional opt-in to discovery**, not a base-provider constraint. Exclude
  `[NotMapped]` and owned/complex types; configure those through native EF mapping.
  Contexts sharing an assembly need explicit subsets/assemblies rather than
  silently receiving every root. Do not infer entities solely from providers.
- Apply native configurations after discovery, then explicit context overrides.
  Use explicit assemblies/predicates for multiple models. Configuration scanning
  has no defined ordering and uses parameterless constructors; configurations
  needing dependencies/order should use explicit `ApplyConfiguration` instead.
  Configurations need no separate DI registration. See
  [EF model configuration](https://learn.microsoft.com/en-us/ef/core/modeling/).

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
| A: `DataService.GetProvider<CustomerProvider>()` | One injected gateway, familiar to v1 users | Hides dependencies and delays missing-provider errors; mocking a gateway is unnecessary plumbing. Prefer removal. If retained temporarily, make it a DI-only lookup adapter with no transaction state or ownership. |
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
```

### Proposed setup and adding an entity

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

// Enough for standard CRUD; add specialized methods only when needed.
public class CustomerProvider(IDbContextFactory<MainContext> factory)
    : ProviderBase<Customer, Guid, MainContext>(factory);

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
await Customers.CreateAsync(new Customer { Name = "Ada", Active = true }, ct);
var customers = await Customers.GetAllAsync(ct);
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

This replaces the sample's manual try/finally. Never return an `IQueryable`, lazy
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
| `Task DeleteAsync(TEntity entity, ct)` | Derive the key and expected concurrency tokens from the entity. Missing row at lookup: no-op, preserving v1's missing-delete behavior. A race after lookup or a stale token can raise `DbUpdateConcurrencyException`. |

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
store-generated values into writable current values. SQL Server `rowversion` can
be preserved this way; application-managed token regeneration or shadow tokens
need an explicit specialized operation with original/new values. A stale-token
test must prove the contract. Applications without tokens still accept ordinary
last-writer behavior. Propagate `DbUpdateConcurrencyException`; the UI/application
chooses reload, merge, or retry. See
[EF optimistic concurrency](https://learn.microsoft.com/en-us/ef/core/saving/concurrency).

Do not hide authorization or aggregate rules in generic CRUD; specialized methods
should select allowed fields. Remove the sync CRUD surface; await EF async I/O
and forward cancellation to creation, queries, saves, and transaction operations.
Avoid `Task.Run` / `Task.FromResult` around sync database calls. Providers may
honor cancellation differently; cancellation does not establish commit outcome.
See [EF async guidance](https://learn.microsoft.com/en-us/ef/core/miscellaneous/async).

## 4. Transaction ownership and Blazor concurrency

### Recommended model

Use normal per-operation saves for ordinary CRUD. For a business operation whose
changes can be saved once, use one factory-created context and one
`SaveChangesAsync`; EF already supplies the database transaction. An optional
runner is justified for workflows spanning several saves/providers: it centralizes
begin/commit/rollback/disposal without storing current state in DI.

```csharp
// Customers, Orders, and Transactions are directly injected services.
await Transactions.ExecuteAsync(async (db, token) =>
{
    var customer = new Customer { Name = "Ada" };
    await Customers.CreateAsync(db, customer, token);
    await Orders.CreateAsync(db, new Order { CustomerId = customer.Id }, token);
}, ct);
```

Recommend **explicit-context overloads** of the same CRUD methods, with `TContext`
as the first argument. They borrow the context: they never create, dispose,
commit, or roll it back. Writes save immediately, as the ordinary methods do,
but those saves are enclosed by the runner's transaction. They can flush any
other pending changes on that same tracker. Specialized business methods need
the same explicit-context path when they participate in a shared operation;
ordinary overloads can delegate to it through `WithContextAsync`.

The runner's proposed core signature is
`Task ExecuteAsync(Func<TContext, CancellationToken, Task> operation, CancellationToken ct = default)`.
It performs a final `SaveChangesAsync` for any remaining direct EF changes before
committing. This deliberately differs from v1's commit-only method. Calling an
ordinary provider overload in the callback creates an independent operation;
it does **not** join the transaction. This explicit-context requirement is the
main usability cost and must be visible in XML documentation and examples.

For borrowed-context reads, list reads remain no-tracking; `GetByIdAsync(db,...)`
uses native `FindAsync` and may return a tracked instance, including local changes.
The borrower must not detach it. No promise of database-fresh reads is made.

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

Recommend an initial **single-attempt runner**, without hidden delegate replay.
When a retrying EF execution strategy is configured, the application must wrap
the **whole runner invocation** in `Database.CreateExecutionStrategy().ExecuteAsync(...)`,
creating a fresh runner-owned context/transaction on every attempt. Calling the
runner directly under such a strategy is unsupported and EF may reject the user
transaction. Keep callbacks replay-safe and construct mutable entities per attempt;
external HTTP/email/payment effects must not be replayed as database work.
Commit ambiguity still needs application-specific verification/idempotency.
Automatic strategy integration can be reviewed later if the explicit composition
is too cumbersome. See [EF execution strategies](https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency#execution-strategies-and-transactions).

### Alternatives considered

| Alternative | Assessment |
| --- | --- |
| Keep ambient scoped `TransactionService`, add reset/dispose | Repairs F1/F3 but leaves accidental enlistment and overlapping components. Locking an entire circuit would serialize unrelated operations and still hide ownership. Not recommended. |
| Explicit disposable UnitOfWork with `GetProvider<T>()` | Clear lifetime, but needs a provider rebinding/factory/resolver API and explicit commit/disposal ceremony. Consider only if passing contexts proves harder for real consumers. |
| Callback plus explicit `TContext` overloads | Reuses typed DI providers and EF, makes shared ownership visible, and centralizes cleanup. Recommended despite extra overloads and the need to pass the context. |
| Native factory/context/transaction only | Fewest library concepts and full control. Always supported; for one save, usually preferable. The runner should survive only if it removes recurring failure-handling code. |

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
| `DataService.GetProvider<T>()` / `GetProviderCrud` | Inject typed provider or optional CRUD interface; remove gateway/provider factory. A temporary lookup-only adapter needs explicit review. |
| Provider constructor takes `TransactionService` | Take `IDbContextFactory<TContext>` plus normal DI dependencies. Keep the existing three generic parameters initially. |
| `Create(item,id)` / `Update(item,id)` / `Delete(id)` | `CreateAsync(item,ct)` / `UpdateAsync(item,ct)` / `DeleteAsync(item,ct)`; preserve original concurrency values. For deletion, keep/load the entity or expose a deliberate specialized ID-based operation. |
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

These are proposed small work items for later discussion, **not newly created
issues**. Priority indicates risk, not permission to implement before review.
Items can be reviewed separately; dependencies are explicit.

| Priority / item | Verifiable acceptance criteria |
| --- | --- |
| P0 — Lock down ownership regressions | Add tests reproducing F1/F3/F4 on the inherited implementation: post-commit ordinary call, begin failure, rollback/disposal failure, and rejected nested sample begin. Track created/disposed contexts and transactions with counters/fakes; show which tests initially fail. Independent of public API choice. |
| P0 — Implement transaction ownership contract | After model/API review, implement the optional runner. Verify callback/final-save/commit/cancellation failures, rollback, transaction/context disposal despite secondary failures, exception preservation, and known-success versus unknown commit outcome diagnostics. Two providers commit/roll back together; a later call uses a fresh context. No resource remains after callback completion. Depends on agreed transaction decision. |
| P0 — Prove operation isolation | Use controlled async barriers and distinct connections to a shared SQLite database: two operations on the same scoped services get different contexts; an unrelated ordinary call is not enlisted in the other's rollback; no shared-context parallel work in examples. Add overlapping runner invocations and a fresh-call-after-failure case. Depends on the ownership implementation. |
| P1 — Simplify model and DI registration | Replace runtime/configuration factories with native APIs and bounded opt-in scanning. Resolve full graphs with scope/build validation. Tests cover additional constructor dependencies, disposal, indirect provider inheritance, idempotent registration, two contexts in one assembly, exclusions, marker entities without configurations, and native configurations without markers. Runtime/design-time models match; no per-entity startup edit. |
| P1 — Define and implement CRUD/key semantics | Agree signatures before coding. Test genuinely async/cancelable reads, convention/Fluent/Guid/int/string keys, generated keys, invalid mappings and ID types, duplicate insert races, missing reads/updates/deletes, and scalar-only updates. Reject unsupported composite/shadow generic CRUD clearly; demonstrate native composite lookup. Remove Dynamic LINQ only once no framework use remains. |
| P1 — Preserve optimistic concurrency | Two detached editors with a configured token: stale update and stale delete throw `DbUpdateConcurrencyException`; unrelated columns are not forced modified. SQLite tests use application-managed tokens with an explicit original/new-value operation. Add focused SQL Server integration coverage for `rowversion`, generated values, and transaction failure semantics before claiming provider parity. Depends on CRUD contract; no silent client-wins retry. |
| P1 — Publish migration/sample proof | Port the sample to actual Interactive Server with no write-on-prerender initialization. Show direct DI, standard CRUD, a custom LINQ query, and two-provider transaction. Verify .NET CLI build/test and design-time model creation on Windows/Linux; document/test macOS CLI with a reachable DB. Use one configuration contract, no secret or MARS-warning suppression. Publish a minimal WebAssembly/API example or clearly mark it unsupported until demonstrated. Depends on chosen APIs. |
| P2 — Verify retry composition | Under a configured execution strategy, explicitly wrap the whole runner. Inject a transient failure and verify fresh context/transaction/entities per attempt and complete disposal. Document non-replayable side effects and unknown commit outcome. Must pass before advertising retry-enabled transaction support. |
| P2 — Decide compatibility and convenience additions | Only add a DataService adapter, generalized keys, pooling, paging helpers, or source generation if a concrete consumer scenario beats native EF on code/clarity and has measurable acceptance tests. None blocks a minimal v2. |

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

Review should settle these questions before implementation:

1. Prefer direct DI and removal of DataService, or fund a temporary lookup adapter?
2. Accept callback + borrowed-context overloads, immediate provider saves plus a
   final runner save, single-attempt behavior, and explicit execution-strategy
   composition? Alternatively choose native EF only or a proven simpler session.
3. Accept entity-based update/delete, standard exception/tracking contracts, and
   single CLR-key generic CRUD with native escape hatches?
4. Retain optional marker entity/provider scanning with explicit assembly/model
   boundaries, or prefer entirely explicit EF/DI registration?

Approval of this proposal should lead to small reviewed implementation PRs on
`v2`; it is not approval to change production code, publish, or merge here.
