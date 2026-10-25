using System.Data;
using System.Text.RegularExpressions;
using EfCoreNexus.Framework.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NUnit.Framework;
using Testcontainers.MsSql;

namespace EfCoreNexus.SqlServer.Tests;

[TestFixture, NonParallelizable]
public class SqlServerTests
{
    // Verified against MCR on 2026-10-25. See docs/sql-server-tests.md for selection and updates.
    private const string Image = "mcr.microsoft.com/mssql/server:2025-CU10-ubuntu-24.04"
        + "@sha256:245f6b1c8903edae52cb20fa31419cefcde1a49938bb8c3447099011fc871ca3";

    private MsSqlContainer? _container;
    private IDbContextFactory<SqlServerContext> _factory = null!;
    private Customers _customers = null!;
    private Orders _orders = null!;
    private Commands _commands = null!;

    [OneTimeSetUp]
    public async Task StartSqlServer()
    {
        try
        {
            // MsSqlBuilder accepts ACCEPT_EULA=Y inside this disposable test container only.
            // Its native defaults bind 1433 to a random host port. Never reuse across test runs.
            _container = new MsSqlBuilder(Image)
                .WithPassword($"Nexus!{Guid.NewGuid():N}")
                .WithEnvironment("MSSQL_PID", "Developer")
                .WithEnvironment("MSSQL_MEMORY_LIMIT_MB", "2048")
                .Build();
            using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await _container.StartAsync(startup.Token);
            await using var connection = new SqlConnection(_container.GetConnectionString());
            await connection.OpenAsync(startup.Token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT @@VERSION";
            TestContext.Progress.WriteLine(await command.ExecuteScalarAsync(startup.Token));
        }
        catch (Exception failure)
        {
            if (_container is not null)
            {
                try
                {
                    var logs = await _container.GetLogsAsync();
                    TestContext.Error.WriteLine(logs.Stdout + logs.Stderr);
                }
                catch (Exception logs) { failure.Data["ContainerLogException"] = logs; }
                try { await _container.DisposeAsync(); }
                catch (Exception cleanup) { failure.Data["ContainerCleanupException"] = cleanup; }
                _container = null;
            }
            throw new InvalidOperationException(
                $"SQL Server test setup failed for {Image}. A reachable Docker Engine with Linux amd64 "
                + "containers and enough memory is required. These tests cannot run without SQL Server.", failure);
        }
    }

    [OneTimeTearDown]
    public async Task StopSqlServer()
    {
        if (_container is not null) await _container.DisposeAsync();
    }

    [SetUp]
    public async Task CreateIsolatedDatabase()
    {
        var connection = new SqlConnectionStringBuilder(_container!.GetConnectionString())
        {
            InitialCatalog = $"nexus_{Guid.NewGuid():N}",
            Pooling = false
        };
        _commands = new Commands();
        _factory = new SqlServerFactory(new DbContextOptionsBuilder<SqlServerContext>()
            .UseSqlServer(connection.ConnectionString).AddInterceptors(_commands).Options);
        _customers = new Customers(_factory);
        _orders = new Orders(_factory);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        _commands.Executed.Clear();
    }

    [TearDown]
    public async Task DropIsolatedDatabase()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureDeletedAsync();
    }

    [Test]
    public async Task CrudReturnsIdentityKeysAndIdDeleteUsesCurrentVersionOrNoOpsWhenMissing()
    {
        var row = new Customer { Name = "created" };
        await _customers.CreateAsync(row);
        Assert.That(row.Id, Is.GreaterThan(0));
        var read = (await _customers.GetByIdAsync(row.Id))!;
        Assert.That(read, Is.Not.SameAs(row));
        Assert.That(read.Name, Is.EqualTo("created"));
        Assert.That(read.Version, Is.EqualTo(row.Version));

        read.Name = "new current version";
        await _customers.UpdateAsync(read);
        var current = (await _customers.GetByIdAsync(row.Id))!;
        Assert.That(current.Version, Is.Not.EqualTo(row.Version));
        _commands.Executed.Clear();
        await _customers.DeleteAsync(row.Id); // No snapshot: uses the current database token.
        AssertVersionPredicate("DELETE", current.Id, current.Version);
        Assert.That(await _customers.GetByIdAsync(row.Id), Is.Null);

        _commands.Executed.Clear();
        await _customers.DeleteAsync(row.Id);
        Assert.That(Writes("DELETE"), Is.Empty);
        await using var check = await _factory.CreateDbContextAsync();
        Assert.That(await check.Set<Customer>().CountAsync(), Is.Zero);
    }

