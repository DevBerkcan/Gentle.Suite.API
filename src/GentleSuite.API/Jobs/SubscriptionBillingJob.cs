using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;
using GentleSuite.Infrastructure.Data;
using GentleSuite.Application.Interfaces;
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

    public SubscriptionBillingJob(AppDbContext db, INumberSequenceService seq, IEmailService email, IPdfService pdf, IMolliePaymentService mollie, IConfiguration configuration, ILogger<SubscriptionBillingJob> logger)
    {
        _db = db;
        _seq = seq;
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

    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    public async Task RunAsync()
    {
        var today = DateTimeOffset.UtcNow.Date;
        var preNotificationDays = Math.Max(1, _configuration.GetValue<int?>("Mollie:PreNotificationDays") ?? 14);

        var dueSubs = await _db.CustomerSubscriptions
            .Include(s => s.Plan)
            .Include(s => s.Customer)
            .ThenInclude(c => c.Contacts)
            .Include(s => s.Customer)
            .ThenInclude(c => c.Locations)
            .Where(s =>
                s.Status == SubscriptionStatus.Active &&
                s.MollieMandateStatus == "valid" &&
                s.ContractQuoteId != null &&
                s.BusinessCustomerConfirmed &&
                s.AgreedMonthlyPrice > 0 &&
                s.BillingAuthorizedAt != null &&
                s.NextBillingDate.Date <= today.AddDays(preNotificationDays))
            .ToListAsync();

        var co = await _db.CompanySettings.FirstOrDefaultAsync()
            ?? new CompanySettings { CompanyName = "GentleSuite" };

        foreach (var sub in dueSubs)
            await BillSubscriptionAsync(sub, co, preNotificationDays, today);

        await CollectDueInvoicesAsync(co, today);
    }

    /// <summary>
    /// Bills a single subscription/installment plan immediately, bypassing the NextBillingDate due-date
    /// window used by the automatic daily run — used by the "Rechnung jetzt senden" admin action. The
    /// legally required SEPA pre-notification period still applies to the actual collection date, only
    /// invoice creation/dispatch happens synchronously here.
    /// </summary>
    public async Task<CustomerSubscription> BillNowAsync(Guid subscriptionId)
    {
        var sub = await _db.CustomerSubscriptions
            .Include(s => s.Plan)
            .Include(s => s.Customer).ThenInclude(c => c.Contacts)
            .Include(s => s.Customer).ThenInclude(c => c.Locations)
            .FirstOrDefaultAsync(s => s.Id == subscriptionId)
            ?? throw new KeyNotFoundException("Abonnement wurde nicht gefunden.");

        if (!string.Equals(sub.MollieMandateStatus, "valid", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Das SEPA-Mandat wurde noch nicht bestätigt. Die Rechnung kann erst danach versendet werden.");
        if (sub.Status is SubscriptionStatus.Cancelled or SubscriptionStatus.Completed or SubscriptionStatus.Expired)
            throw new InvalidOperationException("Für dieses Abonnement kann keine weitere Rechnung mehr gestellt werden.");

        sub.BillingAuthorizedAt ??= DateTimeOffset.UtcNow;
        if (sub.Status == SubscriptionStatus.PendingConfirmation) sub.Status = SubscriptionStatus.Active;
        await _db.SaveChangesAsync();

        var co = await _db.CompanySettings.FirstOrDefaultAsync() ?? new CompanySettings { CompanyName = "GentleSuite" };
        var today = DateTimeOffset.UtcNow.Date;
        var preNotificationDays = Math.Max(1, _configuration.GetValue<int?>("Mollie:PreNotificationDays") ?? 14);

        await BillSubscriptionAsync(sub, co, preNotificationDays, today);

        // Attempt collection scheduling immediately too (still respects PaymentCollectionDueDate — the
        // actual SEPA debit only fires once that legally required date is reached).
        await CollectDueInvoicesAsync(co, today);

        return sub;
    }

    private async Task<Invoice?> BillSubscriptionAsync(CustomerSubscription sub, CompanySettings co, int preNotificationDays, DateTimeOffset today)
    {
        var periodStart = sub.NextBillingDate;
            var periodEnd = sub.ContractBillingCycle switch
            {
                BillingCycle.Quarterly => periodStart.AddMonths(3),
                BillingCycle.Yearly => periodStart.AddYears(1),
                _ => periodStart.AddMonths(1)
            };

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

            var billingMonths = sub.ContractBillingCycle switch
            {
                BillingCycle.Quarterly => 3,
                BillingCycle.Yearly => 12,
                _ => 1
            };
            var vatPercent = co.DefaultTaxMode == TaxMode.Standard ? 19 : 0;

            bool isLastInstallment = false;
            decimal netPrice;
            if (sub.IsInstallmentPlan)
            {
                var total = sub.TotalInstallmentAmount ?? 0m;
                var count = sub.ContractDurationMonths ?? 1;
                var baseAmount = sub.AgreedMonthlyPrice ?? Math.Floor(total / count * 100m) / 100m;
                isLastInstallment = sub.InstallmentsCompleted >= count - 1;
                netPrice = isLastInstallment ? total - baseAmount * (count - 1) : baseAmount;
            }
            else
            {
                netPrice = (sub.AgreedMonthlyPrice ?? sub.Plan.MonthlyPrice) * billingMonths;
            }
            var vatAmount = Math.Round(netPrice * (vatPercent / 100m), 2);
            var grossTotal = netPrice + vatAmount;
            var collectionDueDate = periodStart.Date < today.AddDays(preNotificationDays)
                ? today.AddDays(preNotificationDays)
                : periodStart.Date;

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
                PaymentTerms = $"Der Rechnungsbetrag von {grossTotal:N2} € wird am {collectionDueDate:dd.MM.yyyy} auf Grundlage des erteilten SEPA-Lastschriftmandats automatisch über Mollie eingezogen. Vertragsgrundlage: {sub.ContractReference}, Version {sub.ContractVersion}. Mollie-Mandat: {sub.MollieMandateId}."
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

            if (sub.IsInstallmentPlan)
            {
                sub.InstallmentsCompleted++;
                if (isLastInstallment)
                {
                    sub.Status = SubscriptionStatus.Completed;
                    sub.EndDate = DateTimeOffset.UtcNow;
                }
                else
                {
                    sub.NextBillingDate = sub.NextBillingDate.AddMonths(1);
                }
            }
            else
            {
                var nextBilling = sub.Plan.BillingCycle switch
                {
                    BillingCycle.Quarterly => sub.NextBillingDate.AddMonths(3),
                    BillingCycle.Yearly => sub.NextBillingDate.AddYears(1),
                    _ => sub.NextBillingDate.AddMonths(1)
                };
                sub.NextBillingDate = nextBilling;
            }

            await _db.SaveChangesAsync();

            if (sub.IsInstallmentPlan && isLastInstallment)
            {
                await NotifyStaffAsync(
                    "Ratenzahlung abgeschlossen – Wartungsvertrag anbieten",
                    $"{sub.Customer.CompanyName} hat die letzte Rate ({sub.ContractDurationMonths} von {sub.ContractDurationMonths}, {sub.InstallmentSourceTitle ?? sub.Plan.Name}) mit Rechnung {inv.InvoiceNumber} erhalten. Gesamtbetrag {sub.TotalInstallmentAmount:N2} € vollständig abgerechnet (Angebot {sub.ContractReference}). Jetzt Wartungsvertrag/Folgeangebot anbieten.",
                    co,
                    CancellationToken.None);
            }

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

    private async Task CollectDueInvoicesAsync(CompanySettings co, DateTimeOffset today)
    {
        var invoicesToCollect = await _db.Invoices
            .Where(i => i.SubscriptionId != null &&
                        (i.PaymentCollectionStatus == "scheduled" ||
                         i.PaymentCollectionStatus == "open" ||
                         i.PaymentCollectionStatus == "pending" ||
                         i.PaymentCollectionStatus == "authorized") &&
                        i.PaymentCollectionDueDate != null &&
                        i.PaymentCollectionDueDate.Value.Date <= today &&
                        i.Status != InvoiceStatus.Paid &&
                        i.Status != InvoiceStatus.Cancelled)
            .Select(i => i.Id)
            .ToListAsync();

        foreach (var invoiceId in invoicesToCollect)
        {
            try
            {
                await _mollie.CollectInvoiceAsync(invoiceId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Keep it scheduled. The next run first searches Mollie by invoice metadata,
                // so a timeout can be retried without creating a second charge.
                _logger.LogError(ex, "Mollie collection failed for invoice {InvoiceId}", invoiceId);
                await NotifyStaffAsync(
                    "Mollie-Einzug fehlgeschlagen",
                    $"Der Einzugsversuch für Rechnung-ID {invoiceId} ist mit einem Fehler abgebrochen: {ex.Message}. Das System versucht es beim nächsten Lauf erneut.",
                    co,
                    CancellationToken.None);
            }
        }
    }
}
