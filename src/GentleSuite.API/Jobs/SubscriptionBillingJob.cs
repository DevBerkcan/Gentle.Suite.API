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

    public SubscriptionBillingJob(AppDbContext db, INumberSequenceService seq, IEmailService email, IPdfService pdf, IMolliePaymentService mollie, IConfiguration configuration)
    {
        _db = db;
        _seq = seq;
        _email = email;
        _pdf = pdf;
        _mollie = mollie;
        _configuration = configuration;
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
                s.NextBillingDate.Date <= today.AddDays(preNotificationDays))
            .ToListAsync();

        var co = await _db.CompanySettings.FirstOrDefaultAsync()
            ?? new CompanySettings { CompanyName = "GentleSuite" };

        foreach (var sub in dueSubs)
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
                continue;
            }

            var year = DateTime.UtcNow.Year;
            var invoiceNumber = await _seq.NextNumberAsync("Invoice", year, "RE", 4, CancellationToken.None, includeYear: false);

            var billingMonths = sub.ContractBillingCycle switch
            {
                BillingCycle.Quarterly => 3,
                BillingCycle.Yearly => 12,
                _ => 1
            };
            var vatPercent = co.DefaultTaxMode == TaxMode.Standard ? 19 : 0;
            var netPrice = (sub.AgreedMonthlyPrice ?? sub.Plan.MonthlyPrice) * billingMonths;
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
                Subject = $"Serienrechnung – {sub.Plan.Name}",
                TaxMode = co.DefaultTaxMode,
                InvoiceDate = DateTimeOffset.UtcNow,
                DueDate = collectionDueDate,
                SellerTaxId = co.TaxId,
                SellerVatId = co.VatId,
                Status = InvoiceStatus.Final,
                IsFinalized = true,
                FinalizedAt = DateTimeOffset.UtcNow,
                RetentionUntil = DateTimeOffset.UtcNow.AddYears(10),
                PaymentCollectionStatus = "scheduled",
                PaymentCollectionDueDate = collectionDueDate,
                PaymentTerms = $"Der Rechnungsbetrag von {grossTotal:N2} € wird am {collectionDueDate:dd.MM.yyyy} auf Grundlage des erteilten SEPA-Lastschriftmandats automatisch über Mollie eingezogen. Vertragsgrundlage: {sub.ContractReference}, Version {sub.ContractVersion}. Mollie-Mandat: {sub.MollieMandateId}."
            };

            inv.Lines.Add(new InvoiceLine
            {
                Title = sub.Plan.Name,
                Description = $"Abrechnungszeitraum: {periodStart:dd.MM.yyyy} – {periodEnd:dd.MM.yyyy}",
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

            var nextBilling = sub.Plan.BillingCycle switch
            {
                BillingCycle.Quarterly => sub.NextBillingDate.AddMonths(3),
                BillingCycle.Yearly => sub.NextBillingDate.AddYears(1),
                _ => sub.NextBillingDate.AddMonths(1)
            };
            sub.NextBillingDate = nextBilling;

            await _db.SaveChangesAsync();

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
                catch { }
            }
        }

        var invoicesToCollect = await _db.Invoices
            .Where(i => i.SubscriptionId != null &&
                        i.PaymentCollectionStatus == "scheduled" &&
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
                Console.Error.WriteLine($"Mollie collection failed for invoice {invoiceId}: {ex.Message}");
            }
        }
    }
}
