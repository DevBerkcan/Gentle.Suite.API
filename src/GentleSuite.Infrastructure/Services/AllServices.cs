using AutoMapper;
using GentleSuite.Application.DTOs;
using GentleSuite.Application.Interfaces;
using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;
using GentleSuite.Domain.Interfaces;
using GentleSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace GentleSuite.Infrastructure.Services;

// === CurrentUser ===
public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _http;
    public CurrentUserService(IHttpContextAccessor http) { _http = http; }
    public string? UserId => _http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
    public string? UserName => _http.HttpContext?.User.FindFirstValue(ClaimTypes.Name);
    public string? Email => _http.HttpContext?.User.FindFirstValue(ClaimTypes.Email);
    public bool IsAuthenticated => _http.HttpContext?.User.Identity?.IsAuthenticated ?? false;
    public bool IsInRole(string role) => _http.HttpContext?.User.IsInRole(role) ?? false;
}

// === Customer ===
public class CustomerServiceImpl : ICustomerService
{
    private readonly AppDbContext _db; private readonly IMapper _m; private readonly IOnboardingService _ob; private readonly IEmailService _em; private readonly IActivityLogService _act; private readonly INumberSequenceService _seq; private readonly string _frontendBaseUrl;
    public CustomerServiceImpl(AppDbContext db, IMapper m, IOnboardingService ob, IEmailService em, IActivityLogService act, INumberSequenceService seq, Microsoft.Extensions.Configuration.IConfiguration config) { _db = db; _m = m; _ob = ob; _em = em; _act = act; _seq = seq; _frontendBaseUrl = config["FrontendBaseUrl"] ?? "http://localhost:3000"; }

    public async Task<PagedResult<CustomerListDto>> GetCustomersAsync(PaginationParams p, CustomerStatus? status, Guid? serviceId, CancellationToken ct)
    {
        var q = _db.Customers.Include(c => c.Contacts).Include(c => c.Locations).Include(c => c.Projects).Include(c => c.DesiredServices).Include(c => c.Invoices).ThenInclude(i => i.Payments).AsQueryable();
        if (status.HasValue) q = q.Where(c => c.Status == status.Value);
        if (serviceId.HasValue) q = q.Where(c => c.DesiredServices.Any(s => s.Id == serviceId.Value));
        if (!string.IsNullOrWhiteSpace(p.Search)) q = q.Where(c => c.CompanyName.ToLower().Contains(p.Search.ToLower()) || (c.CustomerNumber != null && c.CustomerNumber.ToLower().Contains(p.Search.ToLower())));
        var sortBy = p.SortBy?.ToLower();
        q = sortBy switch { "name" => p.SortDesc ? q.OrderByDescending(c => c.CompanyName) : q.OrderBy(c => c.CompanyName), _ => q.OrderByDescending(c => c.CreatedAt) };
        var total = await q.CountAsync(ct);
        var items = await q.Skip((p.Page-1)*p.PageSize).Take(p.PageSize).ToListAsync(ct);
        var dtos = items.Select(c => {
            var primary = c.Contacts.FirstOrDefault(x => x.IsPrimary) ?? c.Contacts.FirstOrDefault();
            var loc = c.Locations.FirstOrDefault(x => x.IsPrimary) ?? c.Locations.FirstOrDefault();
            var revenue = c.Invoices.SelectMany(i => i.Payments).Sum(pay => pay.Amount);
            return new CustomerListDto(c.Id, c.CompanyName, c.CustomerNumber, c.Industry, c.Status, primary != null ? $"{primary.FirstName} {primary.LastName}" : null, primary?.Email, c.Projects.Count, c.CreatedAt, loc?.Street, loc?.City, revenue);
        }).ToList();
        return new PagedResult<CustomerListDto>(dtos, total, p.Page, p.PageSize);
    }

    public async Task<CustomerDetailDto?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var c = await _db.Customers.Include(x => x.Contacts).Include(x => x.Locations).Include(x => x.DesiredServices).FirstOrDefaultAsync(x => x.Id == id, ct);
        return c == null ? null : _m.Map<CustomerDetailDto>(c);
    }

    public async Task<CustomerDetailDto> CreateAsync(CreateCustomerRequest req, CancellationToken ct)
    {
        var duplicate = await CheckDuplicateAsync(new DuplicateCheckRequest(req.CompanyName, req.PrimaryContact.Email, req.PrimaryContact.Phone), ct);
        if (duplicate.HasDuplicate) throw new ArgumentException($"Dubletten erkannt ({string.Join(", ", duplicate.Matches.Select(m => m.MatchType).Distinct())}).");

        var year = DateTime.UtcNow.Year;
        var number = await _seq.NextNumberAsync("Customer", year, "KD", 5, ct);
        var cust = new Customer { CompanyName = req.CompanyName, Industry = req.Industry, Website = req.Website, TaxId = req.TaxId, VatId = req.VatId, Status = CustomerStatus.Lead, CustomerNumber = number };
        cust.Contacts.Add(new Contact { FirstName = req.PrimaryContact.FirstName, LastName = req.PrimaryContact.LastName, Email = req.PrimaryContact.Email, Phone = req.PrimaryContact.Phone, Position = req.PrimaryContact.Position, IsPrimary = true });
        if (req.PrimaryLocation != null) cust.Locations.Add(new Location { Label = req.PrimaryLocation.Label, Street = req.PrimaryLocation.Street, City = req.PrimaryLocation.City, ZipCode = req.PrimaryLocation.ZipCode, Country = req.PrimaryLocation.Country, IsPrimary = true });
        if (req.DesiredServiceIds?.Any() == true) foreach (var sid in req.DesiredServiceIds) cust.DesiredServices.Add(new CustomerService { ServiceCatalogItemId = sid });
        _db.Customers.Add(cust); await _db.SaveChangesAsync(ct);
        await _act.LogAsync(cust.Id, "Customer", cust.Id, "Created", $"Kunde '{cust.CompanyName}' angelegt", ct: ct);
        try {
            var co = await _db.CompanySettings.FirstOrDefaultAsync(ct);
            await _em.SendTemplatedEmailAsync(req.PrimaryContact.Email, "welcome", new() {
                ["CustomerName"] = cust.CompanyName, ["ContactName"] = req.PrimaryContact.FirstName,
                ["CompanyName"] = co?.CompanyName ?? "GentleSuite", ["CompanyEmail"] = co?.Email ?? "", ["CompanyPhone"] = co?.Phone ?? ""
            }, cust.Id, ct: ct);
        } catch { }
        return (await GetByIdAsync(cust.Id, ct))!;
    }

    public async Task<CustomerDetailDto> UpdateAsync(Guid id, UpdateCustomerRequest req, CancellationToken ct)
    {
        var c = await _db.Customers.Include(x => x.Contacts).Include(x => x.Locations).Include(x => x.DesiredServices).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        var primary = c.Contacts.FirstOrDefault(x => x.IsPrimary) ?? c.Contacts.FirstOrDefault();
        var duplicate = await CheckDuplicateAsync(new DuplicateCheckRequest(req.CompanyName, primary?.Email, primary?.Phone, id), ct);
        if (duplicate.HasDuplicate) throw new ArgumentException($"Dubletten erkannt ({string.Join(", ", duplicate.Matches.Select(m => m.MatchType).Distinct())}).");
        c.CompanyName = req.CompanyName; c.Industry = req.Industry; c.Website = req.Website; c.TaxId = req.TaxId; c.VatId = req.VatId; c.Status = req.Status;
        await _db.SaveChangesAsync(ct); return (await GetByIdAsync(id, ct))!;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct) { var c = await _db.Customers.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); c.IsDeleted = true; c.DeletedAt = DateTimeOffset.UtcNow; await _db.SaveChangesAsync(ct); }
    public async Task<ContactDto> AddContactAsync(Guid cid, CreateContactRequest req, CancellationToken ct) { var co = new Contact { CustomerId = cid, FirstName = req.FirstName, LastName = req.LastName, Email = req.Email, Phone = req.Phone, Position = req.Position, IsPrimary = req.IsPrimary }; _db.Contacts.Add(co); await _db.SaveChangesAsync(ct); return _m.Map<ContactDto>(co); }
    public async Task<ContactDto> UpdateContactAsync(Guid cid, Guid contactId, UpdateContactRequest req, CancellationToken ct) { var c = await _db.Contacts.FirstOrDefaultAsync(x => x.Id == contactId && x.CustomerId == cid, ct) ?? throw new KeyNotFoundException(); c.FirstName = req.FirstName; c.LastName = req.LastName; c.Email = req.Email; c.Phone = req.Phone; c.Position = req.Position; c.IsPrimary = req.IsPrimary; await _db.SaveChangesAsync(ct); return _m.Map<ContactDto>(c); }
    public async Task DeleteContactAsync(Guid cid, Guid contactId, CancellationToken ct) { var c = await _db.Contacts.FirstOrDefaultAsync(x => x.Id == contactId && x.CustomerId == cid, ct) ?? throw new KeyNotFoundException(); _db.Contacts.Remove(c); await _db.SaveChangesAsync(ct); }
    public async Task<LocationDto> AddLocationAsync(Guid cid, CreateLocationRequest req, CancellationToken ct) { var lo = new Location { CustomerId = cid, Label = req.Label, Street = req.Street, City = req.City, ZipCode = req.ZipCode, Country = req.Country, IsPrimary = req.IsPrimary }; _db.Locations.Add(lo); await _db.SaveChangesAsync(ct); return _m.Map<LocationDto>(lo); }
    public async Task<LocationDto> UpdateLocationAsync(Guid cid, Guid locId, UpdateLocationRequest req, CancellationToken ct) { var l = await _db.Locations.FirstOrDefaultAsync(x => x.Id == locId && x.CustomerId == cid, ct) ?? throw new KeyNotFoundException(); l.Label = req.Label; l.Street = req.Street; l.City = req.City; l.ZipCode = req.ZipCode; l.Country = req.Country; l.IsPrimary = req.IsPrimary; await _db.SaveChangesAsync(ct); return _m.Map<LocationDto>(l); }
    public async Task DeleteLocationAsync(Guid cid, Guid locId, CancellationToken ct) { var l = await _db.Locations.FirstOrDefaultAsync(x => x.Id == locId && x.CustomerId == cid, ct) ?? throw new KeyNotFoundException(); _db.Locations.Remove(l); await _db.SaveChangesAsync(ct); }

    public async Task<DuplicateCheckResultDto> CheckDuplicateAsync(DuplicateCheckRequest req, CancellationToken ct)
    {
        var company = Normalize(req.CompanyName);
        var email = Normalize(req.Email);
        var phone = NormalizePhone(req.Phone);

        var customers = await _db.Customers
            .Include(c => c.Contacts)
            .Where(c => !req.ExcludeCustomerId.HasValue || c.Id != req.ExcludeCustomerId.Value)
            .ToListAsync(ct);

        var matches = new List<DuplicateHitDto>();
        foreach (var c in customers)
        {
            var primary = c.Contacts.FirstOrDefault(x => x.IsPrimary) ?? c.Contacts.FirstOrDefault();
            var companyMatch = !string.IsNullOrEmpty(company) && Normalize(c.CompanyName) == company;
            var emailMatch = !string.IsNullOrEmpty(email) && c.Contacts.Any(x => Normalize(x.Email) == email);
            var phoneMatch = !string.IsNullOrEmpty(phone) && c.Contacts.Any(x => NormalizePhone(x.Phone) == phone);

            if (companyMatch) matches.Add(new DuplicateHitDto(c.Id, c.CompanyName, primary?.FullName, primary?.Email, primary?.Phone, "CompanyName"));
            if (emailMatch) matches.Add(new DuplicateHitDto(c.Id, c.CompanyName, primary?.FullName, primary?.Email, primary?.Phone, "Email"));
            if (phoneMatch) matches.Add(new DuplicateHitDto(c.Id, c.CompanyName, primary?.FullName, primary?.Email, primary?.Phone, "Phone"));
        }

        return new DuplicateCheckResultDto(matches.Count > 0, matches);
    }

    public async Task<GdprExportDto> ExportGdprAsync(Guid customerId, CancellationToken ct)
    {
        var c = await _db.Customers
            .Include(x => x.Contacts)
            .Include(x => x.Locations)
            .Include(x => x.Notes)
            .FirstOrDefaultAsync(x => x.Id == customerId, ct) ?? throw new KeyNotFoundException();

        return new GdprExportDto(
            c.Id,
            c.CompanyName,
            c.CustomerNumber,
            c.Industry,
            c.Website,
            c.TaxId,
            c.VatId,
            _m.Map<List<ContactDto>>(c.Contacts),
            _m.Map<List<LocationDto>>(c.Locations),
            _m.Map<List<CustomerNoteDto>>(c.Notes.OrderByDescending(n => n.CreatedAt).ToList()),
            DateTimeOffset.UtcNow
        );
    }

    public async Task EraseGdprAsync(Guid customerId, GdprEraseRequest req, CancellationToken ct)
    {
        var c = await _db.Customers
            .Include(x => x.Contacts)
            .Include(x => x.Locations)
            .Include(x => x.Notes)
            .FirstOrDefaultAsync(x => x.Id == customerId, ct) ?? throw new KeyNotFoundException();

        // Keep references for accounting integrity, redact personal/business-identifying fields.
        c.CompanyName = $"Geloeschter Kunde {c.CustomerNumber ?? c.Id.ToString()[..8]}";
        c.Industry = null;
        c.Website = null;
        c.TaxId = null;
        c.VatId = null;
        c.Status = CustomerStatus.Inactive;
        c.ReminderStop = true;

        foreach (var contact in c.Contacts)
        {
            contact.FirstName = "GDPR";
            contact.LastName = "Deleted";
            contact.Email = $"deleted+{contact.Id:N}@example.invalid";
            contact.Phone = null;
            contact.Position = null;
            contact.IsPrimary = false;
        }

        foreach (var loc in c.Locations)
        {
            loc.Label = "Geloescht";
            loc.Street = "Geloescht";
            loc.City = "Geloescht";
            loc.ZipCode = "00000";
            loc.Country = "DE";
            loc.IsPrimary = false;
        }

        foreach (var note in c.Notes)
        {
            note.Title = "[GDPR entfernt]";
            note.Content = "[Inhalt entfernt]";
            note.IsPinned = false;
        }

        await _db.SaveChangesAsync(ct);
        await _act.LogAsync(c.Id, "Customer", c.Id, "GdprErased", $"DSGVO-Loeschung ausgefuehrt. Grund: {req.Reason}", ct: ct);
    }

    public async Task<CustomerDetailDto> CreateQuickAsync(CreateCustomerQuickRequest req, CancellationToken ct)
    {
        var token = Guid.NewGuid();
        var companyName = !string.IsNullOrWhiteSpace(req.CompanyName) ? req.CompanyName : req.Email;
        var year = DateTime.UtcNow.Year;
        var number = await _seq.NextNumberAsync("Customer", year, "KD", 5, ct);
        var cust = new Customer { CompanyName = companyName, Status = CustomerStatus.Lead, CustomerNumber = number, OnboardingToken = token };
        cust.Contacts.Add(new Contact { FirstName = "Unbekannt", LastName = "", Email = req.Email, IsPrimary = true });
        _db.Customers.Add(cust);
        await _db.SaveChangesAsync(ct);
        await _act.LogAsync(cust.Id, "Customer", cust.Id, "Created", $"Kunde per Schnellerfassung angelegt: {req.Email}", ct: ct);
        var intakeUrl = $"{_frontendBaseUrl}/intake/{token}";
        try { await _em.SendTemplatedEmailAsync(req.Email, "customer-intake", new() { ["IntakeUrl"] = intakeUrl, ["CompanyName"] = companyName }, cust.Id, ct: ct); } catch { }
        return (await GetByIdAsync(cust.Id, ct))!;
    }

    public async Task<CustomerIntakeInfoDto?> GetIntakeInfoAsync(Guid token, CancellationToken ct)
    {
        var cust = await _db.Customers.IgnoreQueryFilters().Include(c => c.Contacts).FirstOrDefaultAsync(c => c.OnboardingToken == token, ct);
        if (cust == null) return null;
        var email = cust.Contacts.FirstOrDefault(c => c.IsPrimary)?.Email ?? cust.Contacts.FirstOrDefault()?.Email ?? "";
        return new CustomerIntakeInfoDto(cust.CompanyName, email, cust.OnboardingIntakeDone);
    }

    public async Task CompleteIntakeAsync(Guid token, CustomerIntakeSubmitRequest req, CancellationToken ct)
    {
        var cust = await _db.Customers.IgnoreQueryFilters().Include(c => c.Contacts).Include(c => c.Locations).FirstOrDefaultAsync(c => c.OnboardingToken == token, ct) ?? throw new KeyNotFoundException("Ungültiger Intake-Link.");
        if (cust.OnboardingIntakeDone) throw new InvalidOperationException("Dieses Formular wurde bereits ausgefüllt.");
        cust.CompanyName = req.CompanyName;
        var contact = cust.Contacts.FirstOrDefault(c => c.IsPrimary) ?? cust.Contacts.FirstOrDefault();
        if (contact != null) { contact.FirstName = req.FirstName; contact.LastName = req.LastName; if (!string.IsNullOrWhiteSpace(req.Phone)) contact.Phone = req.Phone; }
        if (!string.IsNullOrWhiteSpace(req.Street) && !string.IsNullOrWhiteSpace(req.City))
            cust.Locations.Add(new Location { Label = "Hauptadresse", Street = req.Street ?? "", City = req.City ?? "", ZipCode = req.ZipCode ?? "", Country = req.Country ?? "Deutschland", IsPrimary = true });
        cust.OnboardingIntakeDone = true;
        cust.OnboardingToken = null;
        await _db.SaveChangesAsync(ct);
        await _act.LogAsync(cust.Id, "Customer", cust.Id, "IntakeCompleted", $"Kunde hat Erstinformations-Formular ausgefüllt", ct: ct);
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
    private static string NormalizePhone(string? value) => new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
}

