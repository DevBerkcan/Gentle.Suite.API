using GentleSuite.API.Middleware;
using GentleSuite.Application.DTOs;
using GentleSuite.Application.Interfaces;
using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;
using GentleSuite.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;

namespace GentleSuite.API.Controllers;

// ------------------------------------------------------------------ GentleHub (server to server)

public record HubCustomerRequest(
    string? CompanyName,
    [Required, MaxLength(100)] string FirstName,
    [MaxLength(100)] string? LastName,
    [Required, EmailAddress, MaxLength(320)] string Email,
    [MaxLength(50)] string? Phone,
    [MaxLength(100)] string? Position,
    [MaxLength(300)] string? Website,
    [MaxLength(200)] string? Street,
    [MaxLength(20)] string? ZipCode,
    [MaxLength(100)] string? City,
    [MaxLength(100)] string? Country,
    [MaxLength(100)] string? Industry,
    /// <summary>Where the lead came from, e.g. "Outlook-Anfrage vom 03.11.2026: Website-Relaunch". Stored as Datenherkunft.</summary>
    [MaxLength(1000)] string? SourceNote,
    /// <summary>Stable id of the inquiry in GentleHub. Retrying the same request with the same id never creates a second customer.</summary>
    [MaxLength(200)] string? ExternalId);

public record HubContactRequest(
    [Required, MaxLength(100)] string FirstName,
    [MaxLength(100)] string? LastName,
    [Required, EmailAddress, MaxLength(320)] string Email,
    [MaxLength(50)] string? Phone,
    [MaxLength(100)] string? Position,
    bool IsPrimary = false);

/// <summary>Result of a create call. <c>Outcome</c>: "created" (new customer), "existing" (same person already
/// in the CRM, nothing changed), "contact-added" (company existed, the person was added as a new contact).</summary>
public record HubCustomerResult(string Outcome, Guid CustomerId, string? CustomerNumber, string CompanyName, Guid? ContactId, string? MatchedBy, string CrmUrl);
public record HubContactResult(string Outcome, Guid CustomerId, Guid ContactId, string CrmUrl);
public record HubLookupMatch(Guid CustomerId, string? CustomerNumber, string CompanyName, string? ContactName, string? Email, string? Phone, string MatchedBy, string CrmUrl);

/// <summary>
/// Integration surface for GentleHub (inquiries from Outlook → Telegram → "Kunde anlegen").
/// Auth: <see cref="IntegrationApiKeyMiddleware"/> (header X-Api-Key), not the JWT login — hence AllowAnonymous.
/// Creating never sends the welcome e-mail: a fresh inquiry is not yet a customer relationship.
/// </summary>
[ApiController, Route("api/integrations/hub/v1"), AllowAnonymous, EnableRateLimiting("integration-hub")]
public class HubIntegrationController(ICustomerService customers, IActivityLogService activity, AppDbContext db, IConfiguration config) : ControllerBase
{
    string FrontendBaseUrl => (config["FrontendBaseUrl"] ?? "http://localhost:3000").TrimEnd('/');
    string CrmUrl(Guid customerId) => $"{FrontendBaseUrl}/customers/{customerId}";
    string KeyName => HttpContext.Items[IntegrationApiKeyMiddleware.KeyNameItem] as string ?? "API";

    static string Clean(string? s) => (s ?? "").Trim();
    static string? Opt(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Connection test for GentleHub's settings page.</summary>
    [HttpGet("ping")]
    public IActionResult Ping() => Ok(new { ok = true, key = KeyName, time = DateTimeOffset.UtcNow });

    /// <summary>Is this person/company already in the CRM? Matches on e-mail, phone and company name.</summary>
    [HttpGet("customers/lookup")]
    public async Task<ActionResult<List<HubLookupMatch>>> Lookup([FromQuery] string? email, [FromQuery] string? phone, [FromQuery] string? companyName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(phone) && string.IsNullOrWhiteSpace(companyName))
            return BadRequest(new { error = "Bitte email, phone oder companyName angeben." });
        var dup = await customers.CheckDuplicateAsync(new DuplicateCheckRequest(Clean(companyName), Opt(email), Opt(phone)), ct);
        var numbers = await db.Customers.Where(c => dup.Matches.Select(m => m.CustomerId).Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.CustomerNumber, ct);
        return Ok(dup.Matches.Select(m => new HubLookupMatch(m.CustomerId, numbers.GetValueOrDefault(m.CustomerId), m.CompanyName, m.ContactName, m.Email, m.Phone, m.MatchType, CrmUrl(m.CustomerId))).ToList());
    }

