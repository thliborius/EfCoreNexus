using EfCoreNexus.Framework.Services;
using EfCoreNexus.TestApp.Data.Context;
using EfCoreNexus.TestApp.Data.Entities;
using EfCoreNexus.TestApp.Data.Provider;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;

namespace EfCoreNexus.TestApp.Components.Pages;

public class HomeComponent : ComponentBase
{
    [Inject] protected TestProvider Tests { get; set; } = default!;
    [Inject] protected MemoProvider MemoProvider { get; set; } = default!;
    [Inject] protected IDbContextFactory<MainContext> Factory { get; set; } = default!;
    [Inject] protected TransactionRunner<MainContext> Transactions { get; set; } = default!;
    [Inject] protected ILogger<HomeComponent> Logger { get; set; } = default!;
    protected List<Test> TestList { get; set; } = [];
    protected List<Memo> Memos { get; set; } = [];
    protected Test? Edit { get; set; }
    protected Memo? MemoEdit { get; set; }
    protected string NewContent { get; set; } = "Hello EF Core";
    protected string Status { get; set; } = "Ready";
    protected bool Busy { get; set; }
    protected bool Interactive { get; set; }

    protected override void OnAfterRender(bool firstRender)
    {
        if (!firstRender) return;
        Interactive = true;
        StateHasChanged();
    }

    // Prerender and interactive initialization only read. Writes require a user event.
    protected override Task OnInitializedAsync() => ReloadAsync();
    private async Task ReadAsync()
    {
        TestList = await Tests.GetAllAsync();
        Memos = await MemoProvider.GetAllAsync();
    }
    protected Task ReloadAsync() => RunAsync(async () =>
    {
        await ReadAsync();
        Edit = null;
        MemoEdit = null;
    });
    protected Task ShowActiveAsync() => RunAsync(async () =>
        TestList = await Tests.GetActiveOrderedByDateAsync());
    private Test NewEntry() => new() { Content = NewContent, CurrentDate = DateTime.UtcNow, Active = true };
    protected Task CreateAsync() => RunAsync(async () =>
    {
        await Tests.CreateAsync(NewEntry());
        await ReadAsync();
    });
    protected Task EditAsync(Guid id) => RunAsync(async () =>
        Edit = await Tests.GetByIdAsync(id) ?? throw new KeyNotFoundException());
    protected Task SaveAsync() => RunAsync(async () =>
    {
        await Tests.UpdateAsync(Edit!);
        Edit = null;
        await ReadAsync();
    });
    protected Task DeleteAsync(Guid id) => RunAsync(async () =>
    {
        await Tests.DeleteAsync(id);
        if (Edit?.TestId == id) Edit = null;
        await ReadAsync();
    });
    protected Task EditMemoAsync(int id) => RunAsync(async () =>
        MemoEdit = await MemoProvider.GetByIdAsync(id) ?? throw new KeyNotFoundException());
    protected Task SaveMemoAsync() => RunAsync(async () =>
    {
        await MemoProvider.RenameAsync(MemoEdit!.Id, MemoEdit.Text, MemoEdit.Revision);
        MemoEdit = await MemoProvider.GetByIdAsync(MemoEdit.Id);
        await ReadAsync();
    });
    protected Task DeleteMemoAsync() => RunAsync(async () =>
    {
        await MemoProvider.DeleteAsync(MemoEdit!);
        MemoEdit = null;
        await ReadAsync();
    });
    protected Task NativeDeactivateAsync() => RunAsync(async () =>
    {
        await using var db = await Factory.CreateDbContextAsync();
        foreach (var row in await db.Set<Test>().Where(x => x.Active).ToListAsync()) row.Active = false;
        await db.SaveChangesAsync(); // One save already has a native EF transaction.
        await ReadAsync();
    });
    protected Task CreatePairAsync() => RunAsync(async () =>
    {
        await Transactions.ExecuteAsync(async (db, ct) =>
        {
            var entry = NewEntry();
            await Tests.CreateAsync(db, entry, ct);
            await MemoProvider.CreateAsync(db, new Memo { Text = $"Created entry {entry.TestId}" }, ct);
        });
        await ReadAsync();
    });
    private async Task RunAsync(Func<Task> action)
    {
        if (Busy) return;
        Busy = true;
        try { await action(); Status = "Done"; }
        catch (DbUpdateConcurrencyException) { Status = "Conflict: changed or deleted in another tab. Reload before retrying."; }
        catch (KeyNotFoundException) { Status = "The row was deleted. Reload to continue."; }
        catch (Exception error)
        {
            Logger.LogError(error, "Sample operation failed");
            Status = "Operation failed. Reload and check the server log; do not blindly repeat a transaction.";
        }
        finally { Busy = false; }
    }
}