// === CustomerNote ===
public class CustomerNoteServiceImpl : ICustomerNoteService
{
    private readonly AppDbContext _db; private readonly IMapper _m;
    public CustomerNoteServiceImpl(AppDbContext db, IMapper m) { _db = db; _m = m; }
    public async Task<List<CustomerNoteDto>> GetNotesAsync(Guid cid, CancellationToken ct) => _m.Map<List<CustomerNoteDto>>(await _db.CustomerNotes.Where(n => n.CustomerId == cid).OrderByDescending(n => n.IsPinned).ThenByDescending(n => n.CreatedAt).ToListAsync(ct));
    public async Task<CustomerNoteDto> CreateAsync(Guid cid, CreateNoteRequest req, CancellationToken ct) { var n = new CustomerNote { CustomerId = cid, Title = req.Title, Content = req.Content, Type = req.Type, IsPinned = req.IsPinned }; _db.CustomerNotes.Add(n); await _db.SaveChangesAsync(ct); return _m.Map<CustomerNoteDto>(n); }
    public async Task<CustomerNoteDto> UpdateAsync(Guid cid, Guid nid, UpdateNoteRequest req, CancellationToken ct) { var n = await _db.CustomerNotes.FirstOrDefaultAsync(x => x.Id == nid && x.CustomerId == cid, ct) ?? throw new KeyNotFoundException(); n.Title = req.Title; n.Content = req.Content; n.Type = req.Type; n.IsPinned = req.IsPinned; await _db.SaveChangesAsync(ct); return _m.Map<CustomerNoteDto>(n); }
    public async Task DeleteAsync(Guid cid, Guid nid, CancellationToken ct) { var n = await _db.CustomerNotes.FirstOrDefaultAsync(x => x.Id == nid && x.CustomerId == cid, ct) ?? throw new KeyNotFoundException(); n.IsDeleted = true; await _db.SaveChangesAsync(ct); }
}

// === Onboarding ===
public class OnboardingServiceImpl : IOnboardingService
{
    private readonly AppDbContext _db; private readonly IMapper _m;
    public OnboardingServiceImpl(AppDbContext db, IMapper m) { _db = db; _m = m; }
    public async Task<List<OnboardingWorkflowDto>> GetByCustomerAsync(Guid cid, CancellationToken ct)
    {
        var wfs = await _db.OnboardingWorkflows.Include(w => w.Template).Include(w => w.Steps.OrderBy(s => s.SortOrder)).ThenInclude(s => s.Tasks.OrderBy(t => t.SortOrder)).Where(w => w.CustomerId == cid).ToListAsync(ct);
        return _m.Map<List<OnboardingWorkflowDto>>(wfs);
    }
    public async Task<List<OnboardingWorkflowDto>> GetByProjectAsync(Guid pid, CancellationToken ct)
    {
        var wfs = await _db.OnboardingWorkflows
            .Include(w => w.Template)
            .Include(w => w.Steps.OrderBy(s => s.SortOrder))
            .ThenInclude(s => s.Tasks.OrderBy(t => t.SortOrder))
            .Where(w => w.ProjectId == pid)
            .ToListAsync(ct);
        return _m.Map<List<OnboardingWorkflowDto>>(wfs);
    }
    public async Task<List<OnboardingTemplateListDto>> GetTemplatesAsync(CancellationToken ct)
    {
        var templates = await _db.OnboardingWorkflowTemplates.Include(t => t.Steps).ToListAsync(ct);
        return templates.Select(t => new OnboardingTemplateListDto(t.Id, t.Name, t.IsDefault, t.Steps.Count)).ToList();
    }
    public async Task<OnboardingWorkflowDto> StartWorkflowAsync(Guid cid, Guid? tid = null, Guid? pid = null, CancellationToken ct = default)
    {
        var tmpl = tid.HasValue ? await _db.OnboardingWorkflowTemplates.Include(t => t.Steps.OrderBy(s => s.SortOrder)).ThenInclude(s => s.TaskTemplates.OrderBy(tt => tt.SortOrder)).FirstOrDefaultAsync(t => t.Id == tid, ct)
            : await _db.OnboardingWorkflowTemplates.Include(t => t.Steps.OrderBy(s => s.SortOrder)).ThenInclude(s => s.TaskTemplates.OrderBy(tt => tt.SortOrder)).FirstOrDefaultAsync(t => t.IsDefault, ct);
        if (tmpl == null) throw new InvalidOperationException("No template found");
        var wf = new OnboardingWorkflow { CustomerId = cid, ProjectId = pid, TemplateId = tmpl.Id, Status = OnboardingStatus.InProgress, StartedAt = DateTimeOffset.UtcNow };
        var date = DateTimeOffset.UtcNow;
        foreach (var st in tmpl.Steps.OrderBy(s => s.SortOrder))
        {
            var step = new OnboardingStep { Title = st.Title, Description = st.Description, SortOrder = st.SortOrder, DueDate = date.AddDays(st.DefaultDurationDays) };
            foreach (var tt in st.TaskTemplates.OrderBy(t => t.SortOrder)) step.Tasks.Add(new TaskItem { Title = tt.Title, Description = tt.Description, SortOrder = tt.SortOrder, DueDate = step.DueDate });
            wf.Steps.Add(step); date = date.AddDays(st.DefaultDurationDays);
        }
        _db.OnboardingWorkflows.Add(wf); await _db.SaveChangesAsync(ct);
        return _m.Map<OnboardingWorkflowDto>(wf);
    }
    public async Task UpdateStepStatusAsync(Guid sid, UpdateStepStatusRequest req, CancellationToken ct) { var s = await _db.OnboardingSteps.FindAsync(new object[] { sid }, ct) ?? throw new KeyNotFoundException(); s.Status = req.Status; if (req.Status == OnboardingStepStatus.Completed) s.CompletedAt = DateTimeOffset.UtcNow; await _db.SaveChangesAsync(ct); }
    public async Task UpdateTaskStatusAsync(Guid tid, UpdateTaskStatusRequest req, CancellationToken ct) { var t = await _db.TaskItems.FindAsync(new object[] { tid }, ct) ?? throw new KeyNotFoundException(); t.Status = req.Status; if (req.Status == TaskItemStatus.Done) t.CompletedAt = DateTimeOffset.UtcNow; await _db.SaveChangesAsync(ct); }

