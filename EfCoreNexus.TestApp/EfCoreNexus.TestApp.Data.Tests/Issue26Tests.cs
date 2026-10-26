using EfCoreNexus.Framework.Entities;
using EfCoreNexus.Framework.Helper;
using EfCoreNexus.Framework.Provider;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EfCoreNexus.TestApp.Data.Tests;

public class Issue26Tests
{
    private SqliteConnection _connection = null!;
    private Factory _factory = null!;
    private RowProvider _provider = null!;

    [SetUp]
    public async Task Setup()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _factory = MakeFactory(QueryTrackingBehavior.TrackAll);
        _provider = new RowProvider(_factory);
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
    public async Task UpdatePersistsAndRejectsStaleTokenWithConfiguredTracking(QueryTrackingBehavior behavior, bool borrowed)
    {
        var provider = new RowProvider(MakeFactory(behavior));
        await provider.CreateAsync(new Row { Id = 1, Name = "before" });
        var stale = (await provider.GetByIdAsync(1))!;
        var fresh = (await provider.GetByIdAsync(1))!;
        fresh.Name = "after";
        await using var db = MakeFactory(behavior).CreateDbContext();
        if (borrowed) await provider.UpdateAsync(db, fresh); else await provider.UpdateAsync(fresh);
        Assert.That((await _provider.GetByIdAsync(1))!.Name, Is.EqualTo("after"));
        Assert.That(db.ChangeTracker.QueryTrackingBehavior, Is.EqualTo(behavior));
        stale.Name = "stale edit";
        await using var staleDb = MakeFactory(behavior).CreateDbContext();
        if (borrowed) await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => provider.UpdateAsync(staleDb, stale));
        else await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => provider.UpdateAsync(stale));
        Assert.That((await _provider.GetByIdAsync(1))!.Name, Is.EqualTo("after"));
    }

    [TestCase(QueryTrackingBehavior.TrackAll)]
    [TestCase(QueryTrackingBehavior.NoTracking)]
    [TestCase(QueryTrackingBehavior.NoTrackingWithIdentityResolution)]
    public async Task UpdateReusesAlreadyTrackedTarget(QueryTrackingBehavior behavior)
    {
        await _provider.CreateAsync(new Row { Id = 1, Name = "before" });
        var snapshot = (await _provider.GetByIdAsync(1))!;
        await using var db = MakeFactory(behavior).CreateDbContext();
        var tracked = await db.Set<Row>().AsTracking().SingleAsync();
        snapshot.Name = "after";
        snapshot.Details.City = "ignored input graph";
        await _provider.UpdateAsync(db, snapshot);
        Assert.That(db.ChangeTracker.Entries<Row>().Single().Entity, Is.SameAs(tracked));
        Assert.That(db.Entry(snapshot).State, Is.EqualTo(EntityState.Detached));
        Assert.That(db.ChangeTracker.QueryTrackingBehavior, Is.EqualTo(behavior));
        var stored = (await _provider.GetByIdAsync(1))!;
        Assert.That(stored.Name, Is.EqualTo("after"));
        Assert.That(stored.Details.City, Is.EqualTo(""));
    }

    [TestCase(QueryTrackingBehavior.TrackAll)]
    [TestCase(QueryTrackingBehavior.NoTracking)]
    [TestCase(QueryTrackingBehavior.NoTrackingWithIdentityResolution)]
    public async Task UnchangedDetachedUpdateDoesNotAssertStaleToken(QueryTrackingBehavior behavior)
    {
        await _provider.CreateAsync(new Row { Id = 1, Name = "before" });
        var snapshot = (await _provider.GetByIdAsync(1))!;
        var winner = (await _provider.GetByIdAsync(1))!;
        winner.Name = "after";
        await _provider.UpdateAsync(winner);
        snapshot.Name = "after";
        await new RowProvider(MakeFactory(behavior)).UpdateAsync(snapshot);
        Assert.That((await _provider.GetByIdAsync(1))!.Name, Is.EqualTo("after"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task IdDeleteRejectsAddedWithoutSavingAnything(bool storedRow)
    {
        if (storedRow) await _provider.CreateAsync(new Row { Id = 1, Name = "stored" });
        await using var db = _factory.CreateDbContext();
        var pending = new Row { Id = 1, Name = "pending" };
        var other = new Row { Id = 2, Name = "other" };
        db.AddRange(pending, other);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.DeleteAsync(db, 1));
        Assert.That(error!.Message, Does.Contain("Added"));
        Assert.That(db.Entry(pending).State, Is.EqualTo(EntityState.Added));
        Assert.That(db.Entry(other).State, Is.EqualTo(EntityState.Added));
        await using var verify = _factory.CreateDbContext();
        Assert.That((await verify.Set<Row>().AsNoTracking().ToListAsync()).Select(x => x.Name),
            Is.EquivalentTo(storedRow ? new[] { "stored" } : Array.Empty<string>()));
    }

    [TestCase(true, false, false)]
    [TestCase(false, false, false)]
    [TestCase(true, true, false)]
    [TestCase(false, true, false)]
    [TestCase(true, false, true)]
    public async Task PendingEditChecksDetachedExpectedToken(bool stale, bool scalar, bool disableAutoDetect)
    {
        await _provider.CreateAsync(new Row { Id = 1, Name = "before" });
        var snapshot = (await _provider.GetByIdAsync(1))!;
        var winner = (await _provider.GetByIdAsync(1))!;
        winner.Name = "after";
        await _provider.UpdateAsync(winner);
        await using var db = _factory.CreateDbContext();
        var tracked = (await db.Set<Row>().FindAsync(1))!;
        if (scalar) tracked.Name = "local";
        else tracked.Details.City = "local";
        snapshot.Name = tracked.Name; // The detached input introduces no additional scalar edit.
        if (!stale) snapshot.Version = tracked.Version;
        db.ChangeTracker.AutoDetectChangesEnabled = !disableAutoDetect;
        if (stale) await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => _provider.UpdateAsync(db, snapshot));
        else await _provider.UpdateAsync(db, snapshot);
        var stored = (await _provider.GetByIdAsync(1))!;
        Assert.That(stored.Name, Is.EqualTo(!stale && scalar ? "local" : "after"));
        Assert.That(stored.Details.City, Is.EqualTo(!stale && !scalar ? "local" : ""));
    }

    [Test]
    public void PreconfiguredComplexTypeIsNotDiscoveredAsRoot()
    {
        using var native = new ConventionContext(new DbContextOptionsBuilder<ConventionContext>().UseSqlite(_connection).Options);
        Assert.That(native.Model.FindEntityType(typeof(AValue)), Is.Null);
        Assert.That(native.Model.FindEntityType(typeof(ZOwner))!.FindComplexProperty(nameof(ZOwner.Value)), Is.Not.Null);
        using var discovered = new DiscoveryContext(new DbContextOptionsBuilder<DiscoveryContext>().UseSqlite(_connection).Options);
        Assert.That(discovered.Model.FindEntityType(typeof(AValue)), Is.Null);
        Assert.That(discovered.Model.FindEntityType(typeof(ZOwner))!.FindComplexProperty(nameof(ZOwner.Value)), Is.Not.Null);
    }

    private sealed class Factory(DbContextOptions<EdgeContext> options) : IDbContextFactory<EdgeContext>
    {
        public EdgeContext CreateDbContext() => new(options);
    }
    private sealed class RowProvider(IDbContextFactory<EdgeContext> factory) : ProviderBase<Row, int, EdgeContext>(factory);
    private sealed class EdgeContext(DbContextOptions<EdgeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<Row>().Property(x => x.Version).HasComputedColumnSql("\"Name\"", stored: true).IsConcurrencyToken();
            model.Entity<Row>().ComplexProperty(x => x.Details);
        }
    }
    private sealed class Row { public int Id { get; set; } public string Name { get; set; } = ""; public string Version { get; set; } = ""; public Details Details { get; set; } = new(); }
    private sealed class Details { public string City { get; set; } = ""; }

    public sealed class AValue : IEntity { public string City { get; set; } = ""; }
    public sealed class ZOwner : IEntity { public int Id { get; set; } public AValue Value { get; set; } = new(); }
    private sealed class ConventionContext(DbContextOptions<ConventionContext> options) : DbContext(options)
    {
        protected override void ConfigureConventions(ModelConfigurationBuilder builder) => builder.ComplexProperties<AValue>();
        protected override void OnModelCreating(ModelBuilder model) => model.Entity<ZOwner>();
    }
    private sealed class DiscoveryContext(DbContextOptions<DiscoveryContext> options) : DbContext(options)
    {
        protected override void ConfigureConventions(ModelConfigurationBuilder builder) => builder.ComplexProperties<AValue>();
        protected override void OnModelCreating(ModelBuilder model)
            => model.RegisterNexusEntities(typeof(AValue).Assembly, t => t == typeof(AValue) || t == typeof(ZOwner));
    }
}
