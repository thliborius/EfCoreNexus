using EfCoreNexus.Framework.Provider;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EfCoreNexus.TestApp.Data.Tests;

public class Issue28Tests
{
    private SqliteConnection _connection = null!;
    private Factory _factory = null!;

    [SetUp]
    public async Task Setup()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _factory = MakeFactory(QueryTrackingBehavior.TrackAll);
        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public async Task Teardown() => await _connection.DisposeAsync();

    private Factory MakeFactory(QueryTrackingBehavior behavior) => new(new DbContextOptionsBuilder<EdgeContext>()
        .UseSqlite(_connection).UseQueryTrackingBehavior(behavior).Options);

    [TestCase(QueryTrackingBehavior.NoTracking, false)]
    [TestCase(QueryTrackingBehavior.NoTracking, true)]
    [TestCase(QueryTrackingBehavior.NoTrackingWithIdentityResolution, false)]
    [TestCase(QueryTrackingBehavior.NoTrackingWithIdentityResolution, true)]
    [TestCase(QueryTrackingBehavior.TrackAll, false)]
    [TestCase(QueryTrackingBehavior.TrackAll, true)]
    public async Task IdDeleteRemovesOwnedItemsWithShadowKeys(QueryTrackingBehavior behavior, bool borrowed)
    {
        var factory = MakeFactory(behavior);
        var provider = new OwnerProvider(factory);
        await provider.CreateAsync(new Owner { Id = 1, Items = [new OwnedItem { Value = "first" }, new OwnedItem { Value = "second" }] });
        await using var db = factory.CreateDbContext();
        if (borrowed) await provider.DeleteAsync(db, 1); else await provider.DeleteAsync(1);
        Assert.That(db.ChangeTracker.QueryTrackingBehavior, Is.EqualTo(behavior));
        await using var check = _factory.CreateDbContext();
        Assert.That(await check.Set<Owner>().CountAsync(), Is.Zero);
        Assert.That(await check.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM OwnedItems").SingleAsync(), Is.Zero);
    }

    [TestCase(QueryTrackingBehavior.NoTracking)]
    [TestCase(QueryTrackingBehavior.NoTrackingWithIdentityResolution)]
    public async Task IdDeleteReusesTrackedIdentityEvenWhenFiltered(QueryTrackingBehavior behavior)
    {
        var factory = MakeFactory(behavior);
        var provider = new OwnerProvider(factory);
        await provider.CreateAsync(new Owner { Id = 1, Hidden = true, Items = [new OwnedItem()] });
        await using var db = factory.CreateDbContext();
        var tracked = await db.Set<Owner>().IgnoreQueryFilters().AsTracking().SingleAsync();
        await provider.DeleteAsync(db, 1);
        Assert.That(db.Entry(tracked).State, Is.EqualTo(EntityState.Detached));
        Assert.That(db.ChangeTracker.QueryTrackingBehavior, Is.EqualTo(behavior));
        await using var check = _factory.CreateDbContext();
        Assert.That(await check.Set<Owner>().IgnoreQueryFilters().CountAsync(), Is.Zero);
        Assert.That(await check.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM OwnedItems").SingleAsync(), Is.Zero);
    }

    [TestCase(QueryTrackingBehavior.NoTracking)]
    [TestCase(QueryTrackingBehavior.NoTrackingWithIdentityResolution)]
    public async Task IdDeletePreservesFilteredMissingAndAddedBehavior(QueryTrackingBehavior behavior)
    {
        var factory = MakeFactory(behavior);
        var provider = new OwnerProvider(factory);
        await provider.CreateAsync(new Owner { Id = 1, Hidden = true, Items = [new OwnedItem()] });
        await using var db = factory.CreateDbContext();
        db.Add(new Owner { Id = 2 });
        await provider.DeleteAsync(db, 1); // Filtered out, but the pending insert is saved.
        db.Add(new Owner { Id = 5 });
        await provider.DeleteAsync(db, 999); // Missing also saves the tracker.
        var pending = new Owner { Id = 3 };
        var other = new Owner { Id = 4 };
        db.AddRange(pending, other);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.DeleteAsync(db, 3));
        Assert.That(error!.Message, Does.Contain("Added"));
        Assert.That(db.Entry(pending).State, Is.EqualTo(EntityState.Added));
        Assert.That(db.Entry(other).State, Is.EqualTo(EntityState.Added));
        Assert.That(db.ChangeTracker.QueryTrackingBehavior, Is.EqualTo(behavior));
        await using var check = _factory.CreateDbContext();
        Assert.That(await check.Set<Owner>().IgnoreQueryFilters().Select(x => x.Id).ToListAsync(), Is.EquivalentTo(new[] { 1, 2, 5 }));
        Assert.That(await check.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM OwnedItems").SingleAsync(), Is.EqualTo(1));
    }

    [TestCase(QueryTrackingBehavior.NoTracking)]
    [TestCase(QueryTrackingBehavior.NoTrackingWithIdentityResolution)]
    public async Task IdDeleteRestoresTrackingAfterCancelledLookup(QueryTrackingBehavior behavior)
    {
        var factory = MakeFactory(behavior);
        await using var db = factory.CreateDbContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.CatchAsync<OperationCanceledException>(() => new OwnerProvider(factory).DeleteAsync(db, 1, cancellation.Token));
        Assert.That(db.ChangeTracker.QueryTrackingBehavior, Is.EqualTo(behavior));
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task UpdateRejectsDifferentMappedTypeWithoutSaving(bool baseInput, bool borrowed)
    {
        var provider = new RowProvider(_factory);
        await provider.CreateAsync(new DerivedA { Id = 1, Name = "stored", Extra = "original" });
        BaseRow input = baseInput ? new BaseRow { Id = 1, Name = "incoming" }
            : new DerivedB { Id = 1, Name = "incoming", Extra = "wrong subtype" };
        await using var db = _factory.CreateDbContext();
        var tracked = borrowed ? await db.Set<DerivedA>().SingleAsync() : null;
        var pending = new Owner { Id = 2 };
        if (borrowed) db.Add(pending);
        var error = borrowed
            ? await Assert.ThrowsAsync<InvalidOperationException>(() => provider.UpdateAsync(db, input))
            : await Assert.ThrowsAsync<InvalidOperationException>(() => provider.UpdateAsync(input));
        Assert.That(error!.Message, Does.Contain("mapped").IgnoreCase);
        if (borrowed)
        {
            Assert.That(tracked!.Name, Is.EqualTo("stored"));
            Assert.That(tracked.Extra, Is.EqualTo("original"));
            Assert.That(db.Entry(tracked).State, Is.EqualTo(EntityState.Unchanged));
            Assert.That(db.Entry(pending).State, Is.EqualTo(EntityState.Added));
        }
        await using var check = _factory.CreateDbContext();
        var stored = await check.Set<DerivedA>().SingleAsync();
        Assert.That(stored.Name, Is.EqualTo("stored"));
        Assert.That(stored.Extra, Is.EqualTo("original"));
        Assert.That(await check.Set<Owner>().CountAsync(), Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task UpdateAcceptsSameMappedSubtype(bool borrowed)
    {
        var provider = new RowProvider(_factory);
        await provider.CreateAsync(new DerivedA { Id = 1, Name = "before", Extra = "before" });
        var input = new DerivedA { Id = 1, Name = "after", Extra = "after" };
        await using var db = _factory.CreateDbContext();
        if (borrowed) await provider.UpdateAsync(db, input); else await provider.UpdateAsync(input);
        await using var check = _factory.CreateDbContext();
        var stored = await check.Set<DerivedA>().SingleAsync();
        Assert.That(stored.Name, Is.EqualTo("after"));
        Assert.That(stored.Extra, Is.EqualTo("after"));
    }

    [Test]
    public async Task UpdateAcceptsUnmappedRuntimeSubclassOfSameMappedType()
    {
        var provider = new RowProvider(_factory);
        await provider.CreateAsync(new DerivedA { Id = 1, Name = "before", Extra = "before" });
        await provider.UpdateAsync(new RuntimeDerivedA { Id = 1, Name = "after", Extra = "after" });
        await using var check = _factory.CreateDbContext();
        var stored = await check.Set<DerivedA>().SingleAsync();
        Assert.That(stored.Name, Is.EqualTo("after"));
        Assert.That(stored.Extra, Is.EqualTo("after"));
    }

    private sealed class Factory(DbContextOptions<EdgeContext> options) : IDbContextFactory<EdgeContext>
    {
        public EdgeContext CreateDbContext() => new(options);
    }
    private sealed class OwnerProvider(IDbContextFactory<EdgeContext> factory) : ProviderBase<Owner, int, EdgeContext>(factory);
    private sealed class RowProvider(IDbContextFactory<EdgeContext> factory) : ProviderBase<BaseRow, int, EdgeContext>(factory);
    private sealed class EdgeContext(DbContextOptions<EdgeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<Owner>().HasQueryFilter(x => !x.Hidden);
            model.Entity<Owner>().OwnsMany(x => x.Items, owned =>
            {
                owned.ToTable("OwnedItems");
                owned.Property<Guid>("Id").ValueGeneratedOnAdd();
                owned.HasKey("Id");
            });
            model.Entity<BaseRow>();
            model.Entity<DerivedA>();
            model.Entity<DerivedB>();
        }
    }
    private sealed class Owner { public int Id { get; set; } public bool Hidden { get; set; } public List<OwnedItem> Items { get; set; } = []; }
    private sealed class OwnedItem { public string Value { get; set; } = ""; }
    private class BaseRow { public int Id { get; set; } public string Name { get; set; } = ""; }
    private class DerivedA : BaseRow { public string Extra { get; set; } = ""; }
    private sealed class RuntimeDerivedA : DerivedA;
    private sealed class DerivedB : BaseRow { public string Extra { get; set; } = ""; }
}
