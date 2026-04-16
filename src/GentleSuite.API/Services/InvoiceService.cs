using AutoMapper;
using GentleSuite.Application.DTOs;
using GentleSuite.Application.Interfaces;
using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;
using GentleSuite.Infrastructure.Data;
using GentleSuite.Infrastructure.Jobs;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using s2industries.ZUGFeRD;
using System.Security.Cryptography;
using System.Text;
using DomainInvoiceType = GentleSuite.Domain.Enums.InvoiceType;
using ZUGFeRDProfile = s2industries.ZUGFeRD.Profile;

namespace GentleSuite.Infrastructure.Services;

public class InvoiceServiceImpl : IInvoiceService
{
    private readonly AppDbContext _db;
    private readonly IMapper _mapper;
    private readonly IPdfService _pdf;
    private readonly IEmailService _email;
    private readonly IActivityLogService _activity;
    private readonly INumberSequenceService _seq;
    private readonly ReminderJobs _reminders;

    public InvoiceServiceImpl(AppDbContext db, IMapper mapper, IPdfService pdf, IEmailService email, IActivityLogService activity, INumberSequenceService seq)
    { _db = db; _mapper = mapper; _pdf = pdf; _email = email; _activity = activity; _seq = seq; }

    public async Task<PagedResult<InvoiceListDto>> GetInvoicesAsync(PaginationParams p, InvoiceStatus? status, Guid? customerId, CancellationToken ct)
    {
        var q = _db.Invoices.Include(i => i.Customer).Include(i => i.Lines).AsQueryable();
        if (status.HasValue) q = q.Where(i => i.Status == status.Value);
        if (customerId.HasValue) q = q.Where(i => i.CustomerId == customerId.Value);
        if (!string.IsNullOrWhiteSpace(p.Search)) q = q.Where(i => i.InvoiceNumber.Contains(p.Search) || i.Customer.CompanyName.ToLower().Contains(p.Search.ToLower()));
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(i => i.InvoiceDate).Skip((p.Page-1)*p.PageSize).Take(p.PageSize).ToListAsync(ct);
        return new PagedResult<InvoiceListDto>(_mapper.Map<List<InvoiceListDto>>(items), total, p.Page, p.PageSize);
    }

