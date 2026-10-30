using GentleSuite.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GentleSuite.API.Controllers;

/// <summary>
/// Read-only CRM surface for GentleHub's agents: find the customer behind an
/// e-mail address and load the context an agent needs (contacts, projects,
/// quotes, opportunities). Auth is GentleHubApiKeyMiddleware. Returns only
/// fields an agent needs; no financial details beyond quote headlines, no
/// documents, no credentials. Writes (leads, notes, drafts) come later and
/// only after a user approval in GentleHub.
/// </summary>
[ApiController]
[Route("api/integrations/gentlehub")]
[AllowAnonymous]
public class GentleHubIntegrationController(AppDbContext db) : ControllerBase
{
    private const int MaxResults = 10;

    // Mail hosters: a shared domain says nothing about which company wrote the mail.
    private static readonly HashSet<string> FreemailDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "gmx.de", "gmx.net", "web.de", "t-online.de", "outlook.com", "outlook.de",
        "hotmail.com", "hotmail.de", "live.com", "live.de", "yahoo.com", "yahoo.de", "icloud.com", "me.com", "aol.com",
        "freenet.de", "posteo.de", "mailbox.org", "proton.me", "protonmail.com", "online.de", "1und1.de", "arcor.de"
    };

    public record ContactMatch(Guid Id, string Name, string Email, string? Position, bool IsPrimary);

    public record CustomerMatch(
        Guid Id,
        string CompanyName,
        string? CustomerNumber,
        string Status,
        string? Website,
        string MatchedBy,
        IReadOnlyList<ContactMatch> Contacts);

    public record LookupResponse(string Email, string? Domain, bool IsFreemail, IReadOnlyList<CustomerMatch> Matches);

    public record ProjectSummary(Guid Id, string Name, string Status, DateTimeOffset? DueDate, decimal? BudgetHours);

    public record QuoteSummary(Guid Id, string QuoteNumber, string? Subject, string Status, decimal NetTotal, DateTimeOffset CreatedAt);

    public record OpportunitySummary(Guid Id, string Title, string Stage, int Probability);

    public record CustomerContext(
        Guid Id,
        string CompanyName,
        string? CustomerNumber,
        string Status,
        string? Industry,
        string? Website,
        IReadOnlyList<ContactMatch> Contacts,
        IReadOnlyList<ProjectSummary> Projects,
        IReadOnlyList<QuoteSummary> Quotes,
        IReadOnlyList<OpportunitySummary> Opportunities);

    /// <summary>Connection check for GentleHub's "Verbindungen" page.</summary>
    [HttpGet("ping")]
    public IActionResult Ping() => Ok(new { ok = true, system = "GentleSuite", scope = "read" });

    /// <summary>Exact contact e-mail first, then the company domain (website or other contacts); never for freemail domains.</summary>
    [HttpGet("customers/lookup")]
    public async Task<ActionResult<LookupResponse>> Lookup([FromQuery] string email, CancellationToken ct)
    {
        var normalized = (email ?? "").Trim().ToLowerInvariant();
        var at = normalized.LastIndexOf('@');
        if (normalized.Length is < 3 or > 320 || at <= 0 || at == normalized.Length - 1)
        {
            return BadRequest(new { error = "Ungültige E-Mail-Adresse." });
        }

        var domain = normalized[(at + 1)..];
        var freemail = FreemailDomains.Contains(domain);

        var byContact = await db.Customers.AsNoTracking()
            .Where(c => c.Contacts.Any(x => x.Email.ToLower() == normalized))
            .Select(c => c.Id)
            .Take(MaxResults)
            .ToListAsync(ct);

        var byDomain = new List<Guid>();
        if (!freemail)
        {
            var webPattern = "%" + domain;
            var mailPattern = "%@" + domain;
            byDomain = await db.Customers.AsNoTracking()
                .Where(c => !byContact.Contains(c.Id))
                .Where(c => (c.Website != null && (EF.Functions.Like(c.Website, webPattern) || EF.Functions.Like(c.Website, webPattern + "/%")))
                         || c.Contacts.Any(x => EF.Functions.Like(x.Email, mailPattern)))
                .Select(c => c.Id)
                .Take(MaxResults)
                .ToListAsync(ct);
        }

        var matches = new List<CustomerMatch>();
        matches.AddRange(await LoadMatches(byContact, "contact_email", ct));
        matches.AddRange(await LoadMatches(byDomain, "domain", ct));
        return Ok(new LookupResponse(normalized, domain, freemail, matches));
    }

    [HttpGet("customers/search")]
    public async Task<ActionResult<IReadOnlyList<CustomerMatch>>> Search([FromQuery] string q, CancellationToken ct)
    {
        var term = (q ?? "").Trim();
        if (term.Length is < 2 or > 100)
        {
            return BadRequest(new { error = "Suchbegriff mit 2 bis 100 Zeichen angeben." });
        }

        var pattern = "%" + term.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";
        var ids = await db.Customers.AsNoTracking()
            .Where(c => EF.Functions.Like(c.CompanyName, pattern) || (c.CustomerNumber != null && EF.Functions.Like(c.CustomerNumber, pattern)))
            .OrderBy(c => c.CompanyName)
            .Select(c => c.Id)
            .Take(MaxResults)
            .ToListAsync(ct);
        return Ok(await LoadMatches(ids, "search", ct));
    }

    [HttpGet("customers/{id:guid}/context")]
    public async Task<ActionResult<CustomerContext>> Context(Guid id, CancellationToken ct)
    {
        var customer = await db.Customers.AsNoTracking()
            .Include(c => c.Contacts)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (customer is null)
        {
            return NotFound(new { error = "Kunde nicht gefunden." });
        }

        var projects = await db.Projects.AsNoTracking()
            .Where(p => p.CustomerId == id)
            .OrderByDescending(p => p.CreatedAt)
            .Take(20)
            .Select(p => new ProjectSummary(p.Id, p.Name, p.Status.ToString(), p.DueDate, p.BudgetHours))
            .ToListAsync(ct);

        var quotes = (await db.Quotes.AsNoTracking()
                .Include(q => q.Lines)
                .Where(q => q.CustomerId == id && q.IsCurrentVersion)
                .OrderByDescending(q => q.CreatedAt)
                .Take(10)
                .ToListAsync(ct))
            .Select(q => new QuoteSummary(q.Id, q.QuoteNumber, q.Subject, q.Status.ToString(), q.Lines.Sum(l => l.Total), q.CreatedAt))
            .ToList();

        var opportunities = await db.Opportunities.AsNoTracking()
            .Where(o => o.CustomerId == id)
            .OrderByDescending(o => o.CreatedAt)
            .Take(10)
            .Select(o => new OpportunitySummary(o.Id, o.Title, o.Stage.ToString(), o.Probability))
            .ToListAsync(ct);

        return Ok(new CustomerContext(
            customer.Id,
            customer.CompanyName,
            customer.CustomerNumber,
            customer.Status.ToString(),
            customer.Industry,
            customer.Website,
            customer.Contacts.OrderByDescending(x => x.IsPrimary).Select(ToContact).ToList(),
            projects,
            quotes,
            opportunities));
    }

    private async Task<List<CustomerMatch>> LoadMatches(List<Guid> ids, string matchedBy, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var customers = await db.Customers.AsNoTracking()
            .Include(c => c.Contacts)
            .Where(c => ids.Contains(c.Id))
            .ToListAsync(ct);

        return customers
            .OrderBy(c => c.CompanyName)
            .Select(c => new CustomerMatch(
                c.Id,
                c.CompanyName,
                c.CustomerNumber,
                c.Status.ToString(),
                c.Website,
                matchedBy,
                c.Contacts.OrderByDescending(x => x.IsPrimary).Take(10).Select(ToContact).ToList()))
            .ToList();
    }

    private static ContactMatch ToContact(GentleSuite.Domain.Entities.Contact contact) =>
        new(contact.Id, $"{contact.FirstName} {contact.LastName}".Trim(), contact.Email, contact.Position, contact.IsPrimary);
}