    public async Task<OnboardingTemplateDetailDto> GetTemplateByIdAsync(Guid id, CancellationToken ct)
    {
        var t = await _db.OnboardingWorkflowTemplates.Include(x => x.Steps.OrderBy(s => s.SortOrder)).ThenInclude(s => s.TaskTemplates.OrderBy(tt => tt.SortOrder)).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        return new OnboardingTemplateDetailDto(t.Id, t.Name, t.IsDefault, t.Steps.Select(s => new OnboardingStepTemplateDto(s.Id, s.Title, s.Description, s.SortOrder, s.DefaultDurationDays, s.TaskTemplates.Select(tt => new OnboardingTaskTemplateDto(tt.Id, tt.Title, tt.Description, tt.SortOrder)).ToList())).ToList());
    }
    public async Task<OnboardingTemplateDetailDto> CreateTemplateAsync(CreateOnboardingTemplateRequest req, CancellationToken ct)
    {
        if (req.IsDefault) { var existing = await _db.OnboardingWorkflowTemplates.Where(t => t.IsDefault).ToListAsync(ct); foreach (var e in existing) e.IsDefault = false; }
        var tmpl = new OnboardingWorkflowTemplate { Name = req.Name, IsDefault = req.IsDefault };
        foreach (var s in req.Steps) { var step = new OnboardingStepTemplate { Title = s.Title, Description = s.Description, SortOrder = s.SortOrder, DefaultDurationDays = s.DefaultDurationDays }; if (s.Tasks != null) foreach (var t in s.Tasks) step.TaskTemplates.Add(new TaskItemTemplate { Title = t.Title, Description = t.Description, SortOrder = t.SortOrder }); tmpl.Steps.Add(step); }
        _db.OnboardingWorkflowTemplates.Add(tmpl); await _db.SaveChangesAsync(ct); return await GetTemplateByIdAsync(tmpl.Id, ct);
    }
    public async Task<OnboardingTemplateDetailDto> UpdateTemplateAsync(Guid id, CreateOnboardingTemplateRequest req, CancellationToken ct)
    {
        var tmpl = await _db.OnboardingWorkflowTemplates.Include(t => t.Steps).ThenInclude(s => s.TaskTemplates).FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new KeyNotFoundException();
        if (req.IsDefault && !tmpl.IsDefault) { var existing = await _db.OnboardingWorkflowTemplates.Where(t => t.IsDefault && t.Id != id).ToListAsync(ct); foreach (var e in existing) e.IsDefault = false; }
        tmpl.Name = req.Name; tmpl.IsDefault = req.IsDefault;
        foreach (var s in tmpl.Steps) _db.Set<TaskItemTemplate>().RemoveRange(s.TaskTemplates);
        _db.Set<OnboardingStepTemplate>().RemoveRange(tmpl.Steps); tmpl.Steps.Clear();
        foreach (var s in req.Steps) { var step = new OnboardingStepTemplate { Title = s.Title, Description = s.Description, SortOrder = s.SortOrder, DefaultDurationDays = s.DefaultDurationDays }; if (s.Tasks != null) foreach (var t in s.Tasks) step.TaskTemplates.Add(new TaskItemTemplate { Title = t.Title, Description = t.Description, SortOrder = t.SortOrder }); tmpl.Steps.Add(step); }
        await _db.SaveChangesAsync(ct); return await GetTemplateByIdAsync(id, ct);
    }
    public async Task DeleteTemplateAsync(Guid id, CancellationToken ct)
    {
        var tmpl = await _db.OnboardingWorkflowTemplates.Include(t => t.Steps).ThenInclude(s => s.TaskTemplates).FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new KeyNotFoundException();
        foreach (var s in tmpl.Steps) _db.Set<TaskItemTemplate>().RemoveRange(s.TaskTemplates);
        _db.Set<OnboardingStepTemplate>().RemoveRange(tmpl.Steps);
        _db.OnboardingWorkflowTemplates.Remove(tmpl); await _db.SaveChangesAsync(ct);
    }
}

// === Project ===
public class ProjectServiceImpl : IProjectService
{
    private readonly AppDbContext _db; private readonly IMapper _m; private readonly ICurrentUserService _cu; private readonly IOnboardingService _ob;
    public ProjectServiceImpl(AppDbContext db, IMapper m, ICurrentUserService cu, IOnboardingService ob) { _db = db; _m = m; _cu = cu; _ob = ob; }
    public async Task<PagedResult<ProjectListDto>> GetProjectsAsync(PaginationParams p, ProjectStatus? status, Guid? cid, CancellationToken ct)
    {
        var q = _db.Projects.Include(x => x.Customer).AsQueryable();
        if (status.HasValue) q = q.Where(x => x.Status == status.Value);
        if (cid.HasValue) q = q.Where(x => x.CustomerId == cid.Value);
        var total = await q.CountAsync(ct); var items = await q.OrderByDescending(x => x.CreatedAt).Skip((p.Page-1)*p.PageSize).Take(p.PageSize).ToListAsync(ct);
        return new PagedResult<ProjectListDto>(_m.Map<List<ProjectListDto>>(items), total, p.Page, p.PageSize);
    }
    public async Task<ProjectDetailDto?> GetByIdAsync(Guid id, CancellationToken ct) { var p = await _db.Projects.Include(x => x.Milestones).Include(x => x.Comments.OrderByDescending(c => c.CreatedAt)).FirstOrDefaultAsync(x => x.Id == id, ct); return p == null ? null : _m.Map<ProjectDetailDto>(p); }
    public async Task<ProjectDetailDto> CreateAsync(CreateProjectRequest req, CancellationToken ct)
    {
        if (!await _db.Customers.AnyAsync(c => c.Id == req.CustomerId, ct)) throw new ArgumentException("Kunde wurde nicht gefunden.");
        if (req.OnboardingTemplateId == Guid.Empty) throw new ArgumentException("Onboarding-Template ist erforderlich.");
        if (!await _db.OnboardingWorkflowTemplates.AnyAsync(t => t.Id == req.OnboardingTemplateId, ct)) throw new ArgumentException("Onboarding-Template wurde nicht gefunden.");
        ValidateProjectDates(req.StartDate, req.DueDate);
        if (string.IsNullOrWhiteSpace(req.Name)) throw new ArgumentException("Projektname ist erforderlich.");
        var p = new Project { CustomerId = req.CustomerId, Name = req.Name, Description = req.Description, StartDate = req.StartDate, DueDate = req.DueDate, ManagerId = req.ManagerId };
        _db.Projects.Add(p);
        await _db.SaveChangesAsync(ct);
        await _ob.StartWorkflowAsync(req.CustomerId, req.OnboardingTemplateId, p.Id, ct);
        return (await GetByIdAsync(p.Id, ct))!;
    }
    public async Task<ProjectDetailDto> UpdateAsync(Guid id, UpdateProjectRequest req, CancellationToken ct) { var p = await _db.Projects.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); if (string.IsNullOrWhiteSpace(req.Name)) throw new ArgumentException("Projektname ist erforderlich."); ValidateProjectDates(req.StartDate, req.DueDate); p.Name = req.Name; p.Description = req.Description; p.Status = req.Status; p.StartDate = req.StartDate; p.DueDate = req.DueDate; await _db.SaveChangesAsync(ct); return (await GetByIdAsync(id, ct))!; }
    public async Task DeleteAsync(Guid id, CancellationToken ct) { var p = await _db.Projects.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); p.IsDeleted = true; p.DeletedAt = DateTimeOffset.UtcNow; await _db.SaveChangesAsync(ct); }

    public async Task<MilestoneDto> AddMilestoneAsync(Guid projectId, CreateMilestoneRequest req, CancellationToken ct)
    {
        var p = await _db.Projects.FindAsync(new object[] { projectId }, ct) ?? throw new KeyNotFoundException();
        var m = new Milestone { ProjectId = projectId, Title = req.Title, SortOrder = req.SortOrder, DueDate = req.DueDate, IsCompleted = req.IsCompleted };
        _db.Set<Milestone>().Add(m); await _db.SaveChangesAsync(ct);
        return _m.Map<MilestoneDto>(m);
    }
    public async Task<MilestoneDto> UpdateMilestoneAsync(Guid milestoneId, CreateMilestoneRequest req, CancellationToken ct)
    {
        var m = await _db.Set<Milestone>().FindAsync(new object[] { milestoneId }, ct) ?? throw new KeyNotFoundException();
        m.Title = req.Title; m.SortOrder = req.SortOrder; m.DueDate = req.DueDate; m.IsCompleted = req.IsCompleted;
        await _db.SaveChangesAsync(ct); return _m.Map<MilestoneDto>(m);
    }
    public async Task DeleteMilestoneAsync(Guid milestoneId, CancellationToken ct)
    {
        var m = await _db.Set<Milestone>().FindAsync(new object[] { milestoneId }, ct) ?? throw new KeyNotFoundException();
        _db.Set<Milestone>().Remove(m); await _db.SaveChangesAsync(ct);
    }
    public async Task<ProjectCommentDto> AddCommentAsync(Guid projectId, CreateProjectCommentRequest req, CancellationToken ct)
    {
        var p = await _db.Projects.FindAsync(new object[] { projectId }, ct) ?? throw new KeyNotFoundException();
        var c = new ProjectComment { ProjectId = projectId, Content = req.Content, AuthorId = _cu.UserId, AuthorName = _cu.UserName };
        _db.Set<ProjectComment>().Add(c); await _db.SaveChangesAsync(ct);
        return _m.Map<ProjectCommentDto>(c);
    }
    public async Task<List<ProjectBoardTaskDto>> GetBoardTasksAsync(Guid projectId, CancellationToken ct)
    {
        await EnsureBoardAccessAsync(projectId, ct);
        if (!await _db.Projects.AnyAsync(x => x.Id == projectId, ct)) throw new KeyNotFoundException();
        var items = await _db.ProjectBoardTasks
            .Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.Status)
            .ThenBy(x => x.SortOrder)
            .ThenBy(x => x.CreatedAt)
            .ToListAsync(ct);
        return _m.Map<List<ProjectBoardTaskDto>>(items);
    }
    public async Task<ProjectBoardTaskDto> CreateBoardTaskAsync(Guid projectId, CreateProjectBoardTaskRequest req, CancellationToken ct)
    {
        await EnsureBoardAccessAsync(projectId, ct);
        if (!await _db.Projects.AnyAsync(x => x.Id == projectId, ct)) throw new KeyNotFoundException();
        if (string.IsNullOrWhiteSpace(req.Title)) throw new ArgumentException("Task-Titel ist erforderlich.");
        var nextSortOrder = await _db.ProjectBoardTasks
            .Where(x => x.ProjectId == projectId && x.Status == req.Status)
            .Select(x => (int?)x.SortOrder)
            .MaxAsync(ct) ?? -1;
        var entity = new ProjectBoardTask
        {
            ProjectId = projectId,
            Title = req.Title.Trim(),
            Description = req.Description,
            Status = req.Status,
            SortOrder = nextSortOrder + 1,
            AssigneeName = req.AssigneeName,
            DueDate = req.DueDate,
            CreatedBy = _cu.UserName
        };
        _db.ProjectBoardTasks.Add(entity);
        await _db.SaveChangesAsync(ct);
        return _m.Map<ProjectBoardTaskDto>(entity);
    }
    public async Task<ProjectBoardTaskDto> UpdateBoardTaskAsync(Guid projectId, Guid taskId, UpdateProjectBoardTaskRequest req, CancellationToken ct)
    {
        await EnsureBoardAccessAsync(projectId, ct);
        var entity = await _db.ProjectBoardTasks.FirstOrDefaultAsync(x => x.Id == taskId && x.ProjectId == projectId, ct) ?? throw new KeyNotFoundException();
        if (string.IsNullOrWhiteSpace(req.Title)) throw new ArgumentException("Task-Titel ist erforderlich.");
        entity.Title = req.Title.Trim();
        entity.Description = req.Description;
        entity.AssigneeName = req.AssigneeName;
        entity.DueDate = req.DueDate;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedBy = _cu.UserName;
        await _db.SaveChangesAsync(ct);
        return _m.Map<ProjectBoardTaskDto>(entity);
    }
    public async Task<ProjectBoardTaskDto> MoveBoardTaskAsync(Guid projectId, Guid taskId, MoveProjectBoardTaskRequest req, CancellationToken ct)
    {
        await EnsureBoardAccessAsync(projectId, ct);
        var entity = await _db.ProjectBoardTasks.FirstOrDefaultAsync(x => x.Id == taskId && x.ProjectId == projectId, ct) ?? throw new KeyNotFoundException();
        var previousStatus = entity.Status;

        var target = await _db.ProjectBoardTasks
            .Where(x => x.ProjectId == projectId && x.Status == req.Status && x.Id != taskId)
            .OrderBy(x => x.SortOrder)
            .ToListAsync(ct);

        var clamped = Math.Clamp(req.SortOrder, 0, target.Count);
        target.Insert(clamped, entity);
        for (var i = 0; i < target.Count; i++)
        {
            target[i].Status = req.Status;
            target[i].SortOrder = i;
            target[i].UpdatedAt = DateTimeOffset.UtcNow;
            target[i].UpdatedBy = _cu.UserName;
        }

        if (previousStatus != req.Status)
        {
            var old = await _db.ProjectBoardTasks
                .Where(x => x.ProjectId == projectId && x.Status == previousStatus && x.Id != taskId)
                .OrderBy(x => x.SortOrder)
                .ToListAsync(ct);
            for (var i = 0; i < old.Count; i++)
            {
                old[i].SortOrder = i;
                old[i].UpdatedAt = DateTimeOffset.UtcNow;
                old[i].UpdatedBy = _cu.UserName;
            }
        }

        await _db.SaveChangesAsync(ct);
        return _m.Map<ProjectBoardTaskDto>(entity);
    }
    public async Task DeleteBoardTaskAsync(Guid projectId, Guid taskId, CancellationToken ct)
    {
        await EnsureBoardAccessAsync(projectId, ct);
        var entity = await _db.ProjectBoardTasks.FirstOrDefaultAsync(x => x.Id == taskId && x.ProjectId == projectId, ct) ?? throw new KeyNotFoundException();
        var status = entity.Status;
        _db.ProjectBoardTasks.Remove(entity);
        await _db.SaveChangesAsync(ct);

        var items = await _db.ProjectBoardTasks
            .Where(x => x.ProjectId == projectId && x.Status == status)
            .OrderBy(x => x.SortOrder)
            .ToListAsync(ct);
        for (var i = 0; i < items.Count; i++) items[i].SortOrder = i;
        await _db.SaveChangesAsync(ct);
    }
    public async Task<List<TeamMemberDto>> GetMembersAsync(Guid projectId, CancellationToken ct)
    {
        await EnsureBoardAccessAsync(projectId, ct);
        if (!await _db.Projects.AnyAsync(x => x.Id == projectId, ct)) throw new KeyNotFoundException();
        var members = await _db.ProjectTeamMembers
            .Include(x => x.TeamMember)
            .Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.TeamMember.FirstName)
            .ThenBy(x => x.TeamMember.LastName)
            .Select(x => x.TeamMember)
            .ToListAsync(ct);
        return members.Select(ToTeamMemberDto).ToList();
    }
    public async Task AddMemberAsync(Guid projectId, Guid teamMemberId, CancellationToken ct)
    {
        if (!await _db.Projects.AnyAsync(x => x.Id == projectId, ct)) throw new KeyNotFoundException("Projekt nicht gefunden.");
        if (!await _db.TeamMembers.AnyAsync(x => x.Id == teamMemberId && x.IsActive, ct)) throw new KeyNotFoundException("Teammitglied nicht gefunden.");
        if (await _db.ProjectTeamMembers.AnyAsync(x => x.ProjectId == projectId && x.TeamMemberId == teamMemberId, ct)) return;
        _db.ProjectTeamMembers.Add(new ProjectTeamMember { ProjectId = projectId, TeamMemberId = teamMemberId });
        await _db.SaveChangesAsync(ct);
    }
    public async Task RemoveMemberAsync(Guid projectId, Guid teamMemberId, CancellationToken ct)
    {
        var rel = await _db.ProjectTeamMembers.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.TeamMemberId == teamMemberId, ct);
        if (rel == null) return;
        _db.ProjectTeamMembers.Remove(rel);
        await _db.SaveChangesAsync(ct);
    }

    private static void ValidateProjectDates(DateTimeOffset? startDate, DateTimeOffset? dueDate)
    {
        if (startDate.HasValue && dueDate.HasValue && dueDate.Value < startDate.Value) throw new ArgumentException("Projekt-Enddatum darf nicht vor dem Startdatum liegen.");
    }
    private async Task EnsureBoardAccessAsync(Guid projectId, CancellationToken ct)
    {
        var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId, ct) ?? throw new KeyNotFoundException();
        if (_cu.IsInRole(Roles.Admin) || _cu.UserId == project.ManagerId) return;
        if (string.IsNullOrEmpty(_cu.UserId)) throw new UnauthorizedAccessException("Nicht angemeldet.");

        var hasMembers = await _db.ProjectTeamMembers.AnyAsync(x => x.ProjectId == projectId, ct);
        if (!hasMembers) return; // Legacy default: all authenticated users can access when no restriction is set.

        var allowed = await _db.ProjectTeamMembers
            .Include(x => x.TeamMember)
            .AnyAsync(x => x.ProjectId == projectId && x.TeamMember.AppUserId == _cu.UserId && x.TeamMember.IsActive, ct);
        if (!allowed) throw new UnauthorizedAccessException("Kein Zugriff auf dieses Projekt-Board.");
    }
    private static TeamMemberDto ToTeamMemberDto(TeamMember m) => new(m.Id, m.FirstName, m.LastName, m.FullName, m.Email, m.Phone, m.JobTitle, m.IsActive, m.AppUserId);
}

