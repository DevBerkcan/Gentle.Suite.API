using GentleSuite.Application.Interfaces;
using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;
using GentleSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GentleSuite.Infrastructure.Jobs;

public class ReminderJobs
{
    private readonly AppDbContext _db; private readonly IEmailService _email; private readonly ILogger<ReminderJobs> _log; private readonly string _frontendBaseUrl; private readonly INumberSequenceService _seq;
    public ReminderJobs(AppDbContext db, IEmailService email, ILogger<ReminderJobs> log, IConfiguration config, INumberSequenceService seq) { _db = db; _email = email; _log = log; _frontendBaseUrl = config["FrontendBaseUrl"] ?? "http://localhost:3000"; _seq = seq; }

    public async Task CheckOverdueInvoicesAsync()
    {
        var cfg = await _db.ReminderSettings.FirstOrDefaultAsync() ?? new ReminderSettings();
        var overdue = await _db.Invoices.Include(i => i.Customer).ThenInclude(c => c.Contacts)
            .Where(i => (i.Status == InvoiceStatus.Sent || i.Status == InvoiceStatus.Open) && i.DueDate < DateTimeOffset.UtcNow)
            .ToListAsync();
        foreach (var inv in overdue)
        {
            if (inv.ReminderStop || inv.Customer.ReminderStop) continue;
            inv.Status = InvoiceStatus.Overdue;
            var days = (DateTimeOffset.UtcNow - inv.DueDate).Days;
            ReminderLevel level; string key;
            if (days >= cfg.Level3Days && inv.LastReminderLevel != ReminderLevel.Level3) { level = ReminderLevel.Level3; key = "invoice-reminder-3"; }
            else if (days >= cfg.Level2Days && (inv.LastReminderLevel == null || inv.LastReminderLevel == ReminderLevel.Level1)) { level = ReminderLevel.Level2; key = "invoice-reminder-2"; }
            else if (days >= cfg.Level1Days && inv.LastReminderLevel == null) { level = ReminderLevel.Level1; key = "invoice-reminder-1"; }
            else continue;
            var contact = inv.Customer.Contacts.FirstOrDefault(c => c.IsPrimary) ?? inv.Customer.Contacts.FirstOrDefault();
            if (contact == null) continue;
            var fee = level == ReminderLevel.Level1 ? cfg.Level1Fee : level == ReminderLevel.Level2 ? cfg.Level2Fee : cfg.Level3Fee;
            try { await _email.SendTemplatedEmailAsync(contact.Email, key, new() { ["CustomerName"] = inv.Customer.CompanyName, ["ContactName"] = contact.FirstName, ["InvoiceNumber"] = inv.InvoiceNumber, ["Amount"] = inv.GrossTotal.ToString("N2"), ["DueDate"] = inv.DueDate.ToString("dd.MM.yyyy"), ["ReminderLevel"] = ((int)level+1).ToString(), ["ReminderFee"] = fee.ToString("N2"), ["AnnualInterestPercent"] = cfg.AnnualInterestPercent.ToString("N2") }, inv.CustomerId); inv.LastReminderLevel = level; inv.LastReminderSentAt = DateTimeOffset.UtcNow; } catch (Exception ex) { _log.LogError(ex, "Reminder failed for {Nr}", inv.InvoiceNumber); }
        }
        await _db.SaveChangesAsync();
    }

    public async Task CheckOpenQuotesAsync()
    {
        var open = await _db.Quotes.Include(q => q.Customer).ThenInclude(c => c.Contacts)
            .Where(q => (q.Status == QuoteStatus.Sent || q.Status == QuoteStatus.Viewed) && q.SentAt.HasValue && q.SentAt.Value.AddDays(7) < DateTimeOffset.UtcNow).ToListAsync();
        foreach (var q in open)
        {
            var contact = q.Customer.Contacts.FirstOrDefault(c => c.IsPrimary) ?? q.Customer.Contacts.FirstOrDefault();
            if (contact == null) continue;
            try { await _email.SendTemplatedEmailAsync(contact.Email, "quote-reminder", new() { ["CustomerName"] = q.Customer.CompanyName, ["ContactName"] = contact.FirstName, ["QuoteNumber"] = q.QuoteNumber, ["ApprovalLink"] = $"{_frontendBaseUrl}/approval/{q.ApprovalToken}" }, q.CustomerId); } catch { }
        }
    }

