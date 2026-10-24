using System.Collections.Concurrent;
using EfCoreNexus.Framework.Context;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EfCoreNexus.TestApp.Data.Tests;

public class ContextOperationsTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task SuccessUsesFreshContextsAndForwardsToken(bool returnsResult)
    {
        var factory = new ObservedFactory();
        using var cancellation = new CancellationTokenSource();
        for (var i = 0; i < 2; i++)
        {
            await InvokeAsync(factory, returnsResult, (db, token) =>
            {
                Assert.That(token, Is.EqualTo(cancellation.Token));
                Assert.That(db.AsyncDisposeCalls, Is.Zero);
                return Task.CompletedTask;
            }, cancellation.Token);
        }

        var contexts = factory.Created.ToArray();
        Assert.That(contexts, Has.Length.EqualTo(2));
        Assert.That(contexts[0], Is.Not.SameAs(contexts[1]));
        Assert.That(factory.CreationToken, Is.EqualTo(cancellation.Token));
        AssertDisposed(contexts);
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task CallbackFailurePreservesExceptionAndDisposes(bool returnsResult, bool synchronousThrow)
    {
        var factory = new ObservedFactory();
        var failure = new InvalidOperationException("callback failed");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync(factory, returnsResult,
            (_, _) => synchronousThrow ? throw failure : FailCallbackAsync(failure)));

        Assert.That(actual, Is.SameAs(failure));
        if (!synchronousThrow) Assert.That(actual!.StackTrace, Does.Contain(nameof(FailCallbackAsync)));
        AssertDisposed(factory.Created);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CancellationDisposesAndRemainsCanceledWhenDisposalAlsoFails(bool returnsResult)
    {
        var cleanupFailure = new InvalidOperationException("dispose failed");
        var factory = new ObservedFactory { DisposalFailure = cleanupFailure };
        using var cancellation = new CancellationTokenSource();
        var failure = new OperationCanceledException(cancellation.Token);
        var task = InvokeAsync(factory, returnsResult, async (_, token) =>
        {
            Assert.That(token, Is.EqualTo(cancellation.Token));
            await Task.Yield();
            cancellation.Cancel();
            throw failure;
        }, cancellation.Token);

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(async () => await task);
        Assert.That(actual, Is.SameAs(failure));
        Assert.That(actual!.CancellationToken, Is.EqualTo(cancellation.Token));
        Assert.That(actual.Data["EfCoreNexus.DisposeAsyncException"], Is.SameAs(cleanupFailure));
        Assert.That(task.IsCanceled, Is.True);
        AssertDisposed(factory.Created);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CancellationDuringAwaitDisposesContext(bool returnsResult)
    {
        var factory = new ObservedFactory();
        using var cancellation = new CancellationTokenSource();
        var entered = Signal();
        var task = InvokeAsync(factory, returnsResult, async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        var failure = await Assert.ThrowsAsync<TaskCanceledException>(async () => await task);
        Assert.That(failure!.CancellationToken, Is.EqualTo(cancellation.Token));
        Assert.That(task.IsCanceled, Is.True);
        AssertDisposed(factory.Created);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DisposalFailureAfterSuccessIsSurfaced(bool returnsResult)
    {
        var failure = new InvalidOperationException("dispose failed");
        var factory = new ObservedFactory { DisposalFailure = failure };

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync(factory, returnsResult,
            (_, _) => Task.CompletedTask));

        Assert.That(actual, Is.SameAs(failure));
        AssertDisposed(factory.Created);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CallbackAndDisposalFailuresPreserveCallbackStackAndAttachCleanupFailure(bool returnsResult)
    {
        var failure = new InvalidOperationException("callback failed");
        var cleanupFailure = new InvalidOperationException("dispose failed");
        var factory = new ObservedFactory { DisposalFailure = cleanupFailure };

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync(factory, returnsResult,
            (_, _) => FailCallbackAsync(failure)));

        Assert.That(actual, Is.SameAs(failure));
        Assert.That(actual!.StackTrace, Does.Contain(nameof(FailCallbackAsync)));
        Assert.That(actual.Data["EfCoreNexus.DisposeAsyncException"], Is.SameAs(cleanupFailure));
        AssertDisposed(factory.Created);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CreationFailureDoesNotInvokeCallbackOrCreateContext(bool returnsResult)
    {
        var failure = new InvalidOperationException("creation failed");
        var factory = new ObservedFactory { CreationFailure = failure };
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync(factory, returnsResult,
            (_, _) => throw new AssertionException("Callback must not run.")));

        Assert.That(actual, Is.SameAs(failure));
        Assert.That(factory.Created, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CanceledCreationDoesNotInvokeCallbackOrCreateContext(bool returnsResult)
    {
        var factory = new ObservedFactory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var task = InvokeAsync(factory, returnsResult,
            (_, _) => throw new AssertionException("Callback must not run."), cancellation.Token);

        var failure = await Assert.ThrowsAsync<OperationCanceledException>(async () => await task);
        Assert.That(failure!.CancellationToken, Is.EqualTo(cancellation.Token));
        Assert.That(factory.CreationToken, Is.EqualTo(cancellation.Token));
        Assert.That(factory.Created, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AwaitsCallbackThenAwaitsAsyncDisposalBeforeCompleting(bool returnsResult)
    {
        var releaseCallback = Signal();
        var releaseDisposal = Signal();
        var entered = Signal();
        var factory = new ObservedFactory { ReleaseDisposal = releaseDisposal };
        var task = InvokeAsync(factory, returnsResult, async (_, _) =>
        {
            entered.SetResult();
            await releaseCallback.Task;
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var db = factory.Created.Single();
            Assert.That(db.AsyncDisposeCalls, Is.Zero);
            Assert.That(task.IsCompleted, Is.False);
            releaseCallback.SetResult();
            await db.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(task.IsCompleted, Is.False);
        }
        finally
        {
            releaseCallback.TrySetResult();
            releaseDisposal.TrySetResult();
            await task;
        }
        AssertDisposed(factory.Created);
    }

    [Test]
    public async Task OverlappingCallsUseDistinctContextsAndConnectionsAndLaterCallWorks()
    {
        await using var keeper = await OpenDatabaseAsync();
        var options = new DbContextOptionsBuilder<ObservedContext>().UseSqlite(keeper.ConnectionString).Options;
        await using (var setup = new ObservedContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Add(new Row { Name = "shared" });
            await setup.SaveChangesAsync();
        }
        var factory = new ObservedFactory(options);
        var firstEntered = Signal();
        var secondEntered = Signal();
        var release = Signal();
        var first = ContextOperations.WithContextAsync(factory, async (db, token) =>
        {
            await db.Database.OpenConnectionAsync(token);
            var rows = await db.Set<Row>().AsNoTracking().ToListAsync(token);
            firstEntered.SetResult();
            await release.Task;
            return rows;
        });
        var second = ContextOperations.WithContextAsync(factory, async (db, token) =>
        {
            await db.Database.OpenConnectionAsync(token);
            Assert.That(await db.Set<Row>().CountAsync(token), Is.EqualTo(1));
            secondEntered.SetResult();
            await release.Task;
        });
        try
        {
            await Task.WhenAll(firstEntered.Task, secondEntered.Task).WaitAsync(TimeSpan.FromSeconds(10));
            var contexts = factory.Created.ToArray();
            Assert.That(contexts, Has.Length.EqualTo(2));
            Assert.That(contexts[0], Is.Not.SameAs(contexts[1]));
            Assert.That(contexts[0].Database.GetDbConnection(), Is.Not.SameAs(contexts[1].Database.GetDbConnection()));
            Assert.That(contexts.All(db => db.AsyncDisposeCalls == 0), Is.True);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first, second);
        }
        Assert.That((await first).Single().Name, Is.EqualTo("shared"));
        await ContextOperations.WithContextAsync(factory, (_, _) => Task.CompletedTask);
        Assert.That(factory.Created, Has.Count.EqualTo(3));
        AssertDisposed(factory.Created);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DoesNotSaveImplicitlyAndCallbackCanSaveExplicitly(bool returnsResult)
    {
        await using var keeper = await OpenDatabaseAsync();
        var options = new DbContextOptionsBuilder<ObservedContext>().UseSqlite(keeper.ConnectionString).Options;
        await using (var setup = new ObservedContext(options)) await setup.Database.EnsureCreatedAsync();
        var factory = new ObservedFactory(options);
        await InvokeAsync(factory, returnsResult, (db, _) =>
        {
            db.Add(new Row { Name = "unsaved" });
            return Task.CompletedTask;
        });
        Assert.That(factory.Created.Single().SaveCalls, Is.Zero);
        await using var verification = new ObservedContext(options);
        Assert.That(await verification.Set<Row>().CountAsync(), Is.Zero);

        await InvokeAsync(factory, returnsResult, async (db, token) =>
        {
            db.Add(new Row { Name = "saved" });
            await db.SaveChangesAsync(token);
        });
        Assert.That(await verification.Set<Row>().Select(row => row.Name).ToListAsync(), Is.EqualTo(new[] { "saved" }));
        Assert.That(factory.Created.Last().SaveCalls, Is.EqualTo(1));
        AssertDisposed(factory.Created);
    }

    [Test]
    public async Task LaterCallAfterCallbackAndDisposalFailureGetsFreshContext()
    {
        var factory = new ObservedFactory { DisposalFailure = new InvalidOperationException("dispose failed") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ContextOperations.WithContextAsync(factory,
            (_, _) => FailCallbackAsync(new InvalidOperationException("callback failed"))));
        factory.DisposalFailure = null;

        var result = await ContextOperations.WithContextAsync(factory, (_, _) => Task.FromResult(42));
        Assert.That(result, Is.EqualTo(42));
        Assert.That(factory.Created, Has.Count.EqualTo(2));
        Assert.That(factory.Created.First(), Is.Not.SameAs(factory.Created.Last()));
        AssertDisposed(factory.Created);
    }

    private static Task InvokeAsync(ObservedFactory factory, bool returnsResult,
        Func<ObservedContext, CancellationToken, Task> operation, CancellationToken ct = default)
        => returnsResult
            ? ContextOperations.WithContextAsync(factory, async (db, token) =>
            {
                await operation(db, token);
                return 42;
            }, ct)
            : ContextOperations.WithContextAsync(factory, operation, ct);

    private static async Task FailCallbackAsync(Exception failure)
    {
        await Task.Yield();
        throw failure;
    }

    private static void AssertDisposed(IEnumerable<ObservedContext> contexts)
    {
        var observed = contexts.ToArray();
        Assert.That(observed, Is.Not.Empty);
        foreach (var db in observed)
        {
            Assert.That(db.AsyncDisposeCalls, Is.EqualTo(1));
            Assert.That(db.SyncDisposeCalls, Is.Zero);
            Assert.Throws<ObjectDisposedException>(() => db.Set<Row>().ToList());
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<SqliteConnection> OpenDatabaseAsync()
    {
        var connection = new SqliteConnection($"Data Source=ownership-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False");
        await connection.OpenAsync();
        return connection;
    }

    private sealed class ObservedFactory(DbContextOptions<ObservedContext>? options = null) : IDbContextFactory<ObservedContext>
    {
        public ConcurrentQueue<ObservedContext> Created { get; } = new();
        public CancellationToken CreationToken { get; private set; }
        public Exception? CreationFailure { get; init; }
        public Exception? DisposalFailure { get; set; }
        public TaskCompletionSource? ReleaseDisposal { get; init; }
        public ObservedContext CreateDbContext() => throw new AssertionException("Use async factory creation.");

        public async Task<ObservedContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            CreationToken = cancellationToken;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (CreationFailure is not null) throw CreationFailure;
            var db = new ObservedContext(options ?? new DbContextOptionsBuilder<ObservedContext>()
                .UseSqlite("Data Source=:memory:").Options)
            {
                DisposalFailure = DisposalFailure,
                ReleaseDisposal = ReleaseDisposal
            };
            Created.Enqueue(db);
            return db;
        }
    }

    private sealed class ObservedContext(DbContextOptions<ObservedContext> options) : DbContext(options)
    {
        public int AsyncDisposeCalls { get; private set; }
        public int SyncDisposeCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public Exception? DisposalFailure { get; init; }
        public TaskCompletionSource? ReleaseDisposal { get; init; }
        public TaskCompletionSource DisposalStarted { get; } = Signal();

        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Row>().HasKey(row => row.Id);

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            return base.SaveChangesAsync(cancellationToken);
        }

        public override void Dispose()
        {
            SyncDisposeCalls++;
            base.Dispose();
        }

        public override async ValueTask DisposeAsync()
        {
            AsyncDisposeCalls++;
            DisposalStarted.TrySetResult();
            if (ReleaseDisposal is not null) await ReleaseDisposal.Task;
            await base.DisposeAsync();
            if (DisposalFailure is not null) throw DisposalFailure;
        }
    }

    private sealed class Row
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }
}
