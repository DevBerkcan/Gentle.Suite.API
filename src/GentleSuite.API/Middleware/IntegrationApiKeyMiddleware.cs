using GentleSuite.Domain.Entities;
using GentleSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace GentleSuite.API.Middleware;

/// <summary>
/// Machine-to-machine auth for /api/integrations/hub/* (GentleHub). Unlike the GentleBook integration the keys
/// live in the database (hashed), so they can be created and revoked in the UI without touching server config.
/// Header: <c>X-Api-Key: gsk_…</c>. The matched key's name is stored in HttpContext.Items for activity logs.
/// </summary>
public class IntegrationApiKeyMiddleware(RequestDelegate next)
{
    public const string RoutePrefix = "/api/integrations/hub";
    public const string HeaderName = "X-Api-Key";
    public const string KeyNameItem = "IntegrationKeyName";

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    public async Task InvokeAsync(HttpContext ctx, AppDbContext db)
    {
        if (ctx.Request.Path.StartsWithSegments(RoutePrefix))
        {
            var provided = ctx.Request.Headers[HeaderName].FirstOrDefault()?.Trim();
            IntegrationApiKey? key = null;
            if (!string.IsNullOrEmpty(provided) && provided.StartsWith("gsk_") && provided.Length < 200)
            {
                var hash = Hash(provided);
                key = await db.Set<IntegrationApiKey>().FirstOrDefaultAsync(k => k.KeyHash == hash && k.RevokedAt == null);
            }
            if (key == null)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "Ungültiger oder widerrufener API-Schlüssel." });
                return;
            }
            // Throttled bookkeeping: at most one write per minute per key.
            if (key.LastUsedAt == null || key.LastUsedAt < DateTimeOffset.UtcNow.AddMinutes(-1))
            {
                key.LastUsedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
            }
            ctx.Items[KeyNameItem] = key.Name;
        }
        await next(ctx);
    }
}
