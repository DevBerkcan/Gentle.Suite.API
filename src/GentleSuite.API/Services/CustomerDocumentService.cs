using GentleSuite.Application.DTOs;
using GentleSuite.Application.Interfaces;
using GentleSuite.Domain.Entities;
using GentleSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GentleSuite.Infrastructure.Services;

public class CustomerDocumentServiceImpl : ICustomerDocumentService
{
    private readonly AppDbContext _db;
    private readonly IFileStorageService _fs;

    public CustomerDocumentServiceImpl(AppDbContext db, IFileStorageService fs)
    { _db = db; _fs = fs; }

    public async Task<List<CustomerDocumentDto>> GetDocumentsAsync(Guid customerId, CancellationToken ct)
        => await _db.CustomerDocuments
            .Where(d => d.CustomerId == customerId)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new CustomerDocumentDto(d.Id, d.CustomerId, d.FileName, d.ContentType, d.FileSizeBytes, d.Notes, d.CreatedAt))
            .ToListAsync(ct);

    public async Task<CustomerDocumentDto> UploadAsync(Guid customerId, Stream stream, string fileName, string contentType, long fileSize, string? notes, CancellationToken ct)
    {
        var path = await _fs.UploadAsync(stream, fileName, contentType, ct);
        var doc = new CustomerDocument
        {
            CustomerId = customerId,
            FileName = fileName,
            ContentType = contentType,
            FileSizeBytes = fileSize,
            StoragePath = path,
            Notes = notes
        };
        _db.CustomerDocuments.Add(doc);
        await _db.SaveChangesAsync(ct);
        return new CustomerDocumentDto(doc.Id, doc.CustomerId, doc.FileName, doc.ContentType, doc.FileSizeBytes, doc.Notes, doc.CreatedAt);
    }

    public async Task<(Stream Stream, string FileName, string ContentType)> DownloadAsync(Guid customerId, Guid docId, CancellationToken ct)
    {
        var doc = await _db.CustomerDocuments.FirstOrDefaultAsync(d => d.Id == docId && d.CustomerId == customerId, ct)
            ?? throw new KeyNotFoundException("Dokument nicht gefunden.");
        var stream = await _fs.DownloadAsync(doc.StoragePath, ct);
        return (stream, doc.FileName, doc.ContentType);
    }

    public async Task DeleteAsync(Guid customerId, Guid docId, CancellationToken ct)
    {
        var doc = await _db.CustomerDocuments.FirstOrDefaultAsync(d => d.Id == docId && d.CustomerId == customerId, ct)
            ?? throw new KeyNotFoundException("Dokument nicht gefunden.");
        await _fs.DeleteAsync(doc.StoragePath, ct);
        _db.CustomerDocuments.Remove(doc);
        await _db.SaveChangesAsync(ct);
    }
}
