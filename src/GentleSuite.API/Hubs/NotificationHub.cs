using GentleSuite.Domain.Entities;
using GentleSuite.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GentleSuite.API.Hubs;

/// <summary>Live-Kanal für In-App-Benachrichtigungen. Clients werden über die User-ID (NameIdentifier-Claim) adressiert.</summary>
[Authorize]
public class NotificationHub : Hub { }

public record NotificationDto(Guid Id, string Kind, string Title, string? Body, string? Href, DateTimeOffset CreatedAt, bool Read);

/// <summary>
/// Legt Benachrichtigungen für alle aktiven Logins an (außer dem Auslöser) und pusht sie live.
/// Läuft in einem eigenen DI-Scope/DbContext und wirft nie – eine fehlgeschlagene Benachrichtigung darf
/// keinen Geschäftsvorgang (Angebotsannahme, Mollie-Webhook, Ticket) abbrechen.
/// </summary>
public class AppNotifier(IServiceScopeFactory scopes, IHubContext<NotificationHub> hub, ILogger<AppNotifier> log)
{
    public async Task NotifyAsync(string kind, string title, string? body, string? href, string? exceptUserId = null)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var userIds = await db.Users.Where(u => u.IsActive && u.Id != exceptUserId).Select(u => u.Id).ToListAsync();
            if (userIds.Count == 0) return;
            var now = DateTimeOffset.UtcNow;
            var rows = userIds.Select(id => new AppNotification
            {
                UserId = id, Kind = kind, Title = Trim(title, 200)!, Body = Trim(body, 1000), Href = Trim(href, 300), CreatedAt = now
            }).ToList();
            db.AppNotifications.AddRange(rows);
            await db.SaveChangesAsync();
            foreach (var n in rows)
                await hub.Clients.User(n.UserId).SendAsync("notification", new NotificationDto(n.Id, n.Kind, n.Title, n.Body, n.Href, n.CreatedAt, false));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Benachrichtigung „{Title}“ fehlgeschlagen", title);
        }
    }

    private static string? Trim(string? s, int max) => s == null ? null : s.Length <= max ? s : s[..(max - 1)] + "…";
}