// === Subscription ===
public class SubscriptionServiceImpl : ISubscriptionService
{
    private readonly AppDbContext _db; private readonly IMapper _m;
    public SubscriptionServiceImpl(AppDbContext db, IMapper m) { _db = db; _m = m; }
    public async Task<List<SubscriptionPlanDto>> GetPlansAsync(CancellationToken ct) => _m.Map<List<SubscriptionPlanDto>>(await _db.SubscriptionPlans.Include(p => p.IncludedServices).Include(p => p.WorkScopeRule).Include(p => p.SupportPolicy).ToListAsync(ct));
    public async Task<SubscriptionPlanDto> CreatePlanAsync(CreatePlanRequest req, CancellationToken ct) { var plan = new SubscriptionPlan { Name = req.Name, Description = req.Description, MonthlyPrice = req.MonthlyPrice, BillingCycle = req.BillingCycle, Category = req.Category, IsActive = true }; _db.SubscriptionPlans.Add(plan); await _db.SaveChangesAsync(ct); return _m.Map<SubscriptionPlanDto>(plan); }
    public async Task<SubscriptionPlanDto> UpdatePlanAsync(Guid id, UpdatePlanRequest req, CancellationToken ct) { var plan = await _db.SubscriptionPlans.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); plan.Name = req.Name; plan.Description = req.Description; plan.MonthlyPrice = req.MonthlyPrice; plan.BillingCycle = req.BillingCycle; plan.Category = req.Category; plan.IsActive = req.IsActive; await _db.SaveChangesAsync(ct); return _m.Map<SubscriptionPlanDto>(plan); }
    public async Task DeletePlanAsync(Guid id, CancellationToken ct) { var plan = await _db.SubscriptionPlans.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); plan.IsDeleted = true; plan.DeletedAt = DateTimeOffset.UtcNow; await _db.SaveChangesAsync(ct); }
    public async Task<List<CustomerSubscriptionDto>> GetAllAsync(CancellationToken ct) => _m.Map<List<CustomerSubscriptionDto>>(await _db.CustomerSubscriptions.Include(s => s.Plan).Include(s => s.Customer).OrderByDescending(s => s.CreatedAt).ToListAsync(ct));
    public async Task<List<CustomerSubscriptionDto>> GetCustomerSubscriptionsAsync(Guid cid, CancellationToken ct) => _m.Map<List<CustomerSubscriptionDto>>(await _db.CustomerSubscriptions.Include(s => s.Plan).Include(s => s.Customer).Where(s => s.CustomerId == cid).OrderByDescending(s => s.CreatedAt).ToListAsync(ct));
    public async Task<CustomerSubscriptionDto> CreateAsync(CreateSubscriptionRequest req, CancellationToken ct) { var start = req.StartDate ?? DateTimeOffset.UtcNow; var s = new CustomerSubscription { CustomerId = req.CustomerId, PlanId = req.PlanId, Status = SubscriptionStatus.PendingConfirmation, StartDate = start, NextBillingDate = start.AddMonths(1), ContractDurationMonths = req.ContractDurationMonths }; _db.CustomerSubscriptions.Add(s); await _db.SaveChangesAsync(ct); return _m.Map<CustomerSubscriptionDto>(await _db.CustomerSubscriptions.Include(x => x.Plan).Include(x => x.Customer).FirstAsync(x => x.Id == s.Id, ct)); }
    public async Task UpdateStatusAsync(Guid sid, UpdateSubscriptionStatusRequest req, CancellationToken ct) { var s = await _db.CustomerSubscriptions.FindAsync(new object[] { sid }, ct) ?? throw new KeyNotFoundException(); s.Status = req.Status; if (req.Status == SubscriptionStatus.Paused) s.PausedAt = DateTimeOffset.UtcNow; if (req.Status == SubscriptionStatus.Cancelled) { s.CancelledAt = DateTimeOffset.UtcNow; s.CancellationReason = req.Reason; s.EndDate = DateTimeOffset.UtcNow; } await _db.SaveChangesAsync(ct); }
    public async Task ConfirmAsync(Guid sid, CancellationToken ct) { var s = await _db.CustomerSubscriptions.FindAsync(new object[] { sid }, ct) ?? throw new KeyNotFoundException(); s.Status = SubscriptionStatus.Active; s.ConfirmedAt = DateTimeOffset.UtcNow; await _db.SaveChangesAsync(ct); }
    public async Task<List<SubscriptionInvoiceDto>> GetInvoicesAsync(Guid sid, CancellationToken ct) => await _db.Invoices.Where(i => i.SubscriptionId == sid).OrderByDescending(i => i.InvoiceDate).Select(i => new SubscriptionInvoiceDto(i.Id, i.InvoiceNumber, i.InvoiceDate, i.BillingPeriodStart, i.BillingPeriodEnd, i.GrossTotal, i.Status)).ToListAsync(ct);
}

// === Expense ===
public class ExpenseServiceImpl : IExpenseService
{
    private readonly AppDbContext _db; private readonly IMapper _m; private readonly IFileStorageService _fs;
    public ExpenseServiceImpl(AppDbContext db, IMapper m, IFileStorageService fs) { _db = db; _m = m; _fs = fs; }
    public async Task<PagedResult<ExpenseListDto>> GetExpensesAsync(PaginationParams p, Guid? catId, CancellationToken ct)
    {
        var q = _db.Expenses.Include(e => e.Category).AsQueryable();
        if (catId.HasValue) q = q.Where(e => e.ExpenseCategoryId == catId.Value);
        var total = await q.CountAsync(ct); var items = await q.OrderByDescending(e => e.ExpenseDate).Skip((p.Page-1)*p.PageSize).Take(p.PageSize).ToListAsync(ct);
        return new PagedResult<ExpenseListDto>(_m.Map<List<ExpenseListDto>>(items), total, p.Page, p.PageSize);
    }
    public async Task<ExpenseDetailDto?> GetByIdAsync(Guid id, CancellationToken ct) { var e = await _db.Expenses.Include(x => x.Category).Include(x => x.Account).FirstOrDefaultAsync(x => x.Id == id, ct); return e == null ? null : _m.Map<ExpenseDetailDto>(e); }
    public async Task<ExpenseDetailDto> CreateAsync(CreateExpenseRequest req, CancellationToken ct)
    {
        await ValidateExpenseRequestAsync(req.ExpenseCategoryId, req.NetAmount, req.VatPercent, ct);
        var count = await _db.Expenses.CountAsync(ct);
        var exp = new Expense { ExpenseNumber = $"AU-{DateTime.UtcNow.Year}-{(count+1):D4}", Supplier = req.Supplier, SupplierTaxId = req.SupplierTaxId, ExpenseCategoryId = req.ExpenseCategoryId, Description = req.Description, NetAmount = req.NetAmount, VatPercent = req.VatPercent, ExpenseDate = req.ExpenseDate ?? DateTimeOffset.UtcNow, RetentionUntil = DateTimeOffset.UtcNow.AddYears(10) };
        exp.Recalculate(); _db.Expenses.Add(exp); await _db.SaveChangesAsync(ct); return (await GetByIdAsync(exp.Id, ct))!;
    }
    public async Task<ExpenseDetailDto> UpdateAsync(Guid id, UpdateExpenseRequest req, CancellationToken ct)
    {
        var e = await _db.Expenses.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException();
        if (e.Status != ExpenseStatus.Draft) throw new InvalidOperationException("Nur Entwuerfe koennen bearbeitet werden.");
        await ValidateExpenseRequestAsync(req.ExpenseCategoryId, req.NetAmount, req.VatPercent, ct);
        e.Supplier = req.Supplier; e.SupplierTaxId = req.SupplierTaxId; e.ExpenseCategoryId = req.ExpenseCategoryId; e.Description = req.Description; e.NetAmount = req.NetAmount; e.VatPercent = req.VatPercent; if (req.ExpenseDate.HasValue) e.ExpenseDate = req.ExpenseDate.Value;
        e.Recalculate(); await _db.SaveChangesAsync(ct); return (await GetByIdAsync(id, ct))!;
    }
    public async Task DeleteAsync(Guid id, CancellationToken ct) { var e = await _db.Expenses.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); if (e.IsFinalized) throw new InvalidOperationException("Gebuchte Ausgaben koennen nicht geloescht werden."); e.IsDeleted = true; e.DeletedAt = DateTimeOffset.UtcNow; await _db.SaveChangesAsync(ct); }
    public async Task BookAsync(Guid id, CancellationToken ct) { var e = await _db.Expenses.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); e.Status = ExpenseStatus.Booked; e.IsFinalized = true; e.FinalizedAt = DateTimeOffset.UtcNow; await _db.SaveChangesAsync(ct); }
    public async Task<List<ExpenseCategoryDto>> GetCategoriesAsync(CancellationToken ct) => _m.Map<List<ExpenseCategoryDto>>(await _db.ExpenseCategories.OrderBy(c => c.SortOrder).ToListAsync(ct));
    public async Task<ExpenseCategoryDto> CreateCategoryAsync(CreateExpenseCategoryRequest req, CancellationToken ct) { var c = new ExpenseCategory { Name = req.Name, AccountNumber = req.AccountNumber, SortOrder = req.SortOrder }; _db.ExpenseCategories.Add(c); await _db.SaveChangesAsync(ct); return _m.Map<ExpenseCategoryDto>(c); }
    public async Task<ExpenseCategoryDto> UpdateCategoryAsync(Guid id, UpdateExpenseCategoryRequest req, CancellationToken ct) { var c = await _db.ExpenseCategories.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); c.Name = req.Name; c.AccountNumber = req.AccountNumber; c.SortOrder = req.SortOrder; await _db.SaveChangesAsync(ct); return _m.Map<ExpenseCategoryDto>(c); }
    public async Task DeleteCategoryAsync(Guid id, CancellationToken ct) { var c = await _db.ExpenseCategories.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); _db.ExpenseCategories.Remove(c); await _db.SaveChangesAsync(ct); }
    public async Task UploadReceiptAsync(Guid id, Stream stream, string fileName, string contentType, CancellationToken ct) { var e = await _db.Expenses.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); var path = await _fs.UploadAsync(stream, fileName, contentType, ct); e.ReceiptPath = path; await _db.SaveChangesAsync(ct); }
    public async Task<(Stream Stream, string ContentType, string FileName)?> DownloadReceiptAsync(Guid id, CancellationToken ct) { var e = await _db.Expenses.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); if (string.IsNullOrEmpty(e.ReceiptPath)) return null; var stream = await _fs.DownloadAsync(e.ReceiptPath, ct); return (stream, "application/octet-stream", Path.GetFileName(e.ReceiptPath)); }

    private async Task ValidateExpenseRequestAsync(Guid? categoryId, decimal netAmount, int vatPercent, CancellationToken ct)
    {
        if (netAmount < 0) throw new ArgumentException("Nettobetrag darf nicht negativ sein.");
        if (vatPercent < 0 || vatPercent > 100) throw new ArgumentException("MwSt muss zwischen 0 und 100 liegen.");
        if (categoryId.HasValue && !await _db.ExpenseCategories.AnyAsync(c => c.Id == categoryId.Value, ct))
            throw new ArgumentException("Ausgabenkategorie wurde nicht gefunden.");
    }
}

