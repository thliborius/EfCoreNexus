using System.ComponentModel.DataAnnotations.Schema;
using EfCoreNexus.Framework.Entities;
using EfCoreNexus.Framework.Helper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;

namespace EfCoreNexus.TestApp.Data.Tests;

public class EntityRegistrationTests
{
    [Test]
    public async Task MarkerRootsWithoutDefaultConstructorComposeWithNativeConfigurationsAndOverrides()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SalesContext>().UseSqlite(connection).Options;
        await using (var db = new SalesContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var entity = db.Model.FindEntityType(typeof(SalesEntities.Customer))!;
            Assert.That(entity.GetTableName(), Is.EqualTo("Customers"));
            Assert.That(entity.FindProperty(nameof(SalesEntities.Customer.Name))!.GetMaxLength(), Is.EqualTo(80),
                "Explicit context override must follow assembly configuration (120).");
            Assert.That(db.Model.FindEntityType(typeof(SalesEntities.AutomaticallyIncluded)), Is.Not.Null,
                "A new eligible entity needs neither a DbSet nor a configuration class.");
            Assert.That(db.Model.FindEntityType(typeof(ConfigurationOnly)), Is.Not.Null,
                "Native configurations can introduce roots without IEntity.");
            Assert.That(db.Model.FindEntityType(typeof(SalesEntities.AbstractEntity)), Is.Null);
            Assert.That(db.Model.FindEntityType(typeof(SalesEntities.OpenEntity<>)), Is.Null);
            Assert.That(db.Model.FindEntityType(typeof(SalesEntities.IEntityInterface)), Is.Null);
            Assert.That(db.Model.FindEntityType(typeof(SalesEntities.StructEntity)), Is.Null);
            Assert.That(db.Model.FindEntityType(typeof(SalesEntities.Unmapped)), Is.Null);
            Assert.That(db.Model.FindEntityType(typeof(SalesEntities.OwnedValue))!.IsOwned(), Is.True);
            Assert.That(db.Model.FindEntityType(typeof(SalesEntities.ComplexValue)), Is.Null);
            Assert.That(entity.FindComplexProperty(nameof(SalesEntities.Customer.Details)), Is.Not.Null);
            db.Add(new SalesEntities.Customer("Ada"));
            await db.SaveChangesAsync();
        }
        await using var verification = new SalesContext(options);
        var saved = await verification.Set<SalesEntities.Customer>().AsNoTracking().SingleAsync();
        Assert.That(saved.Name, Is.EqualTo("Ada"));
        Assert.That(saved.Id, Is.GreaterThan(0));
    }

    [Test]
    public void ContextsInOneAssemblyUseStablePredicatesForIndependentModelsAndConfigurations()
    {
        using var sales = new SalesContext(new DbContextOptionsBuilder<SalesContext>().UseSqlite("Data Source=:memory:").Options);
        using var inventory = new InventoryContext(new DbContextOptionsBuilder<InventoryContext>().UseSqlite("Data Source=:memory:").Options);
        Assert.That(sales.Model.FindEntityType(typeof(InventoryEntities.Stock)), Is.Null);
        Assert.That(inventory.Model.FindEntityType(typeof(SalesEntities.Customer)), Is.Null);
        Assert.That(inventory.Model.FindEntityType(typeof(ConfigurationOnly)), Is.Null);
        Assert.That(inventory.Model.GetEntityTypes().Select(entity => entity.ClrType),
            Is.EquivalentTo(new[] { typeof(InventoryEntities.Stock) }));
        Assert.That(inventory.Model.FindEntityType(typeof(InventoryEntities.Stock))!.GetTableName(), Is.EqualTo("Stock"));
    }

    [Test]
    public void FluentOwnedComplexNestedComplexAndIgnoredTypesAreNotPromotedToRoots()
    {
        var model = new ModelBuilder();
        model.Entity<FluentEntities.Owner>().HasKey(owner => owner.Id);
        model.Entity<FluentEntities.Owner>().OwnsOne(owner => owner.Owned);
        model.Entity<FluentEntities.Owner>().ComplexProperty(owner => owner.Complex)
            .ComplexProperty(value => value.Nested);
        model.Owned<FluentEntities.UnusedOwned>();
        model.Ignore<FluentEntities.Ignored>();

        model.RegisterNexusEntities(typeof(FluentEntities).Assembly, type => type.DeclaringType == typeof(FluentEntities));
        Assert.That(model.Model.GetEntityTypes().Where(entity => !entity.IsOwned()).Select(entity => entity.ClrType),
            Is.EquivalentTo(new[] { typeof(FluentEntities.Owner) }));
        Assert.That(model.Model.FindEntityType(typeof(FluentEntities.OwnedValue))!.IsOwned(), Is.True);
        Assert.That(model.Model.FindEntityType(typeof(FluentEntities.ComplexValue)), Is.Null);
        Assert.That(model.Model.FindEntityType(typeof(FluentEntities.NestedValue)), Is.Null);
        Assert.That(model.Model.FindEntityType(typeof(FluentEntities.UnusedOwned)), Is.Null);
        Assert.That(model.Model.FindEntityType(typeof(FluentEntities.Ignored)), Is.Null);
    }

    [Test]
    public void RepeatedDiscoveryIsIdempotentAndDoesNotOverwriteFluentMapping()
    {
        var model = new ModelBuilder();
        Func<Type, bool> boundary = type => type == typeof(SalesEntities.AutomaticallyIncluded);
        Assert.That(model.RegisterNexusEntities(typeof(SalesEntities).Assembly, boundary), Is.SameAs(model));
        model.Entity<SalesEntities.AutomaticallyIncluded>().ToTable("ExplicitTable");
        model.RegisterNexusEntities(typeof(SalesEntities).Assembly, boundary);
        Assert.That(model.Model.GetEntityTypes().Count(), Is.EqualTo(1));
        Assert.That(model.Model.FindEntityType(typeof(SalesEntities.AutomaticallyIncluded))!.GetTableName(), Is.EqualTo("ExplicitTable"));
    }

    [Test]
    public void NoPredicateIncludesAllEligibleRootsOfExplicitAssembly()
    {
        // Both sample entities are included without DbSet or per-type registration.
        var model = new ModelBuilder();
        model.RegisterNexusEntities(typeof(Entities.Test).Assembly);
        Assert.That(model.Model.GetEntityTypes().Select(entity => entity.ClrType), Is.EquivalentTo(new[] { typeof(Entities.Test), typeof(Entities.Memo) }));
    }

    [Test]
    public void IncompatibleEntityClassificationsAreDiagnosed()
    {
        var model = new ModelBuilder();
        var error = Assert.Throws<InvalidOperationException>(() => model.RegisterNexusEntities(
            typeof(EntityRegistrationTests).Assembly, type => type == typeof(ConflictingEntity)));
        Assert.That(error!.Message, Does.Contain(typeof(ConflictingEntity).FullName).And.Contain("owned and complex"));
    }

    [Test]
    public void SharedTypeEntitiesRequireExplicitNativeMapping()
    {
        var model = new ModelBuilder();
        model.SharedTypeEntity<SalesEntities.AutomaticallyIncluded>("Shared");
        var error = Assert.Throws<InvalidOperationException>(() => model.RegisterNexusEntities(
            typeof(EntityRegistrationTests).Assembly, type => type == typeof(SalesEntities.AutomaticallyIncluded)));
        Assert.That(error!.Message, Does.Contain(typeof(SalesEntities.AutomaticallyIncluded).FullName).And.Contain("shared-type"));
    }

    [Test]
    public void InvalidArgumentsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => NexusModelBuilderExtensions.RegisterNexusEntities(null!, typeof(SalesEntities).Assembly));
        Assert.Throws<ArgumentNullException>(() => new ModelBuilder().RegisterNexusEntities(null!));
    }

    [Test]
    public async Task NativeRuntimeAndDesignTimeFactoriesBuildTheSameModel()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<SalesContext>(options => options.UseSqlite("Data Source=:memory:"));
        await using var container = services.BuildServiceProvider();
        await using var runtime = await container.GetRequiredService<IDbContextFactory<SalesContext>>().CreateDbContextAsync();
        await using var tooling = new SalesDesignTimeFactory().CreateDbContext([]);
        Assert.That(tooling.Model, Is.Not.SameAs(runtime.Model), "Compare independently built models, not the shared model cache.");
        Assert.That(tooling.Model.ToDebugString(), Is.EqualTo(runtime.Model.ToDebugString()));
    }

    public sealed class SalesContext(DbContextOptions<SalesContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.RegisterNexusEntities(typeof(SalesEntities).Assembly, type => type.DeclaringType == typeof(SalesEntities));
            model.ApplyConfigurationsFromAssembly(typeof(SalesConfiguration).Assembly,
                type => type == typeof(SalesConfiguration) || type == typeof(ConfigurationOnlyMapping));
            model.Entity<SalesEntities.Customer>().Property(customer => customer.Name).HasMaxLength(80);
        }
    }

    public sealed class InventoryContext(DbContextOptions<InventoryContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.RegisterNexusEntities(typeof(InventoryEntities).Assembly, type => type.DeclaringType == typeof(InventoryEntities));
            model.ApplyConfigurationsFromAssembly(typeof(InventoryConfiguration).Assembly, type => type == typeof(InventoryConfiguration));
        }
    }

    private sealed class SalesDesignTimeFactory : IDesignTimeDbContextFactory<SalesContext>
    {
        public SalesContext CreateDbContext(string[] args)
            => new(new DbContextOptionsBuilder<SalesContext>().UseSqlite("Data Source=:memory:")
                .EnableServiceProviderCaching(false).Options);
    }

    public sealed class SalesConfiguration : IEntityTypeConfiguration<SalesEntities.Customer>
    {
        public void Configure(EntityTypeBuilder<SalesEntities.Customer> builder)
        {
            builder.ToTable("Customers");
            builder.Property(customer => customer.Name).HasMaxLength(120);
        }
    }

    public sealed class InventoryConfiguration : IEntityTypeConfiguration<InventoryEntities.Stock>
    {
        public void Configure(EntityTypeBuilder<InventoryEntities.Stock> builder) => builder.ToTable("Stock");
    }

    public sealed class ConfigurationOnlyMapping : IEntityTypeConfiguration<ConfigurationOnly>
    {
        public void Configure(EntityTypeBuilder<ConfigurationOnly> builder) => builder.HasKey(entity => entity.Id);
    }

    public sealed class ConfigurationOnly { public int Id { get; set; } }
    [Owned, ComplexType]
    public sealed class ConflictingEntity : IEntity;
}

