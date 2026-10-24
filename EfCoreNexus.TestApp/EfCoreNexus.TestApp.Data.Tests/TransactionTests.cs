using System.Collections.Concurrent;
using System.Data.Common;
using EfCoreNexus.Framework.Provider;
using EfCoreNexus.Framework.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;

namespace EfCoreNexus.TestApp.Data.Tests;

public class TransactionTests
{
    private SqliteConnection _keeper = null!;
    private Factory _factory = null!;
    private Transactions _transactions = null!;
    private Customers _customers = null!;
    private Orders _orders = null!;
    private TransactionRunner<Shop> _runner = null!;

    [SetUp]
    public async Task SetUp()
    {
        _keeper = new SqliteConnection($"Data Source=transactions-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False;Default Timeout=1");
        await _keeper.OpenAsync();
        _transactions = new Transactions();
        _factory = new Factory(new DbContextOptionsBuilder<Shop>().UseSqlite(_keeper.ConnectionString)
            .AddInterceptors(_transactions).Options);
        _customers = new Customers(_factory);
        _orders = new Orders(_factory);
        _runner = new TransactionRunner<Shop>(_factory);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _keeper.DisposeAsync();
        foreach (var db in _factory.Created)
        {
            Assert.That(db.AsyncDisposals, Is.EqualTo(1));
            Assert.That(db.SyncDisposals, Is.Zero);
        }
    }

    [Test]
    public async Task TwoProvidersCommitGeneratedKeysAndRawChangesAcrossImmediateAndFinalSaves()
    {
        var customer = new Customer { Name = "one" };
        var order = new Order();
        IDbContextTransaction? transaction = null;
        await _runner.ExecuteAsync(async (db, ct) =>
        {
            transaction = db.Database.CurrentTransaction;
            Assert.That(transaction, Is.Not.Null);
            var raw = new Audit { Message = "before provider save" };
            db.Add(raw);
            await _customers.CreateAsync(db, customer, ct);
            Assert.That(customer.Id, Is.GreaterThan(0));
            Assert.That(raw.Id, Is.GreaterThan(0));
            Assert.That(db.Entry(raw).State, Is.EqualTo(EntityState.Unchanged));
            customer.Name = "two";
            await _customers.UpdateAsync(db, customer, ct);
            Assert.That(customer.Version, Is.EqualTo("two"));
            customer.Name = "three";
            await _customers.UpdateAsync(db, customer, ct); // Refreshed tracked original token works again.
            Assert.That(customer.Version, Is.EqualTo("three"));
            order.CustomerId = customer.Id;
            await _orders.CreateAsync(db, order, ct);
            Assert.That(order.Id, Is.GreaterThan(0));
            db.Add(new Audit { Message = "final runner save" });
            Assert.That(db.SaveCalls, Is.EqualTo(4));
            Assert.That(db.AsyncDisposals, Is.Zero);
            Assert.That(db.Database.CurrentTransaction, Is.SameAs(transaction));
        });
        var owned = _factory.Created.Last();
        Assert.That(owned.SaveCalls, Is.EqualTo(5));
        Assert.Throws<ObjectDisposedException>(() => owned.Set<Customer>().ToList());
        Assert.That(transaction!.GetDbTransaction().Connection, Is.Null, "The native transaction is disposed.");
        Assert.That((await _customers.GetByIdAsync(customer.Id))!.Name, Is.EqualTo("three"));
        Assert.That((await _orders.GetByIdAsync(order.Id))!.CustomerId, Is.EqualTo(customer.Id));
        await using var check = await _factory.CreateDbContextAsync();
        Assert.That(await check.Set<Audit>().CountAsync(), Is.EqualTo(2));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CallbackOrProviderFailureRollsBackBothProvidersAndNextCallSucceeds(bool providerFailure)
    {
        var failure = new InvalidOperationException("callback");
        var customer = new Customer { Name = "discard this instance after rollback" };
        async Task Operation(Shop db, CancellationToken ct)
        {
            await _customers.CreateAsync(db, customer, ct);
            await _orders.CreateAsync(db, new Order { CustomerId = customer.Id }, ct);
            db.Add(new Audit { Message = "also roll back" });
            await db.SaveChangesAsync(ct);
            if (providerFailure)
                await _orders.CreateAsync(db, new Order { CustomerId = int.MaxValue }, ct); // Actual FK failure.
            throw failure;
        }
        if (providerFailure) await Assert.ThrowsAsync<DbUpdateException>(() => _runner.ExecuteAsync(Operation));
        else Assert.That(await Assert.ThrowsAsync<InvalidOperationException>(() => _runner.ExecuteAsync(Operation)), Is.SameAs(failure));
        Assert.That(customer.Id, Is.GreaterThan(0), "Rollback does not revert in-memory generated keys.");
        Assert.That(await _customers.GetAllAsync(), Is.Empty);
        Assert.That(await _orders.GetAllAsync(), Is.Empty);
        await using (var check = await _factory.CreateDbContextAsync())
            Assert.That(await check.Set<Audit>().CountAsync(), Is.Zero);
        await _runner.ExecuteAsync((db, ct) => _customers.CreateAsync(db, new Customer { Name = "fresh" }, ct));
        Assert.That((await _customers.GetAllAsync()).Single().Name, Is.EqualTo("fresh"));
    }

    [Test]
    public async Task FinalSaveConstraintFailureRollsBackEarlierProviderSaves()
    {
        await Assert.ThrowsAsync<DbUpdateException>(() => _runner.ExecuteAsync(async (db, ct) =>
        {
            await _customers.CreateAsync(db, new Customer { Name = "first save" }, ct);
            db.Add(new Order { CustomerId = int.MaxValue });
        }));
        Assert.That(await _customers.GetAllAsync(), Is.Empty);
        Assert.That(await _orders.GetAllAsync(), Is.Empty);
    }

    [Test]
    public async Task CanceledCallbackRollsBackRealWritesUsingIndependentCleanupToken()
    {
        using var request = new CancellationTokenSource();
        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(() => _runner.ExecuteAsync(async (db, ct) =>
        {
            var customer = new Customer { Name = "canceled workflow" };
            await _customers.CreateAsync(db, customer, ct);
            await _orders.CreateAsync(db, new Order { CustomerId = customer.Id }, ct);
            request.Cancel();
            ct.ThrowIfCancellationRequested();
        }, request.Token));
        Assert.That(thrown!.CancellationToken, Is.EqualTo(request.Token));
        Assert.That(thrown.Data.Contains("EfCoreNexus.RollbackAsyncException"), Is.False);
        Assert.That(await _customers.GetAllAsync(), Is.Empty);
        Assert.That(await _orders.GetAllAsync(), Is.Empty);
    }

    [Test]
    public async Task StaleDetachedUpdateRollsBackOtherProvidersEarlierWrite()
    {
        var customer = new Customer { Name = "one" };
        await _customers.CreateAsync(customer);
        var stale = (await _customers.GetByIdAsync(customer.Id))!;
        customer.Name = "external";
        await _customers.UpdateAsync(customer);
        stale.Name = "stale";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => _runner.ExecuteAsync(async (db, ct) =>
        {
            await _orders.CreateAsync(db, new Order { CustomerId = customer.Id }, ct);
            // The provider must reuse this tracked instance while asserting detached expected tokens.
            await db.Set<Customer>().FindAsync([customer.Id], ct);
            await _customers.UpdateAsync(db, stale, ct);
        }));
        Assert.That(await _orders.GetAllAsync(), Is.Empty);
        Assert.That((await _customers.GetByIdAsync(customer.Id))!.Name, Is.EqualTo("external"));
    }

