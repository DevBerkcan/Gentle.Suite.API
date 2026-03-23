namespace GentleSuite.Domain.Entities;

public class ReminderSettings : BaseEntity
{
    public int Level1Days { get; set; } = 7;
    public int Level2Days { get; set; } = 14;
    public int Level3Days { get; set; } = 21;
    public decimal Level1Fee { get; set; }
    public decimal Level2Fee { get; set; }
    public decimal Level3Fee { get; set; }
    public decimal AnnualInterestPercent { get; set; }
}