public static class SalesEntities
{
    public sealed class Customer(string name) : IEntity
    {
        public int Id { get; private set; }
        public string Name { get; set; } = name;
        public OwnedValue Address { get; set; } = new();
        public ComplexValue Details { get; set; } = new();
    }
    public sealed class AutomaticallyIncluded : IEntity { public int Id { get; set; } }
    public abstract class AbstractEntity : IEntity;
    public sealed class OpenEntity<T> : IEntity;
    public interface IEntityInterface : IEntity;
    public struct StructEntity : IEntity;
    [NotMapped] public sealed class Unmapped : IEntity;
    [Owned] public sealed class OwnedValue : IEntity { public string Street { get; set; } = ""; }
    [ComplexType] public sealed class ComplexValue : IEntity { public string Description { get; set; } = ""; }
}

public static class InventoryEntities
{
    public sealed class Stock : IEntity { public int Id { get; set; } }
}

public static class FluentEntities
{
    public sealed class Owner : IEntity
    {
        public int Id { get; set; }
        public OwnedValue Owned { get; set; } = new();
        public ComplexValue Complex { get; set; } = new();
    }
    public sealed class OwnedValue : IEntity { public string Value { get; set; } = ""; }
    public sealed class ComplexValue : IEntity { public NestedValue Nested { get; set; } = new(); }
    public sealed class NestedValue : IEntity { public string Value { get; set; } = ""; }
    public sealed class UnusedOwned : IEntity;
    public sealed class Ignored : IEntity;
}
