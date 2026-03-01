using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Interfaces;
using GentleSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace GentleSuite.Infrastructure.Services;

public class NumberSequenceServiceImpl : INumberSequenceService
{
    private readonly AppDbContext _db;
    public NumberSequenceServiceImpl(AppDbContext db) { _db = db; }

    public async Task<string> NextNumberAsync(string entityType, int year, string defaultPrefix, int defaultPadding, CancellationToken ct, bool includeYear = true)
    {
        const int maxRetries = 3;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            try
            {
                var normalizedEntity = NormalizeEntity(entityType);
                var seq = await _db.NumberSequences.SingleOrDefaultAsync(s => s.EntityType == normalizedEntity && s.Year == year, ct);
                if (seq == null)
                {
                    seq = new NumberSequence
                    {
                        EntityType = normalizedEntity,
                        Year = year,
                        Prefix = NormalizePrefix(defaultPrefix),
                        Padding = defaultPadding,
                        LastValue = 0
                    };
                    _db.NumberSequences.Add(seq);
                    await _db.SaveChangesAsync(ct);
                }

                seq.LastValue += 1;
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return Format(seq.Prefix, seq.Year, seq.LastValue, seq.Padding, includeYear);
            }
            catch (DbUpdateException) when (attempt < maxRetries)
            {
                await tx.RollbackAsync(ct);
            }
            catch (InvalidOperationException) when (attempt < maxRetries)
            {
                await tx.RollbackAsync(ct);
            }
        }

        throw new InvalidOperationException("Nummernkreis konnte nach mehreren Versuchen nicht erzeugt werden.");
    }

    public async Task<List<NumberRangeConfig>> GetRangesAsync(int year, CancellationToken ct)
    {
        return await _db.NumberSequences
            .Where(s => s.Year == year)
            .OrderBy(s => s.EntityType)
            .Select(s => new NumberRangeConfig(s.EntityType, s.Year, s.Prefix, s.LastValue + 1, s.Padding))
            .ToListAsync(ct);
    }

    public async Task<NumberRangeConfig> UpsertRangeAsync(NumberRangeConfig config, CancellationToken ct)
    {
        var entity = NormalizeEntity(config.EntityType);
        var prefix = NormalizePrefix(config.Prefix);
        if (config.Padding < 1 || config.Padding > 12) throw new ArgumentException("Padding muss zwischen 1 und 12 liegen.");
        if (config.NextValue < 1) throw new ArgumentException("NextValue muss >= 1 sein.");

        var seq = await _db.NumberSequences.SingleOrDefaultAsync(s => s.EntityType == entity && s.Year == config.Year, ct);
        if (seq == null)
        {
            seq = new NumberSequence
            {
                EntityType = entity,
                Year = config.Year,
                Prefix = prefix,
                Padding = config.Padding,
                LastValue = config.NextValue - 1
            };
            _db.NumberSequences.Add(seq);
        }
        else
        {
            seq.Prefix = prefix;
            seq.Padding = config.Padding;
            seq.LastValue = config.NextValue - 1;
        }

        await _db.SaveChangesAsync(ct);
        return new NumberRangeConfig(seq.EntityType, seq.Year, seq.Prefix, seq.LastValue + 1, seq.Padding);
    }

    private static string NormalizeEntity(string entityType)
    {
        if (string.IsNullOrWhiteSpace(entityType)) throw new ArgumentException("EntityType ist erforderlich.");
        return entityType.Trim();
    }

    private static string NormalizePrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) throw new ArgumentException("Prefix ist erforderlich.");
        return prefix.Trim().ToUpperInvariant();
    }

    private static string Format(string prefix, int year, int value, int padding, bool includeYear = true) =>
        includeYear ? $"{prefix}-{year}-{value.ToString($"D{padding}")}" : $"{prefix}-{value.ToString($"D{padding}")}";
}

