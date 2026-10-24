using EfCoreNexus.Framework.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace EfCoreNexus.TestApp.Data.Tests;

// Small boundary doubles deliberately avoid provider behavior: real transaction behavior lives in TransactionTests.
public class TransactionRunnerFailureTests
{
    [TestCase("create")]
    [TestCase("begin")]
    [TestCase("callback")]
    [TestCase("save")]
    [TestCase("commit")]
    public async Task InitiatingFailureIsPreservedAndEveryAcquiredResourceIsDisposed(string stage)
    {
        var probe = new Probe();
        var failure = new InvalidOperationException(stage);
        probe.Failures[stage] = failure;
        var runner = new TransactionRunner<ObservedContext>(probe);
        using var request = new CancellationTokenSource();
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ExecuteAsync(async (_, ct) =>
        {
            await probe.StepAsync("callback", ct);
        }, request.Token));
        Assert.That(thrown, Is.SameAs(failure));
        Assert.That(thrown!.StackTrace, Does.Contain(nameof(Probe.StepAsync)));
        var expected = stage switch
        {
            "create" => new[] { "create" },
            "begin" => new[] { "create", "begin", "context-dispose" },
            "callback" => new[] { "create", "begin", "callback", "rollback", "transaction-dispose", "context-dispose" },
            "save" => new[] { "create", "begin", "callback", "save", "rollback", "transaction-dispose", "context-dispose" },
            _ => new[] { "create", "begin", "callback", "save", "commit", "rollback", "transaction-dispose", "context-dispose" }
        };
        Assert.That(probe.Events, Is.EqualTo(expected));
        foreach (var name in new[] { "create", "begin", "callback", "save", "commit" }.Intersect(probe.Events))
            Assert.That(probe.Tokens[name], Is.EqualTo(request.Token), name);
        if (stage == "commit") Assert.That(thrown.Data["EfCoreNexus.CommitOutcome"], Is.EqualTo("Unknown"));
        else Assert.That(thrown.Data.Contains("EfCoreNexus.CommitOutcome"), Is.False);
    }

    [Test]
    public async Task SynchronousCallbackFailureAndAllCleanupFailuresRetainOriginalExceptionAndDiagnostics()
    {
        var probe = new Probe();
        var callback = new InvalidOperationException("original");
        var rollback = new InvalidOperationException("rollback");
        var transaction = new InvalidOperationException("transaction-dispose");
        var context = new InvalidOperationException("context-dispose");
        probe.Failures["rollback"] = rollback;
        probe.Failures["transaction-dispose"] = transaction;
        probe.Failures["context-dispose"] = context;
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TransactionRunner<ObservedContext>(probe).ExecuteAsync((_, _) => throw callback));
        Assert.That(thrown, Is.SameAs(callback));
        Assert.That(thrown!.Data["EfCoreNexus.RollbackAsyncException"], Is.SameAs(rollback));
        Assert.That(thrown.Data["EfCoreNexus.TransactionDisposeAsyncException"], Is.SameAs(transaction));
        Assert.That(thrown.Data["EfCoreNexus.DisposeAsyncException"], Is.SameAs(context));
        Assert.That(probe.Events, Is.EqualTo(new[] { "create", "begin", "rollback", "transaction-dispose", "context-dispose" }));
    }

    [TestCase("create")]
    [TestCase("begin")]
    [TestCase("callback")]
    [TestCase("save")]
    [TestCase("commit")]
    public async Task CancellationPreservesOriginalAndRollbackIgnoresCanceledRequest(string stage)
    {
        var probe = new Probe();
        using var request = new CancellationTokenSource();
        var cancellation = new OperationCanceledException(request.Token);
        probe.BeforeStep = (name, _) =>
        {
            if (name == stage) { request.Cancel(); throw cancellation; }
            return Task.CompletedTask;
        };
        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new TransactionRunner<ObservedContext>(probe).ExecuteAsync((_, ct) => probe.StepAsync("callback", ct), request.Token));
        Assert.That(thrown, Is.SameAs(cancellation));
        if (stage is "callback" or "save" or "commit")
        {
            var cleanup = probe.Tokens["rollback"];
            Assert.That(cleanup, Is.Not.EqualTo(request.Token));
            Assert.That(cleanup.CanBeCanceled, Is.True);
            Assert.That(probe.RollbackWasCanceled, Is.False);
            Assert.That(probe.Events.TakeLast(2), Is.EqualTo(new[] { "transaction-dispose", "context-dispose" }));
        }
        else Assert.That(probe.Events, Does.Not.Contain("rollback"));
    }

    [Test]
    public async Task RollbackTokenHasFiniteBudgetAndTimeoutDoesNotPreventDisposal()
    {
        var probe = new Probe();
        var failure = new InvalidOperationException("callback");
        probe.BeforeStep = async (name, ct) =>
        {
            if (name == "rollback") await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        };
        var execution = new TransactionRunner<ObservedContext>(probe).ExecuteAsync((_, _) => throw failure);
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await execution.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.That(thrown, Is.SameAs(failure));
        Assert.That(thrown!.Data["EfCoreNexus.RollbackAsyncException"], Is.InstanceOf<OperationCanceledException>());
        Assert.That(probe.Events.TakeLast(2), Is.EqualTo(new[] { "transaction-dispose", "context-dispose" }));
    }

    [TestCase("transaction-dispose", false)]
    [TestCase("context-dispose", false)]
    [TestCase("transaction-dispose", true)]
    public async Task CleanupFailureAfterSuccessfulCommitIsMarkedCommittedAndNeverRollsBack(string stage, bool both)
    {
        var probe = new Probe();
        var failure = new InvalidOperationException(stage);
        probe.Failures[stage] = failure;
        var secondary = new InvalidOperationException("secondary context disposal");
        if (both) probe.Failures["context-dispose"] = secondary;
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TransactionRunner<ObservedContext>(probe).ExecuteAsync((_, _) => Task.CompletedTask));
        Assert.That(thrown, Is.SameAs(failure));
        Assert.That(thrown!.Data["EfCoreNexus.CommitOutcome"], Is.EqualTo("Committed"));
        if (both) Assert.That(thrown.Data["EfCoreNexus.DisposeAsyncException"], Is.SameAs(secondary));
        Assert.That(probe.Events, Is.EqualTo(new[] { "create", "begin", "save", "commit", "transaction-dispose", "context-dispose" }));
    }

    [Test]
    public async Task BeginFailureAlsoPreservesContextDisposalFailure()
    {
        var probe = new Probe();
        var begin = new InvalidOperationException("begin");
        var disposal = new InvalidOperationException("context-dispose");
        probe.Failures["begin"] = begin;
        probe.Failures["context-dispose"] = disposal;
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TransactionRunner<ObservedContext>(probe).ExecuteAsync((_, _) => Task.CompletedTask));
        Assert.That(thrown, Is.SameAs(begin));
        Assert.That(thrown!.Data["EfCoreNexus.DisposeAsyncException"], Is.SameAs(disposal));
    }

    [Test]
    public async Task SuccessAwaitsCallbackFinalSaveCommitAndBothAsyncDisposalsInOrder()
    {
        var probe = new Probe();
        var callbackEntered = Signal();
        var releaseCallback = Signal();
        var transactionDisposeEntered = Signal();
        var releaseTransactionDispose = Signal();
        var contextDisposeEntered = Signal();
        var releaseContextDispose = Signal();
        probe.BeforeStep = async (name, _) =>
        {
            if (name == "transaction-dispose")
            {
                transactionDisposeEntered.SetResult();
                await releaseTransactionDispose.Task;
            }
            if (name == "context-dispose")
            {
                contextDisposeEntered.SetResult();
                await releaseContextDispose.Task;
            }
        };
        var execution = new TransactionRunner<ObservedContext>(probe).ExecuteAsync(async (_, _) =>
        {
            callbackEntered.SetResult();
            await releaseCallback.Task;
        });
        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(probe.Events, Is.EqualTo(new[] { "create", "begin" }));
            Assert.That(execution.IsCompleted, Is.False);
            releaseCallback.SetResult();
            await transactionDisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(probe.Events, Does.Contain("commit").And.Not.Contain("context-dispose"));
            Assert.That(execution.IsCompleted, Is.False);
            releaseTransactionDispose.SetResult();
            await contextDisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(execution.IsCompleted, Is.False);
        }
        finally
        {
            releaseCallback.TrySetResult();
            releaseTransactionDispose.TrySetResult();
            releaseContextDispose.TrySetResult();
            await execution;
        }
        Assert.That(probe.Events, Is.EqualTo(new[] { "create", "begin", "save", "commit", "transaction-dispose", "context-dispose" }));
    }

    [Test]
    public async Task ArgumentsAreValidatedBeforeAcquiringResources()
    {
        Assert.Throws<ArgumentNullException>(() => new TransactionRunner<ObservedContext>(null!));
        var probe = new Probe();
        await Assert.ThrowsAsync<ArgumentNullException>(() => new TransactionRunner<ObservedContext>(probe).ExecuteAsync(null!));
        Assert.That(probe.Events, Is.Empty);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Probe : IDbContextFactory<ObservedContext>
    {
        public List<string> Events { get; } = [];
        public Dictionary<string, Exception> Failures { get; } = [];
        public Dictionary<string, CancellationToken> Tokens { get; } = [];
        public Func<string, CancellationToken, Task>? BeforeStep { get; set; }
        public bool RollbackWasCanceled { get; private set; }
        public async Task StepAsync(string name, CancellationToken ct = default)
        {
            Events.Add(name);
            Tokens[name] = ct;
            if (name == "rollback") RollbackWasCanceled = ct.IsCancellationRequested;
            await Task.Yield();
            if (BeforeStep is not null) await BeforeStep(name, ct);
            if (Failures.TryGetValue(name, out var failure)) throw failure;
            ct.ThrowIfCancellationRequested();
        }
        public ObservedContext CreateDbContext() => throw new AssertionException("Use async factory creation.");
        public async Task<ObservedContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            await StepAsync("create", cancellationToken);
            return new ObservedContext(this);
        }
    }

    private sealed class ObservedContext : DbContext
    {
        private readonly Probe _probe;
        private readonly DatabaseFacade _database;
        public ObservedContext(Probe probe)
        {
            _probe = probe;
            _database = new ObservedDatabase(this, probe);
        }
        public override DatabaseFacade Database => _database;
        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            await _probe.StepAsync("save", cancellationToken);
            return 0;
        }
        public override void Dispose() => throw new AssertionException("Use asynchronous context disposal.");
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            await _probe.StepAsync("context-dispose");
        }
    }

    private sealed class ObservedDatabase(DbContext db, Probe probe) : DatabaseFacade(db)
    {
        public override IDbContextTransaction BeginTransaction() => throw new AssertionException("Begin asynchronously.");
        public override async Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            await probe.StepAsync("begin", cancellationToken);
            return new ObservedTransaction(probe);
        }
    }

    private sealed class ObservedTransaction(Probe probe) : IDbContextTransaction
    {
        public Guid TransactionId { get; } = Guid.NewGuid();
        public void Commit() => throw new AssertionException("Commit asynchronously.");
        public Task CommitAsync(CancellationToken cancellationToken = default) => probe.StepAsync("commit", cancellationToken);
        public void Rollback() => throw new AssertionException("Roll back asynchronously.");
        public Task RollbackAsync(CancellationToken cancellationToken = default) => probe.StepAsync("rollback", cancellationToken);
        public void Dispose() => throw new AssertionException("Dispose transactions asynchronously.");
        public async ValueTask DisposeAsync() => await probe.StepAsync("transaction-dispose");
    }
}
