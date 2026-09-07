using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;
using GentleSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace GentleSuite.Infrastructure.Services;

public static class QuoteLifecycle
{
    // Keep the same predicate for scheduled cleanup and request-time refreshes.
    public static Expression<Func<Quote, bool>> IsExpired(DateTimeOffset now) => q =>
        (q.Status == QuoteStatus.Sent || q.Status == QuoteStatus.Viewed)
        && ((q.ExpiresAt ?? q.ApprovalTokenExpiry ?? (q.SentAt ?? q.CreatedAt).AddDays(14)) <= now
            || q.ApprovalTokenExpiry <= now);

    public static Task<int> ExpireAsync(AppDbContext db, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return db.Quotes.Where(IsExpired(now))
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.Status, QuoteStatus.Expired)
                .SetProperty(q => q.UpdatedAt, now), ct);
    }
}
