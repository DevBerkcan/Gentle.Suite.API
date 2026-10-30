using GentleSuite.Application.DTOs;
using GentleSuite.Application.Interfaces;
using GentleSuite.Domain.Enums;
using GentleSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GentleSuite.Infrastructure.Services;

/// <summary>Zahlungs-Monitoring (docs/mollie-api-contract.md §3): letzter Abrechnungs-/Abgleichslauf und eine
/// Liste konkreter Probleme, damit ausbleibende Einzüge nicht erst beim Kunden auffallen.</summary>
public class PaymentMonitoringService : IPaymentMonitoringService
{
    private readonly AppDbContext _db;
    public PaymentMonitoringService(AppDbContext db) { _db = db; }

    private static readonly string[] InFlight = { "open", "pending", "authorized" };
    private static readonly string[] Failed = { "failed", "expired", "canceled" };

    public async Task<PaymentMonitoringDto> GetPaymentMonitoringAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var today = BerlinTime.Today;
        var issues = new List<PaymentIssueDto>();

        var lastBilling = await _db.JobRunLogs.AsNoTracking().Where(j => j.JobName == "subscription-billing").OrderByDescending(j => j.StartedAt).FirstOrDefaultAsync(ct);
        var lastBillingSuccess = await _db.JobRunLogs.AsNoTracking().Where(j => j.JobName == "subscription-billing" && j.FinishedAt != null).OrderByDescending(j => j.StartedAt).FirstOrDefaultAsync(ct);
        var lastReconcile = await _db.JobRunLogs.AsNoTracking().Where(j => j.JobName == "mandate-reconcile").OrderByDescending(j => j.StartedAt).FirstOrDefaultAsync(ct);

        if (lastBillingSuccess == null)
            issues.Add(new("warning", "billing-job-stale", "Noch kein Abrechnungslauf protokolliert", "Der tägliche Abrechnungslauf (06:00) hat seit dem Update noch nicht stattgefunden.", null, null, null, null));
        else if (now - lastBillingSuccess.StartedAt > TimeSpan.FromHours(30))
            issues.Add(new("critical", "billing-job-stale", $"Abrechnungslauf seit {(int)(now - lastBillingSuccess.StartedAt).TotalHours} h nicht gelaufen",
                "Hangfire-Job 'subscription-billing' prüfen (Server/App-Pool gestoppt?).", null, null, null, lastBillingSuccess.StartedAt));
        if (lastBilling != null && (!lastBilling.Succeeded || lastBilling.Failures > 0) && lastBilling.FinishedAt != null)
            issues.Add(new("critical", "billing-job-failed", $"Letzter Abrechnungslauf mit {lastBilling.Failures} Fehler(n)", Truncate(lastBilling.Error), null, null, null, lastBilling.StartedAt));
        else if (lastBilling != null && lastBilling.FinishedAt == null && now - lastBilling.StartedAt > TimeSpan.FromHours(2))
            issues.Add(new("critical", "billing-job-failed", "Abrechnungslauf wurde nicht beendet", "Der Lauf ist abgebrochen (Absturz/Neustart während des Laufs).", null, null, null, lastBilling.StartedAt));

        var invoices = await _db.Invoices.AsNoTracking()
            .Where(i => i.SubscriptionId != null && i.Status != InvoiceStatus.Paid && i.Status != InvoiceStatus.Cancelled && i.Status != InvoiceStatus.Draft &&
                        (i.PaymentCollectionStatus != null || i.ChargedBackAt != null))
            .Select(i => new { i.Id, i.InvoiceNumber, i.SubscriptionId, CustomerName = i.Customer.CompanyName, i.Status, i.PaymentCollectionStatus, i.PaymentCollectionDueDate, i.CollectionAttemptCount, i.ChargedBackAt, i.ChargebackAmount, i.GrossTotal })
            .ToListAsync(ct);
        foreach (var i in invoices)
        {
            if (i.ChargedBackAt != null)
                issues.Add(new("critical", "chargeback", $"Rücklastschrift {i.InvoiceNumber}", $"{i.ChargebackAmount ?? i.GrossTotal:N2} € zurückgebucht — Kunde kontaktieren.", i.CustomerName, i.SubscriptionId, i.Id, i.ChargedBackAt));
            else if (i.PaymentCollectionStatus == "scheduled" && i.PaymentCollectionDueDate < today)
                issues.Add(new("critical", "collection-overdue", $"Einzug {i.InvoiceNumber} überfällig", $"Geplant für {i.PaymentCollectionDueDate:dd.MM.yyyy}, aber noch nicht gestartet.", i.CustomerName, i.SubscriptionId, i.Id, i.PaymentCollectionDueDate));
            else if (i.PaymentCollectionStatus != null && InFlight.Contains(i.PaymentCollectionStatus) && i.PaymentCollectionDueDate != null && AddBusinessDays(i.PaymentCollectionDueDate.Value, 5) < now)
                issues.Add(new("warning", "collection-stuck", $"Einzug {i.InvoiceNumber} hängt", $"Status '{i.PaymentCollectionStatus}' seit mehr als 5 Werktagen.", i.CustomerName, i.SubscriptionId, i.Id, i.PaymentCollectionDueDate));
            else if (i.PaymentCollectionStatus != null && Failed.Contains(i.PaymentCollectionStatus))
                issues.Add(new("critical", "collection-failed", $"Einzug {i.InvoiceNumber} endgültig fehlgeschlagen", $"{i.CollectionAttemptCount} Versuche — manuell nachverfolgen.", i.CustomerName, i.SubscriptionId, i.Id, i.PaymentCollectionDueDate));
            else if (i.PaymentCollectionStatus == "scheduled" && i.CollectionAttemptCount > 0)
                issues.Add(new("warning", "collection-failed", $"Einzug {i.InvoiceNumber} fehlgeschlagen, erneuter Versuch geplant", $"Versuch {i.CollectionAttemptCount + 1} am {i.PaymentCollectionDueDate:dd.MM.yyyy}.", i.CustomerName, i.SubscriptionId, i.Id, i.PaymentCollectionDueDate));
        }

