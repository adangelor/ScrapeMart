using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ScrapeMart.Entities;

[Table("VtexRetailersConfig")]
public sealed class VtexRetailersConfig
{
    [Key]
    public int Id { get; set; }
    public string RetailerHost { get; set; } = default!;
    public string SalesChannels { get; set; } = default!;
    public bool Enabled { get; set; }
    public string RetailerId { get; internal set; }
}

