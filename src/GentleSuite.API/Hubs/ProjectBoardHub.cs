using GentleSuite.Domain.Enums;
using GentleSuite.Domain.Interfaces;
using GentleSuite.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GentleSuite.API.Hubs;

[Authorize]
public class ProjectBoardHub(AppDbContext db, ICurrentUserService currentUser) : Hub
{
    private static string Group(Guid projectId) => $"project-board-{projectId}";

    public async Task JoinProjectBoard(Guid projectId)
    {
        await EnsureBoardAccessAsync(projectId);
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(projectId));
    }

    public async Task LeaveProjectBoard(Guid projectId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(projectId));
    }

    private async Task EnsureBoardAccessAsync(Guid projectId)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId) ?? throw new HubException("Projekt nicht gefunden.");
        if (currentUser.IsInRole(Roles.Admin) || currentUser.UserId == project.ManagerId) return;
        if (string.IsNullOrEmpty(currentUser.UserId)) throw new HubException("Nicht angemeldet.");

        var hasMembers = await db.ProjectTeamMembers.AnyAsync(x => x.ProjectId == projectId);
        if (!hasMembers) return;

        var allowed = await db.ProjectTeamMembers
            .Include(x => x.TeamMember)
            .AnyAsync(x => x.ProjectId == projectId && x.TeamMember.AppUserId == currentUser.UserId && x.TeamMember.IsActive);
        if (!allowed) throw new HubException("Kein Zugriff auf dieses Projekt-Board.");
    }
}
