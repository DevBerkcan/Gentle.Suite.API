namespace GentleSuite.Domain.Entities;

public abstract class BaseEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>GoBD-compliant entity: hash chain, immutable after finalization</summary>
public abstract class GobdEntity : BaseEntity
{
    public string? DocumentHash { get; set; }
    public string? PreviousDocumentHash { get; set; }
    public bool IsFinalized { get; set; }
    public DateTimeOffset? FinalizedAt { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    /// <summary>GoBD: 10 year retention</summary>
    public DateTimeOffset? RetentionUntil { get; set; }
}
