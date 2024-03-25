using EfCoreNexus.Framework.Helper;
using EfCoreNexus.Framework.Provider;
using EfCoreNexus.Framework.Services;
using EfCoreNexus.TestApp.Data.Context;
using EfCoreNexus.TestApp.Data.Entities;
using EfCoreNexus.TestApp.Data.Provider;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EfCoreNexus.TestApp.Data.Tests
{
    public class TestProviderTests
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");
        private readonly DataAssemblyConfiguration _assemblyConf = new("EfCoreNexus.TestApp.Data");
        private DbContextOptionsBuilder<MainContext> _optionsBuilder = default!;

        [SetUp]
        public void Setup()
        {
            // This creates the SQLite in-memory database, which will persist until the connection is closed at the end of the test (see Dispose below).
            _connection.Open();

            // These options will be used by the context instances in this test suite, including the connection opened above.
            _optionsBuilder = new DbContextOptionsBuilder<MainContext>().UseSqlite(_connection);
        }

        [TearDown]
        public void TearDown()
        {
            _connection.Dispose();
        }

        /// <summary>
        /// Test the dbContext without any provider
        /// </summary>
        [Test]
        public void TestContext()
        {
            var ctxFactory = new MainContextFactory(_assemblyConf, _optionsBuilder);
            using var context = ctxFactory.CreateDbContext();

            var dbExists = context.Database.EnsureCreated();
            Assert.That(dbExists, Is.True);

            context.Add(new Test
            {
                TestId = Guid.NewGuid(),
                CurrentDate = DateTime.Now,
                Content = $"TestApp entry {DateTime.Now:F}",
                Active = true
            });
            context.SaveChanges();
        }

        /// <summary>
        /// Test the provider factory and test provider example functionalities
        /// </summary>
        [Test]
        public async Task TestProvider()
        {
            var ctxFactory = new MainContextFactory(_assemblyConf, _optionsBuilder);
            var providerFactory = new ProviderFactory<MainContext>(_assemblyConf);
            await using var context = ctxFactory.CreateDbContext();

            var dbExists = await context.Database.EnsureCreatedAsync();
            Assert.That(dbExists, Is.True);

            var dataService = new DataService<MainContext>(context, ctxFactory, providerFactory);

            var p = dataService.GetProvider<TestProvider>();

            var newEntity = new Test
            {
                TestId = Guid.NewGuid(),
                CurrentDate = DateTime.Now,
                Content = $"TestApp entry {DateTime.Now:F}",
                Active = true
            };

            await p.Create(newEntity, newEntity.TestId);

            newEntity.TestId = Guid.NewGuid();

            // example for using a transaction
            newEntity.TestId = Guid.NewGuid();
            await p.DeactivateAndCreate(newEntity);

            var lst = await p.GetActiveOrderedByDate();

            Assert.That(lst.Count, Is.EqualTo(1));
            Assert.That(lst[0].TestId, Is.EqualTo(newEntity.TestId));
        }
    }
}