// === Service Catalog ===
public class ServiceCatalogServiceImpl : IServiceCatalogService
{
    private readonly AppDbContext _db; private readonly IMapper _m;
    public ServiceCatalogServiceImpl(AppDbContext db, IMapper m) { _db = db; _m = m; }
    public async Task<List<ServiceCategoryDto>> GetCategoriesAsync(CancellationToken ct) => _m.Map<List<ServiceCategoryDto>>(await _db.ServiceCategories.Include(c => c.Items.OrderBy(i => i.SortOrder)).OrderBy(c => c.SortOrder).ToListAsync(ct));
    public async Task<ServiceCategoryDto> CreateCategoryAsync(CreateServiceCategoryRequest req, CancellationToken ct) { var c = new ServiceCategory { Name = req.Name, Description = req.Description, SortOrder = req.SortOrder }; _db.ServiceCategories.Add(c); await _db.SaveChangesAsync(ct); return _m.Map<ServiceCategoryDto>(c); }
    public async Task<ServiceCategoryDto> UpdateCategoryAsync(Guid id, UpdateServiceCategoryRequest req, CancellationToken ct) { var c = await _db.ServiceCategories.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException(); c.Name = req.Name; c.Description = req.Description; c.SortOrder = req.SortOrder; await _db.SaveChangesAsync(ct); return _m.Map<ServiceCategoryDto>(c); }
    public async Task DeleteCategoryAsync(Guid id, CancellationToken ct) { var c = await _db.ServiceCategories.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); _db.ServiceCategories.Remove(c); await _db.SaveChangesAsync(ct); }
    public async Task<ServiceCatalogItemDto> CreateItemAsync(CreateServiceItemRequest req, CancellationToken ct) { var i = new ServiceCatalogItem { CategoryId = req.CategoryId, Name = req.Name, Description = req.Description, ShortCode = req.ShortCode, DefaultPrice = req.DefaultPrice, DefaultLineType = req.DefaultLineType, SortOrder = req.SortOrder }; _db.ServiceCatalogItems.Add(i); await _db.SaveChangesAsync(ct); return _m.Map<ServiceCatalogItemDto>(i); }
    public async Task<ServiceCatalogItemDto> UpdateItemAsync(Guid id, UpdateServiceItemRequest req, CancellationToken ct) { var i = await _db.ServiceCatalogItems.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); i.Name = req.Name; i.Description = req.Description; i.ShortCode = req.ShortCode; i.DefaultPrice = req.DefaultPrice; i.DefaultLineType = req.DefaultLineType; i.SortOrder = req.SortOrder; await _db.SaveChangesAsync(ct); return _m.Map<ServiceCatalogItemDto>(i); }
    public async Task DeleteItemAsync(Guid id, CancellationToken ct) { var i = await _db.ServiceCatalogItems.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); _db.ServiceCatalogItems.Remove(i); await _db.SaveChangesAsync(ct); }
}

// === Dashboard ===
public class DashboardServiceImpl : IDashboardService
{
    private readonly AppDbContext _db;
    public DashboardServiceImpl(AppDbContext db) { _db = db; }
    public async Task<DashboardKpis> GetKpisAsync(CancellationToken ct) => new(
        await _db.OnboardingWorkflows.CountAsync(w => w.Status == OnboardingStatus.InProgress, ct),
        await _db.TaskItems.CountAsync(t => t.Status != TaskItemStatus.Done && t.DueDate < DateTimeOffset.UtcNow, ct),
        await _db.Quotes.CountAsync(q => q.Status == QuoteStatus.Sent || q.Status == QuoteStatus.Viewed, ct),
        await _db.Invoices.CountAsync(i => (i.Status == InvoiceStatus.Open || i.Status == InvoiceStatus.Sent) && i.DueDate < DateTimeOffset.UtcNow, ct),
        await _db.Customers.CountAsync(c => c.Status == CustomerStatus.Active, ct),
        await _db.CustomerSubscriptions.CountAsync(s => s.Status == SubscriptionStatus.Active, ct),
        await _db.CustomerSubscriptions.Where(s => s.Status == SubscriptionStatus.Active).Include(s => s.Plan).SumAsync(s => s.Plan.MonthlyPrice, ct));

    public async Task<FinanceDashboardDto> GetFinanceDashboardAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow; var startMonth = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero); var startLastMonth = startMonth.AddMonths(-1);
        var revenueThisMonth = await _db.Invoices.Where(i => i.Status == InvoiceStatus.Paid && i.PaidAt >= startMonth).SumAsync(i => i.GrossTotal, ct);
        var revenueLastMonth = await _db.Invoices.Where(i => i.Status == InvoiceStatus.Paid && i.PaidAt >= startLastMonth && i.PaidAt < startMonth).SumAsync(i => i.GrossTotal, ct);
        var openReceivables = await _db.Invoices.Where(i => i.Status == InvoiceStatus.Sent || i.Status == InvoiceStatus.Final || i.Status == InvoiceStatus.Overdue).SumAsync(i => i.GrossTotal, ct);
        var overdueReceivables = await _db.Invoices.Where(i => i.Status == InvoiceStatus.Overdue).SumAsync(i => i.GrossTotal, ct);
        var overdueCount = await _db.Invoices.CountAsync(i => i.Status == InvoiceStatus.Overdue, ct);
        var expensesThisMonth = await _db.Expenses.Where(e => e.ExpenseDate >= startMonth).SumAsync(e => e.GrossAmount, ct);

        var chart = new List<MonthlyRevenueDto>();
        for (int i = 5; i >= 0; i--)
        {
            var ms = startMonth.AddMonths(-i); var me = ms.AddMonths(1);
            var rev = await _db.Invoices.Where(x => x.Status == InvoiceStatus.Paid && x.PaidAt >= ms && x.PaidAt < me).SumAsync(x => x.GrossTotal, ct);
            var exp = await _db.Expenses.Where(x => x.ExpenseDate >= ms && x.ExpenseDate < me).SumAsync(x => x.GrossAmount, ct);
            chart.Add(new MonthlyRevenueDto(ms.Year, ms.Month, rev, exp));
        }
        return new FinanceDashboardDto(revenueThisMonth, revenueLastMonth, openReceivables, overdueReceivables, overdueCount, expensesThisMonth, 0, revenueThisMonth * 0.3m, chart);
    }
}

// === Activity Log ===
public class ActivityLogServiceImpl : IActivityLogService
{
    private readonly AppDbContext _db; private readonly IMapper _m; private readonly ICurrentUserService _cu;
    public ActivityLogServiceImpl(AppDbContext db, IMapper m, ICurrentUserService cu) { _db = db; _m = m; _cu = cu; }
    public async Task<PagedResult<ActivityLogDto>> GetByCustomerAsync(Guid cid, PaginationParams p, CancellationToken ct)
    {
        var q = _db.ActivityLogs.Where(a => a.CustomerId == cid); var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(a => a.CreatedAt).Skip((p.Page-1)*p.PageSize).Take(p.PageSize).ToListAsync(ct);
        return new PagedResult<ActivityLogDto>(_m.Map<List<ActivityLogDto>>(items), total, p.Page, p.PageSize);
    }
    public async Task LogAsync(Guid? cid, string entityType, Guid eid, string action, string? desc, CancellationToken ct)
    { _db.ActivityLogs.Add(new ActivityLog { CustomerId = cid, EntityType = entityType, EntityId = eid, Action = action, Description = desc, UserId = _cu.UserId, UserName = _cu.UserName }); await _db.SaveChangesAsync(ct); }
}

// === Company Settings ===
public class CompanySettingsServiceImpl : ICompanySettingsService
{
    private readonly AppDbContext _db; private readonly IMapper _m;
    public CompanySettingsServiceImpl(AppDbContext db, IMapper m) { _db = db; _m = m; }
    public async Task<CompanySettingsDto> GetAsync(CancellationToken ct) { var s = await _db.CompanySettings.FirstOrDefaultAsync(ct) ?? new CompanySettings(); return _m.Map<CompanySettingsDto>(s); }
    public async Task<CompanySettingsDto> UpdateAsync(UpdateCompanySettingsRequest req, CancellationToken ct)
    {
        var s = await _db.CompanySettings.FirstOrDefaultAsync(ct);
        if (s == null) { s = new CompanySettings(); _db.CompanySettings.Add(s); }
        s.CompanyName = req.CompanyName; s.LegalName = req.LegalName; s.Street = req.Street; s.ZipCode = req.ZipCode; s.City = req.City;
        s.Phone = req.Phone; s.Email = req.Email; s.Website = req.Website; s.TaxId = req.TaxId; s.VatId = req.VatId;
        s.BankName = req.BankName; s.Iban = req.Iban; s.Bic = req.Bic; s.RegisterCourt = req.RegisterCourt; s.RegisterNumber = req.RegisterNumber;
        s.ManagingDirector = req.ManagingDirector; s.DefaultTaxMode = req.DefaultTaxMode; s.AccountingMode = req.AccountingMode;
        s.InvoiceIntroTemplate = req.InvoiceIntroTemplate; s.InvoiceOutroTemplate = req.InvoiceOutroTemplate;
        s.QuoteIntroTemplate = req.QuoteIntroTemplate; s.QuoteOutroTemplate = req.QuoteOutroTemplate;
        s.InvoicePaymentTermDays = req.InvoicePaymentTermDays; s.QuoteValidityDays = req.QuoteValidityDays;
        await _db.SaveChangesAsync(ct); return _m.Map<CompanySettingsDto>(s);
    }
}

