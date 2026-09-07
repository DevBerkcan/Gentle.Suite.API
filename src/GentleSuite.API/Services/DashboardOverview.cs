using GentleSuite.Application.DTOs;
using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace GentleSuite.Infrastructure.Services;

public static class DashboardAmounts
{
    public static decimal Received(decimal gross, decimal payments, bool hasPayments, InvoiceStatus status)
        => hasPayments ? Math.Clamp(payments, 0, Math.Max(0, gross)) : status == InvoiceStatus.Paid ? Math.Max(0, gross) : 0;

    public static decimal PaidNet(decimal net, decimal gross, decimal received)
        => gross > 0 ? Math.Round(net * Math.Clamp(received / gross, 0, 1), 2) : 0;

    public static string Stage(CustomerSubscription s)
    {
        if (s.Status == SubscriptionStatus.Completed) return "completed";
        if (s.Status is SubscriptionStatus.Cancelled or SubscriptionStatus.Expired) return "closed";
        if (s.Status == SubscriptionStatus.Paused) return "paused";
        if (s.ContractQuoteId == null || !s.BusinessCustomerConfirmed || s.AgreedMonthlyPrice is not > 0) return "review";
        if (!string.Equals(s.MollieMandateStatus, "valid", StringComparison.OrdinalIgnoreCase)) return "mandate";
        if (s.BillingAuthorizedAt == null || s.Status == SubscriptionStatus.PendingConfirmation) return "authorization";
        return "ready";
    }

    public static decimal MonthlyRecurringNet(IEnumerable<CustomerSubscription> subscriptions)
        => subscriptions.Where(s => !s.IsInstallmentPlan && s.Status == SubscriptionStatus.Active)
            .Sum(s => Math.Max(0, s.AgreedMonthlyPrice ?? 0));
}

public partial class DashboardServiceImpl
{
    private static readonly (string Key, string Label)[] BillingStages =
    {
        ("ready", "Abrechnung freigegeben"), ("mandate", "Mandat fehlt"),
        ("authorization", "Freigabe fehlt"), ("review", "Vertragsdaten prüfen"),
        ("paused", "Pausiert"), ("completed", "Vollständig fakturiert"), ("closed", "Beendet")
    };

