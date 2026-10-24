using EfCoreNexus.Framework.Context;
using Microsoft.EntityFrameworkCore;

namespace EfCoreNexus.Framework.Provider;

/// <summary>A provider using a fresh, asynchronously disposed context for each operation.</summary>
public abstract class ProviderBase<TEntity, TId, TContext>
    where TEntity : class
    where TId : notnull
    where TContext : DbContext
{
    private readonly IDbContextFactory<TContext> _factory;

    protected ProviderBase(IDbContextFactory<TContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <summary>
    /// Awaits the operation and async context disposal, including on failure/cancellation.
    /// Does not save or enlist in a shared transaction. Await all work inside the callback.
    /// </summary>
    protected Task WithContextAsync(Func<TContext, CancellationToken, Task> operation,
        CancellationToken ct = default)
        => ContextOperations.WithContextAsync(_factory, operation, ct);

    /// <summary>
    /// Awaits the operation and async context disposal. Does not save implicitly.
    /// Materialize results inside the callback; do not return deferred queries, streams,
    /// lazy-loading dependencies, or other objects requiring the live context.
    /// </summary>
    protected Task<TResult> WithContextAsync<TResult>(
        Func<TContext, CancellationToken, Task<TResult>> operation,
        CancellationToken ct = default)
        => ContextOperations.WithContextAsync(_factory, operation, ct);
}