// === Legal Text + Email Log ===
public class LegalTextServiceImpl : ILegalTextService
{
    private readonly AppDbContext _db; private readonly IMapper _m;
    public LegalTextServiceImpl(AppDbContext db, IMapper m) { _db = db; _m = m; }
    public async Task<List<LegalTextBlockDto>> GetAllAsync(CancellationToken ct) => _m.Map<List<LegalTextBlockDto>>(await _db.LegalTextBlocks.Where(l => l.IsActive).OrderBy(l => l.SortOrder).ToListAsync(ct));
    public async Task<LegalTextBlockDto> CreateAsync(CreateLegalTextRequest req, CancellationToken ct) { var l = new LegalTextBlock { Key = req.Key, Title = req.Title, Content = req.Content, SortOrder = req.SortOrder, IsActive = true }; _db.LegalTextBlocks.Add(l); await _db.SaveChangesAsync(ct); return _m.Map<LegalTextBlockDto>(l); }
    public async Task<LegalTextBlockDto> UpdateAsync(Guid id, CreateLegalTextRequest req, CancellationToken ct) { var l = await _db.LegalTextBlocks.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); l.Key = req.Key; l.Title = req.Title; l.Content = req.Content; l.SortOrder = req.SortOrder; await _db.SaveChangesAsync(ct); return _m.Map<LegalTextBlockDto>(l); }
    public async Task DeleteAsync(Guid id, CancellationToken ct) { var l = await _db.LegalTextBlocks.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); _db.LegalTextBlocks.Remove(l); await _db.SaveChangesAsync(ct); }
}
public class EmailLogServiceImpl : IEmailLogService { private readonly AppDbContext _db; private readonly IMapper _m; public EmailLogServiceImpl(AppDbContext db, IMapper m) { _db = db; _m = m; } public async Task<PagedResult<EmailLogDto>> GetLogsAsync(PaginationParams p, Guid? cid, CancellationToken ct) { var q = _db.EmailLogs.AsQueryable(); if (cid.HasValue) q = q.Where(e => e.CustomerId == cid.Value); var total = await q.CountAsync(ct); var items = await q.OrderByDescending(e => e.CreatedAt).Skip((p.Page-1)*p.PageSize).Take(p.PageSize).ToListAsync(ct); return new PagedResult<EmailLogDto>(_m.Map<List<EmailLogDto>>(items), total, p.Page, p.PageSize); } }

// === Journal ===
public class JournalServiceImpl : IJournalService
{
    private readonly AppDbContext _db; private readonly IMapper _m;
    public JournalServiceImpl(AppDbContext db, IMapper m) { _db = db; _m = m; }
    public async Task<PagedResult<JournalEntryDto>> GetEntriesAsync(PaginationParams p, CancellationToken ct)
    {
        var q = _db.JournalEntries.Include(e => e.Lines).AsQueryable();
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(e => e.BookingDate).Skip((p.Page-1)*p.PageSize).Take(p.PageSize).ToListAsync(ct);
        return new PagedResult<JournalEntryDto>(_m.Map<List<JournalEntryDto>>(items), total, p.Page, p.PageSize);
    }
    public async Task<JournalEntryDto> CreateAsync(CreateJournalEntryRequest req, CancellationToken ct)
    {
        var count = await _db.JournalEntries.CountAsync(ct);
        var entry = new JournalEntry { EntryNumber = $"BU-{DateTime.UtcNow.Year}-{(count+1):D4}", BookingDate = req.BookingDate, Description = req.Description, Reference = req.Reference };
        foreach (var l in req.Lines) entry.Lines.Add(new JournalEntryLine { AccountNumber = l.AccountNumber, AccountName = l.AccountName, IsDebit = l.IsDebit, Amount = l.Amount, Note = l.Note });
        _db.JournalEntries.Add(entry); await _db.SaveChangesAsync(ct);
        return _m.Map<JournalEntryDto>(entry);
    }
    public async Task PostAsync(Guid id, CancellationToken ct)
    {
        var e = await _db.JournalEntries.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        if (!e.IsBalanced) throw new InvalidOperationException("Buchung ist nicht ausgeglichen.");
        e.Status = JournalEntryStatus.Posted; e.IsFinalized = true; e.FinalizedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}

// === Bank Transaction ===
public class BankTransactionServiceImpl : IBankTransactionService
{
    private readonly AppDbContext _db; private readonly IMapper _m;
    public BankTransactionServiceImpl(AppDbContext db, IMapper m) { _db = db; _m = m; }
    public async Task<PagedResult<BankTransactionDto>> GetTransactionsAsync(PaginationParams p, CancellationToken ct)
    {
        var q = _db.BankTransactions.AsQueryable();
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(t => t.TransactionDate).Skip((p.Page-1)*p.PageSize).Take(p.PageSize).ToListAsync(ct);
        return new PagedResult<BankTransactionDto>(_m.Map<List<BankTransactionDto>>(items), total, p.Page, p.PageSize);
    }
    public async Task MatchAsync(Guid id, MatchBankTransactionRequest req, CancellationToken ct)
    {
        var t = await _db.BankTransactions.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException();
        t.MatchedInvoiceId = req.InvoiceId; t.MatchedExpenseId = req.ExpenseId; t.Status = BankTransactionStatus.Matched;
        await _db.SaveChangesAsync(ct);
    }
}

// === Chart of Accounts ===
public class ChartOfAccountServiceImpl : IChartOfAccountService
{
    private readonly AppDbContext _db; private readonly IMapper _m;
    public ChartOfAccountServiceImpl(AppDbContext db, IMapper m) { _db = db; _m = m; }
    public async Task<List<ChartOfAccountDto>> GetAllAsync(CancellationToken ct) => _m.Map<List<ChartOfAccountDto>>(await _db.ChartOfAccounts.Where(a => a.IsActive).OrderBy(a => a.AccountNumber).ToListAsync(ct));
}

// === Email Template ===
public class EmailTemplateServiceImpl : IEmailTemplateService
{
    private readonly AppDbContext _db;
    public EmailTemplateServiceImpl(AppDbContext db) { _db = db; }
    public async Task<List<EmailTemplateDto>> GetAllAsync(CancellationToken ct) => await _db.EmailTemplates.OrderBy(t => t.Key).Select(t => new EmailTemplateDto(t.Id, t.Key, t.Subject, t.Body, t.VariablesSchema, t.IsActive)).ToListAsync(ct);
    public async Task<EmailTemplateDto> CreateAsync(CreateEmailTemplateRequest req, CancellationToken ct) { var t = new EmailTemplate { Key = req.Key, Subject = req.Subject, Body = req.Body, VariablesSchema = req.VariablesSchema, IsActive = req.IsActive }; _db.EmailTemplates.Add(t); await _db.SaveChangesAsync(ct); return new EmailTemplateDto(t.Id, t.Key, t.Subject, t.Body, t.VariablesSchema, t.IsActive); }
    public async Task<EmailTemplateDto> UpdateAsync(Guid id, CreateEmailTemplateRequest req, CancellationToken ct) { var t = await _db.EmailTemplates.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); t.Key = req.Key; t.Subject = req.Subject; t.Body = req.Body; t.VariablesSchema = req.VariablesSchema; t.IsActive = req.IsActive; await _db.SaveChangesAsync(ct); return new EmailTemplateDto(t.Id, t.Key, t.Subject, t.Body, t.VariablesSchema, t.IsActive); }
    public async Task DeleteAsync(Guid id, CancellationToken ct) { var t = await _db.EmailTemplates.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); _db.EmailTemplates.Remove(t); await _db.SaveChangesAsync(ct); }
}

// === Contact List ===
public class ContactListServiceImpl : IContactListService
{
    private readonly AppDbContext _db;
    public ContactListServiceImpl(AppDbContext db) { _db = db; }
    public async Task<PagedResult<ContactListDto>> GetAllContactsAsync(PaginationParams p, CancellationToken ct)
    {
        var q = _db.Contacts.Include(c => c.Customer).AsQueryable();
        if (!string.IsNullOrWhiteSpace(p.Search)) q = q.Where(c => (c.FirstName + " " + c.LastName).ToLower().Contains(p.Search.ToLower()) || c.Email.ToLower().Contains(p.Search.ToLower()) || c.Customer.CompanyName.ToLower().Contains(p.Search.ToLower()));
        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(c => c.Customer.CompanyName).ThenBy(c => c.LastName).Skip((p.Page-1)*p.PageSize).Take(p.PageSize)
            .Select(c => new ContactListDto(c.Id, c.FirstName, c.LastName, c.Email, c.Phone, c.Position, c.IsPrimary, c.CustomerId, c.Customer.CompanyName)).ToListAsync(ct);
        return new PagedResult<ContactListDto>(items, total, p.Page, p.PageSize);
    }
}

// === Team Members ===
public class TeamMemberServiceImpl : ITeamMemberService
{
    private readonly AppDbContext _db;
    public TeamMemberServiceImpl(AppDbContext db) { _db = db; }
    private static TeamMemberDto ToDto(TeamMember m) => new(m.Id, m.FirstName, m.LastName, m.FullName, m.Email, m.Phone, m.JobTitle, m.IsActive, m.AppUserId);
    public async Task<List<TeamMemberDto>> GetAllAsync(CancellationToken ct) => (await _db.TeamMembers.OrderBy(m => m.FirstName).ToListAsync(ct)).Select(ToDto).ToList();
    public async Task<TeamMemberDto> GetByIdAsync(Guid id, CancellationToken ct) { var m = await _db.TeamMembers.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); return ToDto(m); }
    public async Task<TeamMemberDto> CreateAsync(CreateTeamMemberRequest req, CancellationToken ct) { var m = new TeamMember { FirstName = req.FirstName, LastName = req.LastName, Email = req.Email, Phone = req.Phone, JobTitle = req.JobTitle, AppUserId = req.AppUserId }; _db.TeamMembers.Add(m); await _db.SaveChangesAsync(ct); return ToDto(m); }
    public async Task<TeamMemberDto> UpdateAsync(Guid id, UpdateTeamMemberRequest req, CancellationToken ct) { var m = await _db.TeamMembers.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); m.FirstName = req.FirstName; m.LastName = req.LastName; m.Email = req.Email; m.Phone = req.Phone; m.JobTitle = req.JobTitle; m.IsActive = req.IsActive; m.AppUserId = req.AppUserId; await _db.SaveChangesAsync(ct); return ToDto(m); }
    public async Task DeleteAsync(Guid id, CancellationToken ct) { var m = await _db.TeamMembers.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); _db.TeamMembers.Remove(m); await _db.SaveChangesAsync(ct); }
}

// === Products ===
public class ProductServiceImpl : IProductService
{
    private readonly AppDbContext _db;
    public ProductServiceImpl(AppDbContext db) { _db = db; }
    private static TeamMemberDto MemberDto(TeamMember m) => new(m.Id, m.FirstName, m.LastName, m.FullName, m.Email, m.Phone, m.JobTitle, m.IsActive, m.AppUserId);
    private static ProductDto ToDto(Product p) => new(p.Id, p.Name, p.Description, p.Unit, p.DefaultPrice, p.IsActive, p.TeamMembers?.Select(ptm => MemberDto(ptm.TeamMember)).ToList() ?? new());
    private async Task<Product> Include(Guid id, CancellationToken ct) => await _db.Products.Include(p => p.TeamMembers).ThenInclude(ptm => ptm.TeamMember).FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new KeyNotFoundException();
    public async Task<List<ProductDto>> GetAllAsync(CancellationToken ct) => (await _db.Products.Include(p => p.TeamMembers).ThenInclude(ptm => ptm.TeamMember).OrderBy(p => p.Name).ToListAsync(ct)).Select(ToDto).ToList();
    public async Task<ProductDto> GetByIdAsync(Guid id, CancellationToken ct) => ToDto(await Include(id, ct));
    public async Task<ProductDto> CreateAsync(CreateProductRequest req, CancellationToken ct) { var p = new Product { Name = req.Name, Description = req.Description, Unit = req.Unit, DefaultPrice = req.DefaultPrice }; _db.Products.Add(p); await _db.SaveChangesAsync(ct); return new ProductDto(p.Id, p.Name, p.Description, p.Unit, p.DefaultPrice, p.IsActive, new()); }
    public async Task<ProductDto> UpdateAsync(Guid id, UpdateProductRequest req, CancellationToken ct) { var p = await Include(id, ct); p.Name = req.Name; p.Description = req.Description; p.Unit = req.Unit; p.DefaultPrice = req.DefaultPrice; p.IsActive = req.IsActive; await _db.SaveChangesAsync(ct); return ToDto(p); }
    public async Task DeleteAsync(Guid id, CancellationToken ct) { var p = await _db.Products.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); _db.Products.Remove(p); await _db.SaveChangesAsync(ct); }
    public async Task AddTeamMemberAsync(Guid productId, Guid teamMemberId, CancellationToken ct) { if (!await _db.Set<ProductTeamMember>().AnyAsync(x => x.ProductId == productId && x.TeamMemberId == teamMemberId, ct)) { _db.Set<ProductTeamMember>().Add(new ProductTeamMember { ProductId = productId, TeamMemberId = teamMemberId }); await _db.SaveChangesAsync(ct); } }
    public async Task RemoveTeamMemberAsync(Guid productId, Guid teamMemberId, CancellationToken ct) { var ptm = await _db.Set<ProductTeamMember>().FindAsync(new object[] { productId, teamMemberId }, ct); if (ptm != null) { _db.Set<ProductTeamMember>().Remove(ptm); await _db.SaveChangesAsync(ct); } }
}