    [Test]
    public async Task BothBorrowedDeleteFormsRollBackWithTheirCallerTransaction()
    {
        var customer = new Customer { Name = "keep" };
        await _customers.CreateAsync(customer);
        var order = new Order { CustomerId = customer.Id };
        await _orders.CreateAsync(order);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _runner.ExecuteAsync(async (db, ct) =>
        {
            await _orders.DeleteAsync(db, order.Id, ct);
            await db.Set<Customer>().FindAsync([customer.Id], ct);
            await _customers.DeleteAsync(db, customer, ct);
            Assert.That(await db.Set<Order>().CountAsync(ct), Is.Zero);
            Assert.That(await db.Set<Customer>().CountAsync(ct), Is.Zero);
            throw new InvalidOperationException("rollback deletes");
        }));
        Assert.That(await _customers.GetByIdAsync(customer.Id), Is.Not.Null);
        Assert.That(await _orders.GetByIdAsync(order.Id), Is.Not.Null);
    }

    [Test]
    public async Task OrdinaryCallsInsideCallbackStayIndependentOfSharedContextAndRollback()
    {
        // SQLite allows only one writer. Defer this transaction's first lock until after the
        // independent write, so the test proves enlistment rather than SQLite lock contention.
        _transactions.DeferredBegin = true;
        var independent = new Customer { Name = "ordinary commit" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => _runner.ExecuteAsync(async (db, ct) =>
        {
            await _customers.CreateAsync(independent, ct);
            var ordinaryContext = _factory.Created.Last();
            Assert.That(ordinaryContext, Is.Not.SameAs(db));
            Assert.That(ordinaryContext.AsyncDisposals, Is.EqualTo(1));
            var read = (await _customers.GetByIdAsync(independent.Id, ct))!;
            Assert.That(read, Is.Not.SameAs(independent));
            Assert.That(db.ChangeTracker.Entries(), Is.Empty);
            await _orders.CreateAsync(db, new Order { CustomerId = independent.Id }, ct);
            throw new InvalidOperationException("only shared writes roll back");
        }));
        Assert.That((await _customers.GetAllAsync()).Single().Name, Is.EqualTo("ordinary commit"));
        Assert.That(await _orders.GetAllAsync(), Is.Empty);
    }

    [Test]
    public async Task OverlappingInvocationsOnOneRunnerOwnDistinctContextsConnectionsAndTransactions()
    {
        // Native deferred SQLite transactions can overlap before any read/write lock is acquired.
        // Writes are sequenced because SQLite is a single-writer database.
        _transactions.DeferredBegin = true;
        var entered = new TaskCompletionSource<Shop>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = _runner.ExecuteAsync(async (db, ct) =>
        {
            entered.SetResult(db);
            await release.Task;
            await _customers.CreateAsync(db, new Customer { Name = "first" }, ct);
        });
        var firstContext = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await _runner.ExecuteAsync(async (db, ct) =>
            {
                Assert.That(db, Is.Not.SameAs(firstContext));
                Assert.That(db.Database.GetDbConnection(), Is.Not.SameAs(firstContext.Database.GetDbConnection()));
                Assert.That(db.Database.CurrentTransaction, Is.Not.SameAs(firstContext.Database.CurrentTransaction));
                await _customers.CreateAsync(db, new Customer { Name = "second" }, ct);
            });
            Assert.That(first.IsCompleted, Is.False);
            Assert.That(firstContext.AsyncDisposals, Is.Zero);
        }
        finally { release.TrySetResult(); await first; }
        Assert.That((await _customers.GetAllAsync()).Select(x => x.Name), Is.EquivalentTo(new[] { "first", "second" }));
    }

    [Test]
    public async Task ExceptionAfterActualDatabaseCommitIsMarkedUnknownAndDoesNotReplayCallback()
    {
        var failure = new InvalidOperationException("lost commit acknowledgement");
        _transactions.AfterCommitFailure = failure;
        var calls = 0;
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => _runner.ExecuteAsync(async (db, ct) =>
        {
            calls++;
            await _customers.CreateAsync(db, new Customer { Name = "actually committed" }, ct);
        }));
        Assert.That(thrown, Is.SameAs(failure));
        Assert.That(thrown!.Data["EfCoreNexus.CommitOutcome"], Is.EqualTo("Unknown"));
        Assert.That(calls, Is.EqualTo(1));
        Assert.That((await _customers.GetAllAsync()).Single().Name, Is.EqualTo("actually committed"));
    }

    [Test]
    public async Task DetachedUpdatePreservesConceptualNullForComparerEqualRequiredForeignKey()
    {
        var customer = new Customer { Name = "principal" };
        await _customers.CreateAsync(customer);
        var order = new Order { CustomerId = customer.Id };
        await _orders.CreateAsync(order);
        await using var db = await _factory.CreateDbContextAsync();
        db.ChangeTracker.DeleteOrphansTiming = CascadeTiming.Never;
        var tracked = (await db.Set<Order>().FindAsync(order.Id))!;
        db.Entry(tracked).Property(nameof(Order.CustomerId)).CurrentValue = null;
        Assert.That(tracked.CustomerId, Is.EqualTo(order.CustomerId));

        // #18: assigning comparer-equal CLR values (e.g. SetValues) clears EF's conceptual null.
        // Keep the pending required-relationship failure instead of silently repairing the tracker.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _orders.UpdateAsync(db, order));
        Assert.That(db.Entry(order).State, Is.EqualTo(EntityState.Detached));
        Assert.That((await _orders.GetByIdAsync(order.Id))!.CustomerId, Is.EqualTo(customer.Id));
    }

    private sealed class Customers(IDbContextFactory<Shop> factory) : ProviderBase<Customer, int, Shop>(factory);
    private sealed class Orders(IDbContextFactory<Shop> factory) : ProviderBase<Order, int, Shop>(factory);

    private sealed class Factory(DbContextOptions<Shop> options) : IDbContextFactory<Shop>
    {
        public ConcurrentQueue<Shop> Created { get; } = new();
        public Shop CreateDbContext() => throw new AssertionException("Use async creation.");
        public Task<Shop> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var db = new Shop(options);
            Created.Enqueue(db);
            return Task.FromResult(db);
        }
    }

    private sealed class Shop(DbContextOptions<Shop> options) : DbContext(options)
    {
        public int AsyncDisposals { get; private set; }
        public int SyncDisposals { get; private set; }
        public int SaveCalls { get; private set; }
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            return base.SaveChangesAsync(cancellationToken);
        }
        public override async ValueTask DisposeAsync() { AsyncDisposals++; await base.DisposeAsync(); }
        public override void Dispose() { SyncDisposals++; base.Dispose(); }
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<Customer>().Property(x => x.Version)
                .HasComputedColumnSql("\"Name\"", stored: true).IsConcurrencyToken();
            model.Entity<Order>().HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId);
            model.Entity<Audit>();
        }
    }

    private sealed class Transactions : DbTransactionInterceptor
    {
        public bool DeferredBegin { get; set; }
        public Exception? AfterCommitFailure { get; set; }
        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(DeferredBegin
                ? InterceptionResult<DbTransaction>.SuppressWithResult(((SqliteConnection)connection).BeginTransaction(deferred: true))
                : result);
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
            => AfterCommitFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }

    private sealed class Customer
    {
        public int Id { get; private set; }
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
    }
    private sealed class Order { public int Id { get; private set; } public int CustomerId { get; set; } }
    private sealed class Audit { public int Id { get; private set; } public string Message { get; set; } = ""; }
}
