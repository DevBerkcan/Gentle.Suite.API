namespace GentleSuite.Domain.Entities;

public class NumberSequence : BaseEntity
{
    public string EntityType { get; set; } = string.Empty;
    public int Year { get; set; }
    public string Prefix { get; set; } = string.Empty;
    public int LastValue { get; set; }
    public int Padding { get; set; } = 4;
}

