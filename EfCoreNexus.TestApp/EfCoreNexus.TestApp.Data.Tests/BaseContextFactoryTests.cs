using EfCoreNexus.Framework.Context;
using EfCoreNexus.TestApp.Data.Context;
using EfCoreNexus.TestApp.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace EfCoreNexus.TestApp.Data.Tests;

public class BaseContextFactoryTests
{
    [Test]
    public void ParameterlessFactoryInitializesOptionsAndDiscoversContextConfigurations()
    {
        var factory = new SqliteMainContextFactory();

        Assert.That(factory.AssemblyConfiguration.AssemblyData, Is.EqualTo(typeof(MainContext).Assembly));
        Assert.That(factory.ConfigurationTypes, Does.Contain(typeof(MainContext).Assembly.GetType(
            "EfCoreNexus.TestApp.Data.Configurations.TestConfiguration")));

        using var context = factory.CreateDbContext([]);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        var entity = new Test { TestId = Guid.NewGuid() };
        context.Add(entity);
        context.SaveChanges();

        Assert.That(context.Set<Test>().Single().TestId, Is.EqualTo(entity.TestId));
    }

    private sealed class SqliteMainContextFactory : BaseContextFactory<MainContext>
    {
        public IEnumerable<Type> ConfigurationTypes => EntityConfigurations.Select(configuration => configuration.GetType());

        public SqliteMainContextFactory()
        {
            OptionsBuilder.UseSqlite("DataSource=:memory:");
        }

        public override MainContext CreateDbContext() => new(OptionsBuilder.Options, EntityConfigurations);
    }
}
