using System.Data;
using System.Data.Common;
using EfCoreNexus.Framework.Provider;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCoreNexus.SqlServer.Tests;

internal sealed class Customer
{
    public int Id { get; private set; }
    public string Name { get; set; } = "";
    public byte[] Version { get; set; } = [];
}

internal sealed class Order
{
    public int Id { get; private set; }
    public int CustomerId { get; set; }
}

internal sealed class Audit
{
    public int Id { get; private set; }
    public string Message { get; set; } = "";
}

internal sealed class SqlServerContext(DbContextOptions<SqlServerContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Customer>().ToTable("Customers");
        model.Entity<Customer>().Property(x => x.Version).IsRowVersion();
        model.Entity<Order>().ToTable("Orders");
        model.Entity<Order>().HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId);
        model.Entity<Audit>().ToTable("Audits");
    }
}

internal sealed class SqlServerFactory(DbContextOptions<SqlServerContext> options)
    : IDbContextFactory<SqlServerContext>
{
    public SqlServerContext CreateDbContext() => new(options);
}

internal sealed class Customers(IDbContextFactory<SqlServerContext> factory)
    : ProviderBase<Customer, int, SqlServerContext>(factory);

internal sealed class Orders(IDbContextFactory<SqlServerContext> factory)
    : ProviderBase<Order, int, SqlServerContext>(factory);

// Observe completed commands without changing execution. Copy byte arrays before EF reuses them.
internal sealed class Commands : DbCommandInterceptor
{
    public List<Command> Executed { get; } = [];

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
        CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Capture(command);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command,
        CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Capture(command);
        return ValueTask.FromResult(result);
    }

    private void Capture(DbCommand command)
        => Executed.Add(new Command(command.CommandText, command.Parameters.Cast<DbParameter>()
            .ToDictionary(p => p.ParameterName, p => new Parameter(p.DbType,
                p.Value is byte[] bytes ? bytes.ToArray() : p.Value))));

    internal sealed record Parameter(DbType Type, object? Value);
    internal sealed record Command(string Sql, Dictionary<string, Parameter> Parameters);
}