    public async Task<DashboardOverviewDto> GetOverviewAsync(CancellationToken ct)
    {
        await QuoteLifecycle.ExpireAsync(_db, ct);
        var now = DateTimeOffset.UtcNow;
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        DateTimeOffset Boundary(DateTime date) => new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), zone));
        var today = Boundary(localNow.Date);
        var monthStart = Boundary(new DateTime(localNow.Year, localNow.Month, 1));
        var chartStart = Boundary(new DateTime(localNow.Year, localNow.Month, 1).AddMonths(-5));
        var upcomingEnd = Boundary(localNow.Date.AddDays(15));

        var subs = await _db.CustomerSubscriptions.AsNoTracking().Include(s => s.Customer).Include(s => s.Plan).ToListAsync(ct);
        // Only scalar invoice data and payment aggregates: no lines/PDFs or per-subscription requests.
        var invoices = await _db.Invoices.AsNoTracking()
            .Where(i => i.Status != InvoiceStatus.Draft && i.Status != InvoiceStatus.Cancelled
                && (i.Type == InvoiceType.Standard || i.Type == InvoiceType.Recurring))
            .Select(i => new
            {
                i.Id, i.InvoiceNumber, CustomerName = i.Customer.CompanyName, i.SubscriptionId,
                i.Status, i.NetTotal, i.GrossTotal, i.DueDate, i.PaidAt,
                i.PaymentCollectionStatus, i.PaymentCollectionDueDate, i.CollectionAttemptCount,
                HasPayments = i.Payments.Any(), Payments = i.Payments.Where(p => p.PaymentDate <= now).Sum(p => (decimal?)p.Amount) ?? 0
            }).ToListAsync(ct);
        var balances = invoices.Select(i => new
        {
            Invoice = i,
            Received = DashboardAmounts.Received(i.GrossTotal, i.Payments, i.HasPayments, i.Status),
            Remaining = Math.Max(0, i.GrossTotal - DashboardAmounts.Received(i.GrossTotal, i.Payments, i.HasPayments, i.Status))
        }).ToList();
        var open = balances.Where(b => b.Remaining > 0).ToList();
        var overdue = open.Where(b => b.Invoice.DueDate < today).ToList();
        var paidBySub = balances.Where(b => b.Invoice.SubscriptionId.HasValue)
            .GroupBy(b => b.Invoice.SubscriptionId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(b => DashboardAmounts.PaidNet(b.Invoice.NetTotal, b.Invoice.GrossTotal, b.Received)));
        var subById = subs.ToDictionary(s => s.Id);
        var trackedPlans = subs.Where(s => s.IsInstallmentPlan && s.Status is not (SubscriptionStatus.Cancelled or SubscriptionStatus.Expired)).ToList();
        decimal Contract(CustomerSubscription s) => Math.Max(0, s.TotalInstallmentAmount ?? 0);
        decimal Paid(CustomerSubscription s) => Math.Min(Contract(s), paidBySub.GetValueOrDefault(s.Id));
        decimal Remaining(CustomerSubscription s) => Math.Max(0, Contract(s) - Paid(s));
        BillingOverviewDto Summary(bool installment)
        {
            var group = subs.Where(s => s.IsInstallmentPlan == installment).ToList();
            return new(group.Count, group.Count(s => s.Status == SubscriptionStatus.Active),
                installment ? 0 : DashboardAmounts.MonthlyRecurringNet(group),
                installment ? trackedPlans.Count(s => Contract(s) > 0 && Remaining(s) == 0) : 0,
                installment ? trackedPlans.Sum(Contract) : 0,
                installment ? trackedPlans.Sum(Paid) : 0,
                installment ? trackedPlans.Sum(Remaining) : 0,
                BillingStages.Select(stage => new BillingStageDto(stage.Key, stage.Label, group.Count(s => DashboardAmounts.Stage(s) == stage.Key))).ToList());
        }

        var attention = new List<DashboardAttentionDto>();
        foreach (var sub in subs)
        {
            var stage = DashboardAmounts.Stage(sub);
            if (stage is "mandate" or "authorization" or "review")
                attention.Add(new($"sub-{sub.Id}", sub.Customer.CompanyName,
                    BillingStages.First(s => s.Key == stage).Label,
                    sub.InstallmentSourceTitle ?? sub.Plan.Name,
                    sub.IsInstallmentPlan ? "/installments" : "/subscriptions", "warning", null));
            if (sub.MandateEmailStatus == "failed" && stage is not ("closed" or "completed"))
                attention.Add(new($"email-{sub.Id}", sub.Customer.CompanyName, "Mandats-E-Mail fehlgeschlagen",
                    "Zustellung prüfen und erneut senden", sub.IsInstallmentPlan ? "/installments" : "/subscriptions", "danger", null));
            if (sub.IsInstallmentPlan && stage != "closed" && sub.TotalInstallmentAmount is not > 0)
                attention.Add(new($"amount-{sub.Id}", sub.Customer.CompanyName, "Ratenbetrag fehlt",
                    "Der Gesamtbetrag kann im Restbetrag nicht berücksichtigt werden.", "/installments", "warning", null));
        }
        static bool Failed(string? status) => status is "failed" or "expired" or "canceled" or "charged_back";
        foreach (var balance in open)
        {
            var inv = balance.Invoice;
            var failed = Failed(inv.PaymentCollectionStatus);
            var late = inv.DueDate < today;
            var retry = inv.CollectionAttemptCount > 0 && inv.PaymentCollectionStatus == "scheduled";
            var partialCollection = balance.Received > 0 && inv.SubscriptionId != null
                && inv.PaymentCollectionStatus is "scheduled" or "open" or "pending" or "authorized";
            if (failed || late || retry || partialCollection || (inv.SubscriptionId != null && inv.Status == InvoiceStatus.Final))
            {
                var title = failed ? "Einzug fehlgeschlagen / Rücklastschrift" : partialCollection ? "Teilzahlung vor Einzug prüfen" : retry ? "Erneuter Einzug geplant"
                    : inv.SubscriptionId != null && inv.Status == InvoiceStatus.Final ? "Rechnungsversand prüfen" : "Rechnung überfällig";
                attention.Add(new($"inv-{inv.Id}", inv.CustomerName, title,
                    partialCollection ? $"{inv.InvoiceNumber} · Einzug weiterhin über den vollen Rechnungsbetrag geplant" : inv.InvoiceNumber,
                    $"/invoices/{inv.Id}", failed || late || partialCollection ? "danger" : "warning", balance.Remaining));
            }
        }
        attention = attention.OrderBy(a => a.Severity == "danger" ? 0 : 1).ThenByDescending(a => a.Amount ?? 0).ThenBy(a => a.CustomerName).ToList();
        var collections = open.Where(b => b.Invoice.SubscriptionId != null).ToList();
        var upcoming = collections.Where(b => b.Invoice.PaymentCollectionDueDate.HasValue
            && b.Invoice.PaymentCollectionDueDate < upcomingEnd
            && b.Invoice.PaymentCollectionStatus is "scheduled" or "open" or "pending" or "authorized")
            .OrderBy(b => b.Invoice.PaymentCollectionDueDate).Select(b => new DashboardCollectionDto(
                b.Invoice.Id, b.Invoice.InvoiceNumber, b.Invoice.CustomerName,
                subById.GetValueOrDefault(b.Invoice.SubscriptionId!.Value)?.IsInstallmentPlan ?? false,
                b.Invoice.PaymentCollectionDueDate!.Value, b.Invoice.GrossTotal, b.Invoice.PaymentCollectionStatus!, b.Invoice.CollectionAttemptCount)).ToList();

        // Actual booked cash movements, including partial payments and negative chargebacks.
        var payments = await _db.InvoicePayments.AsNoTracking().Where(p => p.PaymentDate >= chartStart && p.PaymentDate <= now)
            .Select(p => new { p.PaymentDate, p.Amount }).ToListAsync(ct);
        // Older imported invoices may only have Paid/PaidAt, without ledger rows. Never count both.
        payments.AddRange(invoices.Where(i => !i.HasPayments && i.Status == InvoiceStatus.Paid && i.PaidAt >= chartStart && i.PaidAt <= now)
            .Select(i => new { PaymentDate = i.PaidAt!.Value, Amount = i.GrossTotal }));
        var expenses = await _db.Expenses.AsNoTracking().Where(e => e.Status != ExpenseStatus.Draft && e.ExpenseDate >= chartStart && e.ExpenseDate <= now)
            .Select(e => new { e.ExpenseDate, e.GrossAmount }).ToListAsync(ct);
        var chart = Enumerable.Range(0, 6).Select(offset =>
        {
            var date = new DateTime(localNow.Year, localNow.Month, 1).AddMonths(offset - 5);
            var start = Boundary(date); var end = Boundary(date.AddMonths(1));
            return new MonthlyRevenueDto(date.Year, date.Month,
                payments.Where(p => p.PaymentDate >= start && p.PaymentDate < end).Sum(p => p.Amount),
                expenses.Where(e => e.ExpenseDate >= start && e.ExpenseDate < end).Sum(e => e.GrossAmount));
        }).ToList();
        var activity = await _db.ActivityLogs.AsNoTracking().OrderByDescending(a => a.CreatedAt).Take(6)
            .Select(a => new ActivityLogDto(a.Id, a.EntityType, a.EntityId, a.Action, a.Description, a.UserName, a.CreatedAt)).ToListAsync(ct);
        var tracking = trackedPlans.OrderByDescending(Remaining).ThenBy(s => s.Customer.CompanyName)
            .Take(8).Select(s => new DashboardInstallmentDto(s.Id, s.Customer.CompanyName, s.InstallmentSourceTitle ?? s.Plan.Name,
                BillingStages.First(stage => stage.Key == DashboardAmounts.Stage(s)).Label, s.InstallmentsCompleted,
                s.ContractDurationMonths, Contract(s), Paid(s), Remaining(s))).ToList();

        return new(now,
            await _db.Customers.CountAsync(c => c.Status == CustomerStatus.Active, ct),
            await _db.Quotes.CountAsync(q => q.IsCurrentVersion && (q.Status == QuoteStatus.Sent || q.Status == QuoteStatus.Viewed), ct),
            await _db.OnboardingWorkflows.CountAsync(w => w.Status == OnboardingStatus.InProgress, ct),
            await _db.TaskItems.CountAsync(t => t.Status != TaskItemStatus.Done && t.DueDate < today, ct),
            payments.Where(p => p.PaymentDate >= monthStart).Sum(p => p.Amount),
            open.Sum(b => b.Remaining), overdue.Sum(b => b.Remaining), overdue.Count,
            Summary(false), Summary(true),
            collections.Count(b => b.Invoice.PaymentCollectionStatus == "scheduled"),
            collections.Count(b => b.Invoice.PaymentCollectionStatus is "open" or "pending" or "authorized"),
            collections.Count(b => Failed(b.Invoice.PaymentCollectionStatus)),
            collections.Where(b => b.Invoice.PaymentCollectionStatus == "scheduled").Sum(b => b.Invoice.GrossTotal),
            attention.Count, attention.Take(10).ToList(), upcoming.Count, upcoming.Take(8).ToList(),
            trackedPlans.Count, tracking, chart, activity);
    }
}
