using GentleSuite.Application.DTOs;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using GentleSuite.Application.Interfaces;
using GentleSuite.API.Hubs;
using GentleSuite.Domain.Enums;
using GentleSuite.Infrastructure.Data;
using GentleSuite.Infrastructure.Identity;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace GentleSuite.API.Controllers;

[ApiController, Route("api/[controller]")]
public class AuthController(UserManager<AppUser> userManager, IConfiguration config) : ControllerBase
{
    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest req)
    {
        var user = await userManager.FindByEmailAsync(req.Email);
        if (user == null || !await userManager.CheckPasswordAsync(user, req.Password)) return Unauthorized("Ungültige Anmeldedaten");
        var roles = await userManager.GetRolesAsync(user);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id), new(ClaimTypes.Name, user.FullName), new(ClaimTypes.Email, user.Email!) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"] ?? "GentleSuiteSecretKey_MinLength32Chars!!"));
        var expiry = DateTimeOffset.UtcNow.AddDays(7);
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(claims: claims, expires: expiry.UtcDateTime, signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)));
        return Ok(new LoginResponse(token, user.Email!, user.FullName, roles.ToList(), expiry));
    }

    [HttpPost("forgot-password"), AllowAnonymous]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest req, [FromServices] IEmailService email)
    {
        var user = await userManager.FindByEmailAsync(req.Email);
        if (user == null) return Ok(); // no user disclosure
        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var encodedToken = Uri.EscapeDataString(token);
        var resetUrl = $"{config["FrontendBaseUrl"] ?? "http://localhost:3000"}/reset-password?email={Uri.EscapeDataString(req.Email)}&token={encodedToken}";
        try { await email.SendTemplatedEmailAsync(req.Email, "password-reset", new() { ["ResetUrl"] = resetUrl, ["FullName"] = user.FullName }, ct: CancellationToken.None); } catch { }
        return Ok();
    }

    [HttpPost("reset-password"), AllowAnonymous]
    public async Task<IActionResult> ResetPassword(ResetPasswordConfirmRequest req)
    {
        var user = await userManager.FindByEmailAsync(req.Email);
        if (user == null) return BadRequest("Ungültiger Link.");
        var result = await userManager.ResetPasswordAsync(user, req.Token, req.NewPassword);
        if (!result.Succeeded) return BadRequest(result.Errors.FirstOrDefault()?.Description ?? "Fehler beim Zurücksetzen.");
        return Ok();
    }
}

[ApiController, Route("api/[controller]"), Authorize]
public class CustomersController(ICustomerService svc) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<PagedResult<CustomerListDto>>> List([FromQuery] PaginationParams p, [FromQuery] CustomerStatus? status, [FromQuery] Guid? serviceId) => Ok(await svc.GetCustomersAsync(p, status, serviceId));
    [HttpPost("check-duplicate")] public async Task<ActionResult<DuplicateCheckResultDto>> CheckDuplicate(DuplicateCheckRequest req) => Ok(await svc.CheckDuplicateAsync(req));
    [HttpGet("{id}")] public async Task<ActionResult<CustomerDetailDto>> Get(Guid id) { var r = await svc.GetByIdAsync(id); return r == null ? NotFound() : Ok(r); }
    [HttpPost] public async Task<ActionResult<CustomerDetailDto>> Create(CreateCustomerRequest req) => Ok(await svc.CreateAsync(req));
    [HttpPost("quick")] public async Task<ActionResult<CustomerDetailDto>> CreateQuick(CreateCustomerQuickRequest req) => Ok(await svc.CreateQuickAsync(req));
    [HttpPut("{id}")] public async Task<ActionResult<CustomerDetailDto>> Update(Guid id, UpdateCustomerRequest req) => Ok(await svc.UpdateAsync(id, req));
    [HttpPut("{id}/reminder-stop")]
    public async Task<IActionResult> UpdateReminderStop(Guid id, UpdateReminderStopRequest req, [FromServices] AppDbContext db)
    {
        var c = await db.Customers.FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        c.ReminderStop = req.ReminderStop;
        await db.SaveChangesAsync();
        return NoContent();
    }
    [HttpPost("{id}/resend-intake")] public async Task<IActionResult> ResendIntake(Guid id) { await svc.ResendIntakeAsync(id); return NoContent(); }
    [HttpPost("{id}/send-email")]
    public async Task<IActionResult> SendEmail(Guid id, SendCustomerEmailRequest req, [FromServices] IEmailService email)
    {
        if (string.IsNullOrWhiteSpace(req.To) || string.IsNullOrWhiteSpace(req.Subject)) return BadRequest("Empfänger und Betreff sind erforderlich.");
        await email.SendEmailAsync(req.To, req.Subject, req.Body ?? "");
        return NoContent();
    }
    [HttpPost("{id}/gdpr-export")] public async Task<ActionResult<GdprExportDto>> GdprExport(Guid id) => Ok(await svc.ExportGdprAsync(id));
    [HttpPost("{id}/gdpr-erase"), Authorize(Policy = "AdminOnly")] public async Task<IActionResult> GdprErase(Guid id, GdprEraseRequest req) { await svc.EraseGdprAsync(id, req); return NoContent(); }
    [HttpDelete("{id}")] public async Task<IActionResult> Delete(Guid id) { await svc.DeleteAsync(id); return NoContent(); }
    [HttpPost("{id}/contacts")] public async Task<ActionResult<ContactDto>> AddContact(Guid id, CreateContactRequest req) => Ok(await svc.AddContactAsync(id, req));
    [HttpPut("{id}/contacts/{contactId}")] public async Task<ActionResult<ContactDto>> UpdateContact(Guid id, Guid contactId, UpdateContactRequest req) => Ok(await svc.UpdateContactAsync(id, contactId, req));
    [HttpDelete("{id}/contacts/{contactId}")] public async Task<IActionResult> DeleteContact(Guid id, Guid contactId) { await svc.DeleteContactAsync(id, contactId); return NoContent(); }
    [HttpPost("{id}/locations")] public async Task<ActionResult<LocationDto>> AddLocation(Guid id, CreateLocationRequest req) => Ok(await svc.AddLocationAsync(id, req));
    [HttpPut("{id}/locations/{locationId}")] public async Task<ActionResult<LocationDto>> UpdateLocation(Guid id, Guid locationId, UpdateLocationRequest req) => Ok(await svc.UpdateLocationAsync(id, locationId, req));
    [HttpDelete("{id}/locations/{locationId}")] public async Task<IActionResult> DeleteLocation(Guid id, Guid locationId) { await svc.DeleteLocationAsync(id, locationId); return NoContent(); }

    [HttpGet("export.csv")]
    public async Task<IActionResult> ExportCsv([FromServices] AppDbContext db, [FromQuery] CustomerStatus? status)
    {
        var q = db.Customers.Include(c => c.Contacts).AsQueryable();
        if (status.HasValue) q = q.Where(c => c.Status == status.Value);
        var items = await q.OrderBy(c => c.CompanyName).ToListAsync();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Kundennummer;Firmenname;Status;Branche;Website;Ansprechpartner;E-Mail;Telefon");
        foreach (var c in items)
        {
            var primary = c.Contacts.FirstOrDefault(x => x.IsPrimary) ?? c.Contacts.FirstOrDefault();
            sb.AppendLine($"{c.CustomerNumber};{c.CompanyName};{c.Status};{c.Industry};{c.Website};{primary?.FullName};{primary?.Email};{primary?.Phone}");
        }
        return File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", $"Kunden_{DateTime.Today:yyyy-MM-dd}.csv");
    }
    [HttpPost("import-csv"), Consumes("multipart/form-data")]
    public async Task<ActionResult<CsvImportResultDto>> ImportCsv(IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0) return BadRequest("Keine Datei.");
        // Detect encoding: check BOM, otherwise default to Windows-1252 (Lexware/DATEV exports)
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var ms = new System.IO.MemoryStream();
        await file.OpenReadStream().CopyToAsync(ms);
        ms.Position = 0;
        var bom = new byte[3]; ms.Read(bom, 0, 3); ms.Position = 0;
        var enc = (bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
            ? System.Text.Encoding.UTF8
            : System.Text.Encoding.GetEncoding("windows-1252");
        using var reader = new System.IO.StreamReader(ms, enc);
        var lines = new List<string>();
        while (!reader.EndOfStream) { var line = await reader.ReadLineAsync(); if (line != null) lines.Add(line); }
        if (lines.Count < 2) return BadRequest("CSV leer oder nur Header.");

        // Normalize headers to lowercase for flexible matching
        var headers = lines[0].Split(';').Select(h => h.Trim().Trim('"').ToLowerInvariant()).ToList();
        int ColIdx(string name) => headers.IndexOf(name.ToLowerInvariant());

        int imported = 0, skipped = 0;
        var errors = new List<string>();

        // Strip leading quote+plus (e.g. '+49...) from phone numbers
        static string CleanPhone(string p)
        {
            var s = p.TrimStart('\'').Trim();
            if (s.Length == 0) return "";
            return s.StartsWith('+') ? s : (s.Length > 0 ? "+" + s : "");
        }

        for (int i = 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = line.Split(';').Select(c => c.Trim().Trim('"')).ToArray();
            string Get(string name) { var idx = ColIdx(name); return idx >= 0 && idx < cols.Length ? cols[idx].Trim() : ""; }
            string GetAny(params string[] names) { foreach (var n in names) { var v = Get(n); if (v.Length > 0) return v; } return ""; }

            // Company-level fields (Lexware/DATEV export format)
            var company  = GetAny("firmenname", "firma", "companyname", "unternehmen");
            var topFn    = GetAny("vorname", "firstname");
            var topLn    = GetAny("nachname", "lastname");
            var email    = GetAny("e-mail 1", "e-mail", "email 1", "email");
            var phone    = CleanPhone(GetAny("telefon 1", "telefon", "phone 1", "phone"));
            var taxId    = GetAny("steuernummer");
            var vatId    = GetAny("umsatzsteuer id", "umsatzsteuer-id", "ustid");
            var street   = GetAny("straße 1", "strasse 1", "straße", "strasse", "street");
            var zip      = GetAny("plz 1", "plz", "zipcode");
            var city     = GetAny("ort 1", "ort", "city");
            var country  = GetAny("land 1", "land", "country");

            // Ansprechpartner 1 (contact person)
            var cpFn    = GetAny("ansprechpartner 1 vorname");
            var cpLn    = GetAny("ansprechpartner 1 nachname");
            var cpEmail = GetAny("ansprechpartner 1 e-mail");
            var cpPhone = CleanPhone(GetAny("ansprechpartner 1 telefon"));

            if (string.IsNullOrWhiteSpace(company) && string.IsNullOrWhiteSpace(topLn))
            { errors.Add($"Zeile {i + 1}: Firmenname oder Nachname fehlt."); skipped++; continue; }

            var effectiveCompany = !string.IsNullOrWhiteSpace(company)
                ? company
                : $"{topFn} {topLn}".Trim();

            // Prefer Ansprechpartner 1 data; fall back to top-level Vorname/Nachname
            var contactFn    = cpFn.Length > 0 ? cpFn : topFn;
            var contactLn    = cpLn.Length > 0 ? cpLn : topLn;
            var contactEmail = cpEmail.Length > 0 ? cpEmail : email;
            var contactPhone = cpPhone.Length > 0 ? cpPhone : phone;

            try
            {
                var req = new CreateCustomerRequest(
                    CompanyName: effectiveCompany,
                    Industry: null,
                    Website: GetAny("website") is { Length: > 0 } w ? w : null,
                    TaxId: taxId.Length > 0 ? taxId : null,
                    VatId: vatId.Length > 0 ? vatId : null,
                    PrimaryContact: new CreateContactRequest(
                        FirstName: contactFn, LastName: contactLn,
                        Email: contactEmail.Length > 0 ? contactEmail : "",
                        Phone: contactPhone.Length > 0 ? contactPhone : null,
                        Position: null, IsPrimary: true),
                    PrimaryLocation: (street.Length > 0 || city.Length > 0 || zip.Length > 0) ? new CreateLocationRequest(
                        Label: "Hauptsitz",
                        Street: street, City: city, ZipCode: zip,
                        Country: country.Length > 0 ? country : "Deutschland") : null,
                    DesiredServiceIds: null);
                await svc.CreateAsync(req, ct);
                imported++;
            }
            catch (Exception ex) { errors.Add($"Zeile {i + 1} ({effectiveCompany}): {ex.Message}"); skipped++; }
        }

        return Ok(new CsvImportResultDto(imported, skipped, errors));
    }
}