    /// <summary>Creates a customer (status Lead) with the person as primary contact — or returns the existing record
    /// if the person is already known, or adds the person as contact if only the company is known.</summary>
    [HttpPost("customers")]
    public async Task<ActionResult<HubCustomerResult>> CreateCustomer(HubCustomerRequest req, CancellationToken ct)
    {
        var email = Clean(req.Email).ToLowerInvariant();
        var firstName = Clean(req.FirstName);
        var lastName = Clean(req.LastName);
        var company = Opt(req.CompanyName) ?? $"{firstName} {lastName}".Trim();
        var externalRef = Opt(req.ExternalId) is { } ext ? $"GH-{ext}" : null;

        // 1) Idempotency: the same inquiry sent twice (e.g. double tap in Telegram) returns the first result.
        if (externalRef != null)
        {
            var known = await db.Customers.Include(c => c.Contacts).FirstOrDefaultAsync(c => c.ExternalRef == externalRef, ct);
            if (known != null)
                return Ok(Result("existing", known, known.Contacts.FirstOrDefault(x => string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase))?.Id, "ExternalId"));
        }

        // 2) Duplicates: same person (e-mail/phone) → return existing; same company only → add as contact.
        var dup = await customers.CheckDuplicateAsync(new DuplicateCheckRequest(company, email, Opt(req.Phone)), ct);
        var person = dup.Matches.FirstOrDefault(m => m.MatchType == "Email") ?? dup.Matches.FirstOrDefault(m => m.MatchType == "Phone");
        if (person != null)
        {
            var existing = await db.Customers.Include(c => c.Contacts).FirstAsync(c => c.Id == person.CustomerId, ct);
            var contact = existing.Contacts.FirstOrDefault(x => string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase));
            return Ok(Result("existing", existing, contact?.Id, person.MatchType));
        }
        var sameCompany = Opt(req.CompanyName) != null ? dup.Matches.FirstOrDefault(m => m.MatchType == "CompanyName") : null;
        if (sameCompany != null)
        {
            var target = await db.Customers.FirstAsync(c => c.Id == sameCompany.CustomerId, ct);
            var added = await customers.AddContactAsync(target.Id, new CreateContactRequest(firstName, lastName, email, Opt(req.Phone), Opt(req.Position), false), ct);
            await activity.LogAsync(target.Id, "Contact", added.Id, "Created", $"Kontakt {firstName} {lastName} über {KeyName} hinzugefügt{(Opt(req.SourceNote) is { } n ? $" ({n})" : "")}", ct);
            return Ok(Result("contact-added", target, added.Id, "CompanyName"));
        }

        // 3) New customer.
        var location = Opt(req.Street) != null || Opt(req.City) != null
            ? new CreateLocationRequest("Hauptsitz", Clean(req.Street), Clean(req.City), Clean(req.ZipCode), Opt(req.Country) ?? "Deutschland", true)
            : null;
        var sourceNote = $"{KeyName}: {Opt(req.SourceNote) ?? "Anfrage"}";
        var created = await customers.CreateAsync(new CreateCustomerRequest(company, Opt(req.Industry), Opt(req.Website), null, null,
            new CreateContactRequest(firstName, lastName, email, Opt(req.Phone), Opt(req.Position), true), location, null,
            OpportunitySource.Inbound, sourceNote.Length > 1000 ? sourceNote[..1000] : sourceNote), ct, sendWelcomeEmail: false,
            // Same person (e-mail/phone) and same company were handled above; what is left is e.g. two private
            // persons with the same name — different people, so both get their own customer record.
            checkDuplicates: false);
        if (externalRef != null)
            await db.Customers.Where(c => c.Id == created.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.ExternalRef, externalRef), ct);
        await activity.LogAsync(created.Id, "Customer", created.Id, "Created", $"Angelegt über {KeyName}", ct);

        var fresh = await db.Customers.Include(c => c.Contacts).FirstAsync(c => c.Id == created.Id, ct);
        return StatusCode(StatusCodes.Status201Created, Result("created", fresh, fresh.Contacts.FirstOrDefault(x => x.IsPrimary)?.Id, null));
    }

    /// <summary>Adds a contact to an existing customer; returns the existing contact if the e-mail is already there.</summary>
    [HttpPost("customers/{customerId:guid}/contacts")]
    public async Task<ActionResult<HubContactResult>> AddContact(Guid customerId, HubContactRequest req, CancellationToken ct)
    {
        var customer = await db.Customers.Include(c => c.Contacts).FirstOrDefaultAsync(c => c.Id == customerId, ct);
        if (customer == null) return NotFound(new { error = "Kunde nicht gefunden." });
        var email = Clean(req.Email).ToLowerInvariant();
        var existing = customer.Contacts.FirstOrDefault(x => string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return Ok(new HubContactResult("existing", customer.Id, existing.Id, CrmUrl(customer.Id)));
        // A new primary contact replaces the old primary flag instead of leaving two primaries.
        if (req.IsPrimary) foreach (var c in customer.Contacts.Where(c => c.IsPrimary)) c.IsPrimary = false;
        await db.SaveChangesAsync(ct);
        var added = await customers.AddContactAsync(customer.Id, new CreateContactRequest(Clean(req.FirstName), Clean(req.LastName), email, Opt(req.Phone), Opt(req.Position), req.IsPrimary), ct);
        await activity.LogAsync(customer.Id, "Contact", added.Id, "Created", $"Kontakt {added.FirstName} {added.LastName} über {KeyName} hinzugefügt", ct);
        return StatusCode(StatusCodes.Status201Created, new HubContactResult("created", customer.Id, added.Id, CrmUrl(customer.Id)));
    }

    HubCustomerResult Result(string outcome, Customer c, Guid? contactId, string? matchedBy) =>
        new(outcome, c.Id, c.CustomerNumber, c.CompanyName, contactId, matchedBy, CrmUrl(c.Id));
}

