using EfCoreNexus.Framework.Entities;
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
        private DataService<MainContext> _dataService = default!;
        private MainContext _context = default!;

        [SetUp]
        public void Setup()
        {
            // This creates the SQLite in-memory database, which will persist until the connection is closed at the end of the test (see Dispose below).
            _connection.Open();

            // These options will be used by the context instances in this test suite, including the connection opened above.
            _optionsBuilder = new DbContextOptionsBuilder<MainContext>().UseSqlite(_connection);

            var ctxFactory = new MainContextFactory(_assemblyConf, _optionsBuilder);
            var providerFactory = new ProviderFactory<MainContext>(_assemblyConf);
            _context = ctxFactory.CreateDbContext();

            var dbExists = _context.Database.EnsureCreated();
            Assert.That(dbExists, Is.True);

            _dataService = new DataService<MainContext>(ctxFactory, providerFactory);
        }

        [TearDown]
        public void TearDown()
        {
            _connection.Dispose();
            _context.Dispose();
        }

        /// <summary>
        /// Test the dbContext without any provider
        /// </summary>
        [Test]
        public void TestContext()
        {
            _context.Add(new Test
            {
                TestId = Guid.NewGuid(),
                CurrentDate = DateTime.Now,
                Content = $"TestApp entry {DateTime.Now:F}",
                Active = true
            });
            _context.SaveChanges();
        }

        private Test CreateNewEntity()
        {
            return new Test
            {
                TestId = Guid.NewGuid(),
                CurrentDate = DateTime.Now,
                Content = $"TestApp entry {DateTime.Now:F}",
                Active = true
            };
        }

        [Test]
        public void GetProviderCrud()
        {
            var p = _dataService.GetProviderCrud<Test, Guid>();
            Assert.That(p, Is.InstanceOf<TestProvider>());
        }

        [Test]
        public void GetProvider()
        {
            var p = _dataService.GetProvider<TestProvider>();
            Assert.That(p, Is.InstanceOf<TestProvider>());
        }

        [Test]
        public void GetProviderFail()
        {
            Assert.Throws<ArgumentException>(() => _dataService.GetProvider<FakeProvider>());
            Assert.Throws<ArgumentException>(() => _dataService.GetProviderCrud<FakeEntity, Guid>());
        }

        [Test]
        public async Task GetActiveOrderedByDate()
        {
            var p = _dataService.GetProvider<TestProvider>();

            var newEntity = CreateNewEntity();

            // example for using a transaction
            newEntity.TestId = Guid.NewGuid();
            await p.DeactivateAndCreate(newEntity);

            var lst = await p.GetActiveOrderedByDate();

            Assert.That(lst.Count, Is.EqualTo(1));
            Assert.That(lst[0].TestId, Is.EqualTo(newEntity.TestId));
        }

        [Test]
        public async Task Create()
        {
            var p = _dataService.GetProvider<TestProvider>();

            var newEntity = CreateNewEntity();

            await p.Create(newEntity, newEntity.TestId);

            var lst = await p.GetAllAsync();

            Assert.That(lst.Count, Is.EqualTo(1));
            Assert.That(lst[0].TestId, Is.EqualTo(newEntity.TestId));
        }

        [Test]
        public async Task CreateAlreadyExists()
        {
            var p = _dataService.GetProvider<TestProvider>();

            var newEntity = CreateNewEntity();

            await p.Create(newEntity, newEntity.TestId);

            Assert.ThrowsAsync<Exception>(async () => await p.Create(newEntity, newEntity.TestId));
        }

        [Test]
        public async Task GetById()
        {
            var p = _dataService.GetProvider<TestProvider>();

            var newEntity = CreateNewEntity();

            await p.Create(newEntity, newEntity.TestId);

            var test = await p.GetById(newEntity.TestId);
            Assert.That(test, Is.Not.Null);
            Assert.That(test?.TestId, Is.EqualTo(newEntity.TestId));

            test = await p.GetById(Guid.NewGuid());
            Assert.That(test, Is.Null);
        }

        [Test]
        public async Task Delete()
        {
            var p = _dataService.GetProvider<TestProvider>();

            var newEntity = CreateNewEntity();

            await p.Create(newEntity, newEntity.TestId);

            var test = await p.GetById(newEntity.TestId);
            Assert.That(test, Is.Not.Null);
            Assert.That(test?.TestId, Is.EqualTo(newEntity.TestId));

            await p.Delete(newEntity.TestId);

            test = await p.GetById(newEntity.TestId);
            Assert.That(test, Is.Null);
        }

        [Test]
        public async Task Update()
        {
            var p = _dataService.GetProvider<TestProvider>();

            var newEntity = CreateNewEntity();

            await p.Create(newEntity, newEntity.TestId);

            var test = await p.GetById(newEntity.TestId);
            Assert.That(test, Is.Not.Null);
            Assert.That(test?.TestId, Is.EqualTo(newEntity.TestId));

            var s = "hasChanged";
            test.Content = s;

            await p.Update(test, test.TestId);

            var changed = await p.GetById(newEntity.TestId);
            Assert.That(changed, Is.Not.Null);
            Assert.That(changed?.Content, Is.EqualTo(s));
        }

        [Test]
        public async Task UpdateFail()
        {
            var p = _dataService.GetProvider<TestProvider>();

            var newEntity = CreateNewEntity();

            await p.Create(newEntity, newEntity.TestId);

            await p.Delete(newEntity.TestId);

            Assert.ThrowsAsync<Exception>(async () => await p.Update(newEntity, newEntity.TestId));
        }

        [Test]
        public void GetAll()
        {
            var p = _dataService.GetProvider<TestProvider>();

            var lst = p.GetAll();

            Assert.That(lst.Count, Is.EqualTo(0));
        }

        [Test]
        public async Task Transaction()
        {
            _dataService.BeginTransaction();

            try
            {
                await Update();

                await _dataService.CommitTransaction();
            }
            finally
            {
                await _dataService.DisposeTransaction();
            }
        }

        [Test]
        public async Task TransactionFails()
        {
            _dataService.BeginTransaction();

            Assert.Throws<Exception>(() => _dataService.BeginTransaction());

            try
            {
                await Update();

                await _dataService.CommitTransaction();
            }
            finally
            {
                await _dataService.DisposeTransaction();
            }

            Assert.ThrowsAsync<Exception>(async () => await _dataService.CommitTransaction());
        }

        private class FakeProvider(TransactionService<MainContext> transactionSvc) : ProviderBase<FakeEntity, Guid, MainContext>(transactionSvc);

        private class FakeEntity : IEntity;
    }
}