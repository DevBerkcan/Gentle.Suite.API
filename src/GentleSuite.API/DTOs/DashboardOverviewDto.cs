namespace GentleSuite.Application.DTOs;

public record BillingStageDto(string Key, string Label, int Count);
public record BillingOverviewDto(int Total, int Active, decimal MonthlyNet, int Settled,
    decimal ContractNet, decimal PaidNet, decimal RemainingNet, List<BillingStageDto> Stages);
public record DashboardAttentionDto(string Id, string CustomerName, string Title, string Detail,
    string Href, string Severity, decimal? Amount);
public record DashboardCollectionDto(Guid InvoiceId, string InvoiceNumber, string CustomerName,
    bool IsInstallmentPlan, DateTimeOffset DueDate, decimal Amount, string Status, int Attempts);
public record DashboardInstallmentDto(Guid Id, string CustomerName, string Title, string Stage,
    int Issued, int? TotalInstallments, decimal ContractNet, decimal PaidNet, decimal RemainingNet);
public record DashboardOverviewDto(
    DateTimeOffset GeneratedAt, int ActiveCustomers, int OpenQuotes, int OpenOnboardings, int OverdueTasks,
    decimal ReceivedThisMonth, decimal OpenInvoiceAmount, decimal OverdueInvoiceAmount, int OverdueInvoiceCount,
    BillingOverviewDto Recurring, BillingOverviewDto Installments,
    int ScheduledCollections, int ProcessingCollections, int FailedCollections, decimal ScheduledAmount,
    int AttentionCount, List<DashboardAttentionDto> Attention,
    int UpcomingCount, List<DashboardCollectionDto> Upcoming,
    int InstallmentTrackingCount, List<DashboardInstallmentDto> InstallmentTracking,
    List<MonthlyRevenueDto> PaymentChart, List<ActivityLogDto> RecentActivity);
