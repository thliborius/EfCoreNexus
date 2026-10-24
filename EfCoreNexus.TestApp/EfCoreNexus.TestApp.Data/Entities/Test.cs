using EfCoreNexus.Framework.Entities;

namespace EfCoreNexus.TestApp.Data.Entities;

public sealed class Test : IEntity
{
    public Guid TestId { get; set; }
    public string Content { get; set; } = "";
    public DateTime CurrentDate { get; set; }
    public bool Active { get; set; }
}