    public async Task GenerateSubscriptionInvoicesAsync()
    {
        var due = await _db.CustomerSubscriptions.Include(s => s.Plan).Include(s => s.Customer).ThenInclude(c => c.Contacts)
            .Where(s => s.Status == SubscriptionStatus.Active && s.NextBillingDate <= DateTimeOffset.UtcNow).ToListAsync();
        foreach (var sub in due)
        {
            try
            {
                var cycleMonths = sub.Plan.BillingCycle == BillingCycle.Quarterly ? 3 : sub.Plan.BillingCycle == BillingCycle.Yearly ? 12 : 1;
                var periodStart = sub.NextBillingDate.AddMonths(-cycleMonths);
                var periodEnd = sub.NextBillingDate;
                var exists = await _db.Invoices.AnyAsync(i => i.SubscriptionId == sub.Id && i.BillingPeriodStart == periodStart && i.BillingPeriodEnd == periodEnd);
                if (exists)
                {
                    sub.NextBillingDate = sub.NextBillingDate.AddMonths(cycleMonths);
                    await _db.SaveChangesAsync();
                    _log.LogInformation("Skipped duplicate recurring invoice for subscription {SubId} ({From} - {To})", sub.Id, periodStart, periodEnd);
                    continue;
                }

                var year = DateTime.UtcNow.Year;
                var invoiceNumber = await _seq.NextNumberAsync("Invoice", year, "RE", 4);
                var inv = new Invoice
                {
                    InvoiceNumber = invoiceNumber,
                    CustomerId = sub.CustomerId,
                    SubscriptionId = sub.Id,
                    BillingPeriodStart = periodStart,
                    BillingPeriodEnd = periodEnd,
                    Type = InvoiceType.Recurring,
                    Status = InvoiceStatus.Final,
                    InvoiceDate = DateTimeOffset.UtcNow,
                    DueDate = DateTimeOffset.UtcNow.AddDays(14),
                    ServiceDateFrom = periodStart,
                    ServiceDateTo = periodEnd,
                    RetentionUntil = DateTimeOffset.UtcNow.AddYears(10),
                    IsFinalized = true,
                    FinalizedAt = DateTimeOffset.UtcNow
                };
                inv.Lines.Add(new InvoiceLine { Title = sub.Plan.Name, Description = $"Monatliche Abrechnung {DateTimeOffset.UtcNow:MMMM yyyy}", Quantity = 1, UnitPrice = sub.Plan.MonthlyPrice, VatPercent = 19, SortOrder = 1 });
                inv.RecalculateTotals(); _db.Invoices.Add(inv);
                sub.NextBillingDate = sub.NextBillingDate.AddMonths(cycleMonths);
                if (sub.ContractDurationMonths.HasValue)
                {
                    var contractEnd = sub.StartDate.AddMonths(sub.ContractDurationMonths.Value);
                    if (sub.NextBillingDate >= contractEnd)
                    {
                        sub.Status = SubscriptionStatus.Expired;
                        sub.EndDate = contractEnd;
                        _log.LogInformation("Subscription {SubId} expired after {Months} months contract.", sub.Id, sub.ContractDurationMonths.Value);
                    }
                }
                await _db.SaveChangesAsync();

                var contact = sub.Customer.Contacts.FirstOrDefault(c => c.IsPrimary) ?? sub.Customer.Contacts.FirstOrDefault();
                if (contact != null)
                {
                    try
                    {
                        await _email.SendTemplatedEmailAsync(contact.Email, "invoice-recurring", new() {
                            ["CustomerName"] = sub.Customer.CompanyName, ["ContactName"] = contact.FirstName,
                            ["InvoiceNumber"] = inv.InvoiceNumber, ["Amount"] = inv.GrossTotal.ToString("N2"),
                            ["PlanName"] = sub.Plan.Name, ["DueDate"] = inv.DueDate.ToString("dd.MM.yyyy")
                        }, sub.CustomerId);
                        inv.Status = InvoiceStatus.Sent;
                        await _db.SaveChangesAsync();
                    }
                    catch (Exception ex) { _log.LogWarning(ex, "Email failed for invoice {Nr}, invoice created as Final", inv.InvoiceNumber); }
                }
                _log.LogInformation("Generated and finalized invoice {Nr} for {Customer} ({Amount} EUR)", inv.InvoiceNumber, sub.Customer.CompanyName, inv.GrossTotal.ToString("N2"));
            }
            catch (Exception ex)
            {
                // Unique index hit means another worker created the same billing period.
                var duplicatePeriod = ex.InnerException?.Message?.Contains("IX_Invoices_SubscriptionId_BillingPeriodStart_BillingPeriodEnd", StringComparison.OrdinalIgnoreCase) == true
                    || ex.Message.Contains("IX_Invoices_SubscriptionId_BillingPeriodStart_BillingPeriodEnd", StringComparison.OrdinalIgnoreCase);
                if (duplicatePeriod)
                {
                    _log.LogWarning("Recurring invoice already exists for subscription {SubId}; duplicate generation prevented.", sub.Id);
                    continue;
                }
                _log.LogError(ex, "Failed to generate invoice for subscription {SubId} ({Customer})", sub.Id, sub.Customer.CompanyName);
            }
        }
    }

    public async Task GenerateRecurringExpensesAsync()
    {
        var due = await _db.Expenses
            .Where(e => e.IsRecurring && e.RecurringNextDate <= DateTimeOffset.UtcNow && !e.IsDeleted)
            .ToListAsync();
        foreach (var parent in due)
        {
            try
            {
                var count = await _db.Expenses.CountAsync();
                var newExp = new Expense
                {
                    ExpenseNumber = $"AU-{DateTime.UtcNow.Year}-{(count + 1):D4}",
                    Supplier = parent.Supplier,
                    SupplierTaxId = parent.SupplierTaxId,
                    ExpenseCategoryId = parent.ExpenseCategoryId,
                    Description = parent.Description,
                    NetAmount = parent.NetAmount,
                    VatPercent = parent.VatPercent,
                    ExpenseDate = parent.RecurringNextDate!.Value,
                    RetentionUntil = DateTimeOffset.UtcNow.AddYears(10),
                    IsRecurring = true,
                    RecurringInterval = parent.RecurringInterval,
                    RecurringParentId = parent.Id
                };
                newExp.Recalculate();
                _db.Expenses.Add(newExp);
                parent.RecurringNextDate = parent.RecurringInterval switch
                {
                    RecurringInterval.Monthly => parent.RecurringNextDate!.Value.AddMonths(1),
                    RecurringInterval.Quarterly => parent.RecurringNextDate!.Value.AddMonths(3),
                    RecurringInterval.Yearly => parent.RecurringNextDate!.Value.AddYears(1),
                    _ => parent.RecurringNextDate!.Value.AddMonths(1)
                };
                await _db.SaveChangesAsync();
                _log.LogInformation("Generated recurring expense {Nr} from parent {ParentId}", newExp.ExpenseNumber, parent.Id);
            }
            catch (Exception ex) { _log.LogError(ex, "Recurring expense generation failed for {Id}", parent.Id); }
        }
    }
}