// ------------------------------------------------------------------ Admin: manage API keys

public record IntegrationApiKeyDto(Guid Id, string Name, string Prefix, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt);
public record CreateIntegrationApiKeyRequest([Required, MaxLength(100)] string Name);
/// <summary>The only response that ever contains the plaintext key.</summary>
public record CreatedIntegrationApiKeyDto(IntegrationApiKeyDto Key, string PlainKey);

[ApiController, Route("api/integration-keys"), Authorize(Policy = "AdminOnly")]
public class IntegrationKeysController(AppDbContext db, IActivityLogService activity) : ControllerBase
{
    static IntegrationApiKeyDto Dto(IntegrationApiKey k) => new(k.Id, k.Name, k.Prefix, k.CreatedAt, k.LastUsedAt, k.RevokedAt);

    [HttpGet]
    public async Task<ActionResult<List<IntegrationApiKeyDto>>> List(CancellationToken ct) =>
        Ok((await db.Set<IntegrationApiKey>().OrderByDescending(k => k.CreatedAt).ToListAsync(ct)).Select(Dto).ToList());

    [HttpPost]
    public async Task<ActionResult<CreatedIntegrationApiKeyDto>> Create(CreateIntegrationApiKeyRequest req, CancellationToken ct)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var plain = "gsk_" + RandomNumberGenerator.GetString(alphabet, 40);
        var key = new IntegrationApiKey { Name = req.Name.Trim(), Prefix = plain[..10], KeyHash = IntegrationApiKeyMiddleware.Hash(plain), CreatedBy = User.Identity?.Name };
        db.Add(key);
        await db.SaveChangesAsync(ct);
        await activity.LogAsync(null, "IntegrationApiKey", key.Id, "Created", $"API-Schlüssel „{key.Name}“ erzeugt", ct);
        return Ok(new CreatedIntegrationApiKeyDto(Dto(key), plain));
    }

    [HttpPost("{id:guid}/revoke")]
    public async Task<ActionResult<IntegrationApiKeyDto>> Revoke(Guid id, CancellationToken ct)
    {
        var key = await db.Set<IntegrationApiKey>().FirstOrDefaultAsync(k => k.Id == id, ct);
        if (key == null) return NotFound();
        key.RevokedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await activity.LogAsync(null, "IntegrationApiKey", key.Id, "Revoked", $"API-Schlüssel „{key.Name}“ widerrufen", ct);
        return Ok(Dto(key));
    }
}
