using GentleSuite.Domain.Enums;

namespace GentleSuite.Domain.Entities;

public class SubscriptionPlan : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal MonthlyPrice { get; set; }
    public BillingCycle BillingCycle { get; set; } = BillingCycle.Monthly;
    public SubscriptionPlanCategory Category { get; set; } = SubscriptionPlanCategory.Allgemein;
    public bool IsActive { get; set; } = true;
    public List<SubscriptionPlanService> IncludedServices { get; set; } = new();
    public WorkScopeRule? WorkScopeRule { get; set; }
    public SupportPolicy? SupportPolicy { get; set; }
}

public class SubscriptionPlanService : BaseEntity
{
    public Guid PlanId { get; set; }
    public SubscriptionPlan Plan { get; set; } = null!;
    public Guid ServiceCatalogItemId { get; set; }
    public ServiceCatalogItem ServiceCatalogItem { get; set; } = null!;
}

public class CustomerSubscription : BaseEntity
{
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public Guid PlanId { get; set; }
    public SubscriptionPlan Plan { get; set; } = null!;
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Active;
    public DateTimeOffset StartDate { get; set; }
    public DateTimeOffset? EndDate { get; set; }
    public DateTimeOffset? PausedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public DateTimeOffset NextBillingDate { get; set; }
    public string? CancellationReason { get; set; }
    public int? ContractDurationMonths { get; set; }
    public Guid? ContractQuoteId { get; set; }
    public Quote? ContractQuote { get; set; }
    public Guid? QuoteLineId { get; set; }
    public QuoteLine? QuoteLine { get; set; }
    public string? ContractReference { get; set; }
    public int? ContractVersion { get; set; }
    public DateTimeOffset? ContractAcceptedAt { get; set; }
    public string? ContractAcceptedByName { get; set; }
    public string? ContractAcceptedByEmail { get; set; }
    public string? ContractAcceptedIpAddress { get; set; }
    public decimal? AgreedMonthlyPrice { get; set; }
    public BillingCycle ContractBillingCycle { get; set; } = BillingCycle.Monthly;
    public bool BusinessCustomerConfirmed { get; set; }
    public DateTimeOffset? BusinessCustomerConfirmedAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public string? MollieCustomerId { get; set; }
    public string? MollieMandateId { get; set; }
    public string? MollieMandateStatus { get; set; }
    public string? MollieFirstPaymentId { get; set; }
    public string? MollieFirstPaymentStatus { get; set; }
    public int MollieMandateAttempt { get; set; }
    public DateTimeOffset? MandateEmailSentAt { get; set; }
    public string? MandateEmailRecipient { get; set; }
    public string? MandateEmailStatus { get; set; }
    public string? MandateEmailLastError { get; set; }
    public int MandateEmailAttemptCount { get; set; }
    /// <summary>Set once the user has actually issued the invoice covering this contract; only then may SubscriptionBillingJob start billing it.</summary>
    public DateTimeOffset? BillingAuthorizedAt { get; set; }

    // Ratenzahlung (installment plan): a fixed total amount split into a fixed number of equal monthly
    // installments, as opposed to an indefinite subscription. ContractDurationMonths is reused as the
    // installment count, AgreedMonthlyPrice as the per-installment amount.
    /// <summary>True if this row represents a fixed-total installment plan rather than an indefinite subscription.</summary>
    public bool IsInstallmentPlan { get; set; }
    /// <summary>Total amount (net) to be paid off across all installments, e.g. 4000.00.</summary>
    public decimal? TotalInstallmentAmount { get; set; }
    /// <summary>Number of installment invoices already issued.</summary>
    public int InstallmentsCompleted { get; set; }
    /// <summary>Product/service title shown on installment invoice lines instead of the synthetic Plan.Name.</summary>
    public string? InstallmentSourceTitle { get; set; }

    // Preisangebot: surcharge/down-payment metadata for plans created via the payment-plan "Überführen" flow.
    // Null for plans created via the legacy CreateInstallmentPlanFromQuoteAsync (no surcharge/down-payment concept there).
    /// <summary>Surcharge % already baked into TotalInstallmentAmount.</summary>
    public decimal? InstallmentSurchargePercent { get; set; }
    /// <summary>Hybrid plans only: down-payment % of the quote's SubtotalOneTime.</summary>
    public decimal? DownPaymentPercent { get; set; }
    public Guid? DownPaymentInvoiceId { get; set; }
    public Invoice? DownPaymentInvoice { get; set; }
    /// <summary>"hybrid" | "monthly12" | "monthly24" — which Preisangebot option this plan came from.</summary>
    public string? PaymentPlanOptionKey { get; set; }
}

public class WorkScopeRule : BaseEntity
{
    public Guid PlanId { get; set; }
    public SubscriptionPlan Plan { get; set; } = null!;
    public string? FairUseDescription { get; set; }
    public string IncludedItemsJson { get; set; } = "[]";
    public string ExcludedItemsJson { get; set; } = "[]";
    public int? MaxHoursPerMonth { get; set; }
}

public class SupportPolicy : BaseEntity
{
    public Guid PlanId { get; set; }
    public SubscriptionPlan Plan { get; set; } = null!;
    public string? S0ResponseTarget { get; set; }
    public string? S1ResponseTarget { get; set; }
    public string? S2ResponseTarget { get; set; }
    public string? S3ResponseTarget { get; set; }
    public string? S0Description { get; set; }
    public string? S1Description { get; set; }
    public string? S2Description { get; set; }
    public string? S3Description { get; set; }
}
