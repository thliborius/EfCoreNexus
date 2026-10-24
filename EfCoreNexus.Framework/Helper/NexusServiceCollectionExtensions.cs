using System.Reflection;
using EfCoreNexus.Framework.Provider;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EfCoreNexus.Framework.Helper;

public static class NexusServiceCollectionExtensions
{
    /// <summary>
    /// Registers closed concrete providers for exactly TContext as scoped services.
    /// Repeated scans and an existing scoped self-registration are idempotent.
    /// Other unkeyed registrations for a discovered concrete service are conflicts.
    /// Constructors and their dependencies are resolved by ordinary .NET DI.
    /// </summary>
    public static IServiceCollection AddNexusProviders<TContext>(this IServiceCollection services,
        params Assembly[] assemblies) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);
        if (assemblies.Length == 0 || assemblies.Any(assembly => assembly is null))
            throw new ArgumentException("Supply at least one non-null provider assembly.", nameof(assemblies));

        var providers = assemblies.Distinct().SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters
                && HasContextBase<TContext>(type))
            .Distinct().OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();

        // Validate the whole scan before mutating the collection. Never choose a descriptor by order.
        foreach (var provider in providers)
        {
            var existing = services.Where(descriptor => !descriptor.IsKeyedService
                && descriptor.ServiceType == provider).ToArray();
            if (existing.Length == 0) continue;
            if (existing.Length == 1 && existing[0].Lifetime == ServiceLifetime.Scoped
                && existing[0].ImplementationType == provider) continue;

            var registrations = string.Join(", ", existing.Select(descriptor =>
                $"{descriptor.Lifetime} {descriptor.ImplementationType?.FullName
                    ?? (descriptor.ImplementationFactory is not null ? "factory" : "instance")}"));
            throw new InvalidOperationException(
                $"Conflicting registrations for provider '{provider.FullName}' in context '{typeof(TContext).FullName}': "
                + $"{registrations}. Expected one scoped concrete self-registration.");
        }

        foreach (var provider in providers)
        {
            if (!services.Any(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == provider))
                services.AddScoped(provider);
        }

        return services;
    }

    private static bool HasContextBase<TContext>(Type type) where TContext : DbContext
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(ProviderBase<,,>))
                return current.GenericTypeArguments[2] == typeof(TContext);
        }
        return false;
    }
}
