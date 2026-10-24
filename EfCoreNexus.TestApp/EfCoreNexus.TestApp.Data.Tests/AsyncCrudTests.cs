using System.Collections.Concurrent;
using System.Data.Common;
using EfCoreNexus.Framework.Provider;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfCoreNexus.TestApp.Data.Tests;

public class AsyncCrudTests
{
    private SqliteConnection _keeper = null!;
    private CrudFactory _factory = null!;
    private CommandObserver _commands = null!;
    private SaveObserver _saves = null!;

    [SetUp]
    public async Task SetUp()
    {
        _keeper = new SqliteConnection($"Data Source=crud-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False");
        await _keeper.OpenAsync();
        _commands = new CommandObserver();
        _saves = new SaveObserver();
        _factory = new CrudFactory(new DbContextOptionsBuilder<CrudContext>()
            .UseSqlite(_keeper.ConnectionString).AddInterceptors(_commands, _saves).Options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _keeper.DisposeAsync();
        foreach (var db in _factory.Created)
        {
            Assert.That(db.AsyncDisposals, Is.EqualTo(1), "Every acquired context must be asynchronously disposed.");
            Assert.That(db.SyncDisposals, Is.Zero);
        }
    }

    [Test]
    public async Task SixMethodInterfaceSupportsGeneratedConventionGuidKeyAndIndependentReads()
    {
        IProviderCrud<Row, Guid> provider = Provider<Row, Guid>();
        var row = new Row { Name = "created", Enabled = true };
        await provider.CreateAsync(row);
        Assert.That(row.Id, Is.Not.EqualTo(Guid.Empty));
        var all = await provider.GetAllAsync();
        var edit = (await provider.GetByIdAsync(row.Id))!;
        Assert.That(all.Single().Name, Is.EqualTo("created"));
        Assert.That(edit, Is.Not.SameAs(row));
        Assert.That(edit, Is.Not.SameAs(all.Single()));
        edit.Name = "updated";
        await provider.UpdateAsync(edit);
        Assert.That((await provider.GetByIdAsync(row.Id))!.Name, Is.EqualTo("updated"));
        await provider.DeleteAsync(edit);
        Assert.That(await provider.GetByIdAsync(row.Id), Is.Null);
        await provider.CreateAsync(row);
        await provider.DeleteAsync(row.Id);
        Assert.That(await provider.GetAllAsync(), Is.Empty);
    }

    [Test]
    public async Task GeneratedIntKeyWithPrivateSetterIsReturnedAndUsable()
    {
        var provider = Provider<IntRow, int>();
        var row = new IntRow { Name = "one" };
        await provider.CreateAsync(row);
        Assert.That(row.Id, Is.GreaterThan(0));
        row.Name = "two";
        await provider.UpdateAsync(row);
        Assert.That((await provider.GetByIdAsync(row.Id))!.Name, Is.EqualTo("two"));
        await provider.DeleteAsync(row.Id);
        Assert.That(await provider.GetByIdAsync(row.Id), Is.Null);
    }

    [Test]
    public async Task FluentStringKeyUsesMetadataAndDatabaseEnforcesDuplicates()
    {
        var provider = Provider<StringRow, string>();
        var row = new StringRow { Code = "customer/ä'42", Name = "one" };
        await provider.CreateAsync(row);
        await Assert.ThrowsAsync<DbUpdateException>(() => provider.CreateAsync(new StringRow { Code = row.Code }));
        row.Name = "two";
        await provider.UpdateAsync(row);
        Assert.That((await provider.GetByIdAsync(row.Code))!.Name, Is.EqualTo("two"));
        await provider.DeleteAsync(row);
        Assert.That(await provider.GetAllAsync(), Is.Empty);
    }

    [Test]
    public async Task MissingRowsHaveDistinctReadUpdateAndDeleteContracts()
    {
        var provider = Provider<Row, Guid>();
        var missing = new Row { Id = Guid.NewGuid() };
        Assert.That(await provider.GetByIdAsync(missing.Id), Is.Null);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => provider.UpdateAsync(missing));
        await provider.DeleteAsync(missing.Id);
        _commands.Sql.Clear();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => provider.DeleteAsync(missing));
        Assert.That(_commands.Sql.Any(sql => sql.StartsWith("SELECT", StringComparison.Ordinal)), Is.False,
            "Entity delete must not perform an existence lookup.");
        await provider.CreateAsync(new Row { Name = "later call still works" });
    }

    [Test]
    public async Task UpdateWritesOnlyChangedScalarsAndHonorsFullEditDefaultsAndEfComparers()
    {
        var provider = Provider<Row, Guid>();
        var row = new Row { Name = "keep", Enabled = true, Count = 5, Bytes = [1, 2] };
        await provider.CreateAsync(row);
        var edit = (await provider.GetByIdAsync(row.Id))!;
        edit.Enabled = false;
        edit.Count = 0;
        edit.Bytes = [1, 2]; // Structural EF comparer: equal data must not cause an update.
        edit.Created = "client must not overwrite generated values";
        edit.Immutable = "client must not overwrite ignored values";
        _commands.Sql.Clear();
        await provider.UpdateAsync(edit);
        var sql = _commands.Sql.Single(command => command.StartsWith("UPDATE", StringComparison.Ordinal));
        var set = sql[..sql.IndexOf("WHERE", StringComparison.Ordinal)];
        Assert.That(set, Does.Contain("\"Enabled\" =").And.Contain("\"Count\" ="));
        foreach (var excluded in new[] { "Name", "Bytes", "Id", "Created", "Immutable" })
            Assert.That(set, Does.Not.Contain($"\"{excluded}\" ="));
        var stored = (await provider.GetByIdAsync(row.Id))!;
        Assert.That(stored.Enabled, Is.False);
        Assert.That(stored.Count, Is.Zero);
        Assert.That(stored.Created, Is.EqualTo("database"));
        Assert.That(stored.Immutable, Is.EqualTo("initial"));
    }

    [Test]
    public async Task UpdateUsesFieldMappingsAndConvertersWithoutCopyingNavigationOrComplexGraphs()
    {
        var provider = Provider<Row, Guid>();
        var row = new Row { Name = "before", Child = new Child { Name = "child" }, Details = new Details { City = "old" } };
        row.SetNote("old field");
        await provider.CreateAsync(row);
        var edit = (await provider.GetByIdAsync(row.Id))!;
        edit.Name = "after";
        edit.SetNote("new field");
        edit.Mood = Mood.Happy;
        edit.Details.City = "ignored";
        edit.Owned.City = "ignored owned edit";
        edit.Child = new Child { Name = "unwanted insert" };
        await provider.UpdateAsync(edit);
        await using var db = await _factory.CreateDbContextAsync();
        var stored = await db.Set<Row>().Include(x => x.Child).SingleAsync();
        Assert.That(stored.ReadNote(), Is.EqualTo("new field"));
        Assert.That(stored.Mood, Is.EqualTo(Mood.Happy));
        Assert.That(stored.Details.City, Is.EqualTo("old"));
        Assert.That(stored.Owned.City, Is.EqualTo("original owned"));
        Assert.That(stored.Child!.Name, Is.EqualTo("child"));
        Assert.That(await db.Set<Child>().CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task NoChangeEditIssuesNoUpdateEvenWithStaleToken()
    {
        var provider = Provider<VersionRow, int>();
        var row = new VersionRow { Name = "one" };
        await provider.CreateAsync(row);
        var stale = (await provider.GetByIdAsync(row.Id))!;
        row.Name = "two";
        await provider.UpdateAsync(row);
        stale.Name = "two"; // All writable values now equal the database; token remains old.
        _commands.Sql.Clear();
        await provider.UpdateAsync(stale);
        Assert.That(_commands.Sql.Any(sql => sql.StartsWith("UPDATE", StringComparison.Ordinal)), Is.False);
    }

    [Test]
    public async Task StoreGeneratedTokenRejectsDetachedStaleUpdateAndFreshEditSucceeds()
    {
        var provider = Provider<VersionRow, int>();
        var row = new VersionRow { Name = "one", Other = "unchanged" };
        await provider.CreateAsync(row);
        var first = (await provider.GetByIdAsync(row.Id))!;
        var second = (await provider.GetByIdAsync(row.Id))!;
        first.Name = "two";
        await provider.UpdateAsync(first);
        second.Name = "stale edit";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => provider.UpdateAsync(second));
        var fresh = (await provider.GetByIdAsync(row.Id))!;
        Assert.That(fresh.Name, Is.EqualTo("two"));
        Assert.That(fresh.Other, Is.EqualTo("unchanged"));
        Assert.That(fresh.Version, Is.Not.EqualTo(second.Version));
        _commands.Sql.Clear();
        fresh.Name = "three";
        await provider.UpdateAsync(fresh);
        var sql = _commands.Sql.Single(command => command.StartsWith("UPDATE", StringComparison.Ordinal));
        Assert.That(sql[..sql.IndexOf("WHERE", StringComparison.Ordinal)], Does.Not.Contain("\"Version\" ="));
        Assert.That(sql, Does.Contain("AND \"Version\" ="));
        Assert.That((await provider.GetByIdAsync(row.Id))!.Name, Is.EqualTo("three"));
    }

    [Test]
    public async Task EveryConfiguredOriginalTokenIsCheckedForUpdateAndEntityDelete()
    {
        var provider = Provider<VersionRow, int>();
        var row = new VersionRow { Name = "same", Other = "one" };
        await provider.CreateAsync(row);
        var stale = (await provider.GetByIdAsync(row.Id))!;
        row.Other = "two"; // Changes only the second token; the binary first token stays equal.
        await provider.UpdateAsync(row);
        stale.Name = "stale edit";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => provider.UpdateAsync(stale));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => provider.DeleteAsync(stale));
        Assert.That((await provider.GetByIdAsync(row.Id))!.Other, Is.EqualTo("two"));
    }

    [Test]
    public async Task OverlappingUpdatesUseSeparateContextsAndDetectChangeAfterLookup()
    {
        var provider = Provider<VersionRow, int>();
        var row = new VersionRow { Name = "one" };
        await provider.CreateAsync(row);
        var first = (await provider.GetByIdAsync(row.Id))!;
        var second = (await provider.GetByIdAsync(row.Id))!;
        first.Name = "first";
        second.Name = "second";
        var entered = Signal();
        var release = Signal();
        _saves.Next = async (_, ct) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
        };
        var blocked = provider.UpdateAsync(first);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await provider.UpdateAsync(second);
            Assert.That(_factory.Created.Last().AsyncDisposals, Is.EqualTo(1));
            Assert.That(_factory.Created.Reverse().Skip(1).First().AsyncDisposals, Is.Zero);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => blocked);
        Assert.That((await provider.GetByIdAsync(row.Id))!.Name, Is.EqualTo("second"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DeleteBetweenLookupAndSaveRaisesConcurrencyException(bool deleting)
    {
        var provider = Provider<Row, Guid>();
        var row = new Row { Name = "one" };
        await provider.CreateAsync(row);
        row.Name = "two";
        _saves.Next = async (_, ct) =>
        {
            await using var other = await _factory.CreateDbContextAsync(ct);
            await other.Set<Row>().Where(x => x.Id == row.Id).ExecuteDeleteAsync(ct);
        };
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => deleting
            ? provider.DeleteAsync(row.Id) : provider.UpdateAsync(row));
    }

    [Test]
    public async Task EntityDeleteChecksExpectedGeneratedTokenWhileIdDeleteUsesCurrentRow()
    {
        var provider = Provider<VersionRow, int>();
        var row = new VersionRow { Name = "one" };
        await provider.CreateAsync(row);
        var stale = (await provider.GetByIdAsync(row.Id))!;
        row.Name = "two";
        await provider.UpdateAsync(row);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => provider.DeleteAsync(stale));
        await provider.DeleteAsync(row.Id);
        Assert.That(await provider.GetByIdAsync(row.Id), Is.Null);
        await provider.CreateAsync(new VersionRow { Name = "fresh" });
        var fresh = (await provider.GetAllAsync()).Single();
        await provider.DeleteAsync(fresh);
        Assert.That(await provider.GetAllAsync(), Is.Empty);
    }

    [Test]
    public async Task ApplicationManagedTokensRequireExplicitRenewalButSupportEntityDelete()
    {
        var provider = new RevisionProvider(_factory);
        var row = new RevisionRow { Name = "one", Revision = Guid.NewGuid() };
        await provider.CreateAsync(row);
        var original = (await provider.GetByIdAsync(row.Id))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.UpdateAsync(original));
        await provider.RenameAsync(row.Id, "two", original.Revision);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => provider.RenameAsync(row.Id, "stale", original.Revision));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => provider.DeleteAsync(original));
        var fresh = (await provider.GetByIdAsync(row.Id))!;
        Assert.That(fresh.Name, Is.EqualTo("two"));
        Assert.That(fresh.Revision, Is.Not.EqualTo(original.Revision));
        await provider.DeleteAsync(fresh);
    }

    [TestCase("composite")]
    [TestCase("shadow")]
    [TestCase("keyless")]
    [TestCase("mismatch")]
    [TestCase("unmapped")]
    public async Task UnsupportedKeysFailClearlyBeforeDatabaseWrites(string mapping)
    {
        _commands.Sql.Clear();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => mapping switch
        {
            "composite" => Provider<CompositeRow, int>().CreateAsync(new CompositeRow()),
            "shadow" => Provider<ShadowKeyRow, int>().CreateAsync(new ShadowKeyRow()),
            "keyless" => Provider<KeylessRow, int>().GetAllAsync(),
            "mismatch" => Provider<IntRow, Guid>().GetByIdAsync(Guid.NewGuid()),
            _ => Provider<UnmappedRow, int>().CreateAsync(new UnmappedRow())
        });
        Assert.That(failure!.Message, Does.Contain(mapping == "mismatch" ? nameof(IntRow) : mapping switch
        {
            "composite" => nameof(CompositeRow), "shadow" => nameof(ShadowKeyRow),
            "keyless" => nameof(KeylessRow), _ => nameof(UnmappedRow)
        }));
        Assert.That(_commands.Sql, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ShadowConcurrencyTokensAreRejectedForDetachedWrites(bool deleting)
    {
        var provider = Provider<ShadowTokenRow, int>();
        var row = new ShadowTokenRow();
        await provider.CreateAsync(row);
        _commands.Sql.Clear();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => deleting
            ? provider.DeleteAsync(row) : provider.UpdateAsync(row));
        Assert.That(failure!.Message, Does.Contain("Revision").And.Contain("specialized"));
        Assert.That(_commands.Sql, Is.Empty);
    }

    [Test]
    public async Task GeneratedTokenThatAllowsClientWritesIsRejected()
    {
        var provider = Provider<WritableTokenRow, int>();
        var row = new WritableTokenRow();
        await provider.CreateAsync(row);
        _commands.Sql.Clear();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.UpdateAsync(row));
        Assert.That(failure!.Message, Does.Contain(nameof(WritableTokenRow.Version)).And.Contain("Ignore"));
        Assert.That(_commands.Sql, Is.Empty);
    }

    [Test]
    public async Task ComplexConcurrencyTokensRequireSpecializedUpdateAndDelete()
    {
        var provider = Provider<ComplexTokenRow, int>();
        var row = new ComplexTokenRow();
        await provider.CreateAsync(row);
        _commands.Sql.Clear();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.UpdateAsync(row));
        Assert.That(failure!.Message, Does.Contain(nameof(ComplexTokenRow)).And.Contain("Version"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.DeleteAsync(row));
        Assert.That(_commands.Sql, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CancellationReachesQueriesAndSavesAndDisposesAcquiredContext(bool saving)
    {
        var provider = Provider<Row, Guid>();
        using var cancellation = new CancellationTokenSource();
        if (saving)
            _saves.Next = (_, token) =>
            {
                Assert.That(token, Is.EqualTo(cancellation.Token));
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            };
        else
            _commands.Next = token =>
            {
                Assert.That(token, Is.EqualTo(cancellation.Token));
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            };
        var task = saving ? provider.CreateAsync(new Row(), cancellation.Token) : provider.GetAllAsync(cancellation.Token);
        await Assert.CatchAsync<OperationCanceledException>(() => task);
        Assert.That(task.IsCanceled, Is.True);
        Assert.That(_factory.CreationToken, Is.EqualTo(cancellation.Token));
        Assert.That(_factory.Created.Last().AsyncDisposals, Is.EqualTo(1));
        Assert.That(await provider.GetAllAsync(), Is.Empty);
    }

    [Test]
    public async Task PreCanceledCallAcquiresNoContextAndNullArgumentsAreRejected()
    {
        var provider = Provider<StringRow, string>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var count = _factory.Created.Count;
        await Assert.CatchAsync<OperationCanceledException>(() => provider.GetByIdAsync("id", cancellation.Token));
        Assert.That(_factory.Created, Has.Count.EqualTo(count));
        Assert.Throws<ArgumentNullException>(() => provider.GetByIdAsync(null!));
        Assert.Throws<ArgumentNullException>(() => provider.CreateAsync(null!));
        Assert.Throws<ArgumentNullException>(() => provider.UpdateAsync(null!));
        Assert.Throws<ArgumentNullException>(() => provider.DeleteAsync((string)null!));
        Assert.Throws<ArgumentNullException>(() => provider.DeleteAsync((StringRow)null!));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task BorrowedNoOpStillSavesOtherPendingChanges(bool deleteMissing)
    {
        var provider = Provider<IntRow, int>();
        var row = new IntRow { Name = "unchanged" };
        await provider.CreateAsync(row);
        await using var db = await _factory.CreateDbContextAsync();
        var other = new StringRow { Code = "raw", Name = "pending" };
        db.Add(other);
        var createdBefore = _factory.Created.Count;
        if (deleteMissing) await provider.DeleteAsync(db, int.MaxValue);
        else await provider.UpdateAsync(db, row);
        Assert.That(_factory.Created.Count, Is.EqualTo(createdBefore));
        Assert.That(db.AsyncDisposals, Is.Zero);
        Assert.That(db.Entry(other).State, Is.EqualTo(EntityState.Unchanged));
        Assert.That((await Provider<StringRow, string>().GetByIdAsync("raw"))!.Name, Is.EqualTo("pending"));
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task BorrowedDetachedEditReusesTrackedIdentityAndChecksExpectedTokens(bool delete, bool stale)
    {
        var provider = Provider<VersionRow, int>();
        var row = new VersionRow { Name = "one", Other = "first" };
        await provider.CreateAsync(row);
        var edit = (await provider.GetByIdAsync(row.Id))!;
        if (stale)
        {
            row.Other = "second"; // The second token alone must detect the stale edit.
            await provider.UpdateAsync(row);
        }
        await using var db = await _factory.CreateDbContextAsync();
        var tracked = (await db.Set<VersionRow>().FindAsync(row.Id))!;
        edit.Name = "edit";
        // Even if the tracker already holds the same pending edit, detached expected tokens apply.
        if (!delete) tracked.Name = edit.Name;
        Task Write() => delete ? provider.DeleteAsync(db, edit) : provider.UpdateAsync(db, edit);
        if (stale) await Assert.ThrowsAsync<DbUpdateConcurrencyException>(Write);
        else await Write();
        Assert.That(db.Entry(edit).State, Is.EqualTo(EntityState.Detached));
        Assert.That(db.ChangeTracker.Entries<VersionRow>().All(e => ReferenceEquals(e.Entity, tracked)), Is.True);
        var stored = await provider.GetByIdAsync(row.Id);
        if (!stale && delete) Assert.That(stored, Is.Null);
        else Assert.That(stored!.Name, Is.EqualTo(stale ? "one" : "edit"));
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task BorrowedTrackedInputRetainsOriginalTokensDespiteChangedCurrentToken(bool delete, bool stale)
    {
        var provider = Provider<VersionRow, int>();
        var row = new VersionRow { Name = "original" };
        await provider.CreateAsync(row);
        await using var db = await _factory.CreateDbContextAsync();
        var tracked = (await db.Set<VersionRow>().FindAsync(row.Id))!;
        var original = tracked.Version.ToArray();
        if (stale)
        {
            row.Name = "external";
            await provider.UpdateAsync(row);
            tracked.Version = (await provider.GetByIdAsync(row.Id))!.Version;
        }
        else tracked.Version = [0]; // Current tokens must never overwrite tracked OriginalValue.
        tracked.Name = "local";
        Task Write() => delete ? provider.DeleteAsync(db, tracked) : provider.UpdateAsync(db, tracked);
        if (stale)
        {
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(Write);
            Assert.That(db.Entry(tracked).Property(x => x.Version).OriginalValue, Is.EqualTo(original));
        }
        else await Write();
        Assert.That(db.AsyncDisposals, Is.Zero);
        var stored = await provider.GetByIdAsync(row.Id);
        if (!stale && delete) Assert.That(stored, Is.Null);
        else Assert.That(stored!.Name, Is.EqualTo(stale ? "external" : "local"));
    }

    [Test]
    public async Task BorrowedEntityDeletePreservesTrackedApplicationManagedOriginalToken()
    {
        var provider = Provider<RevisionRow, int>();
        var row = new RevisionRow { Revision = Guid.NewGuid() };
        await provider.CreateAsync(row);
        await using var db = await _factory.CreateDbContextAsync();
        var tracked = (await db.Set<RevisionRow>().FindAsync(row.Id))!;
        tracked.Revision = Guid.NewGuid();
        await provider.DeleteAsync(db, tracked);
        Assert.That(await provider.GetByIdAsync(row.Id), Is.Null);
    }

    [TestCase(EntityState.Added, false)]
    [TestCase(EntityState.Deleted, false)]
    [TestCase(EntityState.Added, true)]
    [TestCase(EntityState.Deleted, true)]
    public async Task BorrowedUpdateRejectsAddedOrDeletedTrackedTargets(EntityState state, bool detachedInput)
    {
        var provider = Provider<VersionRow, int>();
        await using var db = await _factory.CreateDbContextAsync();
        var tracked = new VersionRow { Id = 123, Name = "tracked" };
        db.Entry(tracked).State = state;
        var input = detachedInput ? new VersionRow { Id = tracked.Id, Name = "detached" } : tracked;
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.UpdateAsync(db, input));
        Assert.That(db.Entry(tracked).State, Is.EqualTo(state));
        Assert.That(db.AsyncDisposals, Is.Zero);
    }

    [Test]
    public async Task BorrowedWritesValidateArgumentsBeforeUsingCallerResources()
    {
        var provider = Provider<StringRow, string>();
        await using var db = await _factory.CreateDbContextAsync();
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.CreateAsync(null!, new StringRow()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.UpdateAsync(null!, new StringRow()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.DeleteAsync(null!, "key"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.DeleteAsync(null!, new StringRow()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.CreateAsync(db, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.UpdateAsync(db, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.DeleteAsync(db, (string)null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.DeleteAsync(db, (StringRow)null!));
        Assert.That(db.AsyncDisposals, Is.Zero);
    }

    private CrudProvider<TEntity, TId> Provider<TEntity, TId>() where TEntity : class where TId : notnull
        => new(_factory);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class CrudProvider<TEntity, TId>(IDbContextFactory<CrudContext> factory)
        : ProviderBase<TEntity, TId, CrudContext>(factory) where TEntity : class where TId : notnull;

    private sealed class RevisionProvider(IDbContextFactory<CrudContext> factory)
        : ProviderBase<RevisionRow, int, CrudContext>(factory)
    {
        public Task RenameAsync(int id, string name, Guid expected)
            => WithContextAsync(async (db, ct) =>
            {
                var row = await db.Set<RevisionRow>().FindAsync([id], ct) ?? throw new KeyNotFoundException();
                db.Entry(row).Property(x => x.Revision).OriginalValue = expected;
                row.Name = name;
                row.Revision = Guid.NewGuid();
                await db.SaveChangesAsync(ct);
            });
    }

    private sealed class CrudFactory(DbContextOptions<CrudContext> options) : IDbContextFactory<CrudContext>
    {
        public ConcurrentQueue<CrudContext> Created { get; } = new();
        public CancellationToken CreationToken { get; private set; }
        public CrudContext CreateDbContext() => throw new AssertionException("CRUD must use async factory creation.");
        public Task<CrudContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            CreationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            var db = new CrudContext(options);
            Created.Enqueue(db);
            return Task.FromResult(db);
        }
    }

    private sealed class CrudContext(DbContextOptions<CrudContext> options) : DbContext(options)
    {
        public int AsyncDisposals { get; private set; }
        public int SyncDisposals { get; private set; }
        public override async ValueTask DisposeAsync() { AsyncDisposals++; await base.DisposeAsync(); }
        public override void Dispose() { SyncDisposals++; base.Dispose(); }
        protected override void OnModelCreating(ModelBuilder model)
        {
            var row = model.Entity<Row>();
            row.Property(x => x.Created).HasDefaultValue("database").ValueGeneratedOnAdd();
            row.Property(x => x.Immutable).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
            row.Property<string>("_note").HasField("_note");
            row.Property(x => x.Mood).HasConversion<string>();
            row.ComplexProperty(x => x.Details);
            row.OwnsOne(x => x.Owned);
            row.HasOne(x => x.Child).WithMany().HasForeignKey("ChildId");
            model.Entity<IntRow>();
            model.Entity<StringRow>().HasKey(x => x.Code);
            model.Entity<VersionRow>().Property(x => x.Version)
                .HasComputedColumnSql("CAST(\"Name\" AS BLOB)", stored: true).IsConcurrencyToken();
            model.Entity<VersionRow>().Property(x => x.OtherVersion)
                .HasComputedColumnSql("\"Other\"", stored: true).IsConcurrencyToken();
            model.Entity<RevisionRow>().Property(x => x.Revision).IsConcurrencyToken();
            model.Entity<CompositeRow>().HasKey(x => new { x.First, x.Second });
            model.Entity<ShadowKeyRow>().Property<int>("Key");
            model.Entity<ShadowKeyRow>().HasKey("Key");
            model.Entity<KeylessRow>().HasNoKey().ToView("Keyless");
            model.Entity<ShadowTokenRow>().Property<Guid>("Revision").IsConcurrencyToken();
            var token = model.Entity<WritableTokenRow>().Property(x => x.Version).HasDefaultValue(1)
                .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
            token.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Save);
            model.Entity<ComplexTokenRow>().ComplexProperty(x => x.Audit).Property(x => x.Version)
                .HasComputedColumnSql("'fixed'").IsConcurrencyToken();
        }
    }

    private sealed class SaveObserver : SaveChangesInterceptor
    {
        public Func<DbContext, CancellationToken, Task>? Next { get; set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var next = Next;
            Next = null;
            if (next is not null) await next(eventData.Context!, cancellationToken);
            return result;
        }
    }

    private sealed class CommandObserver : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];
        public Action<CancellationToken>? Next { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Sql.Add(command.CommandText);
            var next = Next;
            Next = null;
            next?.Invoke(cancellationToken);
            return ValueTask.FromResult(result);
        }
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result) => throw new AssertionException("CRUD must use async database I/O.");
    }

    private sealed class Row
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public bool Enabled { get; set; }
        public int Count { get; set; }
        public byte[] Bytes { get; set; } = [];
        public string? Created { get; set; }
        public string Immutable { get; set; } = "initial";
        public Mood Mood { get; set; }
        private string _note = "";
        public void SetNote(string value) => _note = value;
        public string ReadNote() => _note;
        public Child? Child { get; set; }
        public Details Details { get; set; } = new();
        public OwnedDetails Owned { get; set; } = new();
    }
    private sealed class Details { public string City { get; set; } = ""; }
    private sealed class OwnedDetails { public string City { get; set; } = "original owned"; }
    private enum Mood { Normal, Happy }
    private sealed class Child { public int Id { get; set; } public string Name { get; set; } = ""; }
    private sealed class IntRow { public int Id { get; private set; } public string Name { get; set; } = ""; }
    private sealed class StringRow { public string Code { get; set; } = ""; public string Name { get; set; } = ""; }
    private sealed class VersionRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Other { get; set; } = "";
        public byte[] Version { get; set; } = [];
        public string OtherVersion { get; set; } = "";
    }
    private sealed class RevisionRow { public int Id { get; set; } public string Name { get; set; } = ""; public Guid Revision { get; set; } }
    private sealed class CompositeRow { public int First { get; set; } public int Second { get; set; } }
    private sealed class ShadowKeyRow;
    private sealed class KeylessRow { public string Name { get; set; } = ""; }
    private sealed class UnmappedRow { public int Id { get; set; } }
    private sealed class ShadowTokenRow { public int Id { get; set; } }
    private sealed class WritableTokenRow { public int Id { get; set; } public int Version { get; set; } }
    private sealed class ComplexTokenRow { public int Id { get; set; } public Audit Audit { get; set; } = new(); }
    private sealed class Audit { public string Version { get; set; } = ""; }
}