[ApiController, Route("api/customers/{customerId}/notes"), Authorize]
public class NotesController(ICustomerNoteService svc) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<List<CustomerNoteDto>>> List(Guid customerId) => Ok(await svc.GetNotesAsync(customerId));
    [HttpPost] public async Task<ActionResult<CustomerNoteDto>> Create(Guid customerId, CreateNoteRequest req) => Ok(await svc.CreateAsync(customerId, req));
    [HttpPut("{noteId}")] public async Task<ActionResult<CustomerNoteDto>> Update(Guid customerId, Guid noteId, UpdateNoteRequest req) => Ok(await svc.UpdateAsync(customerId, noteId, req));
    [HttpDelete("{noteId}")] public async Task<IActionResult> Delete(Guid customerId, Guid noteId) { await svc.DeleteAsync(customerId, noteId); return NoContent(); }
}

[ApiController, Route("api/[controller]"), Authorize]
public class OnboardingController(IOnboardingService svc, AppDbContext db) : ControllerBase
{
    [HttpGet("customer/{customerId}")] public async Task<ActionResult<List<OnboardingWorkflowDto>>> ByCustomer(Guid customerId) => Ok(await svc.GetByCustomerAsync(customerId));
    [HttpGet("project/{projectId}")] public async Task<ActionResult<List<OnboardingWorkflowDto>>> ByProject(Guid projectId) => Ok(await svc.GetByProjectAsync(projectId));
    [HttpGet("templates")] public async Task<ActionResult<List<OnboardingTemplateListDto>>> Templates() => Ok(await svc.GetTemplatesAsync());
    [HttpGet("templates/{id}")] public async Task<ActionResult<OnboardingTemplateDetailDto>> TemplateById(Guid id) => Ok(await svc.GetTemplateByIdAsync(id));
    [HttpPost("templates")] public async Task<ActionResult<OnboardingTemplateDetailDto>> CreateTemplate(CreateOnboardingTemplateRequest req) => Ok(await svc.CreateTemplateAsync(req));
    [HttpPut("templates/{id}")] public async Task<ActionResult<OnboardingTemplateDetailDto>> UpdateTemplate(Guid id, CreateOnboardingTemplateRequest req) => Ok(await svc.UpdateTemplateAsync(id, req));
    [HttpDelete("templates/{id}")] public async Task<IActionResult> DeleteTemplate(Guid id) { await svc.DeleteTemplateAsync(id); return NoContent(); }
    [HttpPost("start/{customerId}")] public async Task<ActionResult<OnboardingWorkflowDto>> Start(Guid customerId, [FromQuery] Guid? templateId) => Ok(await svc.StartWorkflowAsync(customerId, templateId));
    [HttpPost("start/project/{projectId}")]
    public async Task<ActionResult<OnboardingWorkflowDto>> StartForProject(Guid projectId, [FromQuery] Guid templateId)
    {
        if (templateId == Guid.Empty) return BadRequest("Onboarding-Template ist erforderlich.");
        var existing = await svc.GetByProjectAsync(projectId);
        if (existing.Count > 0) return Ok(existing[0]);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId);
        if (project == null) return NotFound();
        return Ok(await svc.StartWorkflowAsync(project.CustomerId, templateId, projectId));
    }
    [HttpPut("steps/{stepId}/status")] public async Task<IActionResult> UpdateStep(Guid stepId, UpdateStepStatusRequest req) { await svc.UpdateStepStatusAsync(stepId, req); return NoContent(); }
    [HttpPut("tasks/{taskId}/status")] public async Task<IActionResult> UpdateTask(Guid taskId, UpdateTaskStatusRequest req) { await svc.UpdateTaskStatusAsync(taskId, req); return NoContent(); }
}

