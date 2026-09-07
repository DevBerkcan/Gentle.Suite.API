using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;
using GentleSuite.Infrastructure.Data;
using GentleSuite.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

var now = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
var expired = QuoteLifecycle.IsExpired(now).Compile();
var checks = 0;
void Check(string name, Quote quote, bool expected)
{
    if (expired(quote) != expected) throw new Exception(name);
    checks++;
}
Check("exact deadline", new() { Status = QuoteStatus.Sent, ExpiresAt = now }, true);
Check("viewed overdue", new() { Status = QuoteStatus.Viewed, ExpiresAt = now.AddSeconds(-1) }, true);
Check("still valid", new() { Status = QuoteStatus.Sent, ExpiresAt = now.AddSeconds(1) }, false);
Check("existing promised validity preserved", new() { Status = QuoteStatus.Sent, SentAt = now.AddDays(-20), ExpiresAt = now.AddDays(10) }, false);
Check("legacy token expiry", new() { Status = QuoteStatus.Sent, ApprovalTokenExpiry = now.AddDays(-1) }, true);
Check("legacy sent date", new() { Status = QuoteStatus.Sent, SentAt = now.AddDays(-14) }, true);
Check("legacy creation date", new() { Status = QuoteStatus.Sent, CreatedAt = now.AddDays(-15) }, true);
Check("legacy recent", new() { Status = QuoteStatus.Sent, SentAt = now.AddDays(-13) }, false);
Check("earlier token expiry", new() { Status = QuoteStatus.Viewed, ExpiresAt = now.AddDays(1), ApprovalTokenExpiry = now }, true);
foreach (var status in new[] { QuoteStatus.Draft, QuoteStatus.Accepted, QuoteStatus.Ordered, QuoteStatus.Rejected, QuoteStatus.Expired, QuoteStatus.Inactive })
    Check($"preserve {status}", new() { Status = status, ExpiresAt = now.AddDays(-30) }, false);
using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer("Server=localhost;Database=Unused;Integrated Security=true;TrustServerCertificate=true").Options);
if (db.Model.FindEntityType(typeof(Quote))!.FindProperty(nameof(Quote.Status))!.IsConcurrencyToken != true) throw new Exception("Status changes must detect concurrent updates");
var sql = db.Quotes.Where(QuoteLifecycle.IsExpired(now)).ToQueryString();
if (!sql.Contains("DATEADD") || !sql.Contains("COALESCE")) throw new Exception("SQL Server translation failed");
Console.WriteLine($"PASS: {checks} lifecycle cases and SQL Server query translation (no database connection).");
