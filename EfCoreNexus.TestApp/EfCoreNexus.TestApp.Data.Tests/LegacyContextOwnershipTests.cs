using EfCoreNexus.Framework.Provider;
using EfCoreNexus.Framework.Services;
using EfCoreNexus.TestApp.Data.Context;
using EfCoreNexus.TestApp.Data.Entities;
using EfCoreNexus.TestApp.Data.Provider;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace EfCoreNexus.TestApp.Data.Tests;

// Characterization of inherited defects, not the intended v2 contract.
// Remove/replace these assertions when the old transaction/provider API is removed.
public class LegacyContextOwnershipTests
{
    [Test]
    public async Task F1_PostCommitOperationReusesUndisposedContextAndNextBeginLeaksIt()
    {
        await using var factory = new LegacyFactory();
        var transactions = new TransactionService<MainContext>(factory);
        var provider = new ContextProbe(transactions);
        transactions.BeginTransaction();
        var first = factory.Created.Single();
        var entity = new Test { TestId = Guid.NewGuid() };
        await provider.Create(entity, entity.TestId);
        await transactions.CommitTransaction();
        await transactions.DisposeTransaction();

        Assert.That(await provider.GetById(entity.TestId), Is.SameAs(entity));
        Assert.That(await provider.SelectedContext(), Is.SameAs(first));
        Assert.That(factory.Created, Has.Count.EqualTo(1));
        Assert.That(first.DisposeCalls, Is.Zero);

        transactions.BeginTransaction();
        Assert.That(factory.Created, Has.Count.EqualTo(2));
        Assert.That(transactions.CtxTransaction, Is.Not.SameAs(first));
        Assert.That(first.DisposeCalls, Is.Zero);
        await transactions.DisposeTransaction();
    }

    [Test]
    public async Task F3_BeginFailureLeavesContextSelectedAndUndisposed()
    {
        var failure = new InvalidOperationException("begin failed");
        await using var factory = new LegacyFactory(db => new FaultingDatabase(db, failure));
        var transactions = new TransactionService<MainContext>(factory);

        Assert.That(Assert.Throws<InvalidOperationException>(transactions.BeginTransaction), Is.SameAs(failure));
        await transactions.DisposeTransaction();
        var created = factory.Created.Single();
        Assert.That(await new ContextProbe(transactions).SelectedContext(), Is.SameAs(created));
        Assert.That(created.DisposeCalls, Is.Zero);
    }

    [Test]
    public async Task F3_DisposalFailureKeepsTransactionAndContextSelected()
    {
        var transaction = new FaultingTransaction();
        await using var factory = new LegacyFactory(db => new FaultingDatabase(db, transaction));
        var transactions = new TransactionService<MainContext>(factory);
        transactions.BeginTransaction();

        Assert.That(await Assert.ThrowsAsync<InvalidOperationException>(transactions.DisposeTransaction),
            Is.SameAs(transaction.Failure));
        Assert.That(Assert.Throws<Exception>(transactions.BeginTransaction)!.Message,
            Is.EqualTo("Transaction open, has to be closed before starting a new one."));
        Assert.That(transactions.CtxTransaction, Is.SameAs(factory.Created.Single()));
        Assert.That(factory.Created.Single().DisposeCalls, Is.Zero);
    }

    [Test]
    public async Task F4_RejectedNestedSampleBeginDisposesOuterTransactionAndRollsBackItsWrite()
    {
        await using var factory = new LegacyFactory();
        var transactions = new TransactionService<MainContext>(factory);
        var provider = new TestProvider(transactions);
        transactions.BeginTransaction();
        var outer = factory.Created.Single();
        var entity = new Test { TestId = Guid.NewGuid() };
        await provider.Create(entity, entity.TestId);

        Assert.That((await Assert.ThrowsAsync<Exception>(() => provider.DeactivateAndCreate(
            new Test { TestId = Guid.NewGuid() })))!.Message,
            Is.EqualTo("Transaction open, has to be closed before starting a new one."));
        Assert.That(outer.Database.CurrentTransaction, Is.Null);
        Assert.That((await Assert.ThrowsAsync<Exception>(transactions.CommitTransaction))!.Message,
            Is.EqualTo("No transaction found, start it first."));
        await using var verification = new MainContext(factory.Options, []);
        Assert.That(await verification.Set<Test>().CountAsync(), Is.Zero);
        Assert.That(outer.DisposeCalls, Is.Zero);
    }

    private sealed class ContextProbe(TransactionService<MainContext> transactions)
        : ProviderBase<Test, Guid, MainContext>(transactions)
    {
        public Task<DbContext> SelectedContext() => GetContextAsync();
    }

    private sealed class LegacyFactory : IDbContextFactory<MainContext>, IAsyncDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly Func<DbContext, DatabaseFacade>? _database;
        public DbContextOptions<MainContext> Options { get; }
        public List<ObservedMainContext> Created { get; } = [];

        public LegacyFactory(Func<DbContext, DatabaseFacade>? database = null)
        {
            _database = database;
            _connection.Open();
            Options = new DbContextOptionsBuilder<MainContext>().UseSqlite(_connection).Options;
            using var setup = new MainContext(Options, []);
            setup.Database.EnsureCreated();
        }

        public MainContext CreateDbContext()
        {
            var db = new ObservedMainContext(Options, _database);
            Created.Add(db);
            return db;
        }

        public async ValueTask DisposeAsync()
        {
            // Tests explicitly clean up the resources the old service leaked.
            foreach (var db in Created) await db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class ObservedMainContext : MainContext
    {
        private readonly DatabaseFacade? _database;
        public int DisposeCalls { get; private set; }
        public override DatabaseFacade Database => _database ?? base.Database;

        public ObservedMainContext(DbContextOptions<MainContext> options,
            Func<DbContext, DatabaseFacade>? database) : base(options, [])
            => _database = database?.Invoke(this);

        public override void Dispose()
        {
            DisposeCalls++;
            base.Dispose();
        }

        public override async ValueTask DisposeAsync()
        {
            DisposeCalls++;
            await base.DisposeAsync();
        }
    }

    private sealed class FaultingDatabase : DatabaseFacade
    {
        private readonly Exception? _beginFailure;
        private readonly IDbContextTransaction? _transaction;
        public FaultingDatabase(DbContext db, Exception failure) : base(db) => _beginFailure = failure;
        public FaultingDatabase(DbContext db, IDbContextTransaction transaction) : base(db) => _transaction = transaction;
        public override IDbContextTransaction BeginTransaction() => _beginFailure is not null
            ? throw _beginFailure : _transaction!;
    }

    private sealed class FaultingTransaction : IDbContextTransaction
    {
        public InvalidOperationException Failure { get; } = new("transaction disposal failed");
        public Guid TransactionId { get; } = Guid.NewGuid();
        public void Commit() => throw new NotSupportedException();
        public Task CommitAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Rollback() => throw new NotSupportedException();
        public Task RollbackAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.FromException(Failure);
    }
}