[ApiController, Route("api/[controller]"), Authorize]
public class QuotesController(IQuoteService svc) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<PagedResult<QuoteListDto>>> List([FromQuery] PaginationParams p, [FromQuery] QuoteStatus? status, [FromQuery] Guid? customerId) => Ok(await svc.GetQuotesAsync(p, status, customerId));
    [HttpGet("{id}")] public async Task<ActionResult<QuoteDetailDto>> Get(Guid id) { var r = await svc.GetByIdAsync(id); return r == null ? NotFound() : Ok(r); }
    [HttpPost] public async Task<ActionResult<QuoteDetailDto>> Create(CreateQuoteRequest req) => Ok(await svc.CreateAsync(req));
    [HttpPost("from-template/{customerId}/{templateId}")] public async Task<ActionResult<QuoteDetailDto>> FromTemplate(Guid customerId, Guid templateId) => Ok(await svc.CreateFromTemplateAsync(customerId, templateId));
    [HttpPut("{id}/lines")] public async Task<ActionResult<QuoteDetailDto>> UpdateLines(Guid id, List<CreateQuoteLineRequest> lines) => Ok(await svc.UpdateLinesAsync(id, lines));
    [HttpPost("{id}/send")] public async Task<IActionResult> Send(Guid id, SendQuoteRequest req) { await svc.SendAsync(id, req); return NoContent(); }
    [HttpGet("{id}/pdf")] public async Task<IActionResult> Pdf(Guid id) => File(await svc.GeneratePdfAsync(id), "application/pdf", $"Angebot.pdf");
    [HttpGet("templates")] public async Task<ActionResult<List<QuoteTemplateDto>>> Templates() => Ok(await svc.GetTemplatesAsync());
    [HttpPost("templates")] public async Task<ActionResult<QuoteTemplateDto>> CreateTemplate(CreateQuoteTemplateRequest req) => Ok(await svc.CreateTemplateAsync(req));
    [HttpPut("templates/{id}")] public async Task<ActionResult<QuoteTemplateDto>> UpdateTemplate(Guid id, UpdateQuoteTemplateRequest req) => Ok(await svc.UpdateTemplateAsync(id, req));
    [HttpDelete("templates/{id}")] public async Task<IActionResult> DeleteTemplate(Guid id) { await svc.DeleteTemplateAsync(id); return NoContent(); }
    [HttpPost("{id}/order")] public async Task<ActionResult<QuoteDetailDto>> MarkAsOrdered(Guid id) => Ok(await svc.MarkAsOrderedAsync(id));
    [HttpPost("{id}/convert-to-invoice")] public async Task<ActionResult<InvoiceDetailDto>> ConvertToInvoice(Guid id) => Ok(await svc.ConvertToInvoiceAsync(id));
    [HttpPut("{id}")] public async Task<ActionResult<QuoteDetailDto>> Update(Guid id, UpdateQuoteRequest req) => Ok(await svc.UpdateAsync(id, req));
    [HttpDelete("{id}")] public async Task<IActionResult> Delete(Guid id) { await svc.DeleteAsync(id); return NoContent(); }
    [HttpPost("{id}/duplicate")] public async Task<ActionResult<QuoteDetailDto>> Duplicate(Guid id) => Ok(await svc.DuplicateAsync(id));
    [HttpPost("{id}/new-version")] public async Task<ActionResult<QuoteDetailDto>> NewVersion(Guid id) => Ok(await svc.CreateNewVersionAsync(id));
    [HttpGet("{id}/versions")] public async Task<ActionResult<List<QuoteVersionDto>>> Versions(Guid id) => Ok(await svc.GetVersionsAsync(id));
}

[ApiController, Route("api/approval")]
public class ApprovalController(IQuoteService svc) : ControllerBase
{
    [HttpGet("{token}")] public async Task<ActionResult<QuoteDetailDto>> Get(string token) { var r = await svc.GetByApprovalTokenAsync(token); return r == null ? NotFound() : Ok(r); }
    [HttpPost("{token}")] public async Task<IActionResult> Process(string token, ApprovalRequest req) { await svc.ProcessApprovalAsync(token, req, HttpContext.Connection.RemoteIpAddress?.ToString()); return NoContent(); }
}

[ApiController, Route("api/[controller]"), Authorize]
public class InvoicesController(IInvoiceService svc) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<PagedResult<InvoiceListDto>>> List([FromQuery] PaginationParams p, [FromQuery] InvoiceStatus? status, [FromQuery] Guid? customerId) => Ok(await svc.GetInvoicesAsync(p, status, customerId));
    [HttpGet("{id}")] public async Task<ActionResult<InvoiceDetailDto>> Get(Guid id) { var r = await svc.GetByIdAsync(id); return r == null ? NotFound() : Ok(r); }
    [HttpPost] public async Task<ActionResult<InvoiceDetailDto>> Create(CreateInvoiceRequest req) => Ok(await svc.CreateAsync(req));
    [HttpPut("{id}")] public async Task<ActionResult<InvoiceDetailDto>> Update(Guid id, UpdateInvoiceRequest req) => Ok(await svc.UpdateAsync(id, req));
    [HttpPost("{id}/finalize"), Authorize(Policy = "AccountingOrAdmin")] public async Task<ActionResult<InvoiceDetailDto>> Finalize(Guid id, FinalizeInvoiceRequest req) => Ok(await svc.FinalizeAsync(id, req));
    [HttpPost("{id}/payment")] public async Task<ActionResult<InvoiceDetailDto>> Payment(Guid id, RecordPaymentRequest req) => Ok(await svc.RecordPaymentAsync(id, req));
    [HttpPost("{id}/cancel"), Authorize(Policy = "AdminOnly")] public async Task<ActionResult<InvoiceDetailDto>> Cancel(Guid id, CreateCancellationRequest req) => Ok(await svc.CreateCancellationAsync(id, req));
    [HttpPut("{id}/reminder-stop")]
    public async Task<IActionResult> UpdateReminderStop(Guid id, UpdateReminderStopRequest req, [FromServices] AppDbContext db)
    {
        var i = await db.Invoices.FirstOrDefaultAsync(x => x.Id == id);
        if (i == null) return NotFound();
        i.ReminderStop = req.ReminderStop;
        await db.SaveChangesAsync();
        return NoContent();
    }
    [HttpPost("{id}/send")] public async Task<IActionResult> Send(Guid id) { await svc.SendAsync(id); return NoContent(); }
    [HttpPost("{id}/send-reminder")] public async Task<IActionResult> SendReminder(Guid id) { await svc.SendReminderAsync(id); return NoContent(); }
    [HttpPost("from-time-entries")] public async Task<ActionResult<InvoiceDetailDto>> FromTimeEntries(CreateInvoiceFromTimeEntriesRequest req) => Ok(await svc.CreateFromTimeEntriesAsync(req));
    [HttpGet("{id}/pdf")] public async Task<IActionResult> Pdf(Guid id) => File(await svc.GeneratePdfAsync(id), "application/pdf", $"Rechnung.pdf");
    [HttpGet("export.csv")]
    public async Task<IActionResult> ExportCsv([FromServices] AppDbContext db, [FromQuery] InvoiceStatus? status)
    {
        var q = db.Invoices.Include(i => i.Customer).AsQueryable();
        if (status.HasValue) q = q.Where(i => i.Status == status.Value);
        var items = await q.OrderByDescending(i => i.InvoiceDate).ToListAsync();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Rechnungsnummer;Kunde;Status;Datum;Faellig;Netto;MwSt;Brutto;Bezahlt");
        foreach (var i in items)
            sb.AppendLine($"{i.InvoiceNumber};{i.Customer?.CompanyName};{i.Status};{i.InvoiceDate:dd.MM.yyyy};{i.DueDate:dd.MM.yyyy};{i.NetTotal:F2};{i.VatAmount:F2};{i.GrossTotal:F2};{(i.PaidAt.HasValue ? i.PaidAt.Value.ToString("dd.MM.yyyy") : "")}");
        return File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", $"Rechnungen_{DateTime.Today:yyyy-MM-dd}.csv");
    }
    [HttpGet("{id}/xrechnung")] public async Task<IActionResult> XRechnung(Guid id)
    {
        var xml = await svc.GenerateXRechnungXmlAsync(id);
        var inv = await svc.GetByIdAsync(id);
        var fileName = $"XRechnung_{inv?.InvoiceNumber ?? id.ToString()}.xml";
        return File(xml, "application/xml", fileName);
    }
}

