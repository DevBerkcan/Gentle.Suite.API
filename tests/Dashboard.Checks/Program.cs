using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;
using GentleSuite.Infrastructure.Data;
using GentleSuite.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

var count = 0;
void Equal<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{name}: expected {expected}, got {actual}");
    count++;
}
Equal(119m, DashboardAmounts.Received(1190, 119, true, InvoiceStatus.Sent), "partial payment");
Equal(100m, DashboardAmounts.PaidNet(1000, 1190, 119), "partial payment net share");
Equal(0m, DashboardAmounts.Received(119, 0, true, InvoiceStatus.Overdue), "reversed payment");
Equal(119m, DashboardAmounts.Received(119, 0, false, InvoiceStatus.Paid), "legacy paid invoice");
Equal(60m, DashboardAmounts.Received(119, 60, true, InvoiceStatus.Paid), "ledger takes precedence");
Equal(119m, DashboardAmounts.Received(119, 150, true, InvoiceStatus.Paid), "overpayment capped for invoice balance");
Equal(0m, DashboardAmounts.PaidNet(0, 0, 0), "zero total");
Equal("completed", DashboardAmounts.Stage(new() { Status = SubscriptionStatus.Completed }), "issued is not paid");
Equal("paused", DashboardAmounts.Stage(new() { Status = SubscriptionStatus.Paused }), "paused process");
Equal(0m, DashboardAmounts.MonthlyRecurringNet(new[] { new CustomerSubscription { Status = SubscriptionStatus.Active, Plan = new SubscriptionPlan { MonthlyPrice = 999 } } }), "missing agreed price is not invented");

if (!args.Contains("--integration"))
{
    Console.WriteLine($"PASS: {count} dashboard amount checks. Add --integration for isolated LocalDB checks.");
    return;
}

// This test only creates/removes its own uniquely named database; never uses app configuration.
var database = "GentleSuiteDashboardTest_" + Guid.NewGuid().ToString("N");
using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
    .UseSqlServer($"Server=(localdb)\\MSSQLLocalDB;Database={database};Integrated Security=true;TrustServerCertificate=true").Options);
