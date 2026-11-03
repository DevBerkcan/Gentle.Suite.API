namespace GentleSuite.Domain.Entities;

/// <summary>API key for a trusted external system (e.g. GentleHub). Only the SHA-256 hash is stored;
/// the plaintext is shown exactly once when the key is created. Keys are revoked, never deleted,
/// so the audit trail ("which key created this customer") stays intact.</summary>
public class IntegrationApiKey : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    /// <summary>First characters of the key (e.g. "gsk_ab12cd") to recognise it in the UI.</summary>
    public string Prefix { get; set; } = string.Empty;
    public string KeyHash { get; set; } = string.Empty;
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
