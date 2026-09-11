using AutoMapper;
using GentleSuite.Application.DTOs;
using GentleSuite.Application.Interfaces;
using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;
using GentleSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;

namespace GentleSuite.Infrastructure.Services;

public class QuoteServiceImpl : IQuoteService
{
    private readonly AppDbContext _db;
    private readonly IMapper _mapper;   
    private readonly IEmailService _email;
    private readonly IPdfService _pdf;
    private readonly IActivityLogService _activity;
    private readonly INumberSequenceService _seq;
    private readonly string _frontendBaseUrl;
    private readonly IInvoiceService _invoiceService;
    private readonly ISubscriptionService _subscriptionSvc;
    private readonly IMolliePaymentService _mollie;
    private readonly IFileStorageService _fs;
    private readonly ILogger<QuoteServiceImpl> _logger;
    public QuoteServiceImpl(AppDbContext db, IMapper mapper, IEmailService email, IPdfService pdf, IActivityLogService activity, IConfiguration config, INumberSequenceService seq, IInvoiceService invoiceService, ISubscriptionService subscriptionSvc, IMolliePaymentService mollie, IFileStorageService fs, ILogger<QuoteServiceImpl> logger)
    { _db = db; _invoiceService = invoiceService; _mapper = mapper; _email = email; _pdf = pdf; _activity = activity; _frontendBaseUrl = config["FrontendBaseUrl"] ?? "http://localhost:3000"; _seq = seq; _subscriptionSvc = subscriptionSvc; _mollie = mollie; _fs = fs; _logger = logger; }

    public async Task<PagedResult<QuoteListDto>> GetQuotesAsync(PaginationParams p, QuoteStatus? status, Guid? customerId, CancellationToken ct)
    {
        await QuoteLifecycle.ExpireAsync(_db, ct);
        var q = _db.Quotes.Include(x => x.Customer).Include(x => x.Lines).Where(x => x.IsCurrentVersion).AsQueryable();
        if (status.HasValue) q = q.Where(x => x.Status == status.Value);
        if (customerId.HasValue) q = q.Where(x => x.CustomerId == customerId.Value);
        if (!string.IsNullOrWhiteSpace(p.Search)) q = q.Where(x => x.QuoteNumber.Contains(p.Search) || x.Customer.CompanyName.ToLower().Contains(p.Search.ToLower()));
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(x => x.CreatedAt).Skip((p.Page-1)*p.PageSize).Take(p.PageSize).ToListAsync(ct);
        var quoteIds = items.Select(x => x.Id).ToList();
        var invoicedIds = (await _db.Invoices
            .Where(i => i.QuoteId.HasValue && quoteIds.Contains(i.QuoteId!.Value))
            .Select(i => i.QuoteId!.Value)
            .ToListAsync(ct))
            .ToHashSet();
        var dtos = items.Select(x => new QuoteListDto(x.Id, x.QuoteNumber, x.Customer.CompanyName, x.Status, x.SignatureStatus, x.GrandTotal, x.Version, x.CreatedAt, x.ExpiresAt, invoicedIds.Contains(x.Id))).ToList();
        return new PagedResult<QuoteListDto>(dtos, total, p.Page, p.PageSize);
    }

