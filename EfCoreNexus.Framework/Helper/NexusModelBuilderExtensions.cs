using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using EfCoreNexus.Framework.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfCoreNexus.Framework.Helper;

public static class NexusModelBuilderExtensions
{
    /// <summary>
    /// Includes concrete, closed IEntity roots from the explicit assembly/model boundary.
    /// No entity is constructed. Excludes NotMapped, owned, complex and ignored types.
    /// Configure unannotated owned/complex types before discovery, then apply native
    /// IEntityTypeConfiguration mappings and final context overrides.
    /// The predicate limits discovery only; native EF navigation/configuration discovery still applies.
    /// </summary>
    public static ModelBuilder RegisterNexusEntities(this ModelBuilder model, Assembly assembly,
        Func<Type, bool>? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(assembly);

        var candidates = assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters
                && typeof(IEntity).IsAssignableFrom(type) && (predicate is null || predicate(type)))
            .OrderBy(type => type.FullName, StringComparer.Ordinal);

        foreach (var type in candidates)
        {
            if (type.IsDefined(typeof(NotMappedAttribute), inherit: true) || model.Model.IsIgnored(type)) continue;

            var owned = type.IsDefined(typeof(OwnedAttribute), inherit: true);
            var complex = type.IsDefined(typeof(ComplexTypeAttribute), inherit: true);
            if (owned && complex)
                throw new InvalidOperationException(
                    $"Entity candidate '{type.FullName}' is classified as both owned and complex. Use one EF classification.");

            if (owned || complex || model.Model.IsOwned(type)
                || model.Model.GetEntityTypes().Any(entity => entity.ClrType == type && entity.IsOwned())
                || model.Model.GetEntityTypes().Any(entity => ContainsComplexType(entity, type))) continue;

            if (model.Model.IsShared(type))
                throw new InvalidOperationException(
                    $"Entity candidate '{type.FullName}' is already a shared-type entity. Configure it explicitly instead of discovering a root.");

            model.Entity(type);
        }

        return model;
    }

    private static bool ContainsComplexType(IReadOnlyTypeBase parent, Type type)
        => parent.GetComplexProperties().Any(property => property.ComplexType.ClrType == type
            || ContainsComplexType(property.ComplexType, type));
}
