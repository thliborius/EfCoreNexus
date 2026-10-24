using EfCoreNexus.Framework.Helper;
using EfCoreNexus.Framework.Services;
using EfCoreNexus.TestApp.Components.Pages;
using EfCoreNexus.TestApp.Data.Context;
using EfCoreNexus.TestApp.Data.Entities;
using EfCoreNexus.TestApp.Data.Provider;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EfCoreNexus.TestApp.Data.Tests;

public class SampleIntegrationTests
{
    private SqliteConnection _keeper = null!;
    private ServiceProvider _services = null!;
    private AsyncServiceScope _scope;
    private TestProvider Tests => _scope.ServiceProvider.GetRequiredService<TestProvider>();
    private MemoProvider Memos => _scope.ServiceProvider.GetRequiredService<MemoProvider>();
    private TransactionRunner<MainContext> Runner => _scope.ServiceProvider.GetRequiredService<TransactionRunner<MainContext>>();
    private IDbContextFactory<MainContext> Factory => _services.GetRequiredService<IDbContextFactory<MainContext>>();

    [SetUp]
    public async Task Setup()
    {
        _keeper = new SqliteConnection($"Data Source={Guid.NewGuid()};Mode=Memory;Cache=Shared");
        await _keeper.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<MainContext>(o => o.UseSqlite(_keeper.ConnectionString));
        services.AddNexusProviders<MainContext>(typeof(MainContext).Assembly);
        services.AddScoped<TransactionRunner<MainContext>>();
        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        _scope = _services.CreateAsyncScope();
        await using var db = await Factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public async Task Cleanup()
    {
        await _scope.DisposeAsync();
        await _services.DisposeAsync();
        await _keeper.DisposeAsync();
    }

    [Test]
    public async Task DiscoveredProvidersAndEntitiesSupportCrudAndCustomQuery()
    {
        var entry = new Test { Content = "first", Active = true };
        await Tests.CreateAsync(entry);
        Assert.That(entry.TestId, Is.Not.EqualTo(Guid.Empty));
        var edit = (await Tests.GetByIdAsync(entry.TestId))!;
        edit.Content = "edited";
        await Tests.UpdateAsync(edit);
        Assert.That((await Tests.GetActiveOrderedByDateAsync()).Single().Content, Is.EqualTo("edited"));
        await Tests.DeleteAsync(entry.TestId);
        Assert.That(await Tests.GetAllAsync(), Is.Empty);
        Assert.That(await Tests.GetByIdAsync(entry.TestId), Is.Null);
        await Memos.CreateAsync(new Memo { Text = "marker only" });
        Assert.That((await Memos.GetAllAsync()).Single().Id, Is.GreaterThan(0));
    }

    [Test]
    public async Task MemoRejectsStaleEditAndDeleteThenFreshDeleteWorks()
    {
        var memo = new Memo { Text = "original" };
        await Memos.CreateAsync(memo);
        var stale = (await Memos.GetByIdAsync(memo.Id))!;
        await Memos.RenameAsync(memo.Id, "winner", memo.Revision);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => Memos.RenameAsync(stale.Id, "loser", stale.Revision));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => Memos.DeleteAsync(stale));
        var fresh = (await Memos.GetByIdAsync(memo.Id))!;
        Assert.That(fresh.Text, Is.EqualTo("winner"));
        await Memos.DeleteAsync(fresh);
        Assert.That(await Memos.GetAllAsync(), Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task TwoProvidersCommitOrRollBackTogether(bool fail)
    {
        async Task Operation() => await Runner.ExecuteAsync(async (db, ct) =>
        {
            await Tests.CreateAsync(db, new Test { Content = "pair" }, ct);
            await Memos.CreateAsync(db, new Memo { Text = "pair" }, ct);
            if (fail) throw new InvalidOperationException("abort after both saves");
        });
        if (fail) await Assert.ThrowsAsync<InvalidOperationException>(Operation);
        else await Operation();
        Assert.That(await Tests.GetAllAsync(), Has.Count.EqualTo(fail ? 0 : 1));
        Assert.That(await Memos.GetAllAsync(), Has.Count.EqualTo(fail ? 0 : 1));
    }

    [Test]
    public async Task ComponentInitializationDoesNotWriteAndNativeActionPersists()
    {
        await Tests.CreateAsync(new Test { Content = "existing", Active = true });
        var page = new PageProbe(Tests, Memos, Factory, Runner);
        await page.InitializeAsync();
        await page.InitializeAsync(); // Prerender + interactive initialization.
        Assert.That(await Tests.GetAllAsync(), Has.Count.EqualTo(1));
        Assert.That(await Memos.GetAllAsync(), Is.Empty);
        await page.DeactivateAsync();
        Assert.That((await Tests.GetAllAsync()).Single().Active, Is.False);
    }

    private sealed class PageProbe : HomeComponent
    {
        public PageProbe(TestProvider tests, MemoProvider memos, IDbContextFactory<MainContext> factory,
            TransactionRunner<MainContext> runner)
        {
            Tests = tests; MemoProvider = memos; Factory = factory; Transactions = runner;
            Logger = NullLogger<HomeComponent>.Instance;
        }
        public Task InitializeAsync() => OnInitializedAsync();
        public Task DeactivateAsync() => NativeDeactivateAsync();
    }
}
