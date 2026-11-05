using GentleSuite.Application.Interfaces;
using GentleSuite.Domain.Enums;
using GentleSuite.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GentleSuite.API.Controllers;

public record MyWeekItemDto(string Kind, Guid Id, string Title, string? CustomerName, Guid? CustomerId, DateTimeOffset? DueDate, bool Overdue, string Href, string? Detail);
public record MyWeekDto(bool Linked, string? TeamMemberName, DateTimeOffset Until, List<MyWeekItemDto> Tasks, List<MyWeekItemDto> Tickets, List<MyWeekItemDto> Opportunities, List<MyWeekItemDto> Onboarding, int UnassignedDueTasks);

/// <summary>„Meine Woche“: was die angemeldete Person in den nächsten 7 Tagen (inkl. Überfälligem) erledigen muss.
/// Zuordnung über Mitarbeiter.AppUserId (Mitarbeiter ↔ Login verknüpfen); Onboarding-Aufgaben direkt über die Login-ID.</summary>
[ApiController, Route("api/me"), Authorize]
public class MeController(AppDbContext db, ICurrentUserService cu) : ControllerBase
{
    [HttpGet("week")]
    public async Task<ActionResult<MyWeekDto>> Week(CancellationToken ct)
    {
        var userId = cu.UserId;
        var now = DateTimeOffset.UtcNow;
        var until = now.AddDays(7);
        var member = userId == null ? null : await db.TeamMembers.AsNoTracking().FirstOrDefaultAsync(t => t.AppUserId == userId && t.IsActive, ct);
        var memberId = member?.Id;

        var tasks = memberId == null ? new() : (await db.CrmActivities.AsNoTracking().Include(a => a.Customer)
                .Where(a => a.AssignedToTeamMemberId == memberId && a.Status == CrmActivityStatus.Open && (a.DueDate == null || a.DueDate <= until))
                .OrderBy(a => a.DueDate == null).ThenBy(a => a.DueDate).Take(50).ToListAsync(ct))
            .Select(a => new MyWeekItemDto(a.Type.ToString(), a.Id, a.Subject, a.Customer?.CompanyName, a.CustomerId, a.DueDate, a.DueDate < now,
                a.CustomerId != null ? $"/customers/{a.CustomerId}" : "/dashboard", null)).ToList();

        var tickets = memberId == null ? new() : (await db.SupportTickets.AsNoTracking().Include(t => t.Customer)
                .Where(t => t.AssignedToTeamMemberId == memberId && (t.Status == TicketStatus.Open || t.Status == TicketStatus.InProgress))
                .OrderBy(t => t.SlaDueDate == null).ThenBy(t => t.SlaDueDate).Take(50).ToListAsync(ct))
            .Select(t => new MyWeekItemDto("Ticket", t.Id, t.Title, t.Customer.CompanyName, t.CustomerId, t.SlaDueDate, t.SlaDueDate < now,
                "/tickets", $"{t.Priority} · {(t.Status == TicketStatus.InProgress ? "in Arbeit" : "offen")}")).ToList();

        var opps = memberId == null ? new() : (await db.Opportunities.AsNoTracking().Include(o => o.Customer)
                .Where(o => o.AssignedToTeamMemberId == memberId && o.Stage != OpportunityStage.ClosedWon && o.Stage != OpportunityStage.ClosedLost
                            && o.CloseDate != null && o.CloseDate <= until)
                .OrderBy(o => o.CloseDate).Take(50).ToListAsync(ct))
            .Select(o => new MyWeekItemDto("Opportunity", o.Id, o.Title, o.Customer.CompanyName, o.CustomerId, o.CloseDate, o.CloseDate < now,
                "/opportunities", $"{o.ExpectedRevenue:N0} € · {o.Probability} %")).ToList();

        var onboarding = userId == null ? new() : (await db.TaskItems.AsNoTracking().Include(t => t.Step).ThenInclude(s => s.Workflow).ThenInclude(w => w.Customer)
                .Where(t => t.AssigneeId == userId && t.Status != TaskItemStatus.Done && t.DueDate != null && t.DueDate <= until)
                .OrderBy(t => t.DueDate).Take(50).ToListAsync(ct))
            .Select(t => new MyWeekItemDto("Onboarding", t.Id, t.Title, t.Step.Workflow.Customer.CompanyName, t.Step.Workflow.CustomerId, t.DueDate, t.DueDate < now,
                "/onboarding", t.Step.Title)).ToList();

        var unassigned = await db.CrmActivities.CountAsync(a => a.AssignedToTeamMemberId == null && a.Status == CrmActivityStatus.Open && a.DueDate != null && a.DueDate <= until, ct);

        return Ok(new MyWeekDto(member != null, member?.FullName, until, tasks, tickets, opps, onboarding, unassigned));
    }
}
