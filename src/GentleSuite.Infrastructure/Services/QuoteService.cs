using AutoMapper;
using GentleSuite.Application.DTOs;
using GentleSuite.Application.Interfaces;
using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;
using GentleSuite.Domain.Interfaces;
using GentleSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
    public QuoteServiceImpl(AppDbContext db, IMapper mapper, IEmailService email, IPdfService pdf, IActivityLogService activity, IConfiguration config, INumberSequenceService seq)
    { _db = db; _mapper = mapper; _email = email; _pdf = pdf; _activity = activity; _frontendBaseUrl = config["FrontendBaseUrl"] ?? "http://localhost:3000"; _seq = seq; }

    public async Task<PagedResult<QuoteListDto>> GetQuotesAsync(PaginationParams p, QuoteStatus? status, Guid? customerId, CancellationToken ct)
    {
        var q = _db.Quotes.Include(x => x.Customer).Include(x => x.Lines).Where(x => x.IsCurrentVersion).AsQueryable();
        if (status.HasValue) q = q.Where(x => x.Status == status.Value);
        if (customerId.HasValue) q = q.Where(x => x.CustomerId == customerId.Value);
        if (!string.IsNullOrWhiteSpace(p.Search)) q = q.Where(x => x.QuoteNumber.Contains(p.Search) || x.Customer.CompanyName.ToLower().Contains(p.Search.ToLower()));
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(x => x.CreatedAt).Skip((p.Page-1)*p.PageSize).Take(p.PageSize).ToListAsync(ct);
        return new PagedResult<QuoteListDto>(_mapper.Map<List<QuoteListDto>>(items), total, p.Page, p.PageSize);
    }

    public async Task<QuoteDetailDto?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var q = await _db.Quotes.Include(x => x.Customer).ThenInclude(c => c.Contacts).Include(x => x.Lines.OrderBy(l => l.SortOrder)).FirstOrDefaultAsync(x => x.Id == id, ct);
        return q == null ? null : _mapper.Map<QuoteDetailDto>(q);
    }

    public async Task<QuoteDetailDto> CreateAsync(CreateQuoteRequest req, CancellationToken ct)
    {
        if (req.TemplateId.HasValue) return await CreateFromTemplateAsync(req.CustomerId, req.TemplateId.Value, ct);
        if (!await _db.Customers.AnyAsync(c => c.Id == req.CustomerId, ct)) throw new ArgumentException("Kunde wurde nicht gefunden.");
        if (req.Lines != null && req.Lines.Count > 0) ValidateQuoteLines(req.Lines);

        var co = await _db.CompanySettings.FirstOrDefaultAsync(ct);
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
            TaxRate = req.TaxRate, TaxMode = req.TaxMode, Status = QuoteStatus.Draft,
            LegalTextBlocks = req.LegalTextBlockKeys != null ? JsonSerializer.Serialize(req.LegalTextBlockKeys) : null
        };
        quote.QuoteGroupId = quote.Id;
        if (req.Lines != null) foreach (var l in req.Lines)
            quote.Lines.Add(new QuoteLine { ServiceCatalogItemId = l.ServiceCatalogItemId, Title = l.Title, Description = l.Description, Quantity = l.Quantity, UnitPrice = l.UnitPrice, DiscountPercent = l.DiscountPercent, LineType = l.LineType, VatPercent = l.VatPercent, SortOrder = l.SortOrder });
        _db.Quotes.Add(quote);
        await _db.SaveChangesAsync(ct);
        await _activity.LogAsync(quote.CustomerId, "Quote", quote.Id, "Created", $"Angebot {quote.QuoteNumber} erstellt", ct: ct);
        return (await GetByIdAsync(quote.Id, ct))!;
    }

    public async Task<QuoteDetailDto> CreateFromTemplateAsync(Guid customerId, Guid templateId, CancellationToken ct)
    {
        if (!await _db.Customers.AnyAsync(c => c.Id == customerId, ct)) throw new ArgumentException("Kunde wurde nicht gefunden.");
        var tmpl = await _db.QuoteTemplates.Include(t => t.Lines.OrderBy(l => l.SortOrder)).FirstOrDefaultAsync(t => t.Id == templateId, ct) ?? throw new KeyNotFoundException("Template not found");
        var year = DateTime.UtcNow.Year;
        var quoteNumber = await _seq.NextNumberAsync("Quote", year, "AN", 4, ct, includeYear: false);
        var quote = new Quote { QuoteNumber = quoteNumber, QuoteGroupId = Guid.NewGuid(), IsCurrentVersion = true, CustomerId = customerId, Subject = tmpl.Name, Notes = tmpl.Description, TaxRate = 19m, Status = QuoteStatus.Draft };
        quote.QuoteGroupId = quote.Id;
        foreach (var tl in tmpl.Lines) quote.Lines.Add(new QuoteLine { ServiceCatalogItemId = tl.ServiceCatalogItemId, Title = tl.Title, Description = tl.Description, Quantity = tl.Quantity, UnitPrice = tl.UnitPrice, DiscountPercent = 0, LineType = tl.LineType, SortOrder = tl.SortOrder });
        var keys = new List<string>();
        if (tmpl.Lines.Any(l => l.Title.Contains("Care"))) { keys.Add("unlimited-care-fairuse"); keys.Add("sla-levels"); }
        if (keys.Any()) quote.LegalTextBlocks = JsonSerializer.Serialize(keys);
        _db.Quotes.Add(quote); await _db.SaveChangesAsync(ct);
        return (await GetByIdAsync(quote.Id, ct))!;
    }

    public async Task<QuoteDetailDto> UpdateLinesAsync(Guid id, List<CreateQuoteLineRequest> lines, CancellationToken ct)
    {
        var quote = await _db.Quotes.Include(q => q.Lines).FirstOrDefaultAsync(q => q.Id == id, ct) ?? throw new KeyNotFoundException();
        if (!quote.IsCurrentVersion) throw new InvalidOperationException("Nur aktuelle Angebotsversionen sind bearbeitbar.");
        if (quote.Status != QuoteStatus.Draft) throw new InvalidOperationException("Only draft quotes can be edited");
        if (lines == null || lines.Count == 0) throw new ArgumentException("Mindestens eine Angebotsposition ist erforderlich.");
        ValidateQuoteLines(lines);
        _db.QuoteLines.RemoveRange(quote.Lines); quote.Lines.Clear();
        foreach (var l in lines) quote.Lines.Add(new QuoteLine { ServiceCatalogItemId = l.ServiceCatalogItemId, Title = l.Title, Description = l.Description, Quantity = l.Quantity, UnitPrice = l.UnitPrice, DiscountPercent = l.DiscountPercent, LineType = l.LineType, VatPercent = l.VatPercent, SortOrder = l.SortOrder });
        await _db.SaveChangesAsync(ct);
        return (await GetByIdAsync(quote.Id, ct))!;
    }

    public async Task SendAsync(Guid id, SendQuoteRequest req, CancellationToken ct)
    {
        var quote = await _db.Quotes.Include(q => q.Customer).ThenInclude(c => c.Contacts).Include(q => q.Lines).FirstOrDefaultAsync(q => q.Id == id, ct) ?? throw new KeyNotFoundException();
        if (!quote.IsCurrentVersion) throw new InvalidOperationException("Nur aktuelle Angebotsversionen koennen versendet werden.");
        if (!quote.Lines.Any()) throw new ArgumentException("Ein Angebot ohne Positionen kann nicht versendet werden.");
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace("+","").Replace("/","").Replace("=","");
        using var sha = SHA256.Create();
        quote.ApprovalToken = token;
        quote.ApprovalTokenHash = Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(token)));
        quote.ApprovalTokenExpiry = DateTimeOffset.UtcNow.AddDays(req.ExpirationDays);
        quote.ExpiresAt = quote.ApprovalTokenExpiry;
        quote.Status = QuoteStatus.Sent; quote.SentAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        var contact = quote.Customer.Contacts.FirstOrDefault(c => c.IsPrimary) ?? quote.Customer.Contacts.First();
        var recipientEmail = !string.IsNullOrWhiteSpace(req.RecipientEmail) ? req.RecipientEmail : contact.Email;
        var variables = new Dictionary<string, object>
        {
            ["CustomerName"] = quote.Customer.CompanyName, ["ContactName"] = contact.FirstName,
            ["QuoteNumber"] = quote.QuoteNumber, ["Total"] = quote.GrandTotal.ToString("N2"),
            ["ApprovalLink"] = $"{_frontendBaseUrl}/approval/{token}", ["ExpiresAt"] = quote.ExpiresAt.Value.ToString("dd.MM.yyyy"),
            ["RequireSignature"] = req.RequireSignature.ToString()
        };
        if (!string.IsNullOrWhiteSpace(req.Message)) variables["Message"] = req.Message;
        await _email.SendTemplatedEmailAsync(recipientEmail, "quote-sent", variables, quote.CustomerId, ct: ct);
        await _activity.LogAsync(quote.CustomerId, "Quote", quote.Id, "Sent", $"Angebot {quote.QuoteNumber} versendet", ct: ct);
    }

    public async Task<QuoteDetailDto?> GetByApprovalTokenAsync(string token, CancellationToken ct)
    {
        var quote = await _db.Quotes.Include(q => q.Customer).ThenInclude(c => c.Contacts).Include(q => q.Lines.OrderBy(l => l.SortOrder)).FirstOrDefaultAsync(q => q.ApprovalToken == token, ct);
        if (quote == null || !quote.IsCurrentVersion || quote.ApprovalTokenExpiry < DateTimeOffset.UtcNow) return null;
        if (quote.Status == QuoteStatus.Sent) { quote.Status = QuoteStatus.Viewed; quote.ViewedAt = DateTimeOffset.UtcNow; await _db.SaveChangesAsync(ct); }
        return _mapper.Map<QuoteDetailDto>(quote);
    }

    public async Task ProcessApprovalAsync(string token, ApprovalRequest req, string? ipAddress, CancellationToken ct)
    {
        var quote = await _db.Quotes.Include(q => q.Customer).ThenInclude(c => c.Contacts).Include(q => q.Lines).FirstOrDefaultAsync(q => q.ApprovalToken == token, ct) ?? throw new KeyNotFoundException();
        if (!quote.IsCurrentVersion) throw new InvalidOperationException("Dieses Angebot wurde durch eine neuere Version ersetzt.");
        if (quote.ApprovalTokenExpiry < DateTimeOffset.UtcNow) throw new InvalidOperationException("Link expired");
        quote.CustomerComment = req.Comment; quote.RespondedAt = DateTimeOffset.UtcNow;
        if (req.Accepted)
        {
            quote.Status = QuoteStatus.Accepted;
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
    }

    public async Task<byte[]> GeneratePdfAsync(Guid id, CancellationToken ct)
    {
        var quote = await _db.Quotes.Include(q => q.Customer).ThenInclude(c => c.Contacts).Include(q => q.Customer).ThenInclude(c => c.Locations).Include(q => q.Lines).FirstOrDefaultAsync(q => q.Id == id, ct) ?? throw new KeyNotFoundException();
        var co = await _db.CompanySettings.FirstOrDefaultAsync(ct) ?? new CompanySettings { CompanyName = "GentleSuite" };
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
        var quote = await _db.Quotes.Include(q => q.Customer).Include(q => q.Lines).FirstOrDefaultAsync(q => q.Id == quoteId, ct) ?? throw new KeyNotFoundException();
        if (quote.Status != QuoteStatus.Ordered)
            throw new InvalidOperationException("Nur bestätigte Aufträge können in eine Rechnung umgewandelt werden.");
        var invoice = await EnsureInvoiceFromQuoteAsync(quote, ct);
        var fullInvoice = await _db.Invoices
            .Include(i => i.Customer)
            .Include(i => i.Lines)
            .Include(i => i.Payments)
            .FirstOrDefaultAsync(i => i.Id == invoice.Id, ct) ?? throw new KeyNotFoundException();
        return _mapper.Map<InvoiceDetailDto>(fullInvoice);
    }

    private async Task<Invoice> EnsureInvoiceFromQuoteAsync(Quote quote, CancellationToken ct)
    {
        var existing = await _db.Invoices.FirstOrDefaultAsync(i => i.QuoteId == quote.Id, ct);
        if (existing != null) return existing;

        // Gleiche Nummer wie Angebot: AN-0001 → RE-0001
        var quoteSuffix = quote.QuoteNumber.Contains('-') ? quote.QuoteNumber[(quote.QuoteNumber.LastIndexOf('-') + 1)..] : quote.QuoteNumber;
        var invoiceNumber = $"RE-{quoteSuffix}";
        var inv = new Invoice
        {
            InvoiceNumber = invoiceNumber,
            CustomerId = quote.CustomerId,
            QuoteId = quote.Id,
            Subject = quote.Subject,
            TaxMode = quote.TaxMode,
            Status = InvoiceStatus.Draft,
            InvoiceDate = DateTimeOffset.UtcNow,
            DueDate = DateTimeOffset.UtcNow.AddDays(14),
            ServiceDateFrom = DateTimeOffset.UtcNow,
            ServiceDateTo = DateTimeOffset.UtcNow,
            RetentionUntil = DateTimeOffset.UtcNow.AddYears(10)
        };
        foreach (var ql in quote.Lines)
        {
            inv.Lines.Add(new InvoiceLine
            {
                Title = ql.Title,
                Description = ql.Description,
                Quantity = ql.Quantity,
                UnitPrice = ql.Quantity > 0 ? ql.Total / ql.Quantity : ql.UnitPrice,
                VatPercent = ql.VatPercent,
                SortOrder = ql.SortOrder
            });
        }
        inv.RecalculateTotals();
        _db.Invoices.Add(inv);
        await _db.SaveChangesAsync(ct);
        return inv;
    }

    public async Task<QuoteDetailDto> UpdateAsync(Guid id, UpdateQuoteRequest req, CancellationToken ct)
    {
        var quote = await _db.Quotes.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException();
        if (!quote.IsCurrentVersion) throw new InvalidOperationException("Nur aktuelle Angebotsversionen sind bearbeitbar.");
        if (quote.Status != QuoteStatus.Draft) throw new InvalidOperationException("Only draft quotes can be edited");
        if (req.Subject != null) quote.Subject = req.Subject;
        if (req.IntroText != null) quote.IntroText = req.IntroText;
        if (req.OutroText != null) quote.OutroText = req.OutroText;
        if (req.Notes != null) quote.Notes = req.Notes;
        if (req.TaxRate.HasValue) quote.TaxRate = req.TaxRate.Value;
        if (req.TaxMode.HasValue) quote.TaxMode = req.TaxMode.Value;
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
            TaxRate = original.TaxRate, TaxMode = original.TaxMode,
            Status = QuoteStatus.Draft, LegalTextBlocks = original.LegalTextBlocks, Version = 1
        };
        copy.QuoteGroupId = copy.Id;
        foreach (var l in original.Lines)
            copy.Lines.Add(new QuoteLine { ServiceCatalogItemId = l.ServiceCatalogItemId, Title = l.Title, Description = l.Description, Quantity = l.Quantity, UnitPrice = l.UnitPrice, DiscountPercent = l.DiscountPercent, LineType = l.LineType, VatPercent = l.VatPercent, SortOrder = l.SortOrder });
        _db.Quotes.Add(copy);
        await _db.SaveChangesAsync(ct);
        await _activity.LogAsync(copy.CustomerId, "Quote", copy.Id, "Created", $"Angebot {copy.QuoteNumber} dupliziert von {original.QuoteNumber}", ct: ct);
        return (await GetByIdAsync(copy.Id, ct))!;
    }

    public async Task<QuoteDetailDto> CreateNewVersionAsync(Guid id, CancellationToken ct)
    {
        var current = await _db.Quotes.Include(q => q.Lines).FirstOrDefaultAsync(q => q.Id == id, ct) ?? throw new KeyNotFoundException();
        if (!current.IsCurrentVersion) throw new InvalidOperationException("Nur die aktuelle Version kann versioniert werden.");

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
            TaxRate = current.TaxRate,
            TaxMode = current.TaxMode,
            LegalTextBlocks = current.LegalTextBlocks
        };

        foreach (var l in current.Lines.OrderBy(l => l.SortOrder))
        {
            next.Lines.Add(new QuoteLine
            {
                ServiceCatalogItemId = l.ServiceCatalogItemId,
                Title = l.Title,
                Description = l.Description,
                    Quantity = l.Quantity,
                    UnitPrice = l.UnitPrice,
                    DiscountPercent = l.DiscountPercent,
                    LineType = l.LineType,
                    VatPercent = l.VatPercent,
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
        var quote = await _db.Quotes.FirstOrDefaultAsync(q => q.Id == id, ct) ?? throw new KeyNotFoundException();
        var groupId = quote.QuoteGroupId == Guid.Empty ? quote.Id : quote.QuoteGroupId;
        var versions = await _db.Quotes
            .Where(q => q.QuoteGroupId == groupId)
            .OrderByDescending(q => q.Version)
            .ToListAsync(ct);
        return _mapper.Map<List<QuoteVersionDto>>(versions);
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
}