[ApiController, Route("api/[controller]"), Authorize]
public class ExpensesController(IExpenseService svc) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<PagedResult<ExpenseListDto>>> List([FromQuery] PaginationParams p, [FromQuery] Guid? categoryId) => Ok(await svc.GetExpensesAsync(p, categoryId));
    [HttpGet("{id}")] public async Task<ActionResult<ExpenseDetailDto>> Get(Guid id) { var r = await svc.GetByIdAsync(id); return r == null ? NotFound() : Ok(r); }
    [HttpPost] public async Task<ActionResult<ExpenseDetailDto>> Create(CreateExpenseRequest req) => Ok(await svc.CreateAsync(req));
    [HttpPut("{id}")] public async Task<ActionResult<ExpenseDetailDto>> Update(Guid id, UpdateExpenseRequest req) => Ok(await svc.UpdateAsync(id, req));
    [HttpDelete("{id}")] public async Task<IActionResult> Delete(Guid id) { await svc.DeleteAsync(id); return NoContent(); }
    [HttpPost("{id}/book")] public async Task<IActionResult> Book(Guid id) { await svc.BookAsync(id); return NoContent(); }
    [HttpGet("categories")] public async Task<ActionResult<List<ExpenseCategoryDto>>> Categories() => Ok(await svc.GetCategoriesAsync());
    [HttpPost("categories")] public async Task<ActionResult<ExpenseCategoryDto>> CreateCategory(CreateExpenseCategoryRequest req) => Ok(await svc.CreateCategoryAsync(req));
    [HttpPut("categories/{id}")] public async Task<ActionResult<ExpenseCategoryDto>> UpdateCategory(Guid id, UpdateExpenseCategoryRequest req) => Ok(await svc.UpdateCategoryAsync(id, req));
    [HttpDelete("categories/{id}")] public async Task<IActionResult> DeleteCategory(Guid id) { await svc.DeleteCategoryAsync(id); return NoContent(); }
    [HttpPost("{id}/receipt")] public async Task<IActionResult> UploadReceipt(Guid id, IFormFile file) { await svc.UploadReceiptAsync(id, file.OpenReadStream(), file.FileName, file.ContentType); return NoContent(); }
    [HttpGet("{id}/receipt")] public async Task<IActionResult> DownloadReceipt(Guid id) { var r = await svc.DownloadReceiptAsync(id); return r == null ? NotFound() : File(r.Value.Stream, r.Value.ContentType, r.Value.FileName); }
}

[ApiController, Route("api/[controller]"), Authorize]
public class ProjectsController(IProjectService svc, IHubContext<ProjectBoardHub> hub) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<PagedResult<ProjectListDto>>> List([FromQuery] PaginationParams p, [FromQuery] ProjectStatus? status, [FromQuery] Guid? customerId) => Ok(await svc.GetProjectsAsync(p, status, customerId));
    [HttpGet("{id}")] public async Task<ActionResult<ProjectDetailDto>> Get(Guid id) { var r = await svc.GetByIdAsync(id); return r == null ? NotFound() : Ok(r); }
    [HttpPost]
    public async Task<ActionResult<ProjectDetailDto>> Create(CreateProjectRequest req)
    {
        if (req.OnboardingTemplateId == Guid.Empty) return BadRequest("Onboarding-Template ist erforderlich.");
        return Ok(await svc.CreateAsync(req));
    }
    [HttpPut("{id}")] public async Task<ActionResult<ProjectDetailDto>> Update(Guid id, UpdateProjectRequest req) => Ok(await svc.UpdateAsync(id, req));
    [HttpDelete("{id}")] public async Task<IActionResult> Delete(Guid id) { await svc.DeleteAsync(id); return NoContent(); }
    [HttpPost("{id}/milestones")] public async Task<ActionResult<MilestoneDto>> AddMilestone(Guid id, CreateMilestoneRequest req) => Ok(await svc.AddMilestoneAsync(id, req));
    [HttpPut("milestones/{milestoneId}")] public async Task<ActionResult<MilestoneDto>> UpdateMilestone(Guid milestoneId, CreateMilestoneRequest req) => Ok(await svc.UpdateMilestoneAsync(milestoneId, req));
    [HttpDelete("milestones/{milestoneId}")] public async Task<IActionResult> DeleteMilestone(Guid milestoneId) { await svc.DeleteMilestoneAsync(milestoneId); return NoContent(); }
    [HttpPost("{id}/comments")] public async Task<ActionResult<ProjectCommentDto>> AddComment(Guid id, CreateProjectCommentRequest req) => Ok(await svc.AddCommentAsync(id, req));
    [HttpGet("{id}/board/tasks")] public async Task<ActionResult<List<ProjectBoardTaskDto>>> BoardTasks(Guid id) => Ok(await svc.GetBoardTasksAsync(id));
    [HttpPost("{id}/board/tasks")]
    public async Task<ActionResult<ProjectBoardTaskDto>> CreateBoardTask(Guid id, CreateProjectBoardTaskRequest req)
    {
        var created = await svc.CreateBoardTaskAsync(id, req);
        await PublishBoardUpdate(id, "task-created", created.Id);
        return Ok(created);
    }
    [HttpPut("{id}/board/tasks/{taskId}")]
    public async Task<ActionResult<ProjectBoardTaskDto>> UpdateBoardTask(Guid id, Guid taskId, UpdateProjectBoardTaskRequest req)
    {
        var updated = await svc.UpdateBoardTaskAsync(id, taskId, req);
        await PublishBoardUpdate(id, "task-updated", updated.Id);
        return Ok(updated);
    }
    [HttpPut("{id}/board/tasks/{taskId}/move")]
    public async Task<ActionResult<ProjectBoardTaskDto>> MoveBoardTask(Guid id, Guid taskId, MoveProjectBoardTaskRequest req)
    {
        var moved = await svc.MoveBoardTaskAsync(id, taskId, req);
        await PublishBoardUpdate(id, "task-moved", moved.Id);
        return Ok(moved);
    }
    [HttpDelete("{id}/board/tasks/{taskId}")]
    public async Task<IActionResult> DeleteBoardTask(Guid id, Guid taskId)
    {
        await svc.DeleteBoardTaskAsync(id, taskId);
        await PublishBoardUpdate(id, "task-deleted", taskId);
        return NoContent();
    }
    [HttpGet("{id}/members")] public async Task<ActionResult<List<TeamMemberDto>>> Members(Guid id) => Ok(await svc.GetMembersAsync(id));
    [HttpPost("{id}/members/{teamMemberId}"), Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> AddMember(Guid id, Guid teamMemberId)
    {
        await svc.AddMemberAsync(id, teamMemberId);
        await PublishBoardUpdate(id, "members-updated", null);
        return NoContent();
    }
    [HttpDelete("{id}/members/{teamMemberId}"), Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> RemoveMember(Guid id, Guid teamMemberId)
    {
        await svc.RemoveMemberAsync(id, teamMemberId);
        await PublishBoardUpdate(id, "members-updated", null);
        return NoContent();
    }

    private async Task PublishBoardUpdate(Guid projectId, string type, Guid? taskId)
    {
        var payload = new { projectId, type, taskId, at = DateTimeOffset.UtcNow };
        await hub.Clients.Group($"project-board-{projectId}").SendAsync("BoardUpdated", payload);
    }
}

[ApiController, Route("api/[controller]"), Authorize]
public class SubscriptionsController(ISubscriptionService svc) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<List<CustomerSubscriptionDto>>> All() => Ok(await svc.GetAllAsync());
    [HttpGet("plans")] public async Task<ActionResult<List<SubscriptionPlanDto>>> Plans() => Ok(await svc.GetPlansAsync());
    [HttpPost("plans")] public async Task<ActionResult<SubscriptionPlanDto>> CreatePlan(CreatePlanRequest req) => Ok(await svc.CreatePlanAsync(req));
    [HttpPut("plans/{id}")] public async Task<ActionResult<SubscriptionPlanDto>> UpdatePlan(Guid id, UpdatePlanRequest req) => Ok(await svc.UpdatePlanAsync(id, req));
    [HttpDelete("plans/{id}")] public async Task<IActionResult> DeletePlan(Guid id) { await svc.DeletePlanAsync(id); return NoContent(); }
    [HttpGet("customer/{customerId}")] public async Task<ActionResult<List<CustomerSubscriptionDto>>> CustomerSubs(Guid customerId) => Ok(await svc.GetCustomerSubscriptionsAsync(customerId));
    [HttpPost] public async Task<ActionResult<CustomerSubscriptionDto>> Create(CreateSubscriptionRequest req) => Ok(await svc.CreateAsync(req));
    [HttpPut("{id}/status")] public async Task<IActionResult> UpdateStatus(Guid id, UpdateSubscriptionStatusRequest req) { await svc.UpdateStatusAsync(id, req); return NoContent(); }
    [HttpPost("{id}/confirm")] public async Task<IActionResult> Confirm(Guid id) { await svc.ConfirmAsync(id); return Ok(); }
    [HttpGet("{id}/invoices")] public async Task<ActionResult<List<SubscriptionInvoiceDto>>> GetInvoices(Guid id) => Ok(await svc.GetInvoicesAsync(id));
}

