using Microsoft.EntityFrameworkCore;

namespace EfCoreNexus.Framework.Context;

// Internal until ProviderBase moves from TransactionService to the native factory.
internal static class ContextOperations
{
    internal static Task WithContextAsync<TContext>(
        IDbContextFactory<TContext> factory,
        Func<TContext, CancellationToken, Task> operation,
        CancellationToken ct = default) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(operation);
        return WithContextAsync(factory, async (db, token) =>
        {
            await operation(db, token).ConfigureAwait(false);
            return true;
        }, ct);
    }

    /// <summary>
    /// Creates, awaits and asynchronously disposes one context per call. Does not save.
    /// Materialize results inside the callback; do not return deferred queries, streams,
    /// or other values that need the live context. Do not let background work escape.
    /// </summary>
    internal static async Task<TResult> WithContextAsync<TContext, TResult>(
        IDbContextFactory<TContext> factory,
        Func<TContext, CancellationToken, Task<TResult>> operation,
        CancellationToken ct = default) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(operation);

        var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        Exception? operationFailure = null;
        try
        {
            return await operation(db, ct).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            operationFailure = failure;
            throw;
        }
        finally
        {
            try
            {
                await db.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupFailure) when (operationFailure is not null)
            {
                // Keep the initiating exception and its stack, including cancellation.
                operationFailure.Data["EfCoreNexus.DisposeAsyncException"] = cleanupFailure;
            }
        }
    }
}