    public async Task<InvoiceDetailDto?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var inv = await _db.Invoices.Include(i => i.Customer).ThenInclude(c => c.Contacts).Include(i => i.Lines.OrderBy(l => l.SortOrder)).Include(i => i.Payments).FirstOrDefaultAsync(i => i.Id == id, ct);
        return inv == null ? null : _mapper.Map<InvoiceDetailDto>(inv);
    }

    public async Task<InvoiceDetailDto> CreateAsync(CreateInvoiceRequest req, CancellationToken ct)
    {
        var co = await _db.CompanySettings.FirstOrDefaultAsync(ct)
            ?? new CompanySettings { CompanyName = "Gentle Group" };
        var year = DateTime.UtcNow.Year;
        var invoiceNumber = await _seq.NextNumberAsync("Invoice", year, "RE", 4, ct, includeYear: false);
        var inv = new Invoice
        {
            InvoiceNumber = invoiceNumber,
            CustomerId = req.CustomerId,
            QuoteId = req.QuoteId,
            Subject = req.Subject,
            IntroText = req.IntroText ?? co?.InvoiceIntroTemplate,
            OutroText = req.OutroText ?? co?.InvoiceOutroTemplate,
            Notes = req.Notes,
            TaxMode = req.TaxMode,
            InvoiceDate = DateTimeOffset.UtcNow,
            DueDate = DateTimeOffset.UtcNow.AddDays(req.PaymentTermDays),
            SellerTaxId = co?.TaxId,
            SellerVatId = co?.VatId,
            Status = InvoiceStatus.Draft,
            Type = req.Type,
            RetentionUntil = DateTimeOffset.UtcNow.AddYears(10)
        };
        if (req.Lines != null)
            foreach (var l in req.Lines)
                inv.Lines.Add(new InvoiceLine { Title = l.Title, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity, UnitPrice = l.UnitPrice, VatPercent = l.VatPercent, SortOrder = l.SortOrder });
        inv.RecalculateTotals();
        _db.Invoices.Add(inv);
        await _db.SaveChangesAsync(ct);
        await _activity.LogAsync(inv.CustomerId, "Invoice", inv.Id, "Created", $"Rechnung {inv.InvoiceNumber} erstellt", ct: ct);

        if (inv.Type == DomainInvoiceType.Recurring)
            await HandleRecurringSetupAsync(inv.Id, ct);

        return (await GetByIdAsync(inv.Id, ct))!;
    }

    public async Task HandleRecurringSetupAsync(Guid invoiceId, CancellationToken ct)
    {
        var inv = await _db.Invoices
            .Include(i => i.Lines)
            .Include(i => i.Customer)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, ct)
            ?? throw new KeyNotFoundException();

        var recurringLines = inv.Lines
            .Where(l => l.LineType == 1)
            .ToList();

        if (!recurringLines.Any())
            return;

        var now = DateTimeOffset.UtcNow;

        foreach (var line in recurringLines)
        {
            var monthlyPrice = line.Quantity * line.UnitPrice;
            var discount = monthlyPrice * (line.DiscountPercent / 100m);
            monthlyPrice -= discount;

            var existingSub = await _db.CustomerSubscriptions
                .Include(s => s.Plan)
                .Where(s => s.CustomerId == inv.CustomerId
                         && s.Status == SubscriptionStatus.Active
                         && s.Plan.Name == line.Title)
                .FirstOrDefaultAsync(ct);

            Guid subscriptionId;

            if (existingSub != null)
            {
                subscriptionId = existingSub.Id;

                var existingRecurringInvoice = await _db.Invoices
                    .Where(i => i.SubscriptionId == subscriptionId
                             && i.BillingPeriodStart == null
                             && i.BillingPeriodEnd == null)
                    .FirstOrDefaultAsync(ct);

                if (existingRecurringInvoice != null && existingRecurringInvoice.Id != inv.Id)
                {
                    inv.SubscriptionId = subscriptionId;
                    await _db.SaveChangesAsync(ct);
                    continue;
                }
            }
            else
            {
                var plan = new SubscriptionPlan
                {
                    Name = line.Title,
                    Description = line.Description ?? string.Empty,
                    MonthlyPrice = monthlyPrice,
                    BillingCycle = BillingCycle.Monthly,
                    Category = SubscriptionPlanCategory.Allgemein,
                    IsActive = true
                };
                _db.SubscriptionPlans.Add(plan);
                await _db.SaveChangesAsync(ct);

                var sub = new CustomerSubscription
                {
                    CustomerId = inv.CustomerId,
                    PlanId = plan.Id,
                    Status = SubscriptionStatus.Active,
                    StartDate = now,
                    NextBillingDate = now.AddMonths(1),
                    ConfirmedAt = now
                };
                _db.CustomerSubscriptions.Add(sub);
                await _db.SaveChangesAsync(ct);

                subscriptionId = sub.Id;
            }

            if (inv.SubscriptionId != subscriptionId)
            {
                inv.SubscriptionId = subscriptionId;
                await _db.SaveChangesAsync(ct);
            }

            var existingScheduledJob = await _db.Invoices
                .Where(i => i.SubscriptionId == subscriptionId
                         && i.IsFinalized
                         && i.Id != inv.Id)
                .AnyAsync(ct);

            if (!existingScheduledJob)
            {
                BackgroundJob.Schedule<RecurringInvoiceJob>(
                    j => j.RunAsync(subscriptionId, inv.Id, CancellationToken.None),
                    existingSub?.NextBillingDate ?? now.AddMonths(1));
            }
        }

        if (!inv.IsFinalized)
        {
            await FinalizeAsync(invoiceId, new FinalizeInvoiceRequest { SendEmail = true }, ct);
        }
    }


    public async Task HandleRecurringSetupFromQuoteAsync(Guid invoiceId, List<QuoteLine> recurringLines, CancellationToken ct)
    {
        var inv = await _db.Invoices
            .Include(i => i.Customer)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, ct)
            ?? throw new KeyNotFoundException();

        // Calculate total from recurring lines only
        var monthlyPrice = recurringLines.Sum(l =>
        {
            var baseAmount = l.Quantity * l.UnitPrice;
            var discount = baseAmount * ((l.DiscountPercent) / 100m);
            return baseAmount - discount;
        });

        // Create plan name from the first recurring line or subject
        var firstRecurring = recurringLines.First();
        var planName = firstRecurring.Title;
        if (recurringLines.Count > 1)
            planName = $"{firstRecurring.Title} + {recurringLines.Count - 1} weitere Leistungen";

        var planDescription = recurringLines.FirstOrDefault()?.Description ?? string.Empty;

        var existingSub = await _db.CustomerSubscriptions
            .Include(s => s.Plan)
            .Where(s => s.CustomerId == inv.CustomerId && s.Status == SubscriptionStatus.Active)
            .FirstOrDefaultAsync(ct);

        Guid subscriptionId;

        if (existingSub != null)
        {
            subscriptionId = existingSub.Id;
        }
        else
        {
            var plan = new SubscriptionPlan
            {
                Name = planName,
                Description = planDescription,
                MonthlyPrice = monthlyPrice,
                BillingCycle = BillingCycle.Monthly,
                Category = SubscriptionPlanCategory.Allgemein,
                IsActive = true
            };
            _db.SubscriptionPlans.Add(plan);
            await _db.SaveChangesAsync(ct);

            var now = DateTimeOffset.UtcNow;
            var sub = new CustomerSubscription
            {
                CustomerId = inv.CustomerId,
                PlanId = plan.Id,
                Status = SubscriptionStatus.Active,
                StartDate = now,
                NextBillingDate = now.AddMonths(1),
                ConfirmedAt = now
            };
            _db.CustomerSubscriptions.Add(sub);
            await _db.SaveChangesAsync(ct);

            subscriptionId = sub.Id;
        }

        inv.SubscriptionId = subscriptionId;
        await _db.SaveChangesAsync(ct);

        await FinalizeAsync(invoiceId, new FinalizeInvoiceRequest { SendEmail = true }, ct);

        BackgroundJob.Schedule<RecurringInvoiceJob>(
            j => j.RunAsync(subscriptionId, invoiceId, CancellationToken.None),
            existingSub?.NextBillingDate ?? DateTimeOffset.UtcNow.AddMonths(1));
    }

    public async Task<InvoiceDetailDto> UpdateAsync(Guid id, UpdateInvoiceRequest req, CancellationToken ct)
    {
        var inv = await _db.Invoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new KeyNotFoundException();
        if (inv.IsFinalized || inv.Status != InvoiceStatus.Draft) throw new InvalidOperationException("Nur Entwurfsrechnungen koennen bearbeitet werden.");
        ValidateUpdateRequest(req);

        inv.Subject = req.Subject;
        inv.IntroText = req.IntroText;
        inv.OutroText = req.OutroText;
        inv.Notes = req.Notes;
        inv.TaxMode = req.TaxMode;
        inv.InvoiceDate = req.InvoiceDate;
        inv.DueDate = req.DueDate;

        var existingLines = inv.Lines.OrderBy(l => l.SortOrder).ToList();
        var targetLines = req.Lines ?? new List<UpdateInvoiceLineRequest>();
        var sharedCount = Math.Min(existingLines.Count, targetLines.Count);

        for (var i = 0; i < sharedCount; i++)
        {
            var existing = existingLines[i];
            var line = targetLines[i];
            existing.Title = line.Title;
            existing.Description = line.Description;
            existing.Unit = line.Unit;
            existing.Quantity = line.Quantity;
            existing.UnitPrice = line.UnitPrice;
            existing.VatPercent = line.VatPercent;
            existing.DiscountPercent = line.DiscountPercent;
            existing.SortOrder = i;
        }

        if (existingLines.Count > targetLines.Count)
        {
            var toRemove = existingLines.Skip(targetLines.Count).ToList();
            var toRemoveIds = toRemove.Select(l => l.Id).ToList();
            await _db.InvoiceLines.IgnoreQueryFilters()
                .Where(l => toRemoveIds.Contains(l.Id))
                .ExecuteDeleteAsync(ct);
            foreach (var line in toRemove) inv.Lines.Remove(line);
        }

        if (targetLines.Count > existingLines.Count)
        {
            foreach (var line in targetLines.Skip(existingLines.Count))
            {
                inv.Lines.Add(new InvoiceLine
                {
                    Title = line.Title,
                    Description = line.Description,
                    Unit = line.Unit,
                    Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice,
                    VatPercent = line.VatPercent,
                    SortOrder = inv.Lines.Count
                });
            }
        }

        inv.RecalculateTotals();
        await _db.SaveChangesAsync(ct);
        await _activity.LogAsync(inv.CustomerId, "Invoice", inv.Id, "Updated", $"Rechnung {inv.InvoiceNumber} aktualisiert", ct: ct);
        return (await GetByIdAsync(inv.Id, ct))!;
    }

    private static void ValidateUpdateRequest(UpdateInvoiceRequest req)
    {
        if (req.Lines == null || req.Lines.Count == 0) throw new ArgumentException("Mindestens eine Position ist erforderlich.");
        if (req.DueDate < req.InvoiceDate) throw new ArgumentException("Faelligkeitsdatum darf nicht vor dem Rechnungsdatum liegen.");

        for (var i = 0; i < req.Lines.Count; i++)
        {
            var line = req.Lines[i];
            if (string.IsNullOrWhiteSpace(line.Title)) throw new ArgumentException($"Position {i + 1}: Titel ist erforderlich.");
            if (line.Quantity <= 0) throw new ArgumentException($"Position {i + 1}: Menge muss groesser als 0 sein.");
            if (line.UnitPrice < 0) throw new ArgumentException($"Position {i + 1}: Preis darf nicht negativ sein.");
            if (line.VatPercent < 0 || line.VatPercent > 100) throw new ArgumentException($"Position {i + 1}: MwSt muss zwischen 0 und 100 liegen.");
        }
    }

    public async Task<InvoiceDetailDto> FinalizeAsync(Guid id, FinalizeInvoiceRequest req, CancellationToken ct)
    {
        var inv = await _db.Invoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new KeyNotFoundException();
        if (inv.IsFinalized) throw new InvalidOperationException("Already finalized");
        inv.RecalculateTotals();
        inv.Status = InvoiceStatus.Final;
        inv.IsFinalized = true;
        inv.FinalizedAt = DateTimeOffset.UtcNow;
        // GoBD hash chain
        var lastHash = await _db.Invoices.Where(i => i.IsFinalized && i.Id != inv.Id).OrderByDescending(i => i.FinalizedAt).Select(i => i.DocumentHash).FirstOrDefaultAsync(ct);
        var content = $"{inv.InvoiceNumber}|{inv.GrossTotal}|{inv.InvoiceDate:O}|{lastHash ?? "GENESIS"}";
        using var sha = SHA256.Create();
        inv.DocumentHash = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(content)));
        inv.PreviousDocumentHash = lastHash;
        await _db.SaveChangesAsync(ct);
        if (req.SendEmail) await SendAsync(id, ct);
        return (await GetByIdAsync(inv.Id, ct))!;
    }

    public async Task<InvoiceDetailDto> RecordPaymentAsync(Guid id, RecordPaymentRequest req, CancellationToken ct)
    {
        var inv = await _db.Invoices
            .AsNoTracking()
            .Select(i => new { i.Id, i.GrossTotal, i.Status })
            .FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new KeyNotFoundException();

        _db.InvoicePayments.Add(new InvoicePayment
        {
            InvoiceId = id,
            Amount = req.Amount,
            PaymentDate = req.PaymentDate ?? DateTimeOffset.UtcNow,
            PaymentMethod = req.PaymentMethod,
            Reference = req.Reference,
            Note = req.Note
        });
        await _db.SaveChangesAsync(ct);

        var totalPaid = await _db.InvoicePayments
            .Where(p => p.InvoiceId == id)
            .SumAsync(p => p.Amount, ct);

        if (totalPaid >= inv.GrossTotal)
        {
            await _db.Invoices
                .Where(i => i.Id == id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.Status, InvoiceStatus.Paid)
                    .SetProperty(i => i.PaidAt, DateTimeOffset.UtcNow), ct);
        }

        return (await GetByIdAsync(id, ct))!;
    }

    public async Task<InvoiceDetailDto> CreateCancellationAsync(Guid id, CreateCancellationRequest req, CancellationToken ct)
    {
        var orig = await _db.Invoices.Include(i => i.Customer).Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new KeyNotFoundException();
        if (!orig.IsFinalized) throw new InvalidOperationException("Only finalized invoices can be cancelled");
        orig.Status = InvoiceStatus.Cancelled;
        var year = DateTime.UtcNow.Year;
        var stornoNumber = await _seq.NextNumberAsync("CancellationInvoice", year, "SR", 4, ct, includeYear: false);
        var storno = new Invoice
        {
            InvoiceNumber = stornoNumber,
            Type = DomainInvoiceType.Cancellation,
            CustomerId = orig.CustomerId,
            CancellationOfInvoiceId = orig.Id,
            Subject = $"Storno zu {orig.InvoiceNumber}",
            Notes = req.Reason,
            TaxMode = orig.TaxMode,
            InvoiceDate = DateTimeOffset.UtcNow,
            DueDate = DateTimeOffset.UtcNow.AddDays(14),
            Status = InvoiceStatus.Final,
            IsFinalized = true,
            FinalizedAt = DateTimeOffset.UtcNow,
            RetentionUntil = DateTimeOffset.UtcNow.AddYears(10)
        };
        foreach (var l in orig.Lines) storno.Lines.Add(new InvoiceLine { Title = l.Title, Description = $"Storno: {l.Description}", Quantity = l.Quantity, UnitPrice = -l.UnitPrice, VatPercent = l.VatPercent, SortOrder = l.SortOrder });
        storno.RecalculateTotals();
        _db.Invoices.Add(storno);
        await _db.SaveChangesAsync(ct);
        return (await GetByIdAsync(storno.Id, ct))!;
    }

    public async Task<byte[]> GeneratePdfAsync(Guid id, CancellationToken ct)
    {
        var inv = await _db.Invoices.Include(i => i.Customer).ThenInclude(c => c.Contacts).Include(i => i.Customer).ThenInclude(c => c.Locations).Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new KeyNotFoundException();
        var co = await _db.CompanySettings.FirstOrDefaultAsync(ct) ?? new CompanySettings { CompanyName = "Gentle Group" };
        return await _pdf.GenerateInvoicePdfAsync(inv, co, ct);
    }

    public async Task<byte[]> GenerateXRechnungXmlAsync(Guid id, CancellationToken ct)
    {
        var inv = await _db.Invoices
            .Include(i => i.Customer).ThenInclude(c => c.Locations)
            .Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new KeyNotFoundException();
        var co = await _db.CompanySettings.FirstOrDefaultAsync(ct) ?? new CompanySettings { CompanyName = "Gentle Group" };

        var desc = InvoiceDescriptor.CreateInvoice(inv.InvoiceNumber, inv.InvoiceDate.UtcDateTime, CurrencyCodes.EUR);

        // Seller
        desc.SetSeller(
            id: "",
            name: co.LegalName ?? co.CompanyName,
            postcode: co.ZipCode ?? "",
            city: co.City ?? "",
            street: co.Street ?? "",
            country: CountryCodes.DE);
        if (!string.IsNullOrEmpty(co.VatId))
            desc.AddSellerTaxRegistration(co.VatId, TaxRegistrationSchemeID.VA);
        if (!string.IsNullOrEmpty(co.TaxId))
            desc.AddSellerTaxRegistration(co.TaxId, TaxRegistrationSchemeID.FC);

        // Buyer
        var loc = inv.Customer.Locations.FirstOrDefault(l => l.IsPrimary) ?? inv.Customer.Locations.FirstOrDefault();
        desc.SetBuyer(
            name: inv.Customer.CompanyName,
            postcode: loc?.ZipCode ?? "",
            city: loc?.City ?? "",
            street: loc?.Street ?? "",
            country: CountryCodes.DE,
            id: inv.Customer.CustomerNumber);

        // Payment terms

        // Bank account
        if (!string.IsNullOrEmpty(co.Iban))
        {
            desc.SetPaymentMeans(PaymentMeansTypeCodes.SEPACreditTransfer);
            desc.AddCreditorFinancialAccount(co.Iban, co.Bic, bankName: co.BankName);
        }

        // Determine VAT category based on TaxMode
        var vatCategory = inv.TaxMode switch
        {
            TaxMode.SmallBusiness => TaxCategoryCodes.E,   // Exempt (§19 UStG)
            TaxMode.ReverseCharge => TaxCategoryCodes.AE,  // Reverse charge
            _ => TaxCategoryCodes.S                          // Standard
        };

        // VAT summary per rate
        var vatGroups = inv.Lines.GroupBy(l => l.VatPercent);
        foreach (var grp in vatGroups)
        {
            var basisAmount = grp.Sum(l => l.NetTotal);
            var taxAmount = grp.Sum(l => l.VatAmount);
            desc.AddApplicableTradeTax(
                basisAmount: basisAmount,
                percent: grp.Key,
                taxAmount: taxAmount,
                typeCode: TaxTypes.VAT,
                categoryCode: vatCategory);
        }


        // Line items
        foreach (var line in inv.Lines)
        {
            desc.AddTradeLineItem(
                name: line.Title,
                description: line.Description,
                netUnitPrice: line.UnitPrice,
                billedQuantity: line.Quantity,
                unitCode: MapUnit(line.Unit),
                taxPercent: line.VatPercent,
                taxType: TaxTypes.VAT,
                categoryCode: vatCategory);
        }

        // Totals
        desc.SetTotals(
            lineTotalAmount: inv.NetTotal,
            taxBasisAmount: inv.NetTotal,
            taxTotalAmount: inv.VatAmount,
            grandTotalAmount: inv.GrossTotal,
            duePayableAmount: inv.GrossTotal);

        using var ms = new MemoryStream();
        desc.Save(ms, ZUGFeRDVersion.Version20, ZUGFeRDProfile.XRechnung);
        return ms.ToArray();
    }

    private static QuantityCodes MapUnit(string? unit) => unit?.ToUpperInvariant() switch
    {
        "H" or "STD" or "HUR" => QuantityCodes.HUR,
        "DAY" or "TAG" or "D" => QuantityCodes.DAY,
        "MON" or "MONAT" => QuantityCodes.MON,
        _ => QuantityCodes.H87  // Stück / piece
    };

    public async Task SendAsync(Guid id, CancellationToken ct)
    {
        var inv = await _db.Invoices
            .Include(i => i.Customer).ThenInclude(c => c.Contacts)
            .Include(i => i.Customer).ThenInclude(c => c.Locations)
            .Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new KeyNotFoundException();

        var contact = inv.Customer.Contacts.FirstOrDefault(c => c.IsPrimary)
            ?? inv.Customer.Contacts.First();

        if (inv.Status == InvoiceStatus.Final) inv.Status = InvoiceStatus.Sent;
        await _db.SaveChangesAsync(ct);

        var co = await _db.CompanySettings.FirstOrDefaultAsync(ct)
            ?? new CompanySettings { CompanyName = "Gentle Group" };

        var pdfBytes = await _pdf.GenerateInvoicePdfAsync(inv, co, ct);

        await _email.SendTemplatedEmailAsync(
            contact.Email,
            "invoice-sent",
            new Dictionary<string, object>
            {
                ["CustomerName"] = inv.Customer.CompanyName,
                ["ContactName"] = contact.FirstName,
                ["InvoiceNumber"] = inv.InvoiceNumber,
                ["InvoiceDate"] = inv.InvoiceDate.ToString("dd.MM.yyyy"),
                ["NetTotal"] = inv.NetTotal.ToString("N2"),
                ["VatAmount"] = inv.VatAmount.ToString("N2"),
                ["GrossTotal"] = inv.GrossTotal.ToString("N2"),
                ["DueDate"] = inv.DueDate.ToString("dd.MM.yyyy"),
            },
            inv.CustomerId,
            attachments: new[]
            {
            new EmailAttachment($"Rechnung_{inv.InvoiceNumber}.pdf", pdfBytes, "application/pdf")
            },
            ct: ct);

        await _activity.LogAsync(inv.CustomerId, "Invoice", inv.Id, "Sent",
            $"Rechnung {inv.InvoiceNumber} per E-Mail gesendet", ct: ct);
    }

    public async Task<InvoiceDetailDto> CreateFromTimeEntriesAsync(CreateInvoiceFromTimeEntriesRequest req, CancellationToken ct)
    {
        var entries = await _db.TimeEntries.Include(t => t.Project).Where(t => req.TimeEntryIds.Contains(t.Id) && !t.IsInvoiced && t.IsBillable).ToListAsync(ct);
        if (!entries.Any()) throw new InvalidOperationException("Keine abrechenbaren Zeiteinträge gefunden.");
        var co = await _db.CompanySettings.FirstOrDefaultAsync(ct);
        var year = DateTime.UtcNow.Year;
        var invoiceNumber = await _seq.NextNumberAsync("Invoice", year, "RE", 4, ct, includeYear: false);
        var inv = new Invoice
        {
            InvoiceNumber = invoiceNumber,
            CustomerId = req.CustomerId,
            Subject = req.Subject ?? "Zeiterfassung",
            IntroText = co?.InvoiceIntroTemplate,
            OutroText = co?.InvoiceOutroTemplate,
            TaxMode = TaxMode.Standard,
            InvoiceDate = DateTimeOffset.UtcNow,
            DueDate = DateTimeOffset.UtcNow.AddDays(req.PaymentTermDays),
            SellerTaxId = co?.TaxId,
            SellerVatId = co?.VatId,
            Status = InvoiceStatus.Draft,
            RetentionUntil = DateTimeOffset.UtcNow.AddYears(10)
        };
        int sort = 0;
        foreach (var e in entries.OrderBy(e => e.Date))
        {
            var rate = e.HourlyRate ?? 95m;
            inv.Lines.Add(new InvoiceLine
            {
                Title = e.Description,
                Description = e.Project?.Name,
                Unit = "h",
                Quantity = e.Hours,
                UnitPrice = rate,
                VatPercent = 19,
                SortOrder = sort++
            });
        }
        inv.RecalculateTotals();
        _db.Invoices.Add(inv);
        foreach (var e in entries) e.IsInvoiced = true;
        await _db.SaveChangesAsync(ct);
        await _activity.LogAsync(req.CustomerId, "Invoice", inv.Id, "CreatedFromTime", $"Rechnung {inv.InvoiceNumber} aus {entries.Count} Zeiteinträgen erstellt", ct: ct);
        return (await GetByIdAsync(inv.Id, ct))!;
    }

    public async Task SendReminderAsync(Guid id, CancellationToken ct)
    {
        var inv = await _db.Invoices.Include(i => i.Customer).ThenInclude(c => c.Contacts).FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new KeyNotFoundException();
        if (inv.ReminderStop) throw new InvalidOperationException("Mahnsperre aktiv.");
        var contact = inv.Customer.Contacts.FirstOrDefault(c => c.IsPrimary) ?? inv.Customer.Contacts.FirstOrDefault() ?? throw new InvalidOperationException("Kein Kontakt gefunden.");
        await _email.SendTemplatedEmailAsync(contact.Email, "invoice-reminder", new Dictionary<string, object>
        {
            ["CustomerName"] = inv.Customer.CompanyName,
            ["ContactName"] = contact.FirstName,
            ["InvoiceNumber"] = inv.InvoiceNumber,
            ["Amount"] = inv.GrossTotal.ToString("N2"),
            ["DueDate"] = inv.DueDate.ToString("dd.MM.yyyy"),
            ["DaysOverdue"] = (int)(DateTimeOffset.UtcNow - inv.DueDate).TotalDays
        }, inv.CustomerId, ct: ct);
        await _activity.LogAsync(inv.CustomerId, "Invoice", inv.Id, "ReminderSent", $"Mahnung gesendet für {inv.InvoiceNumber}", ct: ct);
    }
}
