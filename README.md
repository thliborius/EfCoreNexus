<!-- PROJECT SHIELDS -->
<!--
*** I'm using markdown "reference style" links for readability.
*** Reference links are enclosed in brackets [ ] instead of parentheses ( ).
*** See the bottom of this document for the declaration of the reference variables
*** for contributors-url, forks-url, etc. This is an optional, concise syntax you may use.
*** https://www.markdownguide.org/basic-syntax/#reference-style-links
-->
[![Contributors][contributors-shield]][contributors-url]
[![Forks][forks-shield]][forks-url]
[![Stargazers][stars-shield]][stars-url]
[![Issues][issues-shield]][issues-url]
[![MIT License][license-shield]][license-url]
[![Nuget][nuget-shield]][nuget-url]

# EfCoreNexus

<div align="center">
  <a href="https://github.com/thliborius/EfCoreNexus">
    <img src="logo.png" alt="Logo" width="120" height="120">
  </a>
</div>

Integrate the entity framework core into your blazor app with ease and less code.


## About The Project

**Tired of copy & pasting a lots of classes when creating a new table and connecting it to your blazor app? EfCoreNexus helps you integrating the entity framework core into your blazor app.**

With the help of reflection, the entity, the provider and the optional configuration classes getting registered. After it had been set up, it is really easy to add new table to your app. Now you only have to add two classes: the entity class and a provider class, which handle the CRUD operations. Simple operations are covered by the base class while you can add the specific ones. Additionally you can add a configuration for each entity.


## Development branches

- `main` contains the stable, published version and remains the default branch.
- `v2` is the development branch for the next major version, EfCoreNexus 2.0.
- Create feature branches from `v2` and integrate them through pull requests
  targeting `v2`. The .NET 10 baseline migration is the first such pull request.

## Build and test (.NET 10 baseline)

All four projects target `net10.0` and use EF Core 10.0.13. Install the stable
.NET 10 SDK (verified with 10.0.113 and runtime 10.0.13 on Linux Mint 22.3 /
Ubuntu 24.04). The SDK includes the .NET and ASP.NET Core runtimes needed by
this solution; no .NET 8 SDK/runtime or SQL Server installation is needed for tests.
Use a current .NET 10 servicing release. SDK-free app execution requires the
.NET 10 and ASP.NET Core 10 runtimes.

From the repository root, run these same commands in a Linux shell or Windows
PowerShell/Command Prompt:

```sh
dotnet --info
dotnet restore
dotnet build --no-restore
dotnet test --no-build --verbosity normal
dotnet list package --vulnerable --include-transitive
```

The tests create disposable SQLite in-memory databases. They do not start the
Blazor app, connect to SQL Server, or require `EFCORETOOLSDB`. GitHub Actions
runs these checks with .NET 10 and audits direct and transitive dependencies;
known vulnerabilities fail the restore step.

### Migration and compatibility notes

- This is the technical v2 baseline; the architecture work in issue #1 follows
  separately. The published v1.0.5 package is unchanged. Source/project-reference
  consumers now require .NET 10; this branch does not multi-target .NET 8.
