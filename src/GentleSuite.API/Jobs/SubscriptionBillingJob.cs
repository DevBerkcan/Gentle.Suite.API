using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;
using GentleSuite.Infrastructure.Data;
using GentleSuite.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace GentleSuite.Infrastructure.Jobs;

public class SubscriptionBillingJob
{
    private readonly AppDbContext _db;
    private readonly INumberSequenceService _seq;
    private readonly IEmailService _email;

    public SubscriptionBillingJob(AppDbContext db, INumberSequenceService seq, IEmailService email)
    {
        _db = db;
        _seq = seq;
        _email = email;
    }

    public async Task RunAsync()
    {
        var today = DateTimeOffset.UtcNow.Date;

        var dueSubs = await _db.CustomerSubscriptions
            .Include(s => s.Plan)
            .Include(s => s.Customer)
            .ThenInclude(c => c.Contacts)
            .Where(s =>
                s.Status == SubscriptionStatus.Active &&
                s.NextBillingDate.Date == today)
            .ToListAsync();

        var co = await _db.CompanySettings.FirstOrDefaultAsync()
            ?? new CompanySettings { CompanyName = "GentleSuite" };

        foreach (var sub in dueSubs)
        {
            var alreadyExists = await _db.Invoices.AnyAsync(i =>
                i.SubscriptionId == sub.Id &&
                i.BillingPeriodStart != null &&
                i.BillingPeriodStart.Value.Date == today);

            if (alreadyExists) continue;

            var periodStart = sub.NextBillingDate;
            var periodEnd = sub.Plan.BillingCycle switch
            {
                BillingCycle.Quarterly => periodStart.AddMonths(3),
                BillingCycle.Yearly => periodStart.AddYears(1),
                _ => periodStart.AddMonths(1)
            };

            var year = DateTime.UtcNow.Year;
            var invoiceNumber = await _seq.NextNumberAsync("Invoice", year, "RE", 4, CancellationToken.None, includeYear: false);

            var vatPercent = 19;
            var netPrice = sub.Plan.MonthlyPrice;
            var vatAmount = Math.Round(netPrice * (vatPercent / 100m), 2);
            var grossTotal = netPrice + vatAmount;

            var inv = new Invoice
            {
                InvoiceNumber = invoiceNumber,
                Type = InvoiceType.Recurring,
                CustomerId = sub.CustomerId,
                SubscriptionId = sub.Id,
                BillingPeriodStart = periodStart,
                BillingPeriodEnd = periodEnd,
                Subject = $"Serienrechnung – {sub.Plan.Name}",
                TaxMode = TaxMode.Standard,
                InvoiceDate = DateTimeOffset.UtcNow,
                DueDate = DateTimeOffset.UtcNow.AddDays(co.InvoicePaymentTermDays > 0 ? co.InvoicePaymentTermDays : 14),
                SellerTaxId = co.TaxId,
                SellerVatId = co.VatId,
                Status = InvoiceStatus.Final,
                IsFinalized = true,
                FinalizedAt = DateTimeOffset.UtcNow,
                RetentionUntil = DateTimeOffset.UtcNow.AddYears(10)
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
                    await _email.SendTemplatedEmailAsync(contact.Email, "invoice-recurring", new Dictionary<string, object>
                    {
                        ["ContactName"] = contact.FirstName,
                        ["InvoiceNumber"] = inv.InvoiceNumber,
                        ["PlanName"] = sub.Plan.Name,
                        ["Amount"] = inv.GrossTotal.ToString("N2"),
                        ["DueDate"] = inv.DueDate.ToString("dd.MM.yyyy")
                    }, sub.CustomerId, ct: CancellationToken.None);
                }
                catch { }
            }
        }
    }
}