// === Price Lists ===
public class PriceListServiceImpl : IPriceListService
{
    private readonly AppDbContext _db;
    public PriceListServiceImpl(AppDbContext db) { _db = db; }
    private static PriceListItemDto ItemDto(PriceListItem i) => new(i.Id, i.Title, i.Description, i.Unit, i.UnitPrice, i.SortOrder);
    private static PriceListDto ToDto(PriceList pl) => new(pl.Id, pl.CustomerId, pl.Name, pl.Description, pl.IsTemplate, pl.IsActive, pl.Items?.OrderBy(i => i.SortOrder).Select(ItemDto).ToList() ?? new());
    private async Task<PriceList> Include(Guid id, CancellationToken ct) => await _db.PriceLists.Include(pl => pl.Items).FirstOrDefaultAsync(pl => pl.Id == id, ct) ?? throw new KeyNotFoundException();
    public async Task<List<PriceListDto>> GetByCustomerAsync(Guid customerId, CancellationToken ct) => (await _db.PriceLists.Include(pl => pl.Items).Where(pl => pl.CustomerId == customerId && !pl.IsTemplate).OrderBy(pl => pl.Name).ToListAsync(ct)).Select(ToDto).ToList();
    public async Task<List<PriceListDto>> GetTemplatesAsync(CancellationToken ct) => (await _db.PriceLists.Include(pl => pl.Items).Where(pl => pl.IsTemplate).OrderBy(pl => pl.Name).ToListAsync(ct)).Select(ToDto).ToList();
    public async Task<PriceListDto> GetByIdAsync(Guid id, CancellationToken ct) => ToDto(await Include(id, ct));
    public async Task<PriceListDto> CreateAsync(CreatePriceListRequest req, CancellationToken ct) { var pl = new PriceList { CustomerId = req.CustomerId, Name = req.Name, Description = req.Description, IsTemplate = req.IsTemplate }; _db.PriceLists.Add(pl); await _db.SaveChangesAsync(ct); return new PriceListDto(pl.Id, pl.CustomerId, pl.Name, pl.Description, pl.IsTemplate, pl.IsActive, new()); }
    public async Task<PriceListDto> UpdateAsync(Guid id, UpdatePriceListRequest req, CancellationToken ct) { var pl = await Include(id, ct); pl.Name = req.Name; pl.Description = req.Description; pl.IsActive = req.IsActive; await _db.SaveChangesAsync(ct); return ToDto(pl); }
    public async Task DeleteAsync(Guid id, CancellationToken ct) { var pl = await _db.PriceLists.Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new KeyNotFoundException(); _db.PriceListItems.RemoveRange(pl.Items); _db.PriceLists.Remove(pl); await _db.SaveChangesAsync(ct); }
    public async Task<PriceListDto> CloneToCustomerAsync(ClonePriceListRequest req, CancellationToken ct)
    {
        var tpl = await Include(req.TemplateId, ct);
        var copy = new PriceList { CustomerId = req.CustomerId, Name = tpl.Name, Description = tpl.Description, IsTemplate = false, IsActive = true };
        copy.Items = tpl.Items.Select(i => new PriceListItem { Title = i.Title, Description = i.Description, Unit = i.Unit, UnitPrice = i.UnitPrice, SortOrder = i.SortOrder }).ToList();
        _db.PriceLists.Add(copy);
        await _db.SaveChangesAsync(ct);
        return ToDto(copy);
    }
    public async Task<PriceListItemDto> AddItemAsync(Guid priceListId, UpsertPriceListItemRequest req, CancellationToken ct) { var item = new PriceListItem { PriceListId = priceListId, Title = req.Title, Description = req.Description, Unit = req.Unit, UnitPrice = req.UnitPrice, SortOrder = req.SortOrder }; _db.PriceListItems.Add(item); await _db.SaveChangesAsync(ct); return ItemDto(item); }
    public async Task<PriceListItemDto> UpdateItemAsync(Guid priceListId, Guid itemId, UpsertPriceListItemRequest req, CancellationToken ct) { var item = await _db.PriceListItems.FirstOrDefaultAsync(i => i.Id == itemId && i.PriceListId == priceListId, ct) ?? throw new KeyNotFoundException(); item.Title = req.Title; item.Description = req.Description; item.Unit = req.Unit; item.UnitPrice = req.UnitPrice; item.SortOrder = req.SortOrder; await _db.SaveChangesAsync(ct); return ItemDto(item); }
    public async Task RemoveItemAsync(Guid priceListId, Guid itemId, CancellationToken ct) { var item = await _db.PriceListItems.FirstOrDefaultAsync(i => i.Id == itemId && i.PriceListId == priceListId, ct) ?? throw new KeyNotFoundException(); _db.PriceListItems.Remove(item); await _db.SaveChangesAsync(ct); }
}

// === Opportunities ===
public class OpportunityServiceImpl : IOpportunityService
{
    private readonly AppDbContext _db;
    public OpportunityServiceImpl(AppDbContext db) { _db = db; }
    private IQueryable<Opportunity> Query() => _db.Opportunities.Include(o => o.Customer).Include(o => o.Contact).Include(o => o.AssignedTo);
    private static OpportunityListDto ToListDto(Opportunity o) => new(o.Id, o.CustomerId, o.Customer?.CompanyName ?? "", o.Title, o.Stage, o.Probability, o.ExpectedRevenue, o.CloseDate, o.AssignedTo?.FullName, o.CreatedAt);
    private static OpportunityDetailDto ToDetailDto(Opportunity o) => new(o.Id, o.CustomerId, o.Customer?.CompanyName ?? "", o.ContactId, o.Contact != null ? $"{o.Contact.FirstName} {o.Contact.LastName}" : null, o.Title, o.Stage, o.Probability, o.ExpectedRevenue, o.CloseDate, o.AssignedToTeamMemberId, o.AssignedTo?.FullName, o.Source, o.LostReason, o.QuoteId, o.Notes, o.CreatedAt);
    public async Task<List<OpportunityListDto>> GetAllAsync(Guid? customerId, OpportunityStage? stage, CancellationToken ct) { var q = Query().AsQueryable(); if (customerId.HasValue) q = q.Where(o => o.CustomerId == customerId.Value); if (stage.HasValue) q = q.Where(o => o.Stage == stage.Value); return (await q.OrderByDescending(o => o.CreatedAt).ToListAsync(ct)).Select(ToListDto).ToList(); }
    public async Task<OpportunityDetailDto?> GetByIdAsync(Guid id, CancellationToken ct) { var o = await Query().FirstOrDefaultAsync(x => x.Id == id, ct); return o == null ? null : ToDetailDto(o); }
    public async Task<OpportunityDetailDto> CreateAsync(CreateOpportunityRequest req, CancellationToken ct) { var o = new Opportunity { CustomerId = req.CustomerId, ContactId = req.ContactId, Title = req.Title, Stage = req.Stage, Probability = req.Probability, ExpectedRevenue = req.ExpectedRevenue, CloseDate = req.CloseDate, AssignedToTeamMemberId = req.AssignedToTeamMemberId, Source = req.Source, Notes = req.Notes }; _db.Opportunities.Add(o); await _db.SaveChangesAsync(ct); return ToDetailDto(await Query().FirstAsync(x => x.Id == o.Id, ct)); }
    public async Task<OpportunityDetailDto> UpdateAsync(Guid id, UpdateOpportunityRequest req, CancellationToken ct) { var o = await Query().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException(); o.ContactId = req.ContactId; o.Title = req.Title; o.Probability = req.Probability; o.ExpectedRevenue = req.ExpectedRevenue; o.CloseDate = req.CloseDate; o.AssignedToTeamMemberId = req.AssignedToTeamMemberId; o.Source = req.Source; o.LostReason = req.LostReason; o.QuoteId = req.QuoteId; o.Notes = req.Notes; await _db.SaveChangesAsync(ct); return ToDetailDto(await Query().FirstAsync(x => x.Id == id, ct)); }
    public async Task<OpportunityDetailDto> UpdateStageAsync(Guid id, UpdateOpportunityStageRequest req, CancellationToken ct) { var o = await _db.Opportunities.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); o.Stage = req.Stage; if (req.LostReason != null) o.LostReason = req.LostReason; await _db.SaveChangesAsync(ct); return ToDetailDto(await Query().FirstAsync(x => x.Id == id, ct)); }
    public async Task DeleteAsync(Guid id, CancellationToken ct) { var o = await _db.Opportunities.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); o.IsDeleted = true; o.DeletedAt = DateTimeOffset.UtcNow; await _db.SaveChangesAsync(ct); }
}

// === Support Tickets ===
public class SupportTicketServiceImpl : ISupportTicketService
{
    private readonly AppDbContext _db;
    public SupportTicketServiceImpl(AppDbContext db) { _db = db; }
    private IQueryable<SupportTicket> Query() => _db.SupportTickets.Include(t => t.Customer).Include(t => t.AssignedTo).Include(t => t.Comments);
    private static TicketListDto ToListDto(SupportTicket t) => new(t.Id, t.CustomerId, t.Customer?.CompanyName ?? "", t.Title, t.Priority, t.Status, t.Category, t.AssignedTo?.FullName, t.SlaDueDate, t.CreatedAt, t.Comments.Count);
    private static TicketDetailDto ToDetailDto(SupportTicket t) => new(t.Id, t.CustomerId, t.Customer?.CompanyName ?? "", t.SubscriptionId, t.Title, t.Description, t.Priority, t.Status, t.Category, t.AssignedToTeamMemberId, t.AssignedTo?.FullName, t.SlaDueDate, t.ResolvedAt, t.CreatedAt, t.Comments.OrderBy(c => c.CreatedAt).Select(c => new TicketCommentDto(c.Id, c.Content, c.AuthorName, c.IsInternal, c.CreatedAt)).ToList());
    public async Task<List<TicketListDto>> GetAllAsync(Guid? customerId, TicketStatus? status, TicketPriority? priority, CancellationToken ct) { var q = Query().AsQueryable(); if (customerId.HasValue) q = q.Where(t => t.CustomerId == customerId.Value); if (status.HasValue) q = q.Where(t => t.Status == status.Value); if (priority.HasValue) q = q.Where(t => t.Priority == priority.Value); return (await q.OrderByDescending(t => t.CreatedAt).ToListAsync(ct)).Select(ToListDto).ToList(); }
    public async Task<TicketDetailDto?> GetByIdAsync(Guid id, CancellationToken ct) { var t = await Query().FirstOrDefaultAsync(x => x.Id == id, ct); return t == null ? null : ToDetailDto(t); }
    public async Task<TicketDetailDto> CreateAsync(CreateTicketRequest req, CancellationToken ct)
    {
        var t = new SupportTicket { CustomerId = req.CustomerId, SubscriptionId = req.SubscriptionId, Title = req.Title, Description = req.Description, Priority = req.Priority, Category = req.Category, AssignedToTeamMemberId = req.AssignedToTeamMemberId };
        if (req.SubscriptionId.HasValue)
        {
            var sub = await _db.CustomerSubscriptions.Include(s => s.Plan).ThenInclude(p => p.SupportPolicy).FirstOrDefaultAsync(s => s.Id == req.SubscriptionId.Value, ct);
            if (sub?.Plan?.SupportPolicy != null)
            {
                var target = req.Priority switch
                {
                    TicketPriority.Critical => sub.Plan.SupportPolicy.S0ResponseTarget,
                    TicketPriority.High => sub.Plan.SupportPolicy.S1ResponseTarget,
                    TicketPriority.Medium => sub.Plan.SupportPolicy.S2ResponseTarget,
                    _ => sub.Plan.SupportPolicy.S3ResponseTarget
                };
                if (TryParseResponseTarget(target, out var duration))
                    t.SlaDueDate = DateTimeOffset.UtcNow.Add(duration);
            }
        }
        _db.SupportTickets.Add(t); await _db.SaveChangesAsync(ct);
        return ToDetailDto(await Query().FirstAsync(x => x.Id == t.Id, ct));
    }
    public async Task<TicketDetailDto> UpdateAsync(Guid id, UpdateTicketRequest req, CancellationToken ct) { var t = await Query().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException(); t.Title = req.Title; t.Description = req.Description; t.Priority = req.Priority; t.Category = req.Category; t.AssignedToTeamMemberId = req.AssignedToTeamMemberId; t.SubscriptionId = req.SubscriptionId; await _db.SaveChangesAsync(ct); return ToDetailDto(await Query().FirstAsync(x => x.Id == id, ct)); }
    public async Task<TicketDetailDto> UpdateStatusAsync(Guid id, UpdateTicketStatusRequest req, CancellationToken ct) { var t = await _db.SupportTickets.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); t.Status = req.Status; if (req.Status == TicketStatus.Resolved || req.Status == TicketStatus.Closed) t.ResolvedAt ??= DateTimeOffset.UtcNow; await _db.SaveChangesAsync(ct); return ToDetailDto(await Query().FirstAsync(x => x.Id == id, ct)); }
    public async Task<TicketCommentDto> AddCommentAsync(Guid id, AddTicketCommentRequest req, CancellationToken ct) { var c = new TicketComment { TicketId = id, Content = req.Content, IsInternal = req.IsInternal }; _db.TicketComments.Add(c); await _db.SaveChangesAsync(ct); return new TicketCommentDto(c.Id, c.Content, c.AuthorName, c.IsInternal, c.CreatedAt); }
    public async Task DeleteAsync(Guid id, CancellationToken ct) { var t = await _db.SupportTickets.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); t.IsDeleted = true; t.DeletedAt = DateTimeOffset.UtcNow; await _db.SaveChangesAsync(ct); }

    private static bool TryParseResponseTarget(string? raw, out TimeSpan duration)
    {
        duration = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var lower = raw.Trim().ToLowerInvariant();
        var parts = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        if (!double.TryParse(parts[0], out var amount)) return false;
        if (lower.Contains("tag")) { duration = TimeSpan.FromDays(amount); return true; }
        if (lower.Contains("min")) { duration = TimeSpan.FromMinutes(amount); return true; }
        duration = TimeSpan.FromHours(amount);
        return true;
    }
}

