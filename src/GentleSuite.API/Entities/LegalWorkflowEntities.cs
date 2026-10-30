namespace GentleSuite.Domain.Entities;

/// <summary>
/// Legal-Cockpit-style "Rechts-Ablauf" for one project. A project is anchored on its quote (QuoteGroupId stays
/// stable across quote versions). Most steps are derived live from the quote / agency contract; this row only holds
/// what cannot be derived: manual checklist items, the copyright release and the formal acceptance (Abnahme).
/// Created lazily on the first write, so existing quotes need no backfill.
/// </summary>
public class ProjectLegalWorkflow : BaseEntity
{
    public Guid QuoteGroupId { get; set; }
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    /// <summary>{ itemKey: { State: "open"|"done"|"not-required", Note, UpdatedAt } } for the manual steps.</summary>
    public string ManualStepsJson { get; set; } = "{}";

    // Urheberrechte freigeben lassen — customer confirms rights to supplied material via public link.
    public string? RightsTokenHash { get; set; }
    public DateTimeOffset? RightsTokenExpiry { get; set; }
    public DateTimeOffset? RightsRequestedAt { get; set; }
    public string? RightsRequestedTo { get; set; }
    public DateTimeOffset? RightsGrantedAt { get; set; }
    public string? RightsGrantedByName { get; set; }
    public string? RightsGrantedByEmail { get; set; }
    public string? RightsGrantedIp { get; set; }
    public bool RightsReferenceConsent { get; set; }
    /// <summary>Exact declaration text the customer confirmed (frozen).</summary>
    public string? RightsTextSnapshot { get; set; }

    // Rechtssichere Abnahme — formal acceptance request with a deadline (deemed accepted after it passes, § 640 Abs. 2 BGB).
    public string? AcceptanceTokenHash { get; set; }
    public DateTimeOffset? AcceptanceTokenExpiry { get; set; }
    public DateTimeOffset? AcceptanceRequestedAt { get; set; }
    public string? AcceptanceRequestedTo { get; set; }
    public DateTimeOffset? AcceptanceDeadline { get; set; }
    public string? AcceptanceScope { get; set; }
    /// <summary>"accepted" | "accepted-with-reservations" | "declined"; null while open.</summary>
    public string? AcceptanceResult { get; set; }
    public DateTimeOffset? AcceptanceRespondedAt { get; set; }
    public string? AcceptanceByName { get; set; }
    public string? AcceptanceByEmail { get; set; }
    public string? AcceptanceIp { get; set; }
    public string? AcceptanceRemarks { get; set; }
    public string? AcceptanceSignatureData { get; set; }
    public string? AcceptanceTextSnapshot { get; set; }
}
