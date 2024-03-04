using EfCoreNexus.Framework.Helper;
using EfCoreNexus.TestApp.Data.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EfCoreNexus.TestApp.Data.Configurations;


internal class TestConfiguration : EntityTypeConfigurationDependency<Test>
{
    public override void Configure(EntityTypeBuilder<Test> builder)
    {
        builder.HasKey(x => x.TestId);
    }
}