        var subs = await _db.CustomerSubscriptions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => !s.IsDeleted && !s.Customer.IsDeleted &&
                        s.Status != SubscriptionStatus.Cancelled && s.Status != SubscriptionStatus.Expired && s.Status != SubscriptionStatus.Completed)
            .Include(s => s.Customer).Include(s => s.Plan)
            .ToListAsync(ct);
        foreach (var s in subs)
        {
            var mandateValid = string.Equals(s.MollieMandateStatus, "valid", StringComparison.OrdinalIgnoreCase);
            var title = s.InstallmentSourceTitle ?? s.Plan?.Name ?? "Abo";
            if (!mandateValid && s.Status != SubscriptionStatus.Paused && now - s.CreatedAt > TimeSpan.FromDays(3))
                issues.Add(new("warning", "mandate-missing", $"Mandat fehlt: {title}", s.MollieMandateStatus == "pending" ? "Mandat wird von Mollie/Bank noch geprüft." : "Kunde hat die Zahlungsart noch nicht eingerichtet.", s.Customer.CompanyName, s.Id, null, s.CreatedAt));
            if (!mandateValid && string.Equals(s.MandateEmailStatus, "failed", StringComparison.OrdinalIgnoreCase))
                issues.Add(new("warning", "mandate-email-failed", $"Mandats-E-Mail fehlgeschlagen: {title}", s.MandateEmailLastError, s.Customer.CompanyName, s.Id, null, s.MandateEmailSentAt ?? s.UpdatedAt));
            if (mandateValid && s.Status != SubscriptionStatus.Paused && (s.BillingAuthorizedAt == null || s.Status == SubscriptionStatus.PendingConfirmation))
                issues.Add(new("warning", "authorization-missing", $"Abrechnung nicht freigegeben: {title}", "Mandat ist gültig, aber die Abrechnung wurde nie freigegeben (\"Rechnung jetzt senden\" bzw. Rechnung zum Angebot erstellen).", s.Customer.CompanyName, s.Id, null, s.CreatedAt));
        }

        var webhookErrors = await _db.JobRunLogs.AsNoTracking().Where(j => j.JobName == "mollie-webhook" && !j.Succeeded && j.StartedAt > now.AddHours(-24)).ToListAsync(ct);
        if (webhookErrors.Count > 0)
            issues.Add(new("warning", "webhook-errors", $"{webhookErrors.Count} Mollie-Webhook-Fehler in 24 h", Truncate(webhookErrors.OrderByDescending(e => e.StartedAt).First().Error), null, null, null, webhookErrors.Min(e => e.StartedAt)));

        var order = new Dictionary<string, int> { ["critical"] = 0, ["warning"] = 1, ["info"] = 2 };
        issues = issues.OrderBy(i => order[i.Severity]).ThenBy(i => i.Since ?? now).ToList();

        return new PaymentMonitoringDto(
            lastBilling == null ? null : new BillingRunDto(lastBilling.StartedAt, lastBilling.FinishedAt, lastBilling.Succeeded, lastBilling.InvoicesCreated, lastBilling.CollectionsStarted, lastBilling.Failures, lastBilling.Error),
            lastReconcile == null ? null : new MandateReconcileRunDto(lastReconcile.StartedAt, lastReconcile.FinishedAt, lastReconcile.Succeeded, lastReconcile.Updated, lastReconcile.Error),
            issues,
            new PaymentIssueCountsDto(issues.Count(i => i.Severity == "critical"), issues.Count(i => i.Severity == "warning"), issues.Count(i => i.Severity == "info")));
    }

    private static DateTimeOffset AddBusinessDays(DateTimeOffset date, int days)
    {
        while (days > 0)
        {
            date = date.AddDays(1);
            if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) days--;
        }
        return date;
    }

    private static string? Truncate(string? text) => text == null ? null : text.Length > 500 ? text[..500] + " …" : text;
}