    [Test]
    public async Task RowversionGuardsDetachedUpdatesAndEntityDeletesWithOriginalBinaryParameters()
    {
        var row = new Customer { Name = "original" };
        await _customers.CreateAsync(row);
        Assert.That(row.Version, Has.Length.EqualTo(8));
        Assert.That(row.Version, Is.Not.EqualTo(new byte[8]));
        var first = (await _customers.GetByIdAsync(row.Id))!;
        var stale = (await _customers.GetByIdAsync(row.Id))!;
        Assert.That(first.Version, Is.EqualTo(stale.Version));
        Assert.That(first.Version, Is.Not.SameAs(stale.Version));
        var originalToken = first.Version.ToArray();

        first.Name = "winner";
        _commands.Executed.Clear();
        await _customers.UpdateAsync(first);
        AssertVersionPredicate("UPDATE", row.Id, originalToken);
        var current = (await _customers.GetByIdAsync(row.Id))!;
        Assert.That(current.Name, Is.EqualTo("winner"));
        Assert.That(current.Version, Has.Length.EqualTo(8).And.Not.EqualTo(originalToken));
        Assert.That(first.Version, Is.EqualTo(originalToken), "Detached input is not refreshed; read again.");

        stale.Name = "stale overwrite";
        _commands.Executed.Clear();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => _customers.UpdateAsync(stale));
        AssertVersionPredicate("UPDATE", row.Id, originalToken);
        _commands.Executed.Clear();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => _customers.DeleteAsync(stale));
        AssertVersionPredicate("DELETE", row.Id, originalToken);
        Assert.That(_commands.Executed.Any(c => c.Sql.Contains("SELECT", StringComparison.Ordinal)), Is.False);

        await using (var check = await _factory.CreateDbContextAsync())
        {
            var stored = await check.Set<Customer>().AsNoTracking().SingleAsync();
            Assert.That(stored.Name, Is.EqualTo("winner"));
            Assert.That(stored.Version, Is.EqualTo(current.Version));
        }
        _commands.Executed.Clear();
        await _customers.DeleteAsync(current);
        AssertVersionPredicate("DELETE", row.Id, current.Version);
        Assert.That(_commands.Executed.Any(c => c.Sql.Contains("SELECT", StringComparison.Ordinal)), Is.False);
        Assert.That(await _customers.GetByIdAsync(row.Id), Is.Null);
    }

    [Test]
    public async Task UnchangedUpdateIssuesNoUpdateAndDoesNotAssertEvenAStaleRowversion()
    {
        var row = new Customer { Name = "original" };
        await _customers.CreateAsync(row);
        var snapshot = (await _customers.GetByIdAsync(row.Id))!;
        _commands.Executed.Clear();
        await _customers.UpdateAsync(snapshot);
        Assert.That(Writes("UPDATE"), Is.Empty);
        Assert.That((await _customers.GetByIdAsync(row.Id))!.Version, Is.EqualTo(snapshot.Version));

        row.Name = "updated elsewhere";
        await _customers.UpdateAsync(row);
        var current = (await _customers.GetByIdAsync(row.Id))!;
        Assert.That(current.Version, Is.Not.EqualTo(snapshot.Version));
        snapshot.Name = current.Name; // All writable scalars match; expected version remains stale.
        _commands.Executed.Clear();
        await _customers.UpdateAsync(snapshot);
        Assert.That(Writes("UPDATE"), Is.Empty);
        Assert.That((await _customers.GetByIdAsync(row.Id))!.Version, Is.EqualTo(current.Version));
    }

    [Test]
    public async Task RunnerCommitsTwoProvidersImmediateSavesAndTheFinalSaveOnOneNativeTransaction()
    {
        var customer = new Customer { Name = "before" };
        var order = new Order();
        await new TransactionRunner<SqlServerContext>(_factory).ExecuteAsync(async (db, ct) =>
        {
            var transaction = db.Database.CurrentTransaction!;
            Assert.That(transaction.GetDbTransaction(), Is.InstanceOf<SqlTransaction>());
            Assert.That(await db.Database.SqlQueryRaw<int>("SELECT @@TRANCOUNT AS [Value]").SingleAsync(ct), Is.EqualTo(1));
            await _customers.CreateAsync(db, customer, ct);
            var originalToken = customer.Version.ToArray();
            customer.Name = "committed";
            await _customers.UpdateAsync(db, customer, ct);
            Assert.That(customer.Version, Is.Not.EqualTo(originalToken), "Tracked inputs refresh rowversion.");
            order.CustomerId = customer.Id;
            await _orders.CreateAsync(db, order, ct);
            Assert.That(order.Id, Is.GreaterThan(0));
            db.Add(new Audit { Message = "explicit save" });
            await db.SaveChangesAsync(ct);
            Assert.That(await db.Set<Order>().AsNoTracking().CountAsync(ct), Is.EqualTo(1));
            Assert.That(db.Database.CurrentTransaction, Is.SameAs(transaction));
            db.Add(new Audit { Message = "runner final save" });
        });

        await using var check = await _factory.CreateDbContextAsync();
        var stored = await check.Set<Customer>().AsNoTracking().SingleAsync();
        Assert.That(stored.Id, Is.EqualTo(customer.Id));
        Assert.That(stored.Name, Is.EqualTo("committed"));
        Assert.That(stored.Version, Is.EqualTo(customer.Version));
        Assert.That((await check.Set<Order>().SingleAsync()).CustomerId, Is.EqualTo(customer.Id));
        Assert.That(await check.Set<Audit>().Select(x => x.Message).ToListAsync(),
            Is.EquivalentTo(new[] { "explicit save", "runner final save" }));
    }

    [TestCase("callback")]
    [TestCase("provider-save")]
    [TestCase("final-save")]
    public async Task RunnerRollsBackAlreadySavedChangesAfterCallbackOrDatabaseFailure(string failureAt)
    {
        var seed = new Customer { Name = "keep" };
        await _customers.CreateAsync(seed);
        var edit = (await _customers.GetByIdAsync(seed.Id))!;
        edit.Name = "roll back this update";
        var created = new Customer { Name = "roll back this insert" };
        var failure = new InvalidOperationException("callback failed after actual saves");

        async Task Operation(SqlServerContext db, CancellationToken ct)
        {
            Assert.That(db.Database.CurrentTransaction!.GetDbTransaction(), Is.InstanceOf<SqlTransaction>());
            await _customers.UpdateAsync(db, edit, ct);
            await _customers.CreateAsync(db, created, ct);
            await _orders.CreateAsync(db, new Order { CustomerId = created.Id }, ct);
            db.Add(new Audit { Message = "explicitly saved before failure" });
            await db.SaveChangesAsync(ct);
            Assert.That(await db.Set<Customer>().AsNoTracking().CountAsync(ct), Is.EqualTo(2));
            Assert.That(await db.Set<Order>().AsNoTracking().CountAsync(ct), Is.EqualTo(1));
            Assert.That(await db.Set<Audit>().AsNoTracking().CountAsync(ct), Is.EqualTo(1));
            if (failureAt == "callback") throw failure;
            var invalidOrder = new Order { CustomerId = int.MaxValue }; // Real FK violation.
            if (failureAt == "provider-save") await _orders.CreateAsync(db, invalidOrder, ct);
            else db.Add(invalidOrder); // Fails in the runner's final SaveChangesAsync.
        }

        var runner = new TransactionRunner<SqlServerContext>(_factory);
        if (failureAt == "callback")
            Assert.That(await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ExecuteAsync(Operation)), Is.SameAs(failure));
        else
        {
            var thrown = await Assert.ThrowsAsync<DbUpdateException>(() => runner.ExecuteAsync(Operation));
            Assert.That(thrown!.InnerException, Is.InstanceOf<SqlException>());
            Assert.That(((SqlException)thrown.InnerException!).Number, Is.EqualTo(547));
        }
        Assert.That(created.Id, Is.GreaterThan(0), "Generated in-memory keys survive rollback.");
        await using var check = await _factory.CreateDbContextAsync();
        var stored = await check.Set<Customer>().AsNoTracking().SingleAsync();
        Assert.That(stored.Id, Is.EqualTo(seed.Id));
        Assert.That(stored.Name, Is.EqualTo("keep"));
        Assert.That(stored.Version, Is.EqualTo(seed.Version));
        Assert.That(await check.Set<Order>().CountAsync(), Is.Zero);
        Assert.That(await check.Set<Audit>().CountAsync(), Is.Zero);
    }

    private IEnumerable<Commands.Command> Writes(string verb)
        => _commands.Executed.Where(c => Regex.IsMatch(c.Sql, $@"\b{verb}\s+(?:FROM\s+)?\[Customers\]"));

    private void AssertVersionPredicate(string verb, int id, byte[] expectedToken)
    {
        var command = Writes(verb).Single();
        var where = command.Sql[command.Sql.IndexOf("WHERE", StringComparison.Ordinal)..];
        var tokenParameter = Regex.Match(where, @"\[Version\] = (@\w+)");
        var keyParameter = Regex.Match(where, @"\[Id\] = (@\w+)");
        Assert.That(tokenParameter.Success && keyParameter.Success, Is.True, command.Sql);
        var token = command.Parameters[tokenParameter.Groups[1].Value];
        Assert.That(token.Type, Is.EqualTo(DbType.Binary));
        Assert.That(token.Value, Is.EqualTo(expectedToken));
        Assert.That(command.Parameters[keyParameter.Groups[1].Value].Value, Is.EqualTo(id));
        if (verb == "UPDATE")
        {
            var set = command.Sql[command.Sql.IndexOf("SET", StringComparison.Ordinal)..command.Sql.IndexOf("OUTPUT", StringComparison.Ordinal)];
            Assert.That(set, Does.Contain("[Name] =").And.Not.Contain("[Version] =").And.Not.Contain("[Id] ="));
            Assert.That(command.Sql, Does.Contain("OUTPUT INSERTED.[Version]"));
        }
        TestContext.Out.WriteLine($"{command.Sql}\nExpected rowversion parameter {tokenParameter.Groups[1].Value}: "
            + Convert.ToHexString((byte[])token.Value!));
    }
}
