namespace GentleSuite.Domain.Entities;

public class IntegrationSettings : BaseEntity
{
    // PayPal Business
    public string? PayPalClientId { get; set; }
    public string? PayPalClientSecret { get; set; }
    public bool PayPalEnabled { get; set; }
    public DateTimeOffset? PayPalLastSync { get; set; }

    // GoCardless / PSD2 (Fyrst Geschäftskonto)
    public string? GoCardlessSecretId { get; set; }
    public string? GoCardlessSecretKey { get; set; }
    public string? BankRequisitionId { get; set; }
    public string? BankAccountId { get; set; }
    public bool BankEnabled { get; set; }
    public DateTimeOffset? BankLastSync { get; set; }
}