[ApiController, Route("api/[controller]"), Authorize]
public class TimeTrackingController(ITimeTrackingService svc) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<List<TimeEntryDto>>> List([FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] Guid? projectId, [FromQuery] Guid? customerId) => Ok(await svc.GetEntriesAsync(from, to, projectId, customerId));
    [HttpPost] public async Task<ActionResult<TimeEntryDto>> Create(CreateTimeEntryRequest req) => Ok(await svc.CreateAsync(req));
    [HttpPut("{id}")] public async Task<ActionResult<TimeEntryDto>> Update(Guid id, CreateTimeEntryRequest req) => Ok(await svc.UpdateAsync(id, req));
    [HttpDelete("{id}")] public async Task<IActionResult> Delete(Guid id) { await svc.DeleteAsync(id); return NoContent(); }
    [HttpGet("summary")] public async Task<ActionResult<TimeEntrySummaryDto>> Summary([FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to, [FromQuery] Guid? projectId) => Ok(await svc.GetSummaryAsync(from, to, projectId));
}

[ApiController, Route("api/[controller]"), Authorize]
public class DashboardController(IDashboardService svc) : ControllerBase
{
    [HttpGet("kpis")] public async Task<ActionResult<DashboardKpis>> Kpis() => Ok(await svc.GetKpisAsync());
    [HttpGet("finance")] public async Task<ActionResult<FinanceDashboardDto>> Finance() => Ok(await svc.GetFinanceDashboardAsync());
}

[ApiController, Route("api/[controller]"), Authorize]
public class ServiceCatalogController(IServiceCatalogService svc) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<List<ServiceCategoryDto>>> Get() => Ok(await svc.GetCategoriesAsync());
    [HttpPost("categories")] public async Task<ActionResult<ServiceCategoryDto>> CreateCategory(CreateServiceCategoryRequest req) => Ok(await svc.CreateCategoryAsync(req));
    [HttpPut("categories/{id}")] public async Task<ActionResult<ServiceCategoryDto>> UpdateCategory(Guid id, UpdateServiceCategoryRequest req) => Ok(await svc.UpdateCategoryAsync(id, req));
    [HttpDelete("categories/{id}")] public async Task<IActionResult> DeleteCategory(Guid id) { await svc.DeleteCategoryAsync(id); return NoContent(); }
    [HttpPost("items")] public async Task<ActionResult<ServiceCatalogItemDto>> CreateItem(CreateServiceItemRequest req) => Ok(await svc.CreateItemAsync(req));
    [HttpPut("items/{id}")] public async Task<ActionResult<ServiceCatalogItemDto>> UpdateItem(Guid id, UpdateServiceItemRequest req) => Ok(await svc.UpdateItemAsync(id, req));
    [HttpDelete("items/{id}")] public async Task<IActionResult> DeleteItem(Guid id) { await svc.DeleteItemAsync(id); return NoContent(); }
}

[ApiController, Route("api/[controller]"), Authorize]
public class SettingsController(ICompanySettingsService svc, IFileStorageService fs, AppDbContext db, INumberSequenceService seq) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<CompanySettingsDto>> Get() => Ok(await svc.GetAsync());
    [HttpPut] public async Task<ActionResult<CompanySettingsDto>> Update(UpdateCompanySettingsRequest req) => Ok(await svc.UpdateAsync(req));
    [HttpPost("logo")] public async Task<IActionResult> UploadLogo(IFormFile file) { var path = await fs.UploadAsync(file.OpenReadStream(), file.FileName, file.ContentType); var s = await db.CompanySettings.FirstOrDefaultAsync(); if (s != null) { s.LogoPath = path; await db.SaveChangesAsync(); } return Ok(new { path }); }
    [HttpGet("number-ranges"), Authorize(Policy = "AccountingOrAdmin")]
    public async Task<ActionResult<List<NumberRangeDto>>> GetNumberRanges([FromQuery] int? year = null)
    {
        var y = year ?? DateTime.UtcNow.Year;
        var ranges = await seq.GetRangesAsync(y);
        return Ok(ranges.Select(r => new NumberRangeDto(r.EntityType, r.Year, r.Prefix, r.NextValue, r.Padding)).ToList());
    }

    [HttpPut("number-ranges"), Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<NumberRangeDto>> UpsertNumberRange(UpdateNumberRangeRequest req)
    {
        var updated = await seq.UpsertRangeAsync(new(req.EntityType, req.Year, req.Prefix, req.NextValue, req.Padding));
        return Ok(new NumberRangeDto(updated.EntityType, updated.Year, updated.Prefix, updated.NextValue, updated.Padding));
    }

    [HttpGet("reminders"), Authorize(Policy = "AccountingOrAdmin")]
    public async Task<ActionResult<ReminderSettingsDto>> GetReminderSettings()
    {
        var r = await db.ReminderSettings.FirstOrDefaultAsync() ?? new GentleSuite.Domain.Entities.ReminderSettings();
        return Ok(new ReminderSettingsDto(r.Level1Days, r.Level2Days, r.Level3Days, r.Level1Fee, r.Level2Fee, r.Level3Fee, r.AnnualInterestPercent));
    }

    [HttpPut("reminders"), Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<ReminderSettingsDto>> UpdateReminderSettings(UpdateReminderSettingsRequest req)
    {
        if (!(req.Level1Days > 0 && req.Level2Days > req.Level1Days && req.Level3Days > req.Level2Days))
            return BadRequest("Reminder intervals must be ascending and > 0.");

        var r = await db.ReminderSettings.FirstOrDefaultAsync();
        if (r == null) { r = new GentleSuite.Domain.Entities.ReminderSettings(); db.ReminderSettings.Add(r); }
        r.Level1Days = req.Level1Days;
        r.Level2Days = req.Level2Days;
        r.Level3Days = req.Level3Days;
        r.Level1Fee = req.Level1Fee;
        r.Level2Fee = req.Level2Fee;
        r.Level3Fee = req.Level3Fee;
        r.AnnualInterestPercent = req.AnnualInterestPercent;
        await db.SaveChangesAsync();
        return Ok(new ReminderSettingsDto(r.Level1Days, r.Level2Days, r.Level3Days, r.Level1Fee, r.Level2Fee, r.Level3Fee, r.AnnualInterestPercent));
    }
}

[ApiController, Route("api/[controller]"), Authorize]
public class ActivityController(IActivityLogService svc) : ControllerBase
{
    [HttpGet("customer/{customerId}")] public async Task<ActionResult<PagedResult<ActivityLogDto>>> ByCustomer(Guid customerId, [FromQuery] PaginationParams p) => Ok(await svc.GetByCustomerAsync(customerId, p));
    [HttpGet("recent")]
    public async Task<ActionResult<List<ActivityLogDto>>> Recent([FromServices] AppDbContext db, [FromQuery] int limit = 10)
    {
        var items = await db.ActivityLogs
            .Include(a => a.Customer)
            .OrderByDescending(a => a.CreatedAt)
            .Take(Math.Min(limit, 50))
            .Select(a => new ActivityLogDto(a.Id, a.EntityType, a.EntityId, a.Action, a.Description, a.UserName, a.CreatedAt))
            .ToListAsync();
        return Ok(items);
    }
}

[ApiController, Route("api/[controller]"), Authorize]
public class LegalTextsController(ILegalTextService svc) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<List<LegalTextBlockDto>>> Get() => Ok(await svc.GetAllAsync());
    [HttpPost] public async Task<ActionResult<LegalTextBlockDto>> Create(CreateLegalTextRequest req) => Ok(await svc.CreateAsync(req));
    [HttpPut("{id}")] public async Task<ActionResult<LegalTextBlockDto>> Update(Guid id, CreateLegalTextRequest req) => Ok(await svc.UpdateAsync(id, req));
    [HttpDelete("{id}")] public async Task<IActionResult> Delete(Guid id) { await svc.DeleteAsync(id); return NoContent(); }
}

[ApiController, Route("api/[controller]"), Authorize]
public class JournalController(IJournalService svc) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<PagedResult<JournalEntryDto>>> List([FromQuery] PaginationParams p) => Ok(await svc.GetEntriesAsync(p));
    [HttpPost] public async Task<ActionResult<JournalEntryDto>> Create(CreateJournalEntryRequest req) => Ok(await svc.CreateAsync(req));
    [HttpPost("{id}/post"), Authorize(Policy = "AccountingOrAdmin")] public async Task<IActionResult> Post(Guid id) { await svc.PostAsync(id); return NoContent(); }
}

