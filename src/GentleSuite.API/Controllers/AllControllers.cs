using GentleSuite.Application.DTOs;
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
}

[ApiController, Route("api/[controller]"), Authorize]
public class CustomersController(ICustomerService svc) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<PagedResult<CustomerListDto>>> List([FromQuery] PaginationParams p, [FromQuery] CustomerStatus? status, [FromQuery] Guid? serviceId) => Ok(await svc.GetCustomersAsync(p, status, serviceId));
    [HttpPost("check-duplicate")] public async Task<ActionResult<DuplicateCheckResultDto>> CheckDuplicate(DuplicateCheckRequest req) => Ok(await svc.CheckDuplicateAsync(req));
    [HttpGet("{id}")] public async Task<ActionResult<CustomerDetailDto>> Get(Guid id) { var r = await svc.GetByIdAsync(id); return r == null ? NotFound() : Ok(r); }
    [HttpPost] public async Task<ActionResult<CustomerDetailDto>> Create(CreateCustomerRequest req) => Ok(await svc.CreateAsync(req));
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
    [HttpPost("{id}/gdpr-export")] public async Task<ActionResult<GdprExportDto>> GdprExport(Guid id) => Ok(await svc.ExportGdprAsync(id));
    [HttpPost("{id}/gdpr-erase"), Authorize(Policy = "AdminOnly")] public async Task<IActionResult> GdprErase(Guid id, GdprEraseRequest req) { await svc.EraseGdprAsync(id, req); return NoContent(); }
    [HttpDelete("{id}")] public async Task<IActionResult> Delete(Guid id) { await svc.DeleteAsync(id); return NoContent(); }
    [HttpPost("{id}/contacts")] public async Task<ActionResult<ContactDto>> AddContact(Guid id, CreateContactRequest req) => Ok(await svc.AddContactAsync(id, req));
    [HttpPut("{id}/contacts/{contactId}")] public async Task<ActionResult<ContactDto>> UpdateContact(Guid id, Guid contactId, UpdateContactRequest req) => Ok(await svc.UpdateContactAsync(id, contactId, req));
    [HttpDelete("{id}/contacts/{contactId}")] public async Task<IActionResult> DeleteContact(Guid id, Guid contactId) { await svc.DeleteContactAsync(id, contactId); return NoContent(); }
    [HttpPost("{id}/locations")] public async Task<ActionResult<LocationDto>> AddLocation(Guid id, CreateLocationRequest req) => Ok(await svc.AddLocationAsync(id, req));
    [HttpPut("{id}/locations/{locationId}")] public async Task<ActionResult<LocationDto>> UpdateLocation(Guid id, Guid locationId, UpdateLocationRequest req) => Ok(await svc.UpdateLocationAsync(id, locationId, req));
    [HttpDelete("{id}/locations/{locationId}")] public async Task<IActionResult> DeleteLocation(Guid id, Guid locationId) { await svc.DeleteLocationAsync(id, locationId); return NoContent(); }
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
    [HttpGet("{id}/pdf")] public async Task<IActionResult> Pdf(Guid id) => File(await svc.GeneratePdfAsync(id), "application/pdf", $"Rechnung.pdf");
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
    [HttpGet("customer/{customerId}")] public async Task<ActionResult<List<CustomerSubscriptionDto>>> CustomerSubs(Guid customerId) => Ok(await svc.GetCustomerSubscriptionsAsync(customerId));
    [HttpPost] public async Task<ActionResult<CustomerSubscriptionDto>> Create(CreateSubscriptionRequest req) => Ok(await svc.CreateAsync(req));
    [HttpPut("{id}/status")] public async Task<IActionResult> UpdateStatus(Guid id, UpdateSubscriptionStatusRequest req) { await svc.UpdateStatusAsync(id, req); return NoContent(); }
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
public class SettingsController(ICompanySettingsService svc, GentleSuite.Domain.Interfaces.IFileStorageService fs, GentleSuite.Infrastructure.Data.AppDbContext db, GentleSuite.Domain.Interfaces.INumberSequenceService seq) : ControllerBase
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
    [HttpGet("{id}")] public async Task<ActionResult<PriceListDto>> Get(Guid id) => Ok(await svc.GetByIdAsync(id));
    [HttpPost] public async Task<ActionResult<PriceListDto>> Create(CreatePriceListRequest req) => Ok(await svc.CreateAsync(req));
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