- EF Core and the explicit ASP.NET Core package use the same 10.0.13 patch.
  Review the [EF Core 9](https://learn.microsoft.com/ef/core/what-is-new/ef-core-9.0/breaking-changes)
  and [EF Core 10 breaking changes](https://learn.microsoft.com/ef/core/what-is-new/ef-core-10.0/breaking-changes)
  before applying generated migrations to an existing database. No database
  migration is included in this baseline.
- `System.Linq.Dynamic.Core` is updated to 1.7.4, retaining parameterized key
  equality and the existing CRUD API. Its hardened parser defaults remain enabled;
  downstream custom dynamic queries should review the
  [upstream compatibility notes](https://github.com/zzzprojects/System.Linq.Dynamic.Core#breaking-changes).
- The data project explicitly references `System.Security.Cryptography.Pkcs`
  10.0.13 because the SQL Server provider's SqlClient dependency otherwise selects
  vulnerable 9.0.11. This override also protects the app and tests transitively.
- The parameterless `BaseContextFactory` constructor now initializes options and
  discovers entity configurations in `TContext`'s assembly, just as the explicit
  constructor does for its supplied assembly. Derived design-time factories must
  still configure a database provider and connection before creating a context.
- The test tooling uses current stable NUnit, adapter, analyzer, test SDK and
  Coverlet releases with the existing `dotnet test` / VSTest workflow. NUnit 5's
  `Assert.ThrowsAsync` returns a task, so asynchronous exception assertions are
  awaited.

## Run the sample app (requires SQL Server)

The Blazor sample uses SQL Server. Its connection string in `Program.cs` defaults
to a local Windows instance with integrated authentication. Adjust it for your
SQL Server host and authentication method, especially on Linux. The design-time
factory uses the separate `EFCORETOOLSDB` environment variable; changing that
variable does not change the app's runtime connection string.

Install matching stable EF tools once (or update an existing installation using
`dotnet tool update --global dotnet-ef --version 10.0.13`):

```sh
dotnet tool install --global dotnet-ef --version 10.0.13
```

Set a connection string to an empty SQL Server database in your current shell:

```sh
# Linux (bash)
export EFCORETOOLSDB='Server=your-server;Database=EfCoreNexus;User ID=your-user;Password=your-password;Encrypt=True'
```

```powershell
# Windows PowerShell
$env:EFCORETOOLSDB = 'Server=.;Database=EfCoreNexus;Integrated Security=True;TrustServerCertificate=True'
```

Then, from the repository root on either OS:

```sh
dotnet ef migrations add Init --project EfCoreNexus.TestApp/EfCoreNexus.TestApp.Data
dotnet ef database update --project EfCoreNexus.TestApp/EfCoreNexus.TestApp.Data
dotnet run --project EfCoreNexus.TestApp/EfCoreNexus.TestApp
```

On Windows Command Prompt, the existing `migrations_connectionstring.bat`,
`migrations_add.bat Init` and `migrations_updatedb.bat` can alternatively be used
from the data project directory after adjusting their connection string.

## How to use it in your project

These are the steps that are neccessary to hook up the entity framework via EfCoreNexus with your blazor app.

 - Add a reference to the EfCoreNexus.Framework library or install the nuget package.

- Add a DbContext and DbContextFactory class, derived from EfCoreNexus base classes BaseContext and BaseContextFactory.
```
public class MainContext(DbContextOptions<MainContext> options, IEnumerable<EntityTypeConfigurationDependency> configurations) 
	: BaseContext<MainContext>(options, configurations)
{
}
```
```
public class MainContextFactory : BaseContextFactory<MainContext>
{
    public MainContextFactory()
    {    
        // only necessary when you want to use migrations, see implementation in project
    }

    public MainContextFactory(DataAssemblyConfiguration assemblyConf, DbContextOptionsBuilder<MainContext> optionsBuilder)
        : base(assemblyConf, optionsBuilder)    
    {
    }

    public override MainContext CreateDbContext()
    {
        return new MainContext(OptionsBuilder.Options, EntityConfigurations);
    }
}
```
- Create your entity classes. They must implement the IEntity interface to get instatiated via reflection. 
```
public class Test : IEntity
{
    [Key]
    public Guid TestId { get; set; }

    public bool Active { get; set; }
}
```

- For each entity you need a provider class that must be derived from ProviderBase. The base class will supply you with all the standard CRUD operations (GetAll, GetById, Create, Update, Delete). If you'd like to use special queries you can implement your own queries like the GetActiveOrderedByDate method in the example.

```
public class TestProvider(TransactionService<MainContext> transactionSvc) 
	: ProviderBase<Test, Guid, MainContext>(transactionSvc)
{
    public async Task<IList<Test>> GetActiveOrderedByDate()
    {
        var ctx = await GetContextAsync().ConfigureAwait(false);

        try
        {
            var items = GetAllQuery(ctx);
            return await items.Where(x => x.Active == true).OrderBy(x => x.CurrentDate).ToListAsync().ConfigureAwait(false);
        }
        finally
        {
            if (TransactionSvc is { CtxTransaction: null })
            {
                await ctx.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
```
If you have multiple sql statements you can run them in a transaction:
```
public async Task DeactivateAndCreate(Test newEntity)
{
    try
    {
        TransactionSvc.BeginTransaction();

        var ctx = await GetContextAsync().ConfigureAwait(false);
        var itemsToDeactivate = GetDbSet(ctx).Where(x => x.Active == true);
        foreach (var item in itemsToDeactivate)
        {
            item.Active = false;
        }
        ctx.Set<Test>().UpdateRange(itemsToDeactivate);

        GetDbSet(ctx).Add(newEntity);

        await ctx.SaveChangesAsync().ConfigureAwait(false);

        await TransactionSvc.CommitTransaction().ConfigureAwait(false);
    }
    finally
    {
        await TransactionSvc.DisposeTransaction().ConfigureAwait(false);
    }
}
```

- Add a few lines to your startup/program-class to register the context classes in the di container.
```
public static void Main(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);
    ConfigureDataservice(builder.Services);
    ...
}

private static void ConfigureDataservice(IServiceCollection services)
{
    var connectionString = "...";
    var optionsBuilder = new DbContextOptionsBuilder<MainContext>();
    optionsBuilder.UseSqlServer(connectionString);

    var assemblyConf = new DataAssemblyConfiguration("Yournamespace.Data");
    var ctxFactory = new MainContextFactory(assemblyConf, connectionString);
    var startupConf = new StartupConfiguration<MainContext>(ctxFactory, optionsBuilder);

    startupConf.ConfigureDataservice(services);
}
```

- Optional: to configure the entities separately from the entity class you can add configuration classes. They are also automatically instantiated if they derive from EntityTypeConfigurationDependency. 

```
internal class TestConfiguration : EntityTypeConfigurationDependency<Test>
{
    public override void Configure(EntityTypeBuilder<Test> builder)
    {
        builder.HasKey(x => x.TestId);  // already defined as attribute in entity class, for demonstration purpose
    }
}
```

- To use ef core migrations use the batch files provided in the sample app. Don't forget to adjust the batch file that contains the connection string, used as environment variable.

- The dataservice MainSvc is injected into the IoC container at startup. Access it in your pages via the inject attribute.
```
    [Inject]
    protected DataService<MainContext> MainSvc { get; set; } = default!;
```

Now you are ready to go. Here is an example how to add a database entry and retrieve all of them:
```
var p = MainSvc.GetProvider<TestProvider>();

var newEntity = new Test
{
    TestId = Guid.NewGuid(),
    CurrentDate = DateTime.Now,
    Content = $"Testapp entry {DateTime.Now:F}",
    Active = true
};
await p.Create(newEntity, newEntity.TestId);

TestList = await p.GetAllAsync();
```
For detailled information have a look at the sample app in this repo.

## Contributing

Contributions are what make the open source community such an amazing place to learn, inspire, and create. Any contributions you make are **greatly appreciated**.

If you have a suggestion that would make this better, please fork the repo and create a pull request. You can also simply open an issue with the tag "enhancement".
Don't forget to give the project a star! Thanks again!

## License

Distributed under the MIT License. See `LICENSE.txt` for more information.

## Project

Project Link: [https://github.com/thliborius/EfCoreNexus](https://github.com/thliborius/EfCoreNexus)

<!-- MARKDOWN LINKS & IMAGES -->
<!-- https://www.markdownguide.org/basic-syntax/#reference-style-links -->
[contributors-shield]: https://img.shields.io/github/contributors/thliborius/EfCoreNexus.svg?style=for-the-badge
[contributors-url]: https://github.com/thliborius/EfCoreNexus/graphs/contributors
[forks-shield]: https://img.shields.io/github/forks/thliborius/EfCoreNexus.svg?style=for-the-badge
[forks-url]: https://github.com/thliborius/EfCoreNexus/network/members
[stars-shield]: https://img.shields.io/github/stars/thliborius/EfCoreNexus.svg?style=for-the-badge
[stars-url]: https://github.com/thliborius/EfCoreNexus/stargazers
[issues-shield]: https://img.shields.io/github/issues/thliborius/EfCoreNexus.svg?style=for-the-badge
[issues-url]: https://github.com/thliborius/EfCoreNexus/issues
[license-shield]: https://img.shields.io/github/license/thliborius/EfCoreNexus.svg?style=for-the-badge
[license-url]: https://github.com/thliborius/EfCoreNexus/blob/main/license.txt
[nuget-shield]: https://img.shields.io/nuget/v/EfCoreNexus.Framework?style=for-the-badge
[nuget-url]: https://www.nuget.org/packages/EfCoreNexus.Framework/

