using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EfCoreNexus.Framework.Helper;

public abstract class EntityTypeConfigurationDependency
{
    public abstract void Configure(ModelBuilder modelBuilder);
}

public abstract class EntityTypeConfigurationDependency<TEntity> : EntityTypeConfigurationDependency, IEntityTypeConfiguration<TEntity>
    where TEntity : class
{
    public abstract void Configure(EntityTypeBuilder<TEntity> builder);

    public override void Configure(ModelBuilder modelBuilder)
    {
        Configure(modelBuilder.Entity<TEntity>());
    }
}