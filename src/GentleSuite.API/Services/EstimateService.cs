using GentleSuite.Application.DTOs;
using GentleSuite.Application.Interfaces;
using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;
using GentleSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GentleSuite.Infrastructure.Services;

/// <summary>(De)serialization of Estimate.ContentJson, shared by the service and PdfService.</summary>
public static class EstimateJson
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public static EstimateContentDto Parse(string? json)
    {
        EstimateContentDto? c = null;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try { c = JsonSerializer.Deserialize<EstimateContentDto>(json, Opts); }
            catch (JsonException) { /* corrupt content → start empty rather than 500 */ }
        }
        return Normalize(c);
    }

    public static string Serialize(EstimateContentDto? c) => JsonSerializer.Serialize(Normalize(c), Opts);

    /// <summary>Replaces null lists (e.g. omitted by a client) with empty ones.</summary>
    public static EstimateContentDto Normalize(EstimateContentDto? c)
    {
        c ??= new EstimateContentDto();
        c.Basis ??= new(); c.Decisions ??= new(); c.Architecture ??= new(); c.Implementation ??= new(); c.Pilot ??= new();
        c.Modules ??= new(); c.Extensions ??= new(); c.Assumptions ??= new(); c.Exclusions ??= new(); c.Risks ??= new();
        c.OpenQuestions ??= new(); c.Timeline ??= new(); c.NextSteps ??= new();
        return c;
    }

    /// <summary>Sum of the von/bis prices. A row with only one bound counts that bound on both sides.</summary>
    public static (decimal From, decimal To) Range(IEnumerable<EstimateModuleDto> rows)
    {
        decimal from = 0, to = 0;
        foreach (var r in rows)
        {
            var f = r.PriceFrom ?? r.PriceTo ?? 0;
            var t = r.PriceTo ?? r.PriceFrom ?? 0;
            from += f; to += t;
        }
        return (from, to);
    }
}

public class EstimateServiceImpl : IEstimateService
{
    private static readonly string[] Classifications = { "intern", "vertraulich", "öffentlich" };

    private readonly AppDbContext _db;
    private readonly INumberSequenceService _seq;
    private readonly IPdfService _pdf;
    private readonly IActivityLogService _activity;

    public EstimateServiceImpl(AppDbContext db, INumberSequenceService seq, IPdfService pdf, IActivityLogService activity)
    { _db = db; _seq = seq; _pdf = pdf; _activity = activity; }

    private static string? DisplayCustomer(Estimate e) =>
        !string.IsNullOrWhiteSpace(e.CustomerDisplayName) ? e.CustomerDisplayName : e.Customer?.CompanyName;

    private static EstimateDto ToDto(Estimate e)
    {
        var content = EstimateJson.Parse(e.ContentJson);
        var (mf, mt) = EstimateJson.Range(content.Modules);
        var (xf, xt) = EstimateJson.Range(content.Extensions);
        return new EstimateDto(e.Id, e.EstimateNumber, e.CustomerId, e.Customer?.CompanyName, e.CustomerDisplayName,
            e.Title, e.Subtitle, e.Version, e.Date, e.PreparedBy, e.ApprovedBy, e.Responsible,
            e.Classification, e.Status, e.FixedPrice, e.FixedPriceLabel,
            e.SituationText, e.ProposalText, e.CostNote, e.OperationText, e.TimelineNote,
            content, mf, mt, xf, xt, e.CreatedAt, e.UpdatedAt);
    }

