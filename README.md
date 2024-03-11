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


## Run the sample app

- Check out the repo.
- Edit the EfCoreNexus.TestApp\EfCoreNexus.TestApp.Data\migrations_connectionstring.bat and adjust the connection string to an empty sql server database.
- Call migrations_add.bat with a migration name as parameter, e.g. "migrations_add.bat Init".
- Call migrations_updatedb.bat.
- Add something to the table test that was created in the database, if you want to see something in the app.
- Open the solution and start the TestApp (just hit F5).

## How to use it in your project

These are the steps that are neccessary to hook up the entity framework via EfCoreNexus with your blazor app.

 - Add a reference to the EfCoreNexus.Framework library or install the nuget package.

- Add a DbContext and DbContextFactory class, derived from EfCoreNexus base classes BaseContext and BaseContextFactory.
```
public class MainContext : BaseContext<MainContext>
{
    public MainContext(DbContextOptions<MainContext> options, IEnumerable<EntityTypeConfigurationDependency> configurations) 
    : base(options, configurations)
    {    
    }
}
```
```
public class MainContextFactory : BaseContextFactory<MainContext>
{
    public MainContextFactory()
    {    
    }

    public MainContextFactory(DataAssemblyConfiguration assemblyConf, string connectionString)
        : base(assemblyConf, connectionString)
    {
    }

    public override MainContext CreateDbContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<MainContext>();
        optionsBuilder.UseSqlServer(ConnectionString);

        return new MainContext(optionsBuilder.Options, EntityConfigurations);
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

- For each entity you need a provider class that must be derived from ProviderBase. The base class will supply you with all the standard CRUD operations (GetAll, GetById, Create, Update, Delete). If you'd like to use special queries you can implement your own queries according to the GetActiveQuery method.

```
public class TestProvider : ProviderBase<Test, Guid, MainContext>
{
    public TestProvider(TransactionService<MainContext> transactionSvc) : base(transactionSvc)
    {
    }

    protected override IQueryable<Test> GetActiveQuery(DbContext ctx, bool isActive)
    {
        return base.GetAllQuery(ctx).Where(x => x.Active == isActive);
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

Now you are ready to go. Here is an example how to add a database entry and retrieve all of them:
```
var p = MainSvc.GetProvider<TestProvider>();

var newEntity = new Test
{
    TestId = Guid.NewGuid(),
    CurrentDate = DateTime.Now,
    Content = $"Testapp entry from {DateTime.Now:F}",
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

1. Fork the Project
2. Create your Feature Branch (`git checkout -b feature/AmazingFeature`)
3. Commit your Changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the Branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

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