    public async Task<QuoteDetailDto?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        await QuoteLifecycle.ExpireAsync(_db, ct);
        var q = await _db.Quotes.Include(x => x.Customer).ThenInclude(c => c.Contacts).Include(x => x.Lines.OrderBy(l => l.SortOrder)).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (q == null) return null;
        var dto = _mapper.Map<QuoteDetailDto>(q);
        await ResolvePaymentTermOptionsAsync(dto, q.PaymentTermKeys, ct);
        await ResolveLegalTextOptionsAsync(dto, q.LegalTextBlocks, ct);
        ResolvePaymentPlanOptions(dto, q);
        return dto;
    }

    private async Task ResolvePaymentTermOptionsAsync(QuoteDetailDto dto, string? paymentTermKeysJson, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(paymentTermKeysJson)) return;
        var keys = JsonSerializer.Deserialize<List<string>>(paymentTermKeysJson);
        if (keys == null || keys.Count == 0) return;
        var options = await _db.PaymentTermOptions.Where(o => keys.Contains(o.Key) && o.IsActive).OrderBy(o => o.SortOrder).ToListAsync(ct);
        dto.PaymentTermOptions = _mapper.Map<List<PaymentTermOptionDto>>(options);
    }

    /// <summary>Server-computed Preisangebot options (Einmalzahlung/Hybrid/Monatlich 12/24) from the quote's
    /// SubtotalOneTime + stored PaymentPlanConfig — never trusted from the client, so admin and customer views
    /// always show identical numbers.</summary>
    private static void ResolvePaymentPlanOptions(QuoteDetailDto dto, Quote quote)
    {
        if (string.IsNullOrEmpty(quote.PaymentPlanConfig)) return;
        PaymentPlanConfigDto? cfg;
        try { cfg = JsonSerializer.Deserialize<PaymentPlanConfigDto>(quote.PaymentPlanConfig); }
        catch (JsonException) { return; } // altes/inkompatibles Format — Preisangebot muss neu konfiguriert werden
        if (cfg == null) return;
        dto.PaymentPlanConfig = cfg;
        dto.PaymentPlanOptions = PaymentPlanCalculator.Resolve(cfg, quote.SubtotalOneTime);
    }

    /// <summary>Manually chosen legal blocks plus anything marked "automatisch anhängen" (AGB/Datenschutz),
    /// so the admin/customer can see the full set of documents that actually go out with this quote.</summary>
    private async Task ResolveLegalTextOptionsAsync(QuoteDetailDto dto, string? legalTextBlockKeysJson, CancellationToken ct)
    {
        var chosenKeys = string.IsNullOrEmpty(legalTextBlockKeysJson) ? new List<string>() : JsonSerializer.Deserialize<List<string>>(legalTextBlockKeysJson) ?? new List<string>();
        var autoKeys = await _db.LegalTextBlocks.Where(b => b.IsActive && b.AutoAttachToQuotes).Select(b => b.Key).ToListAsync(ct);
        var allKeys = chosenKeys.Union(autoKeys).ToList();
        if (allKeys.Count == 0) return;
        var options = await _db.LegalTextBlocks.Where(b => allKeys.Contains(b.Key) && b.IsActive).OrderBy(b => b.SortOrder).ToListAsync(ct);
        dto.LegalTextBlockOptions = _mapper.Map<List<LegalTextBlockDto>>(options);
    }

    public async Task<QuoteDetailDto> CreateAsync(CreateQuoteRequest req, CancellationToken ct)
    {
        if (req.TemplateId.HasValue) return await CreateFromTemplateAsync(req.CustomerId, req.TemplateId.Value, ct);
        if (!await _db.Customers.AnyAsync(c => c.Id == req.CustomerId, ct)) throw new ArgumentException("Kunde wurde nicht gefunden.");
        if (req.Lines != null && req.Lines.Count > 0) ValidateQuoteLines(req.Lines);

        var co = await _db.CompanySettings.FirstOrDefaultAsync(ct);
        var taxMode = EnforceCompanyTaxMode(co?.DefaultTaxMode ?? TaxMode.Standard, req.TaxMode);
        var year = DateTime.UtcNow.Year;
        var quoteNumber = await _seq.NextNumberAsync("Quote", year, "AN", 4, ct, includeYear: false);
        var quote = new Quote
        {
            QuoteNumber = quoteNumber,
            QuoteGroupId = Guid.NewGuid(),
            IsCurrentVersion = true,
            CustomerId = req.CustomerId, ContactId = req.ContactId,
            Subject = req.Subject, IntroText = req.IntroText ?? co?.QuoteIntroTemplate,
            OutroText = req.OutroText ?? co?.QuoteOutroTemplate, Notes = req.Notes,
            TaxRate = taxMode == TaxMode.SmallBusiness ? 0 : req.TaxRate, TaxMode = taxMode, Status = QuoteStatus.Draft,
            LegalTextBlocks = req.LegalTextBlockKeys != null ? JsonSerializer.Serialize(req.LegalTextBlockKeys) : null,
            PaymentTermKeys = req.PaymentTermKeys != null ? JsonSerializer.Serialize(req.PaymentTermKeys) : null,
            InstallmentPeriodOptionsMonths = req.InstallmentPeriodOptionsMonths != null ? JsonSerializer.Serialize(req.InstallmentPeriodOptionsMonths) : null
        };
        quote.QuoteGroupId = quote.Id;
        if (req.Lines != null) foreach (var l in req.Lines)
            quote.Lines.Add(new QuoteLine { ServiceCatalogItemId = l.ServiceCatalogItemId, SubscriptionPlanId = l.SubscriptionPlanId, Title = l.Title, Description = l.Description, Quantity = l.Quantity, UnitPrice = l.UnitPrice, DiscountPercent = l.DiscountPercent, LineType = l.LineType, VatPercent = EffectiveVatPercent(taxMode, l.VatPercent), SortOrder = l.SortOrder });
        _db.Quotes.Add(quote);
        await _db.SaveChangesAsync(ct);
        await _activity.LogAsync(quote.CustomerId, "Quote", quote.Id, "Created", $"Angebot {quote.QuoteNumber} erstellt", ct: ct);
        return (await GetByIdAsync(quote.Id, ct))!;
    }

    public async Task<QuoteDetailDto> CreateFromTemplateAsync(Guid customerId, Guid templateId, CancellationToken ct)
    {
        if (!await _db.Customers.AnyAsync(c => c.Id == customerId, ct)) throw new ArgumentException("Kunde wurde nicht gefunden.");
        var tmpl = await _db.QuoteTemplates.Include(t => t.Lines.OrderBy(l => l.SortOrder)).FirstOrDefaultAsync(t => t.Id == templateId, ct) ?? throw new KeyNotFoundException("Template not found");
        var companyTaxMode = await _db.CompanySettings.Select(s => s.DefaultTaxMode).FirstOrDefaultAsync(ct);
        var year = DateTime.UtcNow.Year;
        var quoteNumber = await _seq.NextNumberAsync("Quote", year, "AN", 4, ct, includeYear: false);
        var quote = new Quote { QuoteNumber = quoteNumber, QuoteGroupId = Guid.NewGuid(), IsCurrentVersion = true, CustomerId = customerId, Subject = tmpl.Name, Notes = tmpl.Description, TaxRate = companyTaxMode == TaxMode.SmallBusiness ? 0 : 19m, TaxMode = companyTaxMode, Status = QuoteStatus.Draft };
        quote.QuoteGroupId = quote.Id;
        foreach (var tl in tmpl.Lines) quote.Lines.Add(new QuoteLine { ServiceCatalogItemId = tl.ServiceCatalogItemId, Title = tl.Title, Description = tl.Description, Quantity = tl.Quantity, UnitPrice = tl.UnitPrice, DiscountPercent = 0, LineType = tl.LineType, VatPercent = EffectiveVatPercent(companyTaxMode, 19), SortOrder = tl.SortOrder });
        var keys = new List<string>();
        if (tmpl.Lines.Any(l => l.Title.Contains("Care"))) { keys.Add("unlimited-care-fairuse"); keys.Add("sla-levels"); }
        if (keys.Any()) quote.LegalTextBlocks = JsonSerializer.Serialize(keys);
        _db.Quotes.Add(quote); await _db.SaveChangesAsync(ct);
        return (await GetByIdAsync(quote.Id, ct))!;
    }

    public async Task<QuoteDetailDto> UpdateLinesAsync(Guid id, List<CreateQuoteLineRequest> lines, CancellationToken ct)
    {
        var quote = await _db.Quotes
            .AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == id, ct) ?? throw new KeyNotFoundException();

        if (!quote.IsCurrentVersion) throw new InvalidOperationException("Nur aktuelle Angebotsversionen sind bearbeitbar.");
        if (quote.Status != QuoteStatus.Draft) throw new InvalidOperationException("Only draft quotes can be edited");
        if (lines == null || lines.Count == 0) throw new ArgumentException("Mindestens eine Angebotsposition ist erforderlich.");
        ValidateQuoteLines(lines);
        var companyTaxMode = await _db.CompanySettings.Select(s => s.DefaultTaxMode).FirstOrDefaultAsync(ct);

        await _db.QuoteLines
            .Where(l => l.QuoteId == id)
            .ExecuteDeleteAsync(ct);

        var newLines = lines.Select((l, i) => new QuoteLine
        {
            QuoteId = id,
            ServiceCatalogItemId = l.ServiceCatalogItemId,
            SubscriptionPlanId = l.SubscriptionPlanId,
            Title = l.Title,
            Description = l.Description,
            Quantity = l.Quantity,
            UnitPrice = l.UnitPrice,
            DiscountPercent = l.DiscountPercent,
            LineType = l.LineType,
            VatPercent = EffectiveVatPercent(companyTaxMode, l.VatPercent),
            SortOrder = i,
        }).ToList();

        _db.QuoteLines.AddRange(newLines);
        await _db.SaveChangesAsync(ct);

        return (await GetByIdAsync(quote.Id, ct))!;
    }


    public async Task SendAsync(Guid id, SendQuoteRequest req, CancellationToken ct)
    {
        await QuoteLifecycle.ExpireAsync(_db, ct);
        var quote = await _db.Quotes
            .Include(q => q.Customer).ThenInclude(c => c.Contacts)
            .Include(q => q.Lines)
            .FirstOrDefaultAsync(q => q.Id == id, ct) ?? throw new KeyNotFoundException();

        if (!quote.IsCurrentVersion) throw new InvalidOperationException("Nur aktuelle Angebotsversionen koennen versendet werden.");
        if (quote.Status is not (QuoteStatus.Draft or QuoteStatus.Sent or QuoteStatus.Viewed)) throw new InvalidOperationException("Bitte eine neue Angebotsversion erstellen.");
        if (req.ExpirationDays < 1 || req.ExpirationDays > 365) throw new ArgumentException("Die Gueltigkeit muss zwischen 1 und 365 Tagen liegen.");
        if (!quote.Lines.Any()) throw new ArgumentException("Ein Angebot ohne Positionen kann nicht versendet werden.");
        var co = await _db.CompanySettings.FirstOrDefaultAsync(ct);
        quote.TaxMode = EnforceCompanyTaxMode(co?.DefaultTaxMode ?? TaxMode.Standard, quote.TaxMode);
        if (quote.TaxMode == TaxMode.SmallBusiness)
        {
            quote.TaxRate = 0;
            foreach (var line in quote.Lines) line.VatPercent = 0;
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "").Replace("/", "").Replace("=", "");
        quote.ApprovalToken = token;
        quote.ApprovalTokenHash = HashApprovalToken(token);
        quote.ApprovalTokenExpiry = DateTimeOffset.UtcNow.AddDays(req.ExpirationDays);

        var chosenKeys = string.IsNullOrEmpty(quote.LegalTextBlocks)
            ? new List<string>()
            : JsonSerializer.Deserialize<List<string>>(quote.LegalTextBlocks) ?? new List<string>();
        // AGB/Datenschutz marked "automatisch anhängen" go out with every quote, regardless of manual selection.
        var autoAttachKeys = await _db.LegalTextBlocks.Where(b => b.IsActive && b.AutoAttachToQuotes).Select(b => b.Key).ToListAsync(ct);
        var allKeys = chosenKeys.Union(autoAttachKeys).ToList();
        List<EmailAttachment> legalAttachments = new();
        if (allKeys.Count > 0)
        {
            var blocks = await _db.LegalTextBlocks.Where(b => allKeys.Contains(b.Key) && b.IsActive).OrderBy(b => b.SortOrder).ToListAsync(ct);
            quote.LegalTextBlocksSnapshot = JsonSerializer.Serialize(blocks.Select(b => new { b.Key, b.Title, b.Content, b.AttachmentFileName }));
            foreach (var b in blocks.Where(b => !string.IsNullOrEmpty(b.AttachmentPath)))
            {
                try
                {
                    using var fileStream = await _fs.DownloadAsync(b.AttachmentPath!, ct);
                    using var ms = new MemoryStream();
                    await fileStream.CopyToAsync(ms, ct);
                    legalAttachments.Add(new EmailAttachment(b.AttachmentFileName ?? $"{b.Key}.pdf", ms.ToArray(), b.AttachmentContentType ?? "application/pdf"));
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to load legal document attachment {Key} for quote {QuoteId}", b.Key, quote.Id); }
            }
        }
        quote.ExpiresAt = quote.ApprovalTokenExpiry;
        quote.Status = QuoteStatus.Sent;
        quote.SentAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        var contact = quote.Customer.Contacts.FirstOrDefault(c => c.IsPrimary) ?? quote.Customer.Contacts.First();
        var recipientEmail = !string.IsNullOrWhiteSpace(req.RecipientEmail) ? req.RecipientEmail : contact.Email;

        var variables = new Dictionary<string, object>
        {
            ["CustomerName"] = quote.Customer.CompanyName,
            ["ContactName"] = contact.FirstName,
            ["QuoteNumber"] = quote.QuoteNumber,
            ["Total"] = quote.GrandTotal.ToString("N2"),
            ["SubtotalOneTime"] = quote.SubtotalOneTime.ToString("N2"),
            ["SubtotalMonthly"] = quote.SubtotalMonthly.ToString("N2"),
            ["TaxAmount"] = quote.TaxAmount.ToString("N2"),
            ["ApprovalLink"] = $"{_frontendBaseUrl}/approval/{token}",
            ["ExpiresAt"] = quote.ExpiresAt.Value.ToString("dd.MM.yyyy"),
            ["RequireSignature"] = req.RequireSignature.ToString(),
            ["CompanyName"] = co?.CompanyName ?? "",
            ["CompanyEmail"] = co?.Email ?? "",
        };

        if (!string.IsNullOrWhiteSpace(req.Message))
            variables["Message"] = req.Message;

        await _email.SendTemplatedEmailAsync(recipientEmail, "quote-sent", variables, quote.CustomerId, attachments: legalAttachments.Count > 0 ? legalAttachments : null, ct: ct);
        await _activity.LogAsync(quote.CustomerId, "Quote", quote.Id, "Sent", $"Angebot {quote.QuoteNumber} versendet", ct: ct);
    }


    public async Task<QuoteDetailDto> DeactivateAsync(Guid id, CancellationToken ct)
    {
        await QuoteLifecycle.ExpireAsync(_db, ct);
        var changed = await _db.Quotes.Where(q => q.Id == id && q.IsCurrentVersion
            && (q.Status == QuoteStatus.Draft || q.Status == QuoteStatus.Sent || q.Status == QuoteStatus.Viewed))
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.Status, QuoteStatus.Inactive)
                .SetProperty(q => q.ApprovalTokenExpiry, DateTimeOffset.UtcNow)
                .SetProperty(q => q.UpdatedAt, DateTimeOffset.UtcNow), ct);
        if (changed == 0) throw new InvalidOperationException("Nur aktuelle Entwuerfe oder offene Angebote koennen deaktiviert werden.");
        var quote = (await GetByIdAsync(id, ct))!;
        await _activity.LogAsync(quote.CustomerId, "Quote", id, "Deactivated", $"Angebot {quote.QuoteNumber} auf nicht aktiv gesetzt", ct: ct);
        return quote;
    }

    private static string HashApprovalToken(string token)
    {
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(token)));
    }

    public async Task<QuoteDetailDto?> GetByApprovalTokenAsync(string token, CancellationToken ct)
    {
        await QuoteLifecycle.ExpireAsync(_db, ct);
        var tokenHash = HashApprovalToken(token);
        var quote = await _db.Quotes.Include(q => q.Customer).ThenInclude(c => c.Contacts).Include(q => q.Lines.OrderBy(l => l.SortOrder)).FirstOrDefaultAsync(q => q.ApprovalTokenHash == tokenHash, ct);
        if (quote == null || !quote.IsCurrentVersion || quote.ApprovalTokenExpiry <= DateTimeOffset.UtcNow || quote.Status is QuoteStatus.Expired or QuoteStatus.Inactive) return null;
        if (quote.Status == QuoteStatus.Sent) { quote.Status = QuoteStatus.Viewed; quote.ViewedAt = DateTimeOffset.UtcNow; await _db.SaveChangesAsync(ct); }
        var dto = _mapper.Map<QuoteDetailDto>(quote);
        await ResolvePaymentTermOptionsAsync(dto, quote.PaymentTermKeys, ct);
        await ResolveLegalTextOptionsAsync(dto, quote.LegalTextBlocks, ct);
        ResolvePaymentPlanOptions(dto, quote);
        return dto;
    }

    public async Task ProcessApprovalAsync(string token, ApprovalRequest req, string? ipAddress, CancellationToken ct)
    {
        await QuoteLifecycle.ExpireAsync(_db, ct);
        var tokenHash = HashApprovalToken(token);
        var quote = await _db.Quotes.Include(q => q.Customer).ThenInclude(c => c.Contacts).Include(q => q.Lines).FirstOrDefaultAsync(q => q.ApprovalTokenHash == tokenHash, ct) ?? throw new KeyNotFoundException();
        if (!quote.IsCurrentVersion) throw new InvalidOperationException("Dieses Angebot wurde durch eine neuere Version ersetzt.");
        if (quote.ApprovalTokenExpiry <= DateTimeOffset.UtcNow || quote.Status is QuoteStatus.Expired or QuoteStatus.Inactive) throw new InvalidOperationException("Link expired");
        if (quote.Status is not (QuoteStatus.Sent or QuoteStatus.Viewed)) throw new InvalidOperationException("Dieses Angebot ist nicht mehr offen.");
        if (quote.RespondedAt != null) throw new InvalidOperationException("Über dieses Angebot wurde bereits entschieden. Der Link kann nicht erneut verwendet werden.");
        quote.CustomerComment = req.Comment; quote.RespondedAt = DateTimeOffset.UtcNow;
        if (req.Accepted)
        {
            if (!req.B2bAuthorityConfirmed)
                throw new InvalidOperationException("Bitte bestätigen Sie, dass Sie als Unternehmer handeln und zur Annahme für das Unternehmen berechtigt sind.");
            if (!string.IsNullOrEmpty(quote.PaymentTermKeys))
            {
                var availableKeys = JsonSerializer.Deserialize<List<string>>(quote.PaymentTermKeys) ?? new List<string>();
                if (availableKeys.Count > 0)
                {
                    if (string.IsNullOrWhiteSpace(req.ChosenPaymentTermKey) || !availableKeys.Contains(req.ChosenPaymentTermKey))
                        throw new InvalidOperationException("Bitte wählen Sie eine Zahlungsbedingung aus.");
                    quote.ChosenPaymentTermKey = req.ChosenPaymentTermKey;
                }
            }
            if (!string.IsNullOrEmpty(quote.InstallmentPeriodOptionsMonths) && quote.SubtotalOneTime > 0)
            {
                var availableMonths = JsonSerializer.Deserialize<List<int>>(quote.InstallmentPeriodOptionsMonths) ?? new List<int>();
                if (availableMonths.Count > 0)
                {
                    if (req.ChosenInstallmentMonths.HasValue && !availableMonths.Contains(req.ChosenInstallmentMonths.Value))
                        throw new InvalidOperationException("Bitte wählen Sie eine gültige Zahlungsweise aus.");
                    quote.ChosenInstallmentMonths = req.ChosenInstallmentMonths;
                }
            }
            if (!string.IsNullOrEmpty(quote.PaymentPlanConfig) && quote.SubtotalOneTime > 0)
            {
                var validKeys = new[] { "onetime", "hybrid", "monthly12", "monthly24" };
                if (string.IsNullOrWhiteSpace(req.ChosenPaymentPlanOptionKey) || !validKeys.Contains(req.ChosenPaymentPlanOptionKey))
                    throw new InvalidOperationException("Bitte wählen Sie eine Zahlungsart aus.");
                // Nur die Wahl speichern — die eigentliche Rechnung/Ratenzahlung entsteht erst, wenn der
                // Admin bewusst auf "Überführen" klickt (TransferPaymentPlanAsync), nicht automatisch hier.
                quote.ChosenPaymentPlanOptionKey = req.ChosenPaymentPlanOptionKey;
            }
            quote.Status = QuoteStatus.Accepted;
            quote.B2bAuthorityConfirmed = true;
            if (!string.IsNullOrEmpty(req.SignatureData))
            {
                quote.SignatureStatus = SignatureStatus.Signed; quote.SignatureData = req.SignatureData;
                quote.SignedByName = req.SignedByName; quote.SignedByEmail = req.SignedByEmail;
                quote.SignedAt = DateTimeOffset.UtcNow; quote.SignedIpAddress = ipAddress;
            }
            if (quote.Customer.Status == CustomerStatus.Lead) quote.Customer.Status = CustomerStatus.Active;
            // Create project
            _db.Projects.Add(new Project { CustomerId = quote.CustomerId, QuoteId = quote.Id, Name = quote.Subject ?? $"Projekt {quote.Customer.CompanyName}", Status = ProjectStatus.Planning });
            await _activity.LogAsync(quote.CustomerId, "Quote", quote.Id, "Accepted", $"Angebot {quote.QuoteNumber} akzeptiert", ct: ct);
        }
        else { quote.Status = QuoteStatus.Rejected; quote.SignatureStatus = SignatureStatus.Declined; await _activity.LogAsync(quote.CustomerId, "Quote", quote.Id, "Rejected", $"Abgelehnt: {req.Comment}", ct: ct); }
        await _db.SaveChangesAsync(ct);

        if (quote.SignatureStatus == SignatureStatus.Signed)
        {
            var planLines = quote.Lines.Where(l => l.LineType == QuoteLineType.RecurringMonthly && l.SubscriptionPlanId != null).ToList();
            foreach (var line in planLines)
            {
                try
                {
                    var created = await _subscriptionSvc.CreateFromSignedQuoteLineAsync(quote.CustomerId, quote.Id, line.Id, ct);
                    try { await _mollie.SendMandateEmailAsync(created.Id, ct); }
                    catch (Exception mex) { _logger.LogError(mex, "Mandate email failed for subscription {SubscriptionId} (quote line {LineId})", created.Id, line.Id); }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Auto subscription creation failed for quote {QuoteId} line {LineId}", quote.Id, line.Id);
                    await _activity.LogAsync(quote.CustomerId, "Quote", quote.Id, "SubscriptionCreationFailed", $"Serienrechnung für Position \"{line.Title}\" konnte nicht automatisch angelegt werden: {ex.Message}", ct: ct);
                }
            }

            if (quote.ChosenInstallmentMonths is > 0 && quote.SubtotalOneTime > 0)
            {
                try
                {
                    var created = await _subscriptionSvc.CreateInstallmentPlanFromQuoteAsync(quote.CustomerId, quote.Id, quote.ChosenInstallmentMonths.Value, ct);
                    try { await _mollie.SendMandateEmailAsync(created.Id, ct); }
                    catch (Exception mex) { _logger.LogError(mex, "Mandate email failed for installment plan {SubscriptionId} (quote {QuoteId})", created.Id, quote.Id); }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Installment plan creation failed for quote {QuoteId}", quote.Id);
                    await _activity.LogAsync(quote.CustomerId, "Quote", quote.Id, "InstallmentPlanCreationFailed", $"Ratenzahlungsplan konnte nicht automatisch angelegt werden: {ex.Message}", ct: ct);
                }
            }
        }
    }

    public async Task<byte[]> GeneratePdfAsync(Guid id, CancellationToken ct)
    {
        var quote = await _db.Quotes.Include(q => q.Customer).ThenInclude(c => c.Contacts).Include(q => q.Customer).ThenInclude(c => c.Locations).Include(q => q.Lines).FirstOrDefaultAsync(q => q.Id == id, ct) ?? throw new KeyNotFoundException();
        var co = await _db.CompanySettings.FirstOrDefaultAsync(ct) ?? new CompanySettings { CompanyName = "Gentle Group" };
        return await _pdf.GenerateQuotePdfAsync(quote, co, ct);
    }

    public async Task<List<QuoteTemplateDto>> GetTemplatesAsync(CancellationToken ct)
    {
        var ts = await _db.QuoteTemplates.Include(t => t.Lines.OrderBy(l => l.SortOrder)).Where(t => t.IsActive).ToListAsync(ct);
        return _mapper.Map<List<QuoteTemplateDto>>(ts);
    }

    public async Task<QuoteTemplateDto> CreateTemplateAsync(CreateQuoteTemplateRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) throw new ArgumentException("Template-Name ist erforderlich.");
        if (req.Lines == null || req.Lines.Count == 0) throw new ArgumentException("Template muss mindestens eine Position enthalten.");
        ValidateTemplateLines(req.Lines);
        var tmpl = new QuoteTemplate { Name = req.Name, Description = req.Description };
        if (req.Lines != null) foreach (var l in req.Lines) tmpl.Lines.Add(new QuoteTemplateLine { Title = l.Title, Description = l.Description, Quantity = l.Quantity, UnitPrice = l.UnitPrice, LineType = l.LineType, SortOrder = l.SortOrder });
        _db.QuoteTemplates.Add(tmpl); await _db.SaveChangesAsync(ct);
        return _mapper.Map<QuoteTemplateDto>(tmpl);
    }

    public async Task<QuoteTemplateDto> UpdateTemplateAsync(Guid id, UpdateQuoteTemplateRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) throw new ArgumentException("Template-Name ist erforderlich.");
        if (req.Lines == null || req.Lines.Count == 0) throw new ArgumentException("Template muss mindestens eine Position enthalten.");
        ValidateTemplateLines(req.Lines);
        var tmpl = await _db.QuoteTemplates.Include(t => t.Lines).FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new KeyNotFoundException();
        tmpl.Name = req.Name; tmpl.Description = req.Description;
        _db.QuoteTemplateLines.RemoveRange(tmpl.Lines); tmpl.Lines.Clear();
        if (req.Lines != null) foreach (var l in req.Lines) tmpl.Lines.Add(new QuoteTemplateLine { Title = l.Title, Description = l.Description, Quantity = l.Quantity, UnitPrice = l.UnitPrice, LineType = l.LineType, SortOrder = l.SortOrder });
        await _db.SaveChangesAsync(ct);
        return _mapper.Map<QuoteTemplateDto>(await _db.QuoteTemplates.Include(t => t.Lines.OrderBy(l => l.SortOrder)).FirstAsync(t => t.Id == id, ct));
    }

    public async Task DeleteTemplateAsync(Guid id, CancellationToken ct)
    {
        var tmpl = await _db.QuoteTemplates.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException();
        tmpl.IsActive = false; await _db.SaveChangesAsync(ct);
    }

    public async Task<QuoteDetailDto> MarkAsOrderedAsync(Guid quoteId, CancellationToken ct)
    {
        var quote = await _db.Quotes.Include(q => q.Customer).Include(q => q.Lines).FirstOrDefaultAsync(q => q.Id == quoteId, ct) ?? throw new KeyNotFoundException();
        if (quote.Status != QuoteStatus.Accepted)
            throw new InvalidOperationException("Nur angenommene Angebote können als Auftrag bestätigt werden.");
        quote.Status = QuoteStatus.Ordered;
        await _db.SaveChangesAsync(ct);
        await _activity.LogAsync(quote.CustomerId, "Quote", quote.Id, "Ordered", $"Angebot {quote.QuoteNumber} als Auftrag bestätigt", ct: ct);
        return _mapper.Map<QuoteDetailDto>(quote);
    }

    public async Task<InvoiceDetailDto> ConvertToInvoiceAsync(Guid quoteId, CancellationToken ct)
    {
        var quote = await _db.Quotes
            .Include(q => q.Customer)
            .Include(q => q.Lines)
            .FirstOrDefaultAsync(q => q.Id == quoteId, ct) ?? throw new KeyNotFoundException();

        var co = await _db.CompanySettings.FirstOrDefaultAsync(ct);
        var taxMode = EnforceCompanyTaxMode(co?.DefaultTaxMode ?? TaxMode.Standard, quote.TaxMode);
        var year = DateTime.UtcNow.Year;
        var invoiceNumber = await _seq.NextNumberAsync("Invoice", year, "RE", 4, ct, includeYear: false);

        var hasRecurring = quote.Lines.Any(l => l.LineType == QuoteLineType.RecurringMonthly);
        var invoiceType = hasRecurring ? InvoiceType.Recurring : InvoiceType.Standard;

        var inv = new Invoice
        {
            InvoiceNumber = invoiceNumber,
            CustomerId = quote.CustomerId,
            QuoteId = quote.Id,
            Subject = quote.Subject,
            IntroText = quote.IntroText ?? co?.InvoiceIntroTemplate,
            OutroText = quote.OutroText ?? co?.InvoiceOutroTemplate,
            Notes = quote.Notes,
            TaxMode = taxMode,
            Type = invoiceType,
            InvoiceDate = DateTimeOffset.UtcNow,
            DueDate = DateTimeOffset.UtcNow.AddDays(14),
            SellerTaxId = co?.TaxId,
            SellerVatId = co?.VatId,
            Status = InvoiceStatus.Draft,
            RetentionUntil = DateTimeOffset.UtcNow.AddYears(Invoice.RetentionYears)
        };

        foreach (var l in quote.Lines)
        {
            inv.Lines.Add(new InvoiceLine
            {
                Title = l.Title,
                Description = l.Description,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                VatPercent = EffectiveVatPercent(taxMode, l.VatPercent),
                DiscountPercent = l.DiscountPercent,
                SortOrder = l.SortOrder,
                LineType = (int)l.LineType  
            });
        }

        inv.RecalculateTotals();
        _db.Invoices.Add(inv);
        quote.Status = QuoteStatus.Ordered;
        await _db.SaveChangesAsync(ct);

        await _activity.LogAsync(inv.CustomerId, "Invoice", inv.Id, "Created",
            $"Rechnung {inv.InvoiceNumber} aus Angebot {quote.QuoteNumber} erstellt", ct: ct);

        if (inv.Type == InvoiceType.Recurring)
            await AuthorizeSubscriptionBillingAsync(quote, inv, ct);

        return (await _invoiceService.GetByIdAsync(inv.Id, ct))!;
    }

    // Marks the subscriptions tied to this quote's recurring lines as billable now that the
    // user has actually issued the invoice for them (not right at quote-signature time).
    private async Task AuthorizeSubscriptionBillingAsync(Quote quote, Invoice inv, CancellationToken ct)
    {
        var recurringLines = quote.Lines.Where(l => l.LineType == QuoteLineType.RecurringMonthly && l.SubscriptionPlanId != null).ToList();
        var authorizedSubscriptionIds = new List<Guid>();
        foreach (var line in recurringLines)
        {
            var sub = await _db.CustomerSubscriptions.FirstOrDefaultAsync(s => s.QuoteLineId == line.Id, ct);
            if (sub == null)
            {
                _logger.LogWarning("Recurring quote line {LineId} on quote {QuoteNumber} has no matching subscription; billing cannot be authorized automatically.", line.Id, quote.QuoteNumber);
                continue;
            }
            sub.BillingAuthorizedAt ??= DateTimeOffset.UtcNow;
            authorizedSubscriptionIds.Add(sub.Id);
        }
        if (authorizedSubscriptionIds.Count == 1)
            inv.SubscriptionId = authorizedSubscriptionIds[0];
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Admin-triggered "Überführen": takes the customer's signed Preisangebot choice and creates the
    /// matching invoice/Ratenzahlungsplan. Deliberately manual (not called from ProcessApprovalAsync) so the
    /// admin keeps control before anything Mollie-related gets set up.</summary>
    public async Task<QuoteDetailDto> TransferPaymentPlanAsync(Guid quoteId, CancellationToken ct)
    {
        var quote = await _db.Quotes.Include(q => q.Customer).Include(q => q.Lines).FirstOrDefaultAsync(q => q.Id == quoteId, ct) ?? throw new KeyNotFoundException();
        if (quote.PaymentPlanTransferredAt != null)
            throw new InvalidOperationException("Für dieses Angebot wurde die gewählte Zahlungsart bereits überführt.");
        if (quote.SignatureStatus != SignatureStatus.Signed || string.IsNullOrEmpty(quote.ChosenPaymentPlanOptionKey))
            throw new InvalidOperationException("Der Kunde hat noch keine Zahlungsart des Preisangebots gewählt.");
        var cfg = string.IsNullOrEmpty(quote.PaymentPlanConfig) ? null : JsonSerializer.Deserialize<PaymentPlanConfigDto>(quote.PaymentPlanConfig);
        if (cfg == null) throw new InvalidOperationException("Für dieses Angebot ist keine Preisangebot-Konfiguration hinterlegt.");

        switch (quote.ChosenPaymentPlanOptionKey)
        {
            case "onetime":
                await ConvertToInvoiceAsync(quote.Id, ct);
                break;
            case "monthly12":
            {
                var infoSurcharge = quote.SubtotalOneTime > 0 ? Math.Round((cfg.Monthly12.TotalAmount / quote.SubtotalOneTime - 1) * 100m, 2) : (decimal?)null;
                var created = await _subscriptionSvc.CreateSurchargedInstallmentPlanAsync(quote.CustomerId, quote.Id, 12, cfg.Monthly12.TotalAmount, infoSurcharge, null, null, "monthly12", ct);
                try { await _mollie.SendMandateEmailAsync(created.Id, ct); }
                catch (Exception mex) { _logger.LogError(mex, "Mandate email failed for surcharged installment plan {SubscriptionId} (quote {QuoteId})", created.Id, quote.Id); }
                break;
            }
            case "monthly24":
            {
                var infoSurcharge = quote.SubtotalOneTime > 0 ? Math.Round((cfg.Monthly24.TotalAmount / quote.SubtotalOneTime - 1) * 100m, 2) : (decimal?)null;
                var created = await _subscriptionSvc.CreateSurchargedInstallmentPlanAsync(quote.CustomerId, quote.Id, 24, cfg.Monthly24.TotalAmount, infoSurcharge, null, null, "monthly24", ct);
                try { await _mollie.SendMandateEmailAsync(created.Id, ct); }
                catch (Exception mex) { _logger.LogError(mex, "Mandate email failed for surcharged installment plan {SubscriptionId} (quote {QuoteId})", created.Id, quote.Id); }
                break;
            }
            case "hybrid":
            {
                var downPayment = Math.Round(cfg.Hybrid.TotalAmount * cfg.Hybrid.DownPaymentPercent / 100m, 2);
                var financedBase = cfg.Hybrid.TotalAmount - downPayment;
                var infoSurcharge = quote.SubtotalOneTime > 0 ? Math.Round((cfg.Hybrid.TotalAmount / quote.SubtotalOneTime - 1) * 100m, 2) : (decimal?)null;
                var dpInvoice = await CreateDownPaymentInvoiceAsync(quote, downPayment, cfg.Hybrid.DownPaymentPercent, ct);
                var created = await _subscriptionSvc.CreateSurchargedInstallmentPlanAsync(quote.CustomerId, quote.Id, cfg.Hybrid.DurationMonths, financedBase, infoSurcharge, cfg.Hybrid.DownPaymentPercent, dpInvoice.Id, "hybrid", ct);
                try { await _mollie.SendMandateEmailAsync(created.Id, ct); }
                catch (Exception mex) { _logger.LogError(mex, "Mandate email failed for surcharged installment plan {SubscriptionId} (quote {QuoteId})", created.Id, quote.Id); }
                break;
            }
            default:
                throw new InvalidOperationException("Unbekannte Zahlungsart.");
        }

        quote.PaymentPlanTransferredAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _activity.LogAsync(quote.CustomerId, "Quote", quote.Id, "PaymentPlanTransferred",
            $"Zahlungsart '{quote.ChosenPaymentPlanOptionKey}' aus Preisangebot überführt", ct: ct);
        return (await GetByIdAsync(quote.Id, ct))!;
    }

    /// <summary>Hybrid-Modell: raises a single Draft invoice for just the down-payment amount, reusing the
    /// same invoice-shape as ConvertToInvoiceAsync but with one synthetic "Anzahlung" line.</summary>
    private async Task<InvoiceDetailDto> CreateDownPaymentInvoiceAsync(Quote quote, decimal downPaymentAmount, decimal downPaymentPercent, CancellationToken ct)
    {
        var co = await _db.CompanySettings.FirstOrDefaultAsync(ct);
        var taxMode = EnforceCompanyTaxMode(co?.DefaultTaxMode ?? TaxMode.Standard, quote.TaxMode);
        var invoiceNumber = await _seq.NextNumberAsync("Invoice", DateTime.UtcNow.Year, "RE", 4, ct, includeYear: false);
        var inv = new Invoice
        {
            InvoiceNumber = invoiceNumber,
            CustomerId = quote.CustomerId,
            QuoteId = quote.Id,
            Subject = $"Anzahlung – {quote.Subject}",
            TaxMode = taxMode,
            Type = InvoiceType.Standard,
            InvoiceDate = DateTimeOffset.UtcNow,
            DueDate = DateTimeOffset.UtcNow.AddDays(14),
            SellerTaxId = co?.TaxId,
            SellerVatId = co?.VatId,
            Status = InvoiceStatus.Draft,
            RetentionUntil = DateTimeOffset.UtcNow.AddYears(Invoice.RetentionYears)
        };
        inv.Lines.Add(new InvoiceLine
        {
            Title = "Anzahlung",
            Description = $"Anzahlung {downPaymentPercent:0.#}% zu Angebot {quote.QuoteNumber}",
            Quantity = 1,
            UnitPrice = downPaymentAmount,
            VatPercent = EffectiveVatPercent(taxMode, 19),
            SortOrder = 0,
            LineType = (int)QuoteLineType.OneTime
        });
        inv.RecalculateTotals();
        _db.Invoices.Add(inv);
        await _db.SaveChangesAsync(ct);
        await _activity.LogAsync(inv.CustomerId, "Invoice", inv.Id, "Created",
            $"Anzahlungsrechnung {inv.InvoiceNumber} aus Angebot {quote.QuoteNumber} erstellt", ct: ct);
        return (await _invoiceService.GetByIdAsync(inv.Id, ct))!;
    }


    public async Task<QuoteDetailDto> UpdateAsync(Guid id, UpdateQuoteRequest req, CancellationToken ct)
    {
        var quote = await _db.Quotes.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException();
        if (!quote.IsCurrentVersion) throw new InvalidOperationException("Nur aktuelle Angebotsversionen sind bearbeitbar.");
        if (quote.Status != QuoteStatus.Draft) throw new InvalidOperationException("Only draft quotes can be edited");
        var companyTaxMode = await _db.CompanySettings.Select(s => s.DefaultTaxMode).FirstOrDefaultAsync(ct);
        if (req.Subject != null) quote.Subject = req.Subject;
        if (req.IntroText != null) quote.IntroText = req.IntroText;
        if (req.OutroText != null) quote.OutroText = req.OutroText;
        if (req.Notes != null) quote.Notes = req.Notes;
        if (req.PaymentTermKeys != null) quote.PaymentTermKeys = JsonSerializer.Serialize(req.PaymentTermKeys);
        if (req.InstallmentPeriodOptionsMonths != null) quote.InstallmentPeriodOptionsMonths = JsonSerializer.Serialize(req.InstallmentPeriodOptionsMonths);
        if (req.LegalTextBlockKeys != null) quote.LegalTextBlocks = JsonSerializer.Serialize(req.LegalTextBlockKeys);
        if (req.PaymentPlanConfig != null) quote.PaymentPlanConfig = JsonSerializer.Serialize(req.PaymentPlanConfig);
        if (req.TaxRate.HasValue) quote.TaxRate = companyTaxMode == TaxMode.SmallBusiness ? 0 : req.TaxRate.Value;
        if (req.TaxMode.HasValue) quote.TaxMode = EnforceCompanyTaxMode(companyTaxMode, req.TaxMode.Value);
        if (companyTaxMode == TaxMode.SmallBusiness) quote.TaxMode = TaxMode.SmallBusiness;
        await _db.SaveChangesAsync(ct);
        return (await GetByIdAsync(quote.Id, ct))!;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var quote = await _db.Quotes.Include(q => q.Lines).FirstOrDefaultAsync(q => q.Id == id, ct) ?? throw new KeyNotFoundException();
        if (!quote.IsCurrentVersion) throw new InvalidOperationException("Historische Versionen koennen nicht geloescht werden.");
        if (quote.Status != QuoteStatus.Draft) throw new InvalidOperationException("Only draft quotes can be deleted");
        _db.QuoteLines.RemoveRange(quote.Lines);
        _db.Quotes.Remove(quote);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<QuoteDetailDto> DuplicateAsync(Guid id, CancellationToken ct)
    {
        var original = await _db.Quotes.Include(q => q.Lines).FirstOrDefaultAsync(q => q.Id == id, ct) ?? throw new KeyNotFoundException();
        var companyTaxMode = await _db.CompanySettings.Select(s => s.DefaultTaxMode).FirstOrDefaultAsync(ct);
        var taxMode = EnforceCompanyTaxMode(companyTaxMode, original.TaxMode);
        var year = DateTime.UtcNow.Year;
        var quoteNumber = await _seq.NextNumberAsync("Quote", year, "AN", 4, ct, includeYear: false);
        var copy = new Quote
        {
            QuoteNumber = quoteNumber,
            QuoteGroupId = Guid.NewGuid(),
            IsCurrentVersion = true,
            CustomerId = original.CustomerId, ContactId = original.ContactId,
            Subject = original.Subject, IntroText = original.IntroText,
            OutroText = original.OutroText, Notes = original.Notes,
            TaxRate = taxMode == TaxMode.SmallBusiness ? 0 : original.TaxRate, TaxMode = taxMode,
            Status = QuoteStatus.Draft, LegalTextBlocks = original.LegalTextBlocks, PaymentTermKeys = original.PaymentTermKeys,
            InstallmentPeriodOptionsMonths = original.InstallmentPeriodOptionsMonths, Version = 1
        };
        copy.QuoteGroupId = copy.Id;
        foreach (var l in original.Lines)
            copy.Lines.Add(new QuoteLine { ServiceCatalogItemId = l.ServiceCatalogItemId, SubscriptionPlanId = l.SubscriptionPlanId, Title = l.Title, Description = l.Description, Quantity = l.Quantity, UnitPrice = l.UnitPrice, DiscountPercent = l.DiscountPercent, LineType = l.LineType, VatPercent = EffectiveVatPercent(taxMode, l.VatPercent), SortOrder = l.SortOrder });
        _db.Quotes.Add(copy);
        await _db.SaveChangesAsync(ct);
        await _activity.LogAsync(copy.CustomerId, "Quote", copy.Id, "Created", $"Angebot {copy.QuoteNumber} dupliziert von {original.QuoteNumber}", ct: ct);
        return (await GetByIdAsync(copy.Id, ct))!;
    }

    public async Task<QuoteDetailDto> CreateNewVersionAsync(Guid id, CancellationToken ct)
    {
        var current = await _db.Quotes.Include(q => q.Lines).FirstOrDefaultAsync(q => q.Id == id, ct) ?? throw new KeyNotFoundException();
        if (!current.IsCurrentVersion) throw new InvalidOperationException("Nur die aktuelle Version kann versioniert werden.");
        var companyTaxMode = await _db.CompanySettings.Select(s => s.DefaultTaxMode).FirstOrDefaultAsync(ct);
        var taxMode = EnforceCompanyTaxMode(companyTaxMode, current.TaxMode);

        var maxVersion = await _db.Quotes.Where(q => q.QuoteGroupId == current.QuoteGroupId).MaxAsync(q => q.Version, ct);
        var year = DateTime.UtcNow.Year;
        var quoteNumber = await _seq.NextNumberAsync("Quote", year, "AN", 4, ct, includeYear: false);

        current.IsCurrentVersion = false;
        if (current.Status == QuoteStatus.Sent || current.Status == QuoteStatus.Viewed)
        {
            current.Status = QuoteStatus.Expired;
            current.ApprovalTokenExpiry = DateTimeOffset.UtcNow;
        }

        var next = new Quote
        {
            QuoteNumber = quoteNumber,
            QuoteGroupId = current.QuoteGroupId == Guid.Empty ? current.Id : current.QuoteGroupId,
            IsCurrentVersion = true,
            CustomerId = current.CustomerId,
            ContactId = current.ContactId,
            Version = maxVersion + 1,
            Status = QuoteStatus.Draft,
            Subject = current.Subject,
            IntroText = current.IntroText,
            OutroText = current.OutroText,
            Notes = current.Notes,
            InternalNotes = current.InternalNotes,
            TaxRate = taxMode == TaxMode.SmallBusiness ? 0 : current.TaxRate,
            TaxMode = taxMode,
            LegalTextBlocks = current.LegalTextBlocks,
            PaymentTermKeys = current.PaymentTermKeys,
            InstallmentPeriodOptionsMonths = current.InstallmentPeriodOptionsMonths
        };

        foreach (var l in current.Lines.OrderBy(l => l.SortOrder))
        {
            next.Lines.Add(new QuoteLine
            {
                ServiceCatalogItemId = l.ServiceCatalogItemId,
                SubscriptionPlanId = l.SubscriptionPlanId,
                Title = l.Title,
                Description = l.Description,
                    Quantity = l.Quantity,
                    UnitPrice = l.UnitPrice,
                    DiscountPercent = l.DiscountPercent,
                    LineType = l.LineType,
                    VatPercent = EffectiveVatPercent(taxMode, l.VatPercent),
                    SortOrder = l.SortOrder
            });
        }

        _db.Quotes.Add(next);
        await _db.SaveChangesAsync(ct);
        await _activity.LogAsync(next.CustomerId, "Quote", next.Id, "Versioned", $"Neue Angebotsversion V{next.Version} aus {current.QuoteNumber}", ct: ct);
        return (await GetByIdAsync(next.Id, ct))!;
    }

    public async Task<List<QuoteVersionDto>> GetVersionsAsync(Guid id, CancellationToken ct)
    {
        await QuoteLifecycle.ExpireAsync(_db, ct);
        var quote = await _db.Quotes.FirstOrDefaultAsync(q => q.Id == id, ct) ?? throw new KeyNotFoundException();
        var groupId = quote.QuoteGroupId == Guid.Empty ? quote.Id : quote.QuoteGroupId;
        var versions = await _db.Quotes
            .Where(q => q.QuoteGroupId == groupId)
            .OrderByDescending(q => q.Version)
            .ToListAsync(ct);
        return _mapper.Map<List<QuoteVersionDto>>(versions);
    }

    public async Task<byte[]> GeneratePdfByTokenAsync(string token, CancellationToken ct)
    {
        var tokenHash = HashApprovalToken(token);
        var quote = await _db.Quotes
            .Include(q => q.Customer).ThenInclude(c => c.Contacts)
            .Include(q => q.Customer).ThenInclude(c => c.Locations)
            .Include(q => q.Lines)
            .FirstOrDefaultAsync(q => q.ApprovalTokenHash == tokenHash, ct)
            ?? throw new KeyNotFoundException();

        if (!quote.IsCurrentVersion || quote.ApprovalTokenExpiry <= DateTimeOffset.UtcNow || quote.Status is QuoteStatus.Expired or QuoteStatus.Inactive)
            throw new InvalidOperationException("Link expired or invalid.");

        var co = await _db.CompanySettings.FirstOrDefaultAsync(ct) ?? new CompanySettings { CompanyName = "GentleSuite" };
        return await _pdf.GenerateQuotePdfAsync(quote, co, ct);
    }

    public async Task<(Stream Stream, string FileName, string ContentType)> DownloadLegalAttachmentByTokenAsync(string token, string key, CancellationToken ct)
    {
        var tokenHash = HashApprovalToken(token);
        var quote = await _db.Quotes.FirstOrDefaultAsync(q => q.ApprovalTokenHash == tokenHash, ct) ?? throw new KeyNotFoundException();
        if (!quote.IsCurrentVersion || quote.ApprovalTokenExpiry <= DateTimeOffset.UtcNow || quote.Status is QuoteStatus.Expired or QuoteStatus.Inactive)
            throw new InvalidOperationException("Link expired or invalid.");
        var block = await _db.LegalTextBlocks.FirstOrDefaultAsync(b => b.Key == key && b.IsActive, ct) ?? throw new KeyNotFoundException("Dokument nicht gefunden.");
        if (string.IsNullOrEmpty(block.AttachmentPath)) throw new InvalidOperationException("Für dieses Dokument wurde keine Datei hochgeladen.");
        var stream = await _fs.DownloadAsync(block.AttachmentPath, ct);
        return (stream, block.AttachmentFileName ?? "dokument.pdf", block.AttachmentContentType ?? "application/octet-stream");
    }


    private static void ValidateQuoteLines(List<CreateQuoteLineRequest> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line.Title)) throw new ArgumentException($"Position {i + 1}: Titel ist erforderlich.");
            if (line.Quantity <= 0) throw new ArgumentException($"Position {i + 1}: Menge muss groesser als 0 sein.");
            if (line.UnitPrice < 0) throw new ArgumentException($"Position {i + 1}: Preis darf nicht negativ sein.");
            if (line.DiscountPercent < 0 || line.DiscountPercent > 100) throw new ArgumentException($"Position {i + 1}: Rabatt muss zwischen 0 und 100 liegen.");
            if (line.VatPercent < 0 || line.VatPercent > 100) throw new ArgumentException($"Position {i + 1}: MwSt muss zwischen 0 und 100 liegen.");
        }
    }

    private static void ValidateTemplateLines(List<CreateQuoteTemplateLineRequest> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line.Title)) throw new ArgumentException($"Template-Position {i + 1}: Titel ist erforderlich.");
            if (line.Quantity <= 0) throw new ArgumentException($"Template-Position {i + 1}: Menge muss groesser als 0 sein.");
            if (line.UnitPrice < 0) throw new ArgumentException($"Template-Position {i + 1}: Preis darf nicht negativ sein.");
        }
    }

    private static TaxMode EnforceCompanyTaxMode(TaxMode companyMode, TaxMode requestedMode)
        => companyMode == TaxMode.SmallBusiness ? TaxMode.SmallBusiness : requestedMode;

    private static int EffectiveVatPercent(TaxMode taxMode, int requestedVatPercent)
        => taxMode == TaxMode.SmallBusiness ? 0 : requestedVatPercent;
}
