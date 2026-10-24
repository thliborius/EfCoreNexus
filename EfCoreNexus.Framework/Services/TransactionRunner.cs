using EfCoreNexus.Framework.Context;
using Microsoft.EntityFrameworkCore;

namespace EfCoreNexus.Framework.Services;

/// <summary>Optional, single-attempt wrapper for native EF transactions spanning several saves.</summary>
public sealed class TransactionRunner<TContext> where TContext : DbContext
{
    private readonly IDbContextFactory<TContext> _factory;

    public TransactionRunner(IDbContextFactory<TContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <summary>
    /// Owns a fresh context and transaction, awaits the callback, saves remaining changes and commits.
    /// Pass this exact context to participating provider writes; ordinary calls remain independent.
    /// Await all work inside the callback and never use its context concurrently. Let failures unwind.
    /// Rollback uses an independent five-second token; cleanup errors never replace the initiating error.
    /// Commit failures may have unknown outcome. Cleanup failures after commit are marked as committed
    /// in Exception.Data. No automatic retries or retrying execution-strategy integration are provided.
    /// </summary>
    public async Task ExecuteAsync(Func<TContext, CancellationToken, Task> operation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var commitAttempted = false;
        var committed = false;
        try
        {
            await ContextOperations.WithContextAsync(_factory, async (db, token) =>
            {
                var transaction = await db.Database.BeginTransactionAsync(token).ConfigureAwait(false);
                Exception? operationFailure = null;
                try
                {
                    await operation(db, token).ConfigureAwait(false);
                    await db.SaveChangesAsync(token).ConfigureAwait(false);
                    commitAttempted = true;
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    committed = true;
                }
                catch (Exception failure)
                {
                    operationFailure = failure;
                    try
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await transaction.RollbackAsync(cleanup.Token).ConfigureAwait(false);
                    }
                    catch (Exception rollbackFailure)
                    {
                        failure.Data["EfCoreNexus.RollbackAsyncException"] = rollbackFailure;
                    }
                    throw;
                }
                finally
                {
                    try
                    {
                        await transaction.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception cleanupFailure) when (operationFailure is not null)
                    {
                        operationFailure.Data["EfCoreNexus.TransactionDisposeAsyncException"] = cleanupFailure;
                    }
                }
            }, ct).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            // A failed commit, even followed by a successful rollback attempt, is not proof of rollback.
            if (commitAttempted)
                failure.Data["EfCoreNexus.CommitOutcome"] = committed ? "Committed" : "Unknown";
            throw;
        }
    }
}
