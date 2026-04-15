using GentleSuite.Application.DTOs;
using GentleSuite.Application.Interfaces;
using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;
using GentleSuite.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace GentleSuite.Infrastructure.Jobs;

public class RecurringInvoiceJob
{
    private readonly AppDbContext _db;
    private readonly IInvoiceService _invoiceService;
    private readonly INumberSequenceService _seq;
    private readonly IActivityLogService _activity;

    public RecurringInvoiceJob(
        AppDbContext db,
        IInvoiceService invoiceService,
        INumberSequenceService seq,
        IActivityLogService activity)
    {
        _db = db;
        _invoiceService = invoiceService;
        _seq = seq;
        _activity = activity;
    }

    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync(Guid subscriptionId, Guid sourceInvoiceId, CancellationToken ct)
    {
        var sub = await _db.CustomerSubscriptions
            .Include(s => s.Plan)
            .Include(s => s.Customer)
            .FirstOrDefaultAsync(s => s.Id == subscriptionId, ct);

        if (sub == null || sub.Status != SubscriptionStatus.Active)
            return;

        var sourceInv = await _db.Invoices
            .Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.Id == sourceInvoiceId, ct);

        if (sourceInv == null)
            return;

        var co = await _db.CompanySettings.FirstOrDefaultAsync(ct);
        var year = DateTime.UtcNow.Year;
        var invoiceNumber = await _seq.NextNumberAsync("Invoice", year, "RE", 4, ct, includeYear: false);

        var billingStart = sub.NextBillingDate;
        var billingEnd = billingStart.AddMonths(1);

        var inv = new Invoice
        {
            InvoiceNumber = invoiceNumber,
            CustomerId = sub.CustomerId,
            SubscriptionId = sub.Id,
            Type = InvoiceType.Recurring,
            Subject = sourceInv.Subject ?? sub.Plan.Name,
            IntroText = sourceInv.IntroText ?? co?.InvoiceIntroTemplate,
            OutroText = sourceInv.OutroText ?? co?.InvoiceOutroTemplate,
            Notes = sourceInv.Notes,
            TaxMode = sourceInv.TaxMode,
            InvoiceDate = DateTimeOffset.UtcNow,
            DueDate = DateTimeOffset.UtcNow.AddDays(14),
            SellerTaxId = co?.TaxId,
            SellerVatId = co?.VatId,
            Status = InvoiceStatus.Draft,
            BillingPeriodStart = billingStart,
            BillingPeriodEnd = billingEnd,
            RetentionUntil = DateTimeOffset.UtcNow.AddYears(10)
        };

        foreach (var l in sourceInv.Lines)
            inv.Lines.Add(new InvoiceLine
            {
                Title = l.Title,
                Description = l.Description,
                Unit = l.Unit,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                VatPercent = l.VatPercent,
                SortOrder = l.SortOrder
            });

        inv.RecalculateTotals();
        _db.Invoices.Add(inv);

        sub.NextBillingDate = billingEnd;
        await _db.SaveChangesAsync(ct);

        await _activity.LogAsync(inv.CustomerId, "Invoice", inv.Id, "Created",
            $"Serienrechnung {inv.InvoiceNumber} automatisch erstellt (Abo: {sub.Plan.Name})", ct: ct);

        await _invoiceService.FinalizeAsync(inv.Id, new FinalizeInvoiceRequest { SendEmail = true }, ct);

        BackgroundJob.Schedule<RecurringInvoiceJob>(
            j => j.RunAsync(subscriptionId, inv.Id, CancellationToken.None),
            sub.NextBillingDate);
    }
}
