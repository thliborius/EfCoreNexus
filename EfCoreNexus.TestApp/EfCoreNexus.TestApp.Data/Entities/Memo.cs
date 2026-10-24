using System.ComponentModel.DataAnnotations;
using EfCoreNexus.Framework.Entities;

namespace EfCoreNexus.TestApp.Data.Entities;

// Discovered without a DbSet, configuration class or per-type DI registration.
public sealed class Memo : IEntity
{
    public int Id { get; set; }
    public string Text { get; set; } = "";
    [ConcurrencyCheck] public Guid Revision { get; set; } = Guid.NewGuid();
}
