using GentleSuite.Infrastructure.Data;
using GentleSuite.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GentleSuite.API.Controllers;

/// <param name="Current">Laufendes Geschäftsjahr (Berliner Kalenderjahr).</param>
/// <param name="Years">Auswählbare Jahre für den Jahres-Umschalter, aufsteigend (erstes Belegjahr … laufendes Jahr).</param>
/// <param name="ClosedYears">Abgeschlossene Jahre: Belege daraus sind nur noch lesbar.</param>
public record FiscalYearInfoDto(int Current, List<int> Years, List<int> ClosedYears, int FirstYearWithYearInNumber);

[ApiController, Route("api/fiscal-year"), Authorize]
public class FiscalYearController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<FiscalYearInfoDto>> Get(CancellationToken ct)
    {
        var current = FiscalYear.Current;
        var firstQuote = await db.Quotes.Select(q => (DateTimeOffset?)q.CreatedAt).MinAsync(ct);
        var firstInvoice = await db.Invoices.Select(i => (DateTimeOffset?)i.InvoiceDate).MinAsync(ct);
        var first = new[] { firstQuote, firstInvoice }.Where(d => d != null).Select(d => FiscalYear.YearOf(d!.Value)).DefaultIfEmpty(current).Min();
        first = Math.Max(first, current - 9); // nie mehr als zehn Jahre im Umschalter
        var years = Enumerable.Range(first, current - first + 1).ToList();
        return Ok(new FiscalYearInfoDto(current, years, years.Where(y => y < current).ToList(), FiscalYear.FirstYearWithYearInNumber));
    }
}
