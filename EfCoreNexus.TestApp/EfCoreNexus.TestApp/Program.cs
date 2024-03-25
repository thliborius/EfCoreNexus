using EfCoreNexus.Framework.Helper;
using EfCoreNexus.TestApp.Components;
using EfCoreNexus.TestApp.Data.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCoreNexus.TestApp
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents()
                .AddInteractiveWebAssemblyComponents();

            ConfigureDataservice(builder.Services);

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.UseWebAssemblyDebugging();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseHttpsRedirection();

            app.UseStaticFiles();
            app.UseAntiforgery();

            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode()
                .AddInteractiveWebAssemblyRenderMode();

            app.Run();
        }

        private static void ConfigureDataservice(IServiceCollection services)
        {
            var connectionString = "Server=.;Initial Catalog=EfCoreNexus;Persist Security Info=False;Integrated Security=true;MultipleActiveResultSets=True;Encrypt=false;TrustServerCertificate=true;Connection Timeout=30";

            var optionsBuilder = new DbContextOptionsBuilder<MainContext>();
            optionsBuilder.UseSqlServer(connectionString);
            optionsBuilder.ConfigureWarnings(w => w.Ignore(SqlServerEventId.SavepointsDisabledBecauseOfMARS));
            optionsBuilder.UseSqlServer(opt => opt.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));

            var assemblyConf = new DataAssemblyConfiguration("EfCoreNexus.TestApp.Data");
            var ctxFactory = new MainContextFactory(assemblyConf, optionsBuilder);
            var startupConf = new StartupConfiguration<MainContext>(ctxFactory, optionsBuilder);

            startupConf.ConfigureDataservice(services);
        }
    }
}
