using GentleSuite.Domain.Entities;
using GentleSuite.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace GentleSuite.Domain.Entities
{
    /// <summary>Generator settings for a customer's website legal texts (Impressum, Datenschutzerklärung,
    /// Barrierefreiheitserklärung). The texts themselves are generated in the frontend from this config,
    /// so they can be regenerated any time the customer's details change.</summary>
    public class WebsiteLegalProfile : BaseEntity
    {
        public Guid CustomerId { get; set; }
        public Customer Customer { get; set; } = null!;
        public string ConfigJson { get; set; } = "{}";
    }
}

namespace GentleSuite.API.Controllers
{
    public record WebsiteLegalPrefillDto(string CompanyName, string? Street, string? ZipCode, string? City, string? Country,
        string? ContactName, string? Email, string? Phone, string? Website, string? VatId, string? TaxId);
    public record WebsiteLegalProfileDto(Guid CustomerId, string CustomerName, JsonElement? Config, DateTimeOffset? UpdatedAt, WebsiteLegalPrefillDto Prefill);

    [ApiController, Route("api/website-legal"), Authorize]
    public class WebsiteLegalController(AppDbContext db) : ControllerBase
    {
        [HttpGet("{customerId}")]
        public async Task<ActionResult<WebsiteLegalProfileDto>> Get(Guid customerId)
        {
            var c = await db.Customers.Include(x => x.Contacts).Include(x => x.Locations).FirstOrDefaultAsync(x => x.Id == customerId);
            if (c == null) return NotFound();
            var p = await db.Set<WebsiteLegalProfile>().FirstOrDefaultAsync(x => x.CustomerId == customerId);
            var loc = c.Locations.FirstOrDefault(l => l.IsPrimary) ?? c.Locations.FirstOrDefault();
            var contact = c.Contacts.FirstOrDefault(x => x.IsPrimary) ?? c.Contacts.FirstOrDefault();
            var prefill = new WebsiteLegalPrefillDto(c.CompanyName, loc?.Street, loc?.ZipCode, loc?.City, loc?.Country,
                contact == null ? null : $"{contact.FirstName} {contact.LastName}".Trim(), contact?.Email, contact?.Phone, c.Website, c.VatId, c.TaxId);
            JsonElement? config = p == null ? null : JsonDocument.Parse(p.ConfigJson).RootElement.Clone();
            return Ok(new WebsiteLegalProfileDto(c.Id, c.CompanyName, config, p?.UpdatedAt ?? p?.CreatedAt, prefill));
        }

        [HttpPut("{customerId}")]
        public async Task<ActionResult<WebsiteLegalProfileDto>> Save(Guid customerId, [FromBody] JsonElement config)
        {
            if (config.ValueKind != JsonValueKind.Object) return BadRequest(new { error = "Ungültige Konfiguration." });
            if (!await db.Customers.AnyAsync(x => x.Id == customerId)) return NotFound();
            var p = await db.Set<WebsiteLegalProfile>().FirstOrDefaultAsync(x => x.CustomerId == customerId);
            if (p == null) { p = new WebsiteLegalProfile { CustomerId = customerId }; db.Add(p); }
            p.ConfigJson = config.GetRawText();
            p.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return await Get(customerId);
        }
    }
}
