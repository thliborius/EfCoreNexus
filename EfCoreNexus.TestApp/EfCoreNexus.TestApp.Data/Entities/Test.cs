using EfCoreNexus.Framework.Entities;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EfCoreNexus.TestApp.Data.Entities;

[Table("Test", Schema = "dbo")]
public class Test : IEntity
{
    [Key]
    public Guid TestId { get; set; }

    public string Content { get; set; } = "";

    public DateTime CurrentDate { get; set; }

    public bool Active { get; set; }
}