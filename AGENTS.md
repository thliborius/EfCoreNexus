# Agent instructions – EfCoreNexus

Read [docs/design-principles.md](docs/design-principles.md) before proposing architecture changes or implementing features.

## Mission and scope

- EfCoreNexus is a **small, optional helper** for EF Core in Blazor: **"Less boilerplate. More Entity Framework."**
- Improve developer ergonomics without reimplementing EF Core or enforcing an application architecture.
- Keep native EF Core APIs usable. Add abstractions only where they measurably remove recurring boilerplate or make common usage safer.
- Prefer existing .NET / EF Core facilities and straightforward code over custom infrastructure, hidden state, or unnecessary reflection.
- No unrelated framework layers (e.g. CQRS, MediatR, event bus, generic cache) without an explicit, justified requirement.
- Don't increase the public API or dependencies merely because a feature might be useful someday.

## Pragmatic implementation and review

- Implement the smallest clear, complete solution for a **supported, demonstrated use case**. No speculative APIs, helpers, flags, architectural layers, or generalized support for hypothetical EF models.
- Keep cohesive logic together. Extract a helper when it is reused, isolates real complexity, or makes the caller clearer; never force unrelated behavior into an oversized method just to avoid helpers.
- Validate untrusted inputs at the public/API, persistence, or EF mapping boundary that owns them. Inside established typed contracts, do not repeat defensive null/type/property checks without a concrete failure scenario.
- Prefer native EF Core and .NET behavior to custom infrastructure. Do not introduce new dependencies or public surface area just to handle a theoretical corner case.
- Handle exceptions only where the code can meaningfully recover, translate an error, or guarantee cleanup. Do not add catch-and-rethrow boilerplate, suppress errors, or obscure cancellation.
- For rare race conditions, concurrency permutations, or security edge cases: identify the exact failure mode, a realistic affected usage scenario, and the code/maintenance cost **before** adding nontrivial complexity. Obtain user agreement for speculative scope expansions.
- Do not ignore reproducible defects in supported functionality, realistic security issues, data corruption/loss, broken transactions, or incorrect DbContext lifetimes. Fix them within scope with focused regression tests.
- Review for **actionable** correctness, security, and maintainability problems first. Record unrelated or purely hypothetical findings separately; do not silently implement them or turn every observation into a follow-up ticket.
- Keep changes and tests proportional to risk while preserving the existing quality gates. A wider review is not authorization to expand the ticket.

## Development workflow

- `main` is the current 2.x development and release branch (including the 2.0.1 release). Base regular changes on current `main` and target `main` with pull requests.
- Do not revive historical v1/v2 branch workflows or modify older release lines without an explicit request.
- Work only within the requested issue's scope. For architecture issue #1, **document recommendations and alternatives; do not implement them**.
- Do not merge a PR, publish a NuGet package, tag a release, or deploy unless specifically asked.
- Keep PRs small and focused. State breaking changes, assumptions, and relevant trade-offs.
- Do not silently change existing CRUD, transaction, or tracking semantics. Propose and test behavior changes explicitly.

## Ticket completion notifications

- Use the same Telegram completion mechanism as the Maus project: include `<!-- TELEGRAM:TICKET_DONE -->` exactly once, as the final line of every terminal ticket response. The existing automation uses this marker to trigger the notification.
- Emit the marker only when ticket handling has ended. Progress updates, intermediate results, pending questions, and running CI must not trigger a Telegram notification.
- Include the marker even when handling ends with a blocker, cancellation, or partial implementation. It signals the end of handling, not success; state the actual outcome and any remaining work clearly.

## Quality gates

- Current development baseline: .NET 10 / EF Core 10; keep package versions compatible and avoid known vulnerable dependencies.
- Keep workflows platform-neutral where feasible (Linux and Windows). Use the existing SQLite in-memory tests without requiring SQL Server.
- For code changes, run `dotnet restore`, `dotnet build`, and `dotnet test`; check known vulnerabilities with `dotnet list package --vulnerable --include-transitive`. Report what was actually executed.
- Include focused regression tests for behavior changes, especially DbContext lifetime/disposal, transactions, concurrency, and failures.
- Do not suppress warnings or weaken tests merely to make checks pass.
- Prefer concise documentation with before/after consumer examples; explain why each abstraction earns its place.