[ApiController, Route("api/[controller]"), Authorize]
public class AccountsController(IChartOfAccountService svc) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<List<ChartOfAccountDto>>> Get() => Ok(await svc.GetAllAsync());
}

[ApiController, Route("api/[controller]"), Authorize]
public class BankTransactionsController(IBankTransactionService svc) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<PagedResult<BankTransactionDto>>> List([FromQuery] PaginationParams p) => Ok(await svc.GetTransactionsAsync(p));
    [HttpPost("{id}/match"), Authorize(Policy = "AccountingOrAdmin")] public async Task<IActionResult> Match(Guid id, MatchBankTransactionRequest req) { await svc.MatchAsync(id, req); return NoContent(); }
}

[ApiController, Route("api/email-templates"), Authorize]
public class EmailTemplatesController(IEmailTemplateService svc) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<List<EmailTemplateDto>>> Get() => Ok(await svc.GetAllAsync());
    [HttpPost] public async Task<ActionResult<EmailTemplateDto>> Create(CreateEmailTemplateRequest req) => Ok(await svc.CreateAsync(req));
    [HttpPut("{id}")] public async Task<ActionResult<EmailTemplateDto>> Update(Guid id, CreateEmailTemplateRequest req) => Ok(await svc.UpdateAsync(id, req));
    [HttpDelete("{id}")] public async Task<IActionResult> Delete(Guid id) { await svc.DeleteAsync(id); return NoContent(); }
}

[ApiController, Route("api/[controller]"), Authorize]
public class ContactsController(IContactListService svc) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<PagedResult<ContactListDto>>> List([FromQuery] PaginationParams p) => Ok(await svc.GetAllContactsAsync(p));
}

[ApiController, Route("api/[controller]"), Authorize]
public class VatController(IVatService svc) : ControllerBase
{
    [HttpGet("report")] public async Task<ActionResult<VatReportDto>> Report([FromQuery] int year, [FromQuery] int month) => Ok(await svc.GetVatReportAsync(year, month));
    [HttpPost("submit"), Authorize(Policy = "AccountingOrAdmin")] public async Task<ActionResult<VatPeriodDto>> Submit([FromQuery] int year, [FromQuery] int month) => Ok(await svc.SubmitVatPeriodAsync(year, month));
    [HttpGet("datev"), Authorize(Policy = "AccountingOrAdmin")] public async Task<IActionResult> Datev([FromQuery] int year, [FromQuery] int month, [FromQuery] bool includeInvoices = true, [FromQuery] bool includeExpenses = true, [FromQuery] bool includeJournal = true)
    {
        var data = await svc.ExportDatevAsync(new DatevExportRequest(year, month, includeInvoices, includeExpenses, includeJournal));
        return File(data, "text/csv", $"DATEV_Export_{year}_{month:D2}.csv");
    }
    [HttpGet("elster-xml"), Authorize(Policy = "AccountingOrAdmin")] public async Task<IActionResult> ElsterXml([FromQuery] int year, [FromQuery] int month)
    {
        var xml = await svc.GenerateElsterXmlAsync(year, month);
        return File(xml, "application/xml", $"ELSTER_UStVA_{year}_{month:00}.xml");
    }
}

[ApiController, Route("api/[controller]"), Authorize]
public class ExportController(IExportService svc) : ControllerBase
{
    [HttpGet("year/stats")]
    public async Task<ActionResult<ExportYearStatsDto>> Stats([FromQuery] int year)
        => Ok(await svc.GetYearStatsAsync(year));

    [HttpGet("year")]
    public async Task<IActionResult> YearZip([FromQuery] int year,
        [FromQuery] bool includeInvoices = true, [FromQuery] bool includeExpenses = true)
    {
        var zip = await svc.ExportYearZipAsync(year, includeInvoices, includeExpenses);
        return File(zip, "application/zip", $"Steuerunterlagen_{year}.zip");
    }
}

[ApiController, Route("api/[controller]"), Authorize]
public class EmailsController(IEmailLogService svc) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<PagedResult<EmailLogDto>>> List([FromQuery] PaginationParams p, [FromQuery] Guid? customerId) => Ok(await svc.GetLogsAsync(p, customerId));
}

[ApiController, Route("api/[controller]"), Authorize(Policy = "AdminOnly")]
public class UsersController(IUserService svc) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<List<UserListDto>>> List() => Ok(await svc.GetAllAsync());
    [HttpPost] public async Task<ActionResult<UserListDto>> Create(CreateUserRequest req) => Ok(await svc.CreateAsync(req));
    [HttpPut("{id}")] public async Task<ActionResult<UserListDto>> Update(string id, UpdateUserRequest req) => Ok(await svc.UpdateAsync(id, req));
    [HttpDelete("{id}")] public async Task<IActionResult> Delete(string id) { await svc.DeleteAsync(id); return NoContent(); }
    [HttpPost("{id}/reset-password")] public async Task<IActionResult> ResetPassword(string id, ResetPasswordRequest req) { await svc.ResetPasswordAsync(id, req); return NoContent(); }
}

[ApiController, Route("api/system"), Authorize(Policy = "AdminOnly")]
public class SystemController : ControllerBase
{
    [HttpPost("trigger-subscription-invoices")]
    public IActionResult TriggerSubscriptionInvoices()
    {
        RecurringJob.TriggerJob("generate-subscription-invoices");
        return NoContent();
    }
    [HttpPost("trigger-bank-sync")]
    public IActionResult TriggerBankSync()
    {
        RecurringJob.TriggerJob("sync-bank-transactions");
        return NoContent();
    }
}

[ApiController, Route("api/integrations"), Authorize(Policy = "AdminOnly")]
public class IntegrationsController(IIntegrationService svc) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IntegrationSettingsDto>> Get() => Ok(await svc.GetAsync());

    [HttpPut("paypal")]
    public async Task<IActionResult> UpdatePayPal(UpdatePayPalRequest req)
    { await svc.UpdatePayPalAsync(req); return NoContent(); }

    [HttpDelete("paypal")]
    public async Task<IActionResult> DisconnectPayPal()
    { await svc.DisconnectPayPalAsync(); return NoContent(); }

    [HttpPost("bank/setup")]
    public async Task<ActionResult<object>> SetupBank(SetupBankRequest req)
    {
        var authUrl = await svc.SetupBankAsync(req);
        return Ok(new { authUrl });
    }

    [HttpPost("bank/confirm")]
    public async Task<IActionResult> ConfirmBank(ConfirmBankRequest req)
    { await svc.ConfirmBankAsync(req); return NoContent(); }

    [HttpDelete("bank")]
    public async Task<IActionResult> DisconnectBank()
    { await svc.DisconnectBankAsync(); return NoContent(); }

    [HttpPost("sync")]
    public IActionResult TriggerSync()
    { RecurringJob.TriggerJob("sync-bank-transactions"); return NoContent(); }
}

[ApiController, Route("api/[controller]"), Authorize]
public class TeamMembersController(ITeamMemberService svc) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<List<TeamMemberDto>>> List() => Ok(await svc.GetAllAsync());
    [HttpGet("{id}")] public async Task<ActionResult<TeamMemberDto>> Get(Guid id) => Ok(await svc.GetByIdAsync(id));
    [HttpPost] public async Task<ActionResult<TeamMemberDto>> Create(CreateTeamMemberRequest req) => Ok(await svc.CreateAsync(req));
    [HttpPut("{id}")] public async Task<ActionResult<TeamMemberDto>> Update(Guid id, UpdateTeamMemberRequest req) => Ok(await svc.UpdateAsync(id, req));
    [HttpDelete("{id}")] public async Task<IActionResult> Delete(Guid id) { await svc.DeleteAsync(id); return NoContent(); }
}

[ApiController, Route("api/[controller]"), Authorize]
public class ProductsController(IProductService svc) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<List<ProductDto>>> List() => Ok(await svc.GetAllAsync());
    [HttpGet("{id}")] public async Task<ActionResult<ProductDto>> Get(Guid id) => Ok(await svc.GetByIdAsync(id));
    [HttpPost] public async Task<ActionResult<ProductDto>> Create(CreateProductRequest req) => Ok(await svc.CreateAsync(req));
    [HttpPut("{id}")] public async Task<ActionResult<ProductDto>> Update(Guid id, UpdateProductRequest req) => Ok(await svc.UpdateAsync(id, req));
    [HttpDelete("{id}")] public async Task<IActionResult> Delete(Guid id) { await svc.DeleteAsync(id); return NoContent(); }
    [HttpPost("{id}/members/{teamMemberId}")] public async Task<IActionResult> AddMember(Guid id, Guid teamMemberId) { await svc.AddTeamMemberAsync(id, teamMemberId); return NoContent(); }
    [HttpDelete("{id}/members/{teamMemberId}")] public async Task<IActionResult> RemoveMember(Guid id, Guid teamMemberId) { await svc.RemoveTeamMemberAsync(id, teamMemberId); return NoContent(); }
}

