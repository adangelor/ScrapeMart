using System.ComponentModel.DataAnnotations;

namespace ScrapeMart.Entities;

public class SweepLog
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    public string RetailerHost { get; set; } = default!;

    [Required]
    [MaxLength(100)]
    public string SweepType { get; set; } = default!;

    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    [MaxLength(50)]
    public string Status { get; set; } = default!; 

    public string? Notes { get; set; }
}
