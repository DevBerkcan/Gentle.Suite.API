using GentleSuite.Domain.Entities;
using GentleSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace GentleSuite.API.Middleware;

/// <summary>
/// Machine-to-machine auth for GentleHub: the write surface /api/integrations/hub/* and the read-only surface
/// /api/integrations/gentlehub/* (customer lookup/context for GentleHub's agents). Unlike the GentleBook integration
/// the keys live in the database (hashed), so they can be created and revoked in the UI without touching server
/// config — one key covers both surfaces. Header: <c>X-Api-Key</c> (GentleHub's existing client sends
/// <c>X-GentleHub-Api-Key</c>, accepted as an alias). The matched key's name is stored in HttpContext.Items.
/// </summary>
public class IntegrationApiKeyMiddleware(RequestDelegate next)
{
    public const string RoutePrefix = "/api/integrations/hub";
    public const string ReadRoutePrefix = "/api/integrations/gentlehub";
    public const string HeaderName = "X-Api-Key";
    public const string LegacyHeaderName = "X-GentleHub-Api-Key";

    /// <summary>The key a request carries, from either header name.</summary>
    public static string? ProvidedKey(HttpContext ctx) =>
        (ctx.Request.Headers[HeaderName].FirstOrDefault() ?? ctx.Request.Headers[LegacyHeaderName].FirstOrDefault())?.Trim();
    public const string KeyNameItem = "IntegrationKeyName";

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    public async Task InvokeAsync(HttpContext ctx, AppDbContext db)
    {
        if (ctx.Request.Path.StartsWithSegments(RoutePrefix) || ctx.Request.Path.StartsWithSegments(ReadRoutePrefix))
        {
            var provided = ProvidedKey(ctx);
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