[ApiController, Route("api/[controller]"), Authorize]
public class PriceListsController(IPriceListService svc) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<List<PriceListDto>>> List([FromQuery] Guid customerId) => Ok(await svc.GetByCustomerAsync(customerId));
    [HttpGet("templates")] public async Task<ActionResult<List<PriceListDto>>> Templates() => Ok(await svc.GetTemplatesAsync());
    [HttpGet("{id}")] public async Task<ActionResult<PriceListDto>> Get(Guid id) => Ok(await svc.GetByIdAsync(id));
    [HttpPost] public async Task<ActionResult<PriceListDto>> Create(CreatePriceListRequest req) => Ok(await svc.CreateAsync(req));
    [HttpPost("clone")] public async Task<ActionResult<PriceListDto>> Clone(ClonePriceListRequest req) => Ok(await svc.CloneToCustomerAsync(req));
    [HttpPut("{id}")] public async Task<ActionResult<PriceListDto>> Update(Guid id, UpdatePriceListRequest req) => Ok(await svc.UpdateAsync(id, req));
    [HttpDelete("{id}")] public async Task<IActionResult> Delete(Guid id) { await svc.DeleteAsync(id); return NoContent(); }
    [HttpPost("{id}/items")] public async Task<ActionResult<PriceListItemDto>> AddItem(Guid id, UpsertPriceListItemRequest req) => Ok(await svc.AddItemAsync(id, req));
    [HttpPut("{id}/items/{itemId}")] public async Task<ActionResult<PriceListItemDto>> UpdateItem(Guid id, Guid itemId, UpsertPriceListItemRequest req) => Ok(await svc.UpdateItemAsync(id, itemId, req));
    [HttpDelete("{id}/items/{itemId}")] public async Task<IActionResult> RemoveItem(Guid id, Guid itemId) { await svc.RemoveItemAsync(id, itemId); return NoContent(); }
}

[ApiController, Route("api/[controller]"), Authorize]
public class OpportunitiesController(IOpportunityService svc) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<List<OpportunityListDto>>> List([FromQuery] Guid? customerId, [FromQuery] OpportunityStage? stage) => Ok(await svc.GetAllAsync(customerId, stage));
    [HttpGet("{id}")] public async Task<ActionResult<OpportunityDetailDto>> Get(Guid id) => Ok(await svc.GetByIdAsync(id));
    [HttpPost] public async Task<ActionResult<OpportunityDetailDto>> Create(CreateOpportunityRequest req) => Ok(await svc.CreateAsync(req));
    [HttpPut("{id}")] public async Task<ActionResult<OpportunityDetailDto>> Update(Guid id, UpdateOpportunityRequest req) => Ok(await svc.UpdateAsync(id, req));
    [HttpPut("{id}/stage")] public async Task<ActionResult<OpportunityDetailDto>> UpdateStage(Guid id, UpdateOpportunityStageRequest req) => Ok(await svc.UpdateStageAsync(id, req));
    [HttpDelete("{id}")] public async Task<IActionResult> Delete(Guid id) { await svc.DeleteAsync(id); return NoContent(); }
}

[ApiController, Route("api/[controller]"), Authorize]
public class TicketsController(ISupportTicketService svc) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<List<TicketListDto>>> List([FromQuery] Guid? customerId, [FromQuery] TicketStatus? status, [FromQuery] TicketPriority? priority) => Ok(await svc.GetAllAsync(customerId, status, priority));
    [HttpGet("{id}")] public async Task<ActionResult<TicketDetailDto>> Get(Guid id) => Ok(await svc.GetByIdAsync(id));
    [HttpPost] public async Task<ActionResult<TicketDetailDto>> Create(CreateTicketRequest req) => Ok(await svc.CreateAsync(req));
    [HttpPut("{id}")] public async Task<ActionResult<TicketDetailDto>> Update(Guid id, UpdateTicketRequest req) => Ok(await svc.UpdateAsync(id, req));
    [HttpPut("{id}/status")] public async Task<ActionResult<TicketDetailDto>> UpdateStatus(Guid id, UpdateTicketStatusRequest req) => Ok(await svc.UpdateStatusAsync(id, req));
    [HttpPost("{id}/comments")] public async Task<ActionResult<TicketCommentDto>> AddComment(Guid id, AddTicketCommentRequest req) => Ok(await svc.AddCommentAsync(id, req));
    [HttpDelete("{id}")] public async Task<IActionResult> Delete(Guid id) { await svc.DeleteAsync(id); return NoContent(); }
}

[ApiController, Route("api/[controller]"), Authorize]
public class CrmActivitiesController(ICrmActivityService svc) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<List<CrmActivityListDto>>> List([FromQuery] Guid? customerId, [FromQuery] Guid? opportunityId, [FromQuery] Guid? ticketId) => Ok(await svc.GetAllAsync(customerId, opportunityId, ticketId));
    [HttpGet("{id}")] public async Task<ActionResult<CrmActivityDetailDto>> Get(Guid id) => Ok(await svc.GetByIdAsync(id));
    [HttpPost] public async Task<ActionResult<CrmActivityDetailDto>> Create(CreateCrmActivityRequest req) => Ok(await svc.CreateAsync(req));
    [HttpPut("{id}")] public async Task<ActionResult<CrmActivityDetailDto>> Update(Guid id, UpdateCrmActivityRequest req) => Ok(await svc.UpdateAsync(id, req));
    [HttpPut("{id}/complete")] public async Task<ActionResult<CrmActivityDetailDto>> Complete(Guid id, CompleteCrmActivityRequest req) => Ok(await svc.CompleteAsync(id, req));
    [HttpDelete("{id}")] public async Task<IActionResult> Delete(Guid id) { await svc.DeleteAsync(id); return NoContent(); }
}


// === Berichte ===
[ApiController, Route("api/[controller]"), Authorize]
public class ReportsController(AppDbContext db) : ControllerBase
{
    [HttpGet("revenue-by-customer")]
    public async Task<IActionResult> RevenueByCustomer([FromQuery] int? year)
    {
        var y = year ?? DateTimeOffset.UtcNow.Year;
        var result = await db.Invoices
            .Where(i => i.InvoiceDate.Year == y && i.Status != GentleSuite.Domain.Enums.InvoiceStatus.Cancelled)
            .Include(i => i.Customer)
            .GroupBy(i => new { i.CustomerId, i.Customer.CompanyName })
            .Select(g => new RevenueByCustomerDto(g.Key.CompanyName, g.Key.CustomerId, g.Sum(i => i.GrossTotal), g.Count()))
            .OrderByDescending(x => x.Revenue)
            .Take(15)
            .ToListAsync();
        return Ok(result);
    }

    [HttpGet("expense-by-category")]
    public async Task<IActionResult> ExpenseByCategory([FromQuery] int? year, [FromQuery] int? month)
    {
        var y = year ?? DateTimeOffset.UtcNow.Year;
        var query = db.Expenses.Include(e => e.Category).Where(e => e.ExpenseDate.Year == y);
        if (month.HasValue) query = query.Where(e => e.ExpenseDate.Month == month.Value);
        var result = await query
            .GroupBy(e => e.Category != null ? e.Category.Name : "Unkategorisiert")
            .Select(g => new ExpenseByCategoryDto(g.Key, g.Sum(e => e.GrossAmount), g.Count()))
            .OrderByDescending(x => x.Amount)
            .ToListAsync();
        return Ok(result);
    }

    [HttpGet("monthly-finance")]
    public async Task<IActionResult> MonthlyFinance([FromQuery] int? year)
    {
        var y = year ?? DateTimeOffset.UtcNow.Year;
        var revenues = await db.Invoices
            .Where(i => i.InvoiceDate.Year == y && i.Status != GentleSuite.Domain.Enums.InvoiceStatus.Cancelled)
            .GroupBy(i => i.InvoiceDate.Month)
            .Select(g => new { Month = g.Key, Amount = g.Sum(i => i.GrossTotal) })
            .ToListAsync();
        var expenses = await db.Expenses
            .Where(e => e.ExpenseDate.Year == y)
            .GroupBy(e => e.ExpenseDate.Month)
            .Select(g => new { Month = g.Key, Amount = g.Sum(e => e.GrossAmount) })
            .ToListAsync();
        var result = Enumerable.Range(1, 12).Select(m => new
        {
            month = m,
            revenue = revenues.FirstOrDefault(r => r.Month == m)?.Amount ?? 0m,
            expenses = expenses.FirstOrDefault(e => e.Month == m)?.Amount ?? 0m
        });
        return Ok(result);
    }
}

