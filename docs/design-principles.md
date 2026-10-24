# EfCoreNexus – Design Principles

> **Less boilerplate. More Entity Framework.**

EfCoreNexus is a **small, optional helper for developers using EF Core with Blazor**. Its purpose is to remove recurring setup and CRUD boilerplate, **not** to replace Entity Framework Core or prescribe how an entire application must be structured.

These are guiding constraints for the v2 architecture review (see [issue #1](https://github.com/thliborius/EfCoreNexus/issues/1)), rather than decisions about specific API signatures.

## Principles

1. **Less ceremony, especially for new entities.** Adding an entity and, when useful, a specialized provider should require as little registration and repetitive code as practical. Automation must be predictable and discoverable.
2. **EF Core stays visible.** Keep native LINQ, DbContext/DbSet, change tracking, configurations, migrations, and other EF Core capabilities available. Avoid unnecessary wrappers or another query language.
3. **Optional, not prescriptive.** Developers should be able to use providers for recurring logic or use EF Core directly. Do not require a repository, DataService, or Unit of Work for every application.
4. **Safe defaults.** Design correct DbContext ownership, short lifetimes in Blazor, async disposal, and transaction boundaries. Handle failures, cancellations, and concurrent UI operations safely rather than relying on undocumented conventions.
5. **Small surface area.** Prefer .NET and EF Core primitives over custom plumbing. Each public abstraction must demonstrably reduce work or prevent a common mistake. Do not introduce CQRS, mediator/event buses, generic caching infrastructure, or layers unrelated to the core purpose.
6. **Low surprise and easy debugging.** Avoid hidden state, opaque magic, and reflection that makes behavior hard to reason about. Favor simple, explicit, testable behavior where convenience and predictability conflict.
7. **Prove the benefit.** Use short before/after consumer examples and runnable tests to demonstrate less boilerplate without loss of clarity. Evaluate complexity from the *consumer's* perspective as well as the library maintainer's.

## Scope of the v2 architecture discussion

The concrete API, whether providers are directly injected or retrieved through `DataService`, and the transaction/Unit-of-Work model are **not decided** by this document. Compare alternatives first and record the trade-offs in the forthcoming architecture proposal.

The existing v1 public NuGet package remains unchanged; work on v2 is integrated into the `v2` branch through reviewed pull requests.
