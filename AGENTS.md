# Agent instructions – EfCoreNexus

Read [docs/design-principles.md](docs/design-principles.md) before proposing architecture changes or implementing features.

## Mission and scope

- EfCoreNexus is a **small, optional helper** for EF Core in Blazor: **"Less boilerplate. More Entity Framework."**
- Improve developer ergonomics without reimplementing EF Core or enforcing an application architecture.
- Keep native EF Core APIs usable. Add abstractions only where they measurably remove recurring boilerplate or make common usage safer.
- Prefer existing .NET / EF Core facilities and straightforward code over custom infrastructure, hidden state, or unnecessary reflection.
- No unrelated framework layers (e.g. CQRS, MediatR, event bus, generic cache) without an explicit, justified requirement.
- Don't increase the public API or dependencies merely because a feature might be useful someday.

## Development workflow

- `main` is the stable v1 branch; **do not modify it for v2 work**.
- Base v2 feature branches on current `origin/v2`, and target `v2` with pull requests.
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
