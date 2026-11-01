using System.Security.Cryptography;
using System.Text;

namespace GentleSuite.API.Middleware;

/// <summary>
/// Machine-to-machine auth for the GentleHub AI-operations surface (/api/integrations/gentlehub/*).
/// Separate key from GentleBook, so either integration can be rotated or revoked on its own.
/// The surface is read-only; the key grants nothing outside this prefix.
/// </summary>
public class GentleHubApiKeyMiddleware(RequestDelegate next, ILogger<GentleHubApiKeyMiddleware> logger)
{
    public const string RoutePrefix = "/api/integrations/gentlehub";
    private const string HeaderName = "X-GentleHub-Api-Key";

    public async Task InvokeAsync(HttpContext ctx, IConfiguration config)
    {
        if (ctx.Request.Path.StartsWithSegments(RoutePrefix))
        {
            var expected = config["Integrations:GentleHubApiKey"];
            var provided = ctx.Request.Headers[HeaderName].FirstOrDefault();

            // Hashing first makes the comparison constant-time regardless of length.
            var isValid = !string.IsNullOrEmpty(expected) && expected.Length >= 32
                && !string.IsNullOrEmpty(provided)
                && CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
                    SHA256.HashData(Encoding.UTF8.GetBytes(provided)));

            if (!isValid)
            {
                logger.LogWarning("Rejected GentleHub integration call to {Path} from {Ip}", ctx.Request.Path, ctx.Connection.RemoteIpAddress);
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
                return;
            }
        }

        await next(ctx);
    }
}
