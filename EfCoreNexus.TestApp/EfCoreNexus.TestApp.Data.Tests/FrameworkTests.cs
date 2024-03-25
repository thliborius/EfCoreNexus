using EfCoreNexus.Framework.Helper;
using EfCoreNexus.TestApp.Data.Context;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EfCoreNexus.TestApp.Data.Tests;

public class FrameworkTests
{
    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public void TestContext()
    {
        Assert.Throws<Exception>(() =>
        {
            var dac = new DataAssemblyConfiguration("not.available");
        });
    }

    [Test]
    public void StartupConfiguration()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var optionsBuilder = new DbContextOptionsBuilder<MainContext>().UseSqlite(connection);
        var assemblyConf = new DataAssemblyConfiguration("EfCoreNexus.TestApp.Data");
        var ctxFactory = new MainContextFactory(assemblyConf, optionsBuilder);

        var sc = new StartupConfiguration<MainContext>(ctxFactory, optionsBuilder);
        var services = new ServiceCollection();
        sc.ConfigureDataservice(services);

        Assert.That(services.Count, Is.EqualTo(6));

        connection.Dispose();
    }
}