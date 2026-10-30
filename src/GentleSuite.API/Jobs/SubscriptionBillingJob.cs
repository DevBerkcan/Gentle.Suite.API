using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;
using GentleSuite.Infrastructure.Data;
using GentleSuite.Application.Interfaces;
using GentleSuite.Infrastructure.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace GentleSuite.Infrastructure.Jobs;

public class SubscriptionBillingJob
{
    private readonly AppDbContext _db;
    private readonly INumberSequenceService _seq;
    private readonly IEmailService _email;
    private readonly IPdfService _pdf;
    private readonly IMolliePaymentService _mollie;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SubscriptionBillingJob> _logger;
    private readonly IAgencyContractService _agencyContracts;

    public SubscriptionBillingJob(AppDbContext db, INumberSequenceService seq, IEmailService email, IPdfService pdf, IMolliePaymentService mollie, IConfiguration configuration, ILogger<SubscriptionBillingJob> logger, IAgencyContractService agencyContracts)
    {
        _db = db;
        _seq = seq;
        _agencyContracts = agencyContracts;
        _email = email;
        _pdf = pdf;
        _mollie = mollie;
        _configuration = configuration;
        _logger = logger;
    }

    private async Task NotifyStaffAsync(string subject, string message, CompanySettings co, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(co.Email)) return;
            await _email.SendEmailAsync(co.Email, subject, $"<p>{message}</p>", ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Staff alert email failed: {Subject}", subject);
        }
    }

    private int PreNotificationDays => Math.Max(1, _configuration.GetValue<int?>("Mollie:PreNotificationDays") ?? 14);
    private static readonly string[] OpenCollectionStatuses = { "scheduled", "open", "pending", "authorized" };

    /// <summary>Subscription incl. plan/customer/contacts/locations — ignoring the soft-delete filter on the
    /// plan, so deleting a plan can never silently hide a running subscription from billing.</summary>
    private IQueryable<CustomerSubscription> BillableSubscriptions() => _db.CustomerSubscriptions.IgnoreQueryFilters()
        .Where(s => !s.IsDeleted && !s.Customer.IsDeleted)
        .Include(s => s.Plan)
        .Include(s => s.Customer).ThenInclude(c => c.Contacts.Where(x => !x.IsDeleted))
        .Include(s => s.Customer).ThenInclude(c => c.Locations.Where(x => !x.IsDeleted));

    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    public async Task RunAsync()
    {
        var run = new JobRunLog { JobName = "subscription-billing", StartedAt = DateTimeOffset.UtcNow };
        _db.JobRunLogs.Add(run);
        await _db.SaveChangesAsync();

        var today = BerlinTime.Today;
        var preNotificationDays = PreNotificationDays;
        var horizon = today.AddDays(preNotificationDays + 1);
        var errors = new List<string>();
        var invoicesCreated = 0;
        var collectionsStarted = 0;
        var co = await _db.CompanySettings.AsNoTracking().FirstOrDefaultAsync()
            ?? new CompanySettings { CompanyName = "GentleSuite" };

        try
        {
            var dueIds = await _db.CustomerSubscriptions.IgnoreQueryFilters()
                .Where(s => !s.IsDeleted && !s.Customer.IsDeleted &&
                    s.Status == SubscriptionStatus.Active &&
                    s.MollieMandateStatus == "valid" &&
                    s.BusinessCustomerConfirmed &&
                    s.AgreedMonthlyPrice > 0 &&
                    s.BillingAuthorizedAt != null &&
                    s.NextBillingDate < horizon &&
                    !(s.IsInstallmentPlan && s.InstallmentsCompleted >= (s.ContractDurationMonths ?? 1)))
                .OrderBy(s => s.NextBillingDate)
                .Select(s => s.Id)
                .ToListAsync();

            // Jede Subscription isoliert: ein Fehler (z. B. PDF, Nummernkreis, DB) darf weder die übrigen
            // Abos noch den anschließenden Einzug fälliger Rechnungen verhindern.
            foreach (var id in dueIds)
            {
                try
                {
                    _db.ChangeTracker.Clear();
                    var sub = await BillableSubscriptions().FirstOrDefaultAsync(s => s.Id == id);
                    if (sub == null) continue;
                    if (await BillSubscriptionAsync(sub, co, preNotificationDays, today) != null) invoicesCreated++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Billing failed for subscription {SubId}", id);
                    errors.Add($"Abrechnung Abo {id}: {ex.Message}{(ex.InnerException != null ? " => " + ex.InnerException.Message : "")}");
                    _db.ChangeTracker.Clear();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading due subscriptions failed");
            errors.Add($"Fällige Abos konnten nicht geladen werden: {ex.Message}");
        }

        // Always runs, even if billing above failed.
        try
        {
            _db.ChangeTracker.Clear();
            var (started, collectErrors) = await CollectDueInvoicesAsync(today);
            collectionsStarted = started;
            errors.AddRange(collectErrors);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Collecting due invoices failed");
            errors.Add($"Einzug fälliger Rechnungen abgebrochen: {ex.Message}");
        }

        if (errors.Count > 0)
            await NotifyStaffAsync(
                $"Abrechnungslauf: {errors.Count} Fehler",
                "Beim täglichen Abrechnungs-/Einzugslauf sind Fehler aufgetreten. Betroffene Vorgänge werden beim nächsten Lauf erneut versucht:<br/>" +
                string.Join("<br/>", errors.Take(50).Select(System.Net.WebUtility.HtmlEncode)),
                co, CancellationToken.None);

        _db.ChangeTracker.Clear();
        var entry = await _db.JobRunLogs.FirstAsync(x => x.Id == run.Id);
        entry.FinishedAt = DateTimeOffset.UtcNow;
        entry.Succeeded = errors.Count == 0;
        entry.InvoicesCreated = invoicesCreated;
        entry.CollectionsStarted = collectionsStarted;
        entry.Failures = errors.Count;
        entry.Error = errors.Count == 0 ? null : string.Join("\n", errors.Take(50));
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Bills a single subscription/installment plan immediately, bypassing the NextBillingDate due-date
    /// window used by the automatic daily run — used by the "Rechnung jetzt senden" admin action. The
    /// legally required SEPA pre-notification period still applies to the actual collection date, only
    /// invoice creation/dispatch happens synchronously here.
    /// </summary>
    public async Task<CustomerSubscription> BillNowAsync(Guid subscriptionId)
    {
        var sub = await BillableSubscriptions().FirstOrDefaultAsync(s => s.Id == subscriptionId)
            ?? throw new KeyNotFoundException("Abonnement wurde nicht gefunden.");

        if (!string.Equals(sub.MollieMandateStatus, "valid", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Das Zahlungsmandat wurde noch nicht bestätigt. Die Rechnung kann erst danach versendet werden.");
        if (sub.Status is SubscriptionStatus.Cancelled or SubscriptionStatus.Completed or SubscriptionStatus.Expired)
            throw new InvalidOperationException("Für dieses Abonnement kann keine weitere Rechnung mehr gestellt werden.");
        if (sub.IsInstallmentPlan && sub.InstallmentsCompleted >= (sub.ContractDurationMonths ?? 1))
            throw new InvalidOperationException("Alle Raten dieses Ratenplans sind bereits fakturiert.");
        if (await HasOpenCollectionAsync(sub.Id))
            throw new InvalidOperationException("Für dieses Abonnement läuft bereits ein offener Einzug. Die nächste Rechnung kann erst danach erstellt werden.");
        if (!await _agencyContracts.IsSubscriptionContractSatisfiedAsync(sub.Id))
            throw new InvalidOperationException("Für dieses Abonnement muss zuerst ein beidseitig unterschriebener Agenturvertrag vorliegen, bevor eine Rechnung erstellt werden kann.");

        sub.BillingAuthorizedAt ??= DateTimeOffset.UtcNow;
        if (sub.Status == SubscriptionStatus.PendingConfirmation) sub.Status = SubscriptionStatus.Active;
        await _db.SaveChangesAsync();

        var co = await _db.CompanySettings.AsNoTracking().FirstOrDefaultAsync() ?? new CompanySettings { CompanyName = "GentleSuite" };
        var today = BerlinTime.Today;

        await BillSubscriptionAsync(sub, co, PreNotificationDays, today);

        // Attempt collection scheduling immediately too (still respects PaymentCollectionDueDate — the
        // actual SEPA debit only fires once that legally required date is reached).
        var (_, errors) = await CollectDueInvoicesAsync(today);
        if (errors.Count > 0)
            await NotifyStaffAsync("Mollie-Einzug fehlgeschlagen", string.Join("<br/>", errors.Select(System.Net.WebUtility.HtmlEncode)), co, CancellationToken.None);

        return sub;
    }

    private Task<bool> HasOpenCollectionAsync(Guid subscriptionId) => _db.Invoices.AnyAsync(i =>
        i.SubscriptionId == subscriptionId &&
        i.Status != InvoiceStatus.Paid && i.Status != InvoiceStatus.Cancelled &&
        i.PaymentCollectionStatus != null && OpenCollectionStatuses.Contains(i.PaymentCollectionStatus));

    private async Task<Invoice?> BillSubscriptionAsync(CustomerSubscription sub, CompanySettings co, int preNotificationDays, DateTimeOffset today)
    {
        var installmentCount = sub.ContractDurationMonths ?? 1;
        if (sub.IsInstallmentPlan && sub.InstallmentsCompleted >= installmentCount)
            return null;

        // Never more than one open (unpaid, not yet settled) collection per subscription — otherwise a
        // backlog produces several debits on consecutive days.
        if (await HasOpenCollectionAsync(sub.Id))
        {
            _logger.LogInformation("Subscription {SubId} still has an open collection; next invoice postponed", sub.Id);
            return null;
        }

        var periodStart = sub.NextBillingDate;
        // Rückstand (Mandat/Freigabe kam später als der geplante Termin, oder der Job lief nicht): verpasste
        // Perioden werden NICHT nachträglich an aufeinanderfolgenden Tagen eingezogen — der Plan startet ab
        // heute neu (Anker = Aktivierungstag). Bei bereits laufendem Abo mit festem Abbuchungstag erst, wenn auch dessen
        // Termin vorbei ist (CollectionSchedule.IsBacklog); ohne festen Tag unverändert "periodStart < today".
        if (CollectionSchedule.IsBacklog(periodStart, sub.CollectionDayOfMonth, sub.BillingAnchorDate != null, today))
        {
            _logger.LogInformation("Subscription {SubId}: billing date {Date:yyyy-MM-dd} lies in the past; schedule restarts today", sub.Id, periodStart);
            periodStart = today;
            sub.BillingAnchorDate = today;
            sub.NextBillingDate = today;
        }
        var anchor = sub.BillingAnchorDate ??= periodStart;
        var stepMonths = BillingMath.CycleMonths(sub.ContractBillingCycle);
        var periodEnd = BillingMath.NextPeriodStart(anchor, periodStart, stepMonths);

            var alreadyExists = await _db.Invoices.AnyAsync(i =>
                i.SubscriptionId == sub.Id &&
                i.BillingPeriodStart != null &&
                i.BillingPeriodStart.Value.Date == periodStart.Date);

            if (alreadyExists)
            {
                sub.NextBillingDate = periodEnd;
                await _db.SaveChangesAsync();
                return null;
            }

            var year = DateTime.UtcNow.Year;
            var invoiceNumber = sub.IsInstallmentPlan
                ? await _seq.NextNumberAsync("InstallmentInvoice", year, "RA", 4, CancellationToken.None, includeYear: false)
                : await _seq.NextNumberAsync("SubscriptionInvoice", year, "AB", 4, CancellationToken.None, includeYear: false);

            var billingMonths = stepMonths;
            var vatPercent = BillingMath.VatPercent(co.DefaultTaxMode);

            bool isLastInstallment = false;
            decimal netPrice;
            if (sub.IsInstallmentPlan)
            {
                var total = sub.TotalInstallmentAmount ?? 0m;
                var baseAmount = sub.AgreedMonthlyPrice ?? BillingMath.InstallmentAmount(total, installmentCount);
                isLastInstallment = sub.InstallmentsCompleted >= installmentCount - 1;
                netPrice = BillingMath.InstallmentNet(total, installmentCount, baseAmount, sub.InstallmentsCompleted);
            }
            else
            {
                netPrice = (sub.AgreedMonthlyPrice ?? sub.Plan.MonthlyPrice) * billingMonths;
            }
            var grossTotal = BillingMath.Gross(netPrice, co.DefaultTaxMode);
            // Einzugstag = Periodenbeginn bzw. fester Abbuchungstag (CollectionDayOfMonth), frühestens aber nach Ablauf
            // der SEPA-Vorabankündigung (Berliner Datum). Wird nur bei Erstellung gesetzt — spätere Änderungen des
            // Abbuchungstags lassen bereits erstellte Rechnungen unberührt.
            var collectionDueDate = CollectionSchedule.DueDate(periodStart, sub.CollectionDayOfMonth, today, preNotificationDays);

            var inv = new Invoice
            {
                InvoiceNumber = invoiceNumber,
                Type = InvoiceType.Recurring,
                CustomerId = sub.CustomerId,
                SubscriptionId = sub.Id,
                BillingPeriodStart = periodStart,
                BillingPeriodEnd = periodEnd,
                Subject = sub.IsInstallmentPlan
                    ? $"Ratenzahlung – {sub.InstallmentSourceTitle ?? sub.Plan.Name} (Rate {sub.InstallmentsCompleted + 1} von {sub.ContractDurationMonths})"
                    : $"Serienrechnung – {sub.Plan.Name}",
                TaxMode = co.DefaultTaxMode,
                InvoiceDate = DateTimeOffset.UtcNow,
                DueDate = collectionDueDate,
                SellerTaxId = co.TaxId,
                SellerVatId = co.VatId,
                Status = InvoiceStatus.Final,
                IsFinalized = true,
                FinalizedAt = DateTimeOffset.UtcNow,
                RetentionUntil = DateTimeOffset.UtcNow.AddYears(Invoice.RetentionYears),
                PaymentCollectionStatus = "scheduled",
                PaymentCollectionDueDate = collectionDueDate,
                PaymentTerms = $"{MandateLabels.CollectionSentence(sub.MollieMandateMethod, grossTotal, collectionDueDate)} Vertragsgrundlage: {sub.ContractReference ?? "-"}, Version {sub.ContractVersion?.ToString() ?? "-"}. Mollie-Mandat: {sub.MollieMandateId}."
            };

            inv.Lines.Add(new InvoiceLine
            {
                Title = sub.IsInstallmentPlan ? (sub.InstallmentSourceTitle ?? sub.Plan.Name) : sub.Plan.Name,
                Description = sub.IsInstallmentPlan
                    ? $"Rate {sub.InstallmentsCompleted + 1} von {sub.ContractDurationMonths} · Gesamtbetrag {sub.TotalInstallmentAmount:N2} €"
                    : $"Abrechnungszeitraum: {periodStart:dd.MM.yyyy} – {periodEnd:dd.MM.yyyy}",
                Unit = "Monat",
                Quantity = 1,
                UnitPrice = netPrice,
                VatPercent = vatPercent,
                SortOrder = 0
            });

            inv.RecalculateTotals();

            var lastHash = await _db.Invoices
                .Where(i => i.IsFinalized)
                .OrderByDescending(i => i.FinalizedAt)
                .Select(i => i.DocumentHash)
                .FirstOrDefaultAsync();

            var content = $"{inv.InvoiceNumber}|{inv.GrossTotal}|{inv.InvoiceDate:O}|{lastHash ?? "GENESIS"}";
            using var sha = SHA256.Create();
            inv.DocumentHash = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(content)));
            inv.PreviousDocumentHash = lastHash;

            _db.Invoices.Add(inv);

            // Nächster Termin immer vom Anker aus (keine Monatsende-Drift). Ein Ratenplan wird hier nur noch
            // "vollständig fakturiert" (InstallmentsCompleted == Anzahl) — Completed setzt erst der Zahlungseingang
            // der letzten offenen Rate (MolliePaymentService.CompleteInstallmentPlanIfFullyPaidAsync).
            if (sub.IsInstallmentPlan)
                sub.InstallmentsCompleted++;
            sub.NextBillingDate = periodEnd;

            await _db.SaveChangesAsync();

            if (sub.IsInstallmentPlan && isLastInstallment)
                _logger.LogInformation("Installment plan {SubId} fully invoiced with {Nr}; completes once all installments are paid", sub.Id, inv.InvoiceNumber);

            var contact = sub.Customer.Contacts.FirstOrDefault(c => c.IsPrimary)
                ?? sub.Customer.Contacts.FirstOrDefault();

            if (contact != null)
            {
                try
                {
                    var pdfBytes = await _pdf.GenerateInvoicePdfAsync(inv, co, CancellationToken.None);

                    await _email.SendTemplatedEmailAsync(
                        contact.Email,
                        "invoice-sent",
                        new Dictionary<string, object>
                        {
                            ["CustomerName"] = sub.Customer.CompanyName,
                            ["ContactName"] = contact.FirstName,
                            ["InvoiceNumber"] = inv.InvoiceNumber,
                            ["InvoiceDate"] = inv.InvoiceDate.ToString("dd.MM.yyyy"),
                            ["NetTotal"] = inv.NetTotal.ToString("N2"),
                            ["VatAmount"] = inv.VatAmount.ToString("N2"),
                            ["GrossTotal"] = inv.GrossTotal.ToString("N2"),
                            ["DueDate"] = inv.DueDate.ToString("dd.MM.yyyy"),
                        },
                        sub.CustomerId,
                        attachments: new[]
                        {
                            new EmailAttachment($"Rechnung_{inv.InvoiceNumber}.pdf", pdfBytes, "application/pdf")
                        },
                        ct: CancellationToken.None);

                    inv.Status = InvoiceStatus.Sent;
                    await _db.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to generate/send pre-notification invoice {Nr} for subscription {SubId}", inv.InvoiceNumber, sub.Id);
                    await NotifyStaffAsync(
                        "Serienrechnung konnte nicht versendet werden",
                        $"Rechnung {inv.InvoiceNumber} für {sub.Customer.CompanyName} (Abo {sub.Id}) konnte nicht per E-Mail zugestellt werden: {ex.Message}. Die Rechnung wurde als '{inv.Status}' gespeichert und muss manuell geprüft/erneut versendet werden.",
                        co,
                        CancellationToken.None);
                }
            }

        return inv;
    }

    private async Task<(int Started, List<string> Errors)> CollectDueInvoicesAsync(DateTimeOffset today)
    {
        var tomorrow = today.AddDays(1);
        var invoicesToCollect = await _db.Invoices
            .Where(i => i.SubscriptionId != null &&
                        (i.PaymentCollectionStatus == "scheduled" ||
                         i.PaymentCollectionStatus == "open" ||
                         i.PaymentCollectionStatus == "pending" ||
                         i.PaymentCollectionStatus == "authorized") &&
                        i.PaymentCollectionDueDate != null &&
                        i.PaymentCollectionDueDate < tomorrow &&
                        i.Status != InvoiceStatus.Paid &&
                        i.Status != InvoiceStatus.Cancelled)
            .Select(i => new { i.Id, i.InvoiceNumber, i.PaymentCollectionStatus })
            .ToListAsync();

        var started = 0;
        var errors = new List<string>();
        foreach (var invoice in invoicesToCollect)
        {
            try
            {
                _db.ChangeTracker.Clear();
                await _mollie.CollectInvoiceAsync(invoice.Id, CancellationToken.None);
                if (invoice.PaymentCollectionStatus == "scheduled") started++;
            }
            catch (Exception ex)
            {
                // Keep it scheduled. The next run first searches Mollie by invoice metadata,
                // so a timeout can be retried without creating a second charge.
                _logger.LogError(ex, "Mollie collection failed for invoice {InvoiceId}", invoice.Id);
                errors.Add($"Einzug Rechnung {invoice.InvoiceNumber}: {ex.Message}");
                _db.ChangeTracker.Clear();
            }
        }
        return (started, errors);
    }
}

/// <summary>Täglicher Mandats-Abgleich mit Mollie (verpasste Webhooks, direkt bei Mollie angelegte Mandate,
/// Mandate aus anderen Verträgen desselben Kunden).</summary>
public class MandateReconcileJob
{
    private readonly IMolliePaymentService _mollie;
    public MandateReconcileJob(IMolliePaymentService mollie) { _mollie = mollie; }

    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    public Task RunAsync() => _mollie.ReconcileMandatesAsync(CancellationToken.None);
}
