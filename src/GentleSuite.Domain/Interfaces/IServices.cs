using GentleSuite.Domain.Entities;

namespace GentleSuite.Domain.Interfaces;

public interface IRepository<T> where T : BaseEntity
{
    Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<List<T>> GetAllAsync(CancellationToken ct = default);
    Task<T> AddAsync(T entity, CancellationToken ct = default);
    Task UpdateAsync(T entity, CancellationToken ct = default);
    Task DeleteAsync(T entity, CancellationToken ct = default);
    IQueryable<T> Query();
}

public interface IUnitOfWork { Task<int> SaveChangesAsync(CancellationToken ct = default); }

public interface IEmailService
{
    Task SendEmailAsync(string to, string subject, string body, string? cc = null, List<string>? attachments = null, CancellationToken ct = default);
    Task SendTemplatedEmailAsync(string to, string templateKey, Dictionary<string, object> variables, Guid? customerId = null, List<string>? attachments = null, CancellationToken ct = default);
}

public interface IFileStorageService
{
    Task<string> UploadAsync(Stream stream, string fileName, string contentType, CancellationToken ct = default);
    Task<Stream> DownloadAsync(string path, CancellationToken ct = default);
    Task DeleteAsync(string path, CancellationToken ct = default);
}

public interface IPdfService
{
    Task<byte[]> GenerateQuotePdfAsync(Quote quote, CompanySettings company, CancellationToken ct = default);
    Task<byte[]> GenerateInvoicePdfAsync(Invoice invoice, CompanySettings company, CancellationToken ct = default);
}

public interface ICurrentUserService
{
    string? UserId { get; }
    string? UserName { get; }
    string? Email { get; }
    bool IsAuthenticated { get; }
    bool IsInRole(string role);
}

public interface IGobdService
{
    string ComputeHash(string content);
    string CreateChainedHash(string content, string? previousHash);
    Task<string?> GetLastInvoiceHashAsync(CancellationToken ct = default);
}

public interface INumberSequenceService
{
    Task<string> NextNumberAsync(string entityType, int year, string defaultPrefix, int defaultPadding, CancellationToken ct = default, bool includeYear = true);
    Task<List<NumberRangeConfig>> GetRangesAsync(int year, CancellationToken ct = default);
    Task<NumberRangeConfig> UpsertRangeAsync(NumberRangeConfig config, CancellationToken ct = default);
}

public record NumberRangeConfig(string EntityType, int Year, string Prefix, int NextValue, int Padding);
