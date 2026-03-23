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
    public DateTimeOffset? ConfirmedAt { get; set; }
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