// === Globale Suche ===
[ApiController, Route("api/[controller]"), Authorize]
public class SearchController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<GlobalSearchResultDto>> Search([FromQuery] string q, [FromQuery] int limit = 5)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2) return Ok(new GlobalSearchResultDto([], [], [], []));
        var search = q.Trim().ToLower();
        var customers = await db.Customers
            .Where(c => c.CompanyName.ToLower().Contains(search) || (c.CustomerNumber != null && c.CustomerNumber.ToLower().Contains(search)))
            .OrderBy(c => c.CompanyName)
            .Take(limit)
            .Select(c => new SearchHitDto(c.Id, c.CompanyName, c.CustomerNumber, $"/customers/{c.Id}"))
            .ToListAsync();
        var invoices = await db.Invoices
            .Where(i => i.InvoiceNumber.ToLower().Contains(search) || i.Customer.CompanyName.ToLower().Contains(search))
            .Include(i => i.Customer)
            .OrderByDescending(i => i.InvoiceDate)
            .Take(limit)
            .Select(i => new SearchHitDto(i.Id, i.InvoiceNumber, i.Customer.CompanyName, $"/invoices/{i.Id}"))
            .ToListAsync();
        var quotes = await db.Quotes
            .Where(q2 => q2.QuoteNumber.ToLower().Contains(search) || q2.Customer.CompanyName.ToLower().Contains(search))
            .Include(q2 => q2.Customer)
            .OrderByDescending(q2 => q2.CreatedAt)
            .Take(limit)
            .Select(q2 => new SearchHitDto(q2.Id, q2.QuoteNumber, q2.Customer.CompanyName, $"/quotes/{q2.Id}"))
            .ToListAsync();
        var projects = await db.Projects
            .Where(p => p.Name.ToLower().Contains(search) || (p.Customer != null && p.Customer.CompanyName.ToLower().Contains(search)))
            .Include(p => p.Customer)
            .OrderByDescending(p => p.CreatedAt)
            .Take(limit)
            .Select(p => new SearchHitDto(p.Id, p.Name, p.Customer != null ? p.Customer.CompanyName : null, $"/projects/{p.Id}"))
            .ToListAsync();
        return Ok(new GlobalSearchResultDto(customers, invoices, quotes, projects));
    }
}

// === Kalender ===
[ApiController, Route("api/[controller]"), Authorize]
public class CalendarController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<CalendarEventDto>>> GetEvents([FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to)
    {
        var start = from ?? new DateTimeOffset(DateTimeOffset.UtcNow.Year, DateTimeOffset.UtcNow.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var end = to ?? start.AddMonths(1);
        var events = new List<CalendarEventDto>();

        var invoices = await db.Invoices
            .Where(i => i.DueDate >= start && i.DueDate < end && i.Status != GentleSuite.Domain.Enums.InvoiceStatus.Paid && i.Status != GentleSuite.Domain.Enums.InvoiceStatus.Cancelled)
            .Include(i => i.Customer)
            .Select(i => new { i.Id, i.InvoiceNumber, i.Customer.CompanyName, i.DueDate })
            .ToListAsync();
        events.AddRange(invoices.Select(i => new CalendarEventDto(i.Id.ToString(), i.DueDate, $"Rechnung fällig: {i.InvoiceNumber}", i.CompanyName, "invoice", $"/invoices/{i.Id}", "#ef4444")));

        var milestones = await db.Milestones
            .Where(m => m.DueDate >= start && m.DueDate < end && !m.IsCompleted)
            .Include(m => m.Project)
            .Select(m => new { m.Id, m.Title, ProjectName = m.Project.Name, m.DueDate })
            .ToListAsync();
        events.AddRange(milestones.Where(m => m.DueDate.HasValue).Select(m => new CalendarEventDto(m.Id.ToString(), m.DueDate!.Value, $"Meilenstein: {m.Title}", m.ProjectName, "milestone", $"/projects/{m.Id}", "#3b82f6")));

        var activities = await db.CrmActivities
            .Where(a => a.DueDate >= start && a.DueDate < end && a.Status == GentleSuite.Domain.Enums.CrmActivityStatus.Open)
            .Include(a => a.Customer)
            .Select(a => new { a.Id, a.Subject, CustomerName = a.Customer != null ? a.Customer.CompanyName : null, a.DueDate })
            .ToListAsync();
        events.AddRange(activities.Where(a => a.DueDate.HasValue).Select(a => new CalendarEventDto(a.Id.ToString(), a.DueDate!.Value, a.Subject, a.CustomerName, "activity", a.CustomerName != null ? $"/customers/{a.Id}" : "/customers", "#22c55e")));

        var subs = await db.CustomerSubscriptions
            .Where(s => s.NextBillingDate >= start && s.NextBillingDate < end && s.Status == GentleSuite.Domain.Enums.SubscriptionStatus.Active)
            .Include(s => s.Customer)
            .Include(s => s.Plan)
            .Select(s => new { s.Id, PlanName = s.Plan.Name, CustomerName = s.Customer.CompanyName, s.NextBillingDate })
            .ToListAsync();
        events.AddRange(subs.Select(s => new CalendarEventDto(s.Id.ToString(), s.NextBillingDate, $"Serienrechnung: {s.PlanName}", s.CustomerName, "subscription", $"/subscriptions", "#a855f7")));

        return Ok(events.OrderBy(e => e.Date).ToList());
    }
}

// === Kundendokumente ===
[ApiController, Route("api/customers/{customerId}/documents"), Authorize]
public class CustomerDocumentsController(ICustomerDocumentService svc) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<List<CustomerDocumentDto>>> List(Guid customerId) => Ok(await svc.GetDocumentsAsync(customerId));
    [HttpPost] public async Task<ActionResult<CustomerDocumentDto>> Upload(Guid customerId, IFormFile file, [FromForm] string? notes)
        => Ok(await svc.UploadAsync(customerId, file.OpenReadStream(), file.FileName, file.ContentType, file.Length, notes));
    [HttpGet("{docId}/download")]
    public async Task<IActionResult> Download(Guid customerId, Guid docId)
    {
        var (stream, fileName, contentType) = await svc.DownloadAsync(customerId, docId);
        return File(stream, contentType, fileName);
    }
    [HttpDelete("{docId}")] public async Task<IActionResult> Delete(Guid customerId, Guid docId) { await svc.DeleteAsync(customerId, docId); return NoContent(); }
}

// === Customer Intake (öffentlich, kein Auth) ===
[ApiController, Route("api/intake")]
public class CustomerIntakeController(ICustomerService svc) : ControllerBase
{
    [HttpGet("{token:guid}")]
    public async Task<ActionResult<CustomerIntakeInfoDto>> GetInfo(Guid token)
    {
        var info = await svc.GetIntakeInfoAsync(token);
        return info == null ? NotFound() : Ok(info);
    }

    [HttpPost("{token:guid}")]
    public async Task<IActionResult> Submit(Guid token, CustomerIntakeSubmitRequest req)
    {
        await svc.CompleteIntakeAsync(token, req);
        return NoContent();
    }
}

[ApiController, Route("api/[controller]"), AllowAnonymous]
public class SetupController(AppDbContext db) : ControllerBase
{
    [HttpGet("init")]
    public async Task<IActionResult> Init()
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
            await using var chk = conn.CreateCommand();
            chk.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='AspNetUsers' AND TABLE_SCHEMA='dbo'";
            var efExists = Convert.ToInt32(await chk.ExecuteScalarAsync()) > 0;
            if (efExists)
                return Ok(new { success = true, message = "Tabellen existieren bereits — kein Handlungsbedarf." });
            var efCreator = db.Database.GetInfrastructure().GetRequiredService<IRelationalDatabaseCreator>();
            await efCreator.CreateTablesAsync();
            return Ok(new { success = true, message = "EF Core Tabellen wurden erstellt." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, error = ex.Message, inner = ex.InnerException?.Message });
        }
    }
}