    public async Task<PagedResult<EstimateListDto>> GetPagedAsync(PaginationParams p, Guid? customerId, EstimateStatus? status, CancellationToken ct)
    {
        var q = _db.Estimates.Include(e => e.Customer).AsQueryable();
        if (customerId.HasValue) q = q.Where(e => e.CustomerId == customerId.Value);
        if (status.HasValue) q = q.Where(e => e.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(p.Search))
        {
            var s = p.Search.Trim().ToLower();
            q = q.Where(e => e.EstimateNumber.ToLower().Contains(s) || e.Title.ToLower().Contains(s)
                || (e.CustomerDisplayName != null && e.CustomerDisplayName.ToLower().Contains(s))
                || (e.Customer != null && e.Customer.CompanyName.ToLower().Contains(s)));
        }
        var page = Math.Max(1, p.Page);
        var size = Math.Clamp(p.PageSize, 1, 200);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(e => e.Date).ThenByDescending(e => e.CreatedAt).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var dtos = items.Select(e =>
        {
            var (mf, mt) = EstimateJson.Range(EstimateJson.Parse(e.ContentJson).Modules);
            return new EstimateListDto(e.Id, e.EstimateNumber, e.Title, e.CustomerId, DisplayCustomer(e), e.Version, e.FixedPrice, mf, mt, e.Status, e.Date, e.CreatedAt);
        }).ToList();
        return new PagedResult<EstimateListDto>(dtos, total, page, size);
    }