try
{
    await db.Database.EnsureCreatedAsync();
    var service = new DashboardServiceImpl(db);
    var empty = await service.GetOverviewAsync(default);
    Equal(0m, empty.OpenInvoiceAmount, "empty database balance");
    Equal(6, empty.PaymentChart.Count, "empty database chart");
    var now = DateTimeOffset.UtcNow;
    var customer = new Customer { CompanyName = "Dashboard Test GmbH", Status = CustomerStatus.Active };
    var plan = new SubscriptionPlan { Name = "Test Service", MonthlyPrice = 100 };
    var quote = new Quote { Customer = customer, QuoteNumber = "TEST-QUOTE", Status = QuoteStatus.Accepted };
    var recurring = new CustomerSubscription { Customer = customer, Plan = plan, Status = SubscriptionStatus.Active,
        AgreedMonthlyPrice = 100, ContractQuote = quote, BusinessCustomerConfirmed = true, MollieMandateStatus = "valid", BillingAuthorizedAt = now, StartDate = now, NextBillingDate = now.AddDays(5) };
    var quarterly = new CustomerSubscription { Customer = customer, Plan = plan, Status = SubscriptionStatus.Active,
        AgreedMonthlyPrice = 200, ContractBillingCycle = BillingCycle.Quarterly, ContractQuote = quote, BusinessCustomerConfirmed = true, MollieMandateStatus = "valid", StartDate = now, NextBillingDate = now.AddDays(5) };
    var installment = new CustomerSubscription { Customer = customer, Plan = plan, Status = SubscriptionStatus.Completed,
        IsInstallmentPlan = true, TotalInstallmentAmount = 1200, AgreedMonthlyPrice = 100,
        ContractDurationMonths = 12, InstallmentsCompleted = 12, StartDate = now, NextBillingDate = now.AddDays(5) };
    db.AddRange(recurring, quarterly, installment);
    Invoice Invoice(string number, decimal net, InvoiceStatus status, CustomerSubscription? subscription = null)
        => new() { InvoiceNumber = number, Customer = customer, Subscription = subscription,
            NetTotal = net, GrossTotal = net * 1.19m, Status = status, DueDate = now.AddDays(-2), InvoiceDate = now,
            Type = subscription == null ? InvoiceType.Standard : InvoiceType.Recurring };
    var partial = Invoice("PARTIAL", 1000, InvoiceStatus.Sent, installment);
    partial.Payments.Add(new() { Amount = 119, PaymentDate = now });
    partial.Payments.Add(new() { Amount = 100, PaymentDate = now.AddDays(5) });
    partial.PaymentCollectionStatus = "scheduled"; partial.PaymentCollectionDueDate = now.AddDays(1); partial.CollectionAttemptCount = 1;
    var overdue = Invoice("OVERDUE", 100, InvoiceStatus.Overdue);
    var paid = Invoice("PAID", 100, InvoiceStatus.Paid, recurring);
    paid.Payments.Add(new() { Amount = 119, PaymentDate = now }); paid.PaidAt = now;
    var reversed = Invoice("REVERSED", 100, InvoiceStatus.Overdue, recurring);
    paid.BillingPeriodStart = now.AddMonths(-1); paid.BillingPeriodEnd = now;
    reversed.BillingPeriodStart = now; reversed.BillingPeriodEnd = now.AddMonths(1);
    reversed.Payments.Add(new() { Amount = 119, PaymentDate = now }); reversed.Payments.Add(new() { Amount = -119, PaymentDate = now }); reversed.PaymentCollectionStatus = "charged_back";
    db.AddRange(partial, overdue, paid, reversed, Invoice("DRAFT", 999, InvoiceStatus.Draft), Invoice("CANCELLED", 999, InvoiceStatus.Cancelled));
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();
    var overview = await service.GetOverviewAsync(default);
    Equal(300m, overview.Recurring.MonthlyNet, "MRR excludes installments and preserves monthly quarterly price");
    Equal(238m, overview.ReceivedThisMonth, "cash uses partial payments and chargebacks");
    Equal(1309m, overview.OpenInvoiceAmount, "open balance excludes paid/draft/cancelled and deducts payments");
    Equal(3, overview.OverdueInvoiceCount, "overdue includes sent and explicit overdue statuses");
    Equal(100m, overview.Installments.PaidNet, "installment received net");
    Equal(1100m, overview.Installments.RemainingNet, "completed billing still has unpaid debt");
    Equal(0, overview.Installments.Settled, "issued does not mean settled");
    Equal(1, overview.ScheduledCollections, "scheduled collection");
    Equal(1, overview.FailedCollections, "chargeback is actionable");
    Equal(1, overview.UpcomingCount, "only actual scheduled invoices are upcoming");
    Equal(1190m, overview.Upcoming[0].Amount, "scheduled debit reflects the amount sent to Mollie, not the remaining balance");
    Equal(1190m, overview.ScheduledAmount, "scheduled debit total");
    Equal(true, overview.Attention.Any(a => a.Title == "Teilzahlung vor Einzug prüfen"), "partial payment before full debit needs review");
    Equal(12, overview.InstallmentTracking[0].Issued, "issued installment count");
    Equal(1, overview.Recurring.Stages.Single(s => s.Key == "authorization").Count, "missing authorization tracked");
    // A fixture from the real endpoint calculation can also be served by the local browser test.
    var output = args.FirstOrDefault(a => a.StartsWith("--fixture="))?[10..];
    if (output != null) await File.WriteAllTextAsync(output, JsonSerializer.Serialize(overview, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    if (output != null) await File.WriteAllTextAsync(Path.ChangeExtension(output, ".empty.json"), JsonSerializer.Serialize(empty, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    Console.WriteLine($"PASS: {count} dashboard checks including real SQL Server queries and fixture aggregation.");
}
finally { await db.Database.EnsureDeletedAsync(); }
