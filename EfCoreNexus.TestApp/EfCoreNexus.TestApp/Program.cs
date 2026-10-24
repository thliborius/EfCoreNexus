using EfCoreNexus.Framework.Helper;
using EfCoreNexus.Framework.Services;
using EfCoreNexus.TestApp.Components;
using EfCoreNexus.TestApp.Data.Context;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
var connection = builder.Configuration.GetConnectionString("Main")
    ?? throw new InvalidOperationException("ConnectionStrings:Main is required.");
builder.Services.AddDbContextFactory<MainContext>(options => options.UseSqlite(connection));
builder.Services.AddNexusProviders<MainContext>(typeof(MainContext).Assembly);
builder.Services.AddScoped<TransactionRunner<MainContext>>();
var app = builder.Build();

// Demo schema setup runs once at startup, never in component rendering. No seed writes.
await using (var db = await app.Services.GetRequiredService<IDbContextFactory<MainContext>>().CreateDbContextAsync())
    await db.Database.EnsureCreatedAsync();

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error");
app.MapStaticAssets();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public partial class Program;