// === CRM Activities ===
public class CrmActivityServiceImpl : ICrmActivityService
{
    private readonly AppDbContext _db;
    public CrmActivityServiceImpl(AppDbContext db) { _db = db; }
    private IQueryable<CrmActivity> Query() => _db.CrmActivities.Include(a => a.Customer).Include(a => a.AssignedTo);
    private static CrmActivityListDto ToListDto(CrmActivity a) => new(a.Id, a.CustomerId, a.Customer?.CompanyName, a.Type, a.Subject, a.Status, a.DueDate, a.CompletedAt, a.AssignedTo?.FullName, a.CreatedAt);
    private static CrmActivityDetailDto ToDetailDto(CrmActivity a) => new(a.Id, a.CustomerId, a.Customer?.CompanyName, a.ProjectId, a.OpportunityId, a.TicketId, a.Type, a.Subject, a.Description, a.Status, a.DueDate, a.CompletedAt, a.Duration, a.Direction, a.AssignedToTeamMemberId, a.AssignedTo?.FullName, a.CreatedAt);
    public async Task<List<CrmActivityListDto>> GetAllAsync(Guid? customerId, Guid? opportunityId, Guid? ticketId, CancellationToken ct) { var q = Query().AsQueryable(); if (customerId.HasValue) q = q.Where(a => a.CustomerId == customerId.Value); if (opportunityId.HasValue) q = q.Where(a => a.OpportunityId == opportunityId.Value); if (ticketId.HasValue) q = q.Where(a => a.TicketId == ticketId.Value); return (await q.OrderByDescending(a => a.CreatedAt).ToListAsync(ct)).Select(ToListDto).ToList(); }
    public async Task<CrmActivityDetailDto?> GetByIdAsync(Guid id, CancellationToken ct) { var a = await Query().FirstOrDefaultAsync(x => x.Id == id, ct); return a == null ? null : ToDetailDto(a); }
    public async Task<CrmActivityDetailDto> CreateAsync(CreateCrmActivityRequest req, CancellationToken ct) { var a = new CrmActivity { CustomerId = req.CustomerId, ProjectId = req.ProjectId, OpportunityId = req.OpportunityId, TicketId = req.TicketId, Type = req.Type, Subject = req.Subject, Description = req.Description, DueDate = req.DueDate, Duration = req.Duration, Direction = req.Direction, AssignedToTeamMemberId = req.AssignedToTeamMemberId }; _db.CrmActivities.Add(a); await _db.SaveChangesAsync(ct); return ToDetailDto(await Query().FirstAsync(x => x.Id == a.Id, ct)); }
    public async Task<CrmActivityDetailDto> UpdateAsync(Guid id, UpdateCrmActivityRequest req, CancellationToken ct) { var a = await _db.CrmActivities.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); a.Subject = req.Subject; a.Description = req.Description; a.DueDate = req.DueDate; a.Duration = req.Duration; a.Direction = req.Direction; a.AssignedToTeamMemberId = req.AssignedToTeamMemberId; await _db.SaveChangesAsync(ct); return ToDetailDto(await Query().FirstAsync(x => x.Id == id, ct)); }
    public async Task<CrmActivityDetailDto> CompleteAsync(Guid id, CompleteCrmActivityRequest req, CancellationToken ct) { var a = await _db.CrmActivities.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); a.Status = CrmActivityStatus.Completed; a.CompletedAt = req.CompletedAt ?? DateTimeOffset.UtcNow; await _db.SaveChangesAsync(ct); return ToDetailDto(await Query().FirstAsync(x => x.Id == id, ct)); }
    public async Task DeleteAsync(Guid id, CancellationToken ct) { var a = await _db.CrmActivities.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException(); a.IsDeleted = true; a.DeletedAt = DateTimeOffset.UtcNow; await _db.SaveChangesAsync(ct); }
}

// === File Storage ===
public class LocalFileStorageService : IFileStorageService
{
    private readonly string _base;
    public LocalFileStorageService(string basePath = "uploads") { _base = basePath; Directory.CreateDirectory(_base); }
    public async Task<string> UploadAsync(Stream stream, string fileName, string contentType, CancellationToken ct) { var dir = Path.Combine(_base, DateTime.UtcNow.ToString("yyyy/MM")); Directory.CreateDirectory(dir); var path = Path.Combine(dir, $"{Guid.NewGuid()}{Path.GetExtension(fileName)}"); using var fs = File.Create(path); await stream.CopyToAsync(fs, ct); return path; }
    public Task<Stream> DownloadAsync(string path, CancellationToken ct) => Task.FromResult<Stream>(File.OpenRead(path));
    public Task DeleteAsync(string path, CancellationToken ct) { if (File.Exists(path)) File.Delete(path); return Task.CompletedTask; }
}

// === Export (Steuerbereich) ===
public class ExportServiceImpl(AppDbContext db, IPdfService pdf, IFileStorageService storage) : IExportService
{
    public async Task<ExportYearStatsDto> GetYearStatsAsync(int year, CancellationToken ct)
    {
        var from = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var invCount = await db.Invoices.CountAsync(i => i.IsFinalized && i.InvoiceDate >= from && i.InvoiceDate < to, ct);
        var invTotal = await db.Invoices.Where(i => i.IsFinalized && i.InvoiceDate >= from && i.InvoiceDate < to).SumAsync(i => (decimal?)i.GrossTotal, ct) ?? 0;
        var expCount = await db.Expenses.CountAsync(e => e.ExpenseDate >= from && e.ExpenseDate < to, ct);
        var expTotal = await db.Expenses.Where(e => e.ExpenseDate >= from && e.ExpenseDate < to).SumAsync(e => (decimal?)e.GrossAmount, ct) ?? 0;
        var expWithReceipt = await db.Expenses.CountAsync(e => e.ExpenseDate >= from && e.ExpenseDate < to && e.ReceiptPath != null, ct);
        return new ExportYearStatsDto(year, invCount, invTotal, expCount, expTotal, expWithReceipt);
    }

    public async Task<byte[]> ExportYearZipAsync(int year, bool includeInvoices, bool includeExpenses, CancellationToken ct)
    {
        var from = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var co = await db.CompanySettings.FirstOrDefaultAsync(ct) ?? new CompanySettings();
        var summary = new StringBuilder("Typ;Nummer;Datum;Partner;Netto (EUR);MwSt (EUR);Brutto (EUR);Status\n");

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (includeInvoices)
            {
                var invoices = await db.Invoices
                    .Include(i => i.Customer).ThenInclude(c => c.Contacts)
                    .Include(i => i.Customer).ThenInclude(c => c.Locations)
                    .Include(i => i.Lines)
                    .Where(i => i.IsFinalized && i.InvoiceDate >= from && i.InvoiceDate < to)
                    .OrderBy(i => i.InvoiceDate)
                    .ToListAsync(ct);

                foreach (var inv in invoices)
                {
                    var pdfBytes = await pdf.GenerateInvoicePdfAsync(inv, co, ct);
                    var safe = SanitizeFileName(inv.Customer.CompanyName);
                    var entry = zip.CreateEntry($"Rechnungen/{inv.InvoiceNumber}_{safe}.pdf", CompressionLevel.Fastest);
                    using var es = entry.Open();
                    await es.WriteAsync(pdfBytes, ct);
                    summary.AppendLine($"Rechnung;{inv.InvoiceNumber};{inv.InvoiceDate:dd.MM.yyyy};{inv.Customer.CompanyName};{inv.NetTotal:N2};{inv.VatAmount:N2};{inv.GrossTotal:N2};{inv.Status}");
                }
            }

            if (includeExpenses)
            {
                var expenses = await db.Expenses
                    .Where(e => e.ExpenseDate >= from && e.ExpenseDate < to)
                    .OrderBy(e => e.ExpenseDate)
                    .ToListAsync(ct);

                foreach (var exp in expenses)
                {
                    if (!string.IsNullOrEmpty(exp.ReceiptPath))
                    {
                        try
                        {
                            using var receiptStream = await storage.DownloadAsync(exp.ReceiptPath, ct);
                            var ext = Path.GetExtension(exp.ReceiptPath);
                            var name = $"{exp.ExpenseDate:yyyy-MM-dd}_{SanitizeFileName(exp.Supplier ?? exp.Description ?? "Ausgabe")}{ext}";
                            var entry = zip.CreateEntry($"Ausgaben/{name}", CompressionLevel.Fastest);
                            using var es = entry.Open();
                            await receiptStream.CopyToAsync(es, ct);
                        }
                        catch { /* skip unreadable receipts */ }
                    }
                    summary.AppendLine($"Ausgabe;{exp.ExpenseNumber ?? ""};{exp.ExpenseDate:dd.MM.yyyy};{exp.Supplier ?? ""};{exp.NetAmount:N2};{exp.VatAmount:N2};{exp.GrossAmount:N2};{exp.Status}");
                }
            }

            // DATEV Jahres-CSV
            var datevBytes = BuildDatevYearlyCsv(year, from, to, co, includeInvoices, includeExpenses);
            var datevEntry = zip.CreateEntry($"DATEV_{year}.csv", CompressionLevel.Fastest);
            using var datevEs = datevEntry.Open();
            await datevEs.WriteAsync(datevBytes, ct);

            // Zusammenfassung CSV
            var sumEntry = zip.CreateEntry($"Zusammenfassung_{year}.csv", CompressionLevel.Fastest);
            using var sumEs = sumEntry.Open();
            await sumEs.WriteAsync(Encoding.UTF8.GetBytes(summary.ToString()), ct);
        }

        ms.Position = 0;
        return ms.ToArray();
    }

    private byte[] BuildDatevYearlyCsv(int year, DateTimeOffset from, DateTimeOffset to, CompanySettings co, bool inclInv, bool inclExp)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"\"EXTF\";700;21;\"Buchungsstapel\";7;;;;;\"\";\"\";\"\";\"{co.TaxId ?? ""}\";;;;;{year}0101;4;;;;;;\"\";\"\"]");
        sb.AppendLine("Umsatz (ohne Soll/Haben-Kz);Soll/Haben-Kz;WKZ Umsatz;Kurs;Basisumsatz;WKZ Basisumsatz;Konto;Gegenkonto (ohne BU-Schluessel);BU-Schluessel;Belegdatum;Belegfeld 1;Belegfeld 2;Skonto;Buchungstext");
        if (inclInv)
        {
            var invs = db.Invoices.Include(i => i.Lines).Where(i => i.IsFinalized && i.InvoiceDate >= from && i.InvoiceDate < to).ToList();
            foreach (var inv in invs)
            {
                var sign = inv.Type == InvoiceType.Cancellation ? -1 : 1;
                foreach (var line in inv.Lines)
                {
                    var account = line.VatPercent == 19 ? "8400" : line.VatPercent == 7 ? "8300" : "8100";
                    var amount = (sign * line.GrossTotal).ToString("F2").Replace(".", ",");
                    sb.AppendLine($"{amount};S;EUR;;;;\"{account}\";\"1400\";;{inv.InvoiceDate:ddMM};\"{inv.InvoiceNumber}\";;;\"{line.Title}\"");
                }
            }
        }
        if (inclExp)
        {
            var exps = db.Expenses.Include(e => e.Category).Where(e => e.Status == ExpenseStatus.Booked && e.ExpenseDate >= from && e.ExpenseDate < to).ToList();
            foreach (var exp in exps)
            {
                var account = exp.Category?.AccountNumber ?? "4590";
                var amount = exp.GrossAmount.ToString("F2").Replace(".", ",");
                sb.AppendLine($"{amount};S;EUR;;;;\"{account}\";\"1600\";;{exp.ExpenseDate:ddMM};\"{exp.ExpenseNumber ?? ""}\";;;\"{exp.Supplier ?? exp.Description ?? ""}\"");
            }
        }
        return Encoding.GetEncoding("iso-8859-1").GetBytes(sb.ToString());
    }

    private static string SanitizeFileName(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
}
