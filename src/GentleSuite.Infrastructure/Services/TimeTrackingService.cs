using AutoMapper;
using GentleSuite.Application.DTOs;
using GentleSuite.Application.Interfaces;
using GentleSuite.Domain.Entities;
using GentleSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GentleSuite.Infrastructure.Services;

public class TimeTrackingServiceImpl : ITimeTrackingService
{
    private readonly AppDbContext _db;
    private readonly IMapper _m;

    public TimeTrackingServiceImpl(AppDbContext db, IMapper m) { _db = db; _m = m; }

    public async Task<List<TimeEntryDto>> GetEntriesAsync(DateTimeOffset? from, DateTimeOffset? to, Guid? projectId, Guid? customerId, CancellationToken ct)
    {
        var q = _db.TimeEntries.Include(t => t.Project).Include(t => t.Customer).AsQueryable();
        if (from.HasValue) q = q.Where(t => t.Date >= from.Value);
        if (to.HasValue) q = q.Where(t => t.Date <= to.Value);
        if (projectId.HasValue) q = q.Where(t => t.ProjectId == projectId.Value);
        if (customerId.HasValue) q = q.Where(t => t.CustomerId == customerId.Value);
        var items = await q.OrderByDescending(t => t.Date).ThenByDescending(t => t.CreatedAt).ToListAsync(ct);
        return _m.Map<List<TimeEntryDto>>(items);
    }

    public async Task<TimeEntryDto> CreateAsync(CreateTimeEntryRequest req, CancellationToken ct)
    {
        var entry = new TimeEntry
        {
            ProjectId = req.ProjectId,
            CustomerId = req.CustomerId,
            Description = req.Description,
            Date = req.Date,
            Hours = req.Hours,
            IsBillable = req.IsBillable,
            HourlyRate = req.HourlyRate
        };
        _db.TimeEntries.Add(entry);
        await _db.SaveChangesAsync(ct);
        var saved = await _db.TimeEntries.Include(t => t.Project).Include(t => t.Customer).FirstAsync(t => t.Id == entry.Id, ct);
        return _m.Map<TimeEntryDto>(saved);
    }

    public async Task<TimeEntryDto> UpdateAsync(Guid id, CreateTimeEntryRequest req, CancellationToken ct)
    {
        var entry = await _db.TimeEntries.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException();
        entry.ProjectId = req.ProjectId;
        entry.CustomerId = req.CustomerId;
        entry.Description = req.Description;
        entry.Date = req.Date;
        entry.Hours = req.Hours;
        entry.IsBillable = req.IsBillable;
        entry.HourlyRate = req.HourlyRate;
        await _db.SaveChangesAsync(ct);
        var saved = await _db.TimeEntries.Include(t => t.Project).Include(t => t.Customer).FirstAsync(t => t.Id == entry.Id, ct);
        return _m.Map<TimeEntryDto>(saved);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var entry = await _db.TimeEntries.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException();
        entry.IsDeleted = true;
        entry.DeletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<TimeEntrySummaryDto> GetSummaryAsync(DateTimeOffset from, DateTimeOffset to, Guid? projectId, CancellationToken ct)
    {
        var q = _db.TimeEntries.Where(t => t.Date >= from && t.Date <= to);
        if (projectId.HasValue) q = q.Where(t => t.ProjectId == projectId.Value);
        var entries = await q.ToListAsync(ct);
        var totalHours = entries.Sum(e => e.Hours);
        var billableHours = entries.Where(e => e.IsBillable).Sum(e => e.Hours);
        var nonBillableHours = entries.Where(e => !e.IsBillable).Sum(e => e.Hours);
        var billableAmount = entries.Where(e => e.IsBillable && e.HourlyRate.HasValue).Sum(e => e.Hours * e.HourlyRate!.Value);
        return new TimeEntrySummaryDto(totalHours, billableHours, nonBillableHours, billableAmount);
    }
}
