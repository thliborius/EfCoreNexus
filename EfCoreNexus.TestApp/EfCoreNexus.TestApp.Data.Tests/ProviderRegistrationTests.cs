using EfCoreNexus.Framework.Helper;
using EfCoreNexus.Framework.Provider;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EfCoreNexus.TestApp.Data.Tests;

public class ProviderRegistrationTests
{
    [Test]
    public async Task ResolvesConcreteProvidersWithNativeFactoryAndScopedDependenciesAndDisposesThem()
    {
        var services = CreateServices();
        Assert.That(services.AddNexusProviders<NativeContext>(typeof(CustomerProvider).Assembly), Is.SameAs(services));
        await using var container = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true, ValidateScopes = true
        });
        var probe = container.GetRequiredService<Probe>();
        Assert.That(probe.Activated, Is.Zero, "Scanning and DI validation must not activate providers.");
        CustomerProvider first;
        AdditionalDependency dependency;
        await using (var scope = container.CreateAsyncScope())
        {
            first = scope.ServiceProvider.GetRequiredService<CustomerProvider>();
            dependency = scope.ServiceProvider.GetRequiredService<AdditionalDependency>();
            Assert.That(first.Dependency, Is.SameAs(dependency));
            Assert.That(scope.ServiceProvider.GetRequiredService<CustomerProvider>(), Is.SameAs(first));
            Assert.That(scope.ServiceProvider.GetRequiredService<IndirectProvider>(), Is.Not.Null);
            Assert.That(scope.ServiceProvider.GetRequiredService<OtherCustomerProvider>(), Is.Not.Null);
            Assert.That(probe.Activated, Is.EqualTo(1));
            using var cancellation = new CancellationTokenSource();
            var result = await first.QueryAsync(cancellation.Token);
            Assert.That(result, Is.EqualTo(42));
        }
        Assert.That(first.Disposed, Is.True);
        Assert.That(dependency.Disposed, Is.True);
        await using var next = container.CreateAsyncScope();
        Assert.That(next.ServiceProvider.GetRequiredService<CustomerProvider>(), Is.Not.SameAs(first));
    }

    [Test]
    public void ExactContextAndClosedConcreteTypesOnly()
    {
        var services = new ServiceCollection();
        services.AddNexusProviders<NativeContext>(typeof(CustomerProvider).Assembly);
        Assert.That(services.Select(descriptor => descriptor.ServiceType), Is.EquivalentTo(new[]
        {
            typeof(CustomerProvider), typeof(OtherCustomerProvider), typeof(IndirectProvider)
        }));
        services.AddNexusProviders<OtherContext>(typeof(CustomerProvider).Assembly);
        Assert.That(services.Count(descriptor => descriptor.ServiceType == typeof(ForeignProvider)), Is.EqualTo(1));
        Assert.That(services.Any(descriptor => descriptor.ServiceType == typeof(OpenProvider<>)), Is.False);
        Assert.That(services.Any(descriptor => descriptor.ServiceType == typeof(AbstractProvider)), Is.False);
    }

    [Test]
    public void DuplicateAssembliesAndScansPreserveExplicitScopedRegistrationWithoutDuplicates()
    {
        var services = new ServiceCollection();
        services.AddScoped<CustomerProvider>();
        var explicitDescriptor = services.Single();
        var assembly = typeof(CustomerProvider).Assembly;
        services.AddNexusProviders<NativeContext>(assembly, assembly);
        services.AddNexusProviders<NativeContext>(assembly);
        Assert.That(services, Has.Count.EqualTo(3));
        Assert.That(services.Single(descriptor => descriptor.ServiceType == typeof(CustomerProvider)),
            Is.SameAs(explicitDescriptor));
        Assert.That(services.All(descriptor => descriptor.Lifetime == ServiceLifetime.Scoped), Is.True);
    }

    [TestCase("singleton")]
    [TestCase("transient")]
    [TestCase("factory")]
    [TestCase("instance")]
    [TestCase("duplicate")]
    [TestCase("different implementation")]
    public void ConflictsNameProviderAndContextWithoutPartialRegistration(string conflict)
    {
        var services = new ServiceCollection();
        switch (conflict)
        {
            case "singleton": services.AddSingleton<CustomerProvider>(); break;
            case "transient": services.AddTransient<CustomerProvider>(); break;
            case "factory": services.AddScoped<CustomerProvider>(_ => throw new AssertionException("Must not activate.")); break;
            case "instance": services.AddSingleton(new CustomerProvider(new UnusedFactory(), new AdditionalDependency(), new Probe())); break;
            case "duplicate": services.AddScoped<CustomerProvider>(); services.AddScoped<CustomerProvider>(); break;
            default: services.AddScoped<CustomerProvider, ReplacementProvider>(); break;
        }
        var before = services.ToArray();
        var error = Assert.Throws<InvalidOperationException>(() =>
            services.AddNexusProviders<NativeContext>(typeof(CustomerProvider).Assembly));
        Assert.That(error!.Message, Does.Contain(typeof(CustomerProvider).FullName).And.Contain(typeof(NativeContext).FullName));
        Assert.That(services, Is.EqualTo(before));
    }

    [Test]
    public void KeyedRegistrationsDoNotConflictWithUnkeyedDiscovery()
    {
        var services = new ServiceCollection();
        services.AddKeyedScoped<CustomerProvider>("special");
        services.AddNexusProviders<NativeContext>(typeof(CustomerProvider).Assembly);
        services.AddNexusProviders<NativeContext>(typeof(CustomerProvider).Assembly);
        Assert.That(services.Count(descriptor => descriptor.ServiceType == typeof(CustomerProvider)), Is.EqualTo(2));
    }

    [Test]
    public void MissingConstructorDependencyIsReportedByNativeDIValidation()
    {
        var services = CreateServices();
        services.Remove(services.Single(descriptor => descriptor.ServiceType == typeof(AdditionalDependency)));
        services.AddNexusProviders<NativeContext>(typeof(CustomerProvider).Assembly);
        var error = Assert.Throws<AggregateException>(() => services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));
        Assert.That(error!.ToString(), Does.Contain(nameof(AdditionalDependency)).And.Contain(nameof(CustomerProvider)));
    }

    [Test]
    public void InvalidArgumentsAreRejectedBeforeRegistration()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(() => NexusServiceCollectionExtensions.AddNexusProviders<NativeContext>(null!, typeof(CustomerProvider).Assembly));
        Assert.Throws<ArgumentNullException>(() => services.AddNexusProviders<NativeContext>(null!));
        Assert.Throws<ArgumentException>(() => services.AddNexusProviders<NativeContext>());
        Assert.Throws<ArgumentException>(() => services.AddNexusProviders<NativeContext>(typeof(CustomerProvider).Assembly, null!));
        Assert.That(services, Is.Empty);
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<NativeContext>(options => options.UseSqlite("Data Source=:memory:"));
        services.AddScoped<AdditionalDependency>();
        services.AddSingleton<Probe>();
        return services;
    }

    public class NativeContext(DbContextOptions<NativeContext> options) : DbContext(options);
    private sealed class UnusedFactory : IDbContextFactory<NativeContext>
    {
        public NativeContext CreateDbContext() => throw new AssertionException("Must not activate.");
    }
    // A derived context still must not match a provider of its parent context.
    public class OtherContext(DbContextOptions<NativeContext> options) : NativeContext(options);
    public sealed class Customer;
    public sealed class Probe { public int Activated { get; set; } }
    public sealed class AdditionalDependency : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    public class CustomerProvider : ProviderBase<Customer, string, NativeContext>, IAsyncDisposable
    {
        public AdditionalDependency Dependency { get; }
        public bool Disposed { get; private set; }
        public CustomerProvider(IDbContextFactory<NativeContext> factory, AdditionalDependency dependency, Probe probe)
            : base(factory)
        {
            Dependency = dependency;
            probe.Activated++;
        }
        public Task<int> QueryAsync(CancellationToken ct) => WithContextAsync((_, token) =>
        {
            Assert.That(token, Is.EqualTo(ct));
            return Task.FromResult(42);
        }, ct);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    // This implementation is abstract so it is only seen as an explicitly conflicting descriptor.
    public abstract class ReplacementProvider(IDbContextFactory<NativeContext> factory, AdditionalDependency dependency, Probe probe)
        : CustomerProvider(factory, dependency, probe);
    public sealed class OtherCustomerProvider(IDbContextFactory<NativeContext> factory)
        : ProviderBase<Customer, string, NativeContext>(factory);
    public abstract class AbstractProvider(IDbContextFactory<NativeContext> factory)
        : ProviderBase<Customer, string, NativeContext>(factory);
    public sealed class IndirectProvider(IDbContextFactory<NativeContext> factory) : AbstractProvider(factory);
    public sealed class OpenProvider<T>(IDbContextFactory<NativeContext> factory)
        : ProviderBase<T, int, NativeContext>(factory) where T : class;
    public sealed class ForeignProvider(IDbContextFactory<OtherContext> factory)
        : ProviderBase<Customer, int, OtherContext>(factory);
}
