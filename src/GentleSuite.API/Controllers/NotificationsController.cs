using GentleSuite.API.Hubs;
using GentleSuite.Application.Interfaces;
using GentleSuite.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GentleSuite.API.Controllers;

public record NotificationListDto(int Unread, List<NotificationDto> Items);

[ApiController, Route("api/notifications"), Authorize]
public class NotificationsController(AppDbContext db, ICurrentUserService cu) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<NotificationListDto>> List(CancellationToken ct)
    {
        var mine = db.AppNotifications.AsNoTracking().Where(n => n.UserId == cu.UserId);
        var unread = await mine.CountAsync(n => n.ReadAt == null, ct);
        var items = await mine.OrderByDescending(n => n.CreatedAt).Take(30)
            .Select(n => new NotificationDto(n.Id, n.Kind, n.Title, n.Body, n.Href, n.CreatedAt, n.ReadAt != null)).ToListAsync(ct);
        return Ok(new NotificationListDto(unread, items));
    }

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> Read(Guid id, CancellationToken ct)
    {
        await db.AppNotifications.Where(n => n.Id == id && n.UserId == cu.UserId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTimeOffset.UtcNow), ct);
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> ReadAll(CancellationToken ct)
    {
        await db.AppNotifications.Where(n => n.UserId == cu.UserId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTimeOffset.UtcNow), ct);
        return NoContent();
    }
}