    public async Task<EstimateDto?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var e = await _db.Estimates.Include(x => x.Customer).FirstOrDefaultAsync(x => x.Id == id, ct);
        return e == null ? null : ToDto(e);
    }

    private async Task Apply(Estimate e, SaveEstimateRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title)) throw new ArgumentException("Titel ist erforderlich.");
        if (req.CustomerId.HasValue && !await _db.Customers.AnyAsync(c => c.Id == req.CustomerId.Value, ct))
            throw new ArgumentException("Kunde nicht gefunden.");
        var classification = string.IsNullOrWhiteSpace(req.Classification) ? "vertraulich" : req.Classification.Trim().ToLowerInvariant();
        if (!Classifications.Contains(classification)) throw new ArgumentException("Klassifizierung muss intern, vertraulich oder öffentlich sein.");
        var content = EstimateJson.Normalize(req.Content);
        foreach (var m in content.Modules.Concat(content.Extensions))
        {
            if (m.PriceFrom < 0 || m.PriceTo < 0) throw new ArgumentException("Preise dürfen nicht negativ sein.");
            if (m.PriceFrom.HasValue && m.PriceTo.HasValue && m.PriceFrom > m.PriceTo)
                throw new ArgumentException($"„{m.Title}“: Preis von darf nicht größer als Preis bis sein.");
        }
        if (req.FixedPrice < 0) throw new ArgumentException("Festpreis darf nicht negativ sein.");
        // Column limits of the "Estimates" table (see Program.cs schema block).
        MaxLen(req.Title, 300, "Titel"); MaxLen(req.Subtitle, 500, "Untertitel"); MaxLen(req.CustomerDisplayName, 300, "Erstellt für");
        MaxLen(req.Version, 32, "Version"); MaxLen(req.PreparedBy, 200, "Erstellt von"); MaxLen(req.ApprovedBy, 200, "Freigegeben von");
        MaxLen(req.Responsible, 200, "Verantwortlicher"); MaxLen(req.FixedPriceLabel, 200, "Festpreis-Bezeichnung");

        e.CustomerId = req.CustomerId;
        e.CustomerDisplayName = Clean(req.CustomerDisplayName);
        e.Title = req.Title.Trim();
        e.Subtitle = Clean(req.Subtitle);
        e.Version = string.IsNullOrWhiteSpace(req.Version) ? "1.0" : req.Version.Trim();
        e.Date = req.Date ?? e.Date;
        e.PreparedBy = Clean(req.PreparedBy);
        e.ApprovedBy = Clean(req.ApprovedBy);
        e.Responsible = Clean(req.Responsible);
        e.Classification = classification;
        if (req.Status.HasValue) e.Status = req.Status.Value;
        e.FixedPrice = req.FixedPrice;
        e.FixedPriceLabel = Clean(req.FixedPriceLabel);
        e.SituationText = Clean(req.SituationText);
        e.ProposalText = Clean(req.ProposalText);
        e.CostNote = Clean(req.CostNote);
        e.OperationText = Clean(req.OperationText);
        e.TimelineNote = Clean(req.TimelineNote);
        e.ContentJson = EstimateJson.Serialize(content);
    }

    private static void MaxLen(string? s, int max, string field)
    {
        if (s != null && s.Trim().Length > max) throw new ArgumentException($"{field} darf höchstens {max} Zeichen lang sein.");
    }

    private static string? Clean(string? s) =>string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public async Task<EstimateDto> CreateAsync(SaveEstimateRequest req, CancellationToken ct)
    {
        var e = new Estimate();
        await Apply(e, req, ct);
        if (!req.Status.HasValue) e.Status = EstimateStatus.Draft;
        e.EstimateNumber = await _seq.NextNumberAsync("Estimate", DateTime.UtcNow.Year, "SC", 3, ct, includeYear: true);
        _db.Estimates.Add(e);
        await _db.SaveChangesAsync(ct);
        await _activity.LogAsync(e.CustomerId, "Estimate", e.Id, "Created", $"Schätzung {e.EstimateNumber} ({e.Title}) angelegt", ct: ct);
        return (await GetByIdAsync(e.Id, ct))!;
    }

    public async Task<EstimateDto> UpdateAsync(Guid id, SaveEstimateRequest req, CancellationToken ct)
    {
        var e = await _db.Estimates.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        await Apply(e, req, ct);
        await _db.SaveChangesAsync(ct);
        return (await GetByIdAsync(e.Id, ct))!;
    }

    public async Task<EstimateDto> DuplicateAsync(Guid id, CancellationToken ct)
    {
        var src = await _db.Estimates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        var copy = new Estimate
        {
            EstimateNumber = src.EstimateNumber,
            CustomerId = src.CustomerId,
            CustomerDisplayName = src.CustomerDisplayName,
            Title = src.Title,
            Subtitle = src.Subtitle,
            Version = NextVersion(src.Version),
            Date = DateTimeOffset.UtcNow,
            PreparedBy = src.PreparedBy,
            ApprovedBy = src.ApprovedBy,
            Responsible = src.Responsible,
            Classification = src.Classification,
            Status = EstimateStatus.Draft,
            FixedPrice = src.FixedPrice,
            FixedPriceLabel = src.FixedPriceLabel,
            SituationText = src.SituationText,
            ProposalText = src.ProposalText,
            CostNote = src.CostNote,
            OperationText = src.OperationText,
            TimelineNote = src.TimelineNote,
            ContentJson = src.ContentJson,
        };
        _db.Estimates.Add(copy);
        await _db.SaveChangesAsync(ct);
        await _activity.LogAsync(copy.CustomerId, "Estimate", copy.Id, "Duplicated", $"Schätzung {copy.EstimateNumber} als Version {copy.Version} dupliziert", ct: ct);
        return (await GetByIdAsync(copy.Id, ct))!;
    }

    /// <summary>"1.0" → "2.0", "3" → "4", anything else → "&lt;v&gt;-2".</summary>
    internal static string NextVersion(string? v)
    {
        var m = Regex.Match(v?.Trim() ?? "", @"^(\d+)(?:[.,](\d+))?$");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var major))
            return m.Groups[2].Success ? $"{major + 1}.0" : (major + 1).ToString(CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(v) ? "2.0" : $"{v.Trim()}-2";
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var e = await _db.Estimates.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        if (e.Status != EstimateStatus.Draft) throw new InvalidOperationException("Nur Schätzungen im Entwurf können gelöscht werden.");
        e.IsDeleted = true;
        e.DeletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _activity.LogAsync(e.CustomerId, "Estimate", e.Id, "Deleted", $"Schätzung {e.EstimateNumber} V{e.Version} gelöscht", ct: ct);
    }

    public async Task<(byte[] Pdf, string FileName)> GeneratePdfAsync(Guid id, CancellationToken ct)
    {
        var e = await _db.Estimates.Include(x => x.Customer).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        var co = await _db.CompanySettings.FirstOrDefaultAsync(ct) ?? new CompanySettings { CompanyName = "Gentle Group" };
        var pdf = await _pdf.GenerateEstimatePdfAsync(e, co, ct);
        var safeVersion = Regex.Replace(e.Version, @"[^\w.\-]", "_");
        return (pdf, $"Aufwandsschaetzung_{e.EstimateNumber}_V{safeVersion}.pdf");
    }
}
