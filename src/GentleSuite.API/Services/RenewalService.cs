using GentleSuite.Domain.Enums;
using GentleSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GentleSuite.Infrastructure.Services;

/// <summary>Ein Abo, das demnächst endet oder dessen Laufzeit-Periode endet.
/// Kind: "ending" = gekündigt/befristet, endet am Datum (Chance auf Rückgewinnung),
/// "term-end" = Ende der (Mindest-)Laufzeit bzw. der aktuellen Verlängerungsperiode (Chance auf Upgrade/Folgeauftrag).</summary>
public record RenewalItemDto(Guid SubscriptionId, Guid CustomerId, string CustomerName, string PlanName, SubscriptionPlanCategory Category,
    string Kind, DateTimeOffset Date, int DaysLeft, decimal MonthlyNet, int? TermMonths, SubscriptionStatus Status);

public class RenewalService
{
    private readonly AppDbContext _db;
    public RenewalService(AppDbContext db) { _db = db; }

    public async Task<List<RenewalItemDto>> GetUpcomingAsync(int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 365);
        var now = DateTimeOffset.UtcNow;
        var until = now.AddDays(days);
        // Ratenpläne sind keine Verträge mit Verlängerung, abgeschlossene/abgelaufene interessieren nicht mehr.
        var subs = await _db.CustomerSubscriptions.AsNoTracking().Include(s => s.Customer).Include(s => s.Plan)
            .Where(s => !s.IsInstallmentPlan
                        && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Paused || s.Status == SubscriptionStatus.Cancelled))
            .ToListAsync(ct);
        var result = new List<RenewalItemDto>();
        foreach (var s in subs)
        {
            var monthly = s.AgreedMonthlyPrice ?? s.Plan.MonthlyPrice;
            if (s.EndDate is { } end)
            {
                if (end >= now && end <= until)
                    result.Add(new(s.Id, s.CustomerId, s.Customer.CompanyName, s.Plan.Name, s.Plan.Category, "ending", end, DaysUntil(now, end), monthly, s.ContractDurationMonths, s.Status));
                continue; // festes Ende bekannt → keine rechnerische Verlängerung
            }
            if (s.Status == SubscriptionStatus.Cancelled || s.ContractDurationMonths is not > 0) continue;
            // Nächstes Laufzeitende: Start + n × Laufzeit (Vertrag verlängert sich ohne Kündigung um die gleiche Laufzeit).
            var term = s.ContractDurationMonths.Value;
            var termEnd = s.StartDate.AddMonths(term);
            for (var i = 2; termEnd < now && i < 1200; i++) termEnd = s.StartDate.AddMonths(term * i);
            if (termEnd <= until)
                result.Add(new(s.Id, s.CustomerId, s.Customer.CompanyName, s.Plan.Name, s.Plan.Category, "term-end", termEnd, DaysUntil(now, termEnd), monthly, term, s.Status));
        }
        return result.OrderBy(r => r.Date).ToList();
    }

    private static int DaysUntil(DateTimeOffset now, DateTimeOffset date) => Math.Max(0, (int)Math.Ceiling((date - now).TotalDays));
}
