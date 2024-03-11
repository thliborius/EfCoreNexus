using EfCoreNexus.Framework.Services;
using EfCoreNexus.TestApp.Data.Context;
using EfCoreNexus.TestApp.Data.Entities;
using EfCoreNexus.TestApp.Data.Provider;
using Microsoft.AspNetCore.Components;

namespace EfCoreNexus.TestApp.Components.Pages;

public class HomeComponent : ComponentBase
{
    [Inject]
    protected DataService<MainContext> MainSvc { get; set; } = default!;

    protected IList<Test> TestList { get; set; } = new List<Test>();

    protected override async Task OnInitializedAsync()
    {
        var p = MainSvc.GetProvider<TestProvider>();

        var newEntity = new Test
        {
            TestId = Guid.NewGuid(),
            CurrentDate = DateTime.Now,
            Content = $"TestApp entry {DateTime.Now:F}",
            Active = true
        };

        await p.Create(newEntity, newEntity.TestId);

        // example for using a transaction
        newEntity.TestId = Guid.NewGuid();
        await p.DeactivateAndCreate(newEntity);

        TestList = await p.GetActiveOrderedByDate();
    }
}