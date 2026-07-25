using System.Security.Cryptography;
using System.Text;

namespace GentleSuite.API.Middleware;

/// <summary>
/// Machine-to-machine auth for the GentleBook integration surface (/api/integrations/gentlebook/*).
/// Not ASP.NET Identity/JWT — that's for human logins. A single shared secret is enough here
/// since there is exactly one trusted caller (the GentleBook API).
/// </summary>
public class GentleBookApiKeyMiddleware(RequestDelegate next)
{
    private const string RoutePrefix = "/api/integrations/gentlebook";
    private const string HeaderName = "X-GentleBook-Api-Key";

    public async Task InvokeAsync(HttpContext ctx, IConfiguration config)
    {
        if (ctx.Request.Path.StartsWithSegments(RoutePrefix))
        {
            var expected = config["Integrations:GentleBookApiKey"];
            var provided = ctx.Request.Headers[HeaderName].FirstOrDefault();

            var isValid = !string.IsNullOrEmpty(expected)
                && !string.IsNullOrEmpty(provided)
                && CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(expected),
                    Encoding.UTF8.GetBytes(provided));

            if (!isValid)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
                return;
            }
        }

        await next(ctx);
    }
}
