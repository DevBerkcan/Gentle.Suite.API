using GentleSuite.Domain.Enums;

namespace GentleSuite.Domain.Entities;

/// <summary>Admin-managed template of default clause sections for a contract type
/// (Wartungsvertrag, SEO-Vertrag, Webdesign-Vertrag, ...). Analogous to LegalTextBlock,
/// but a template groups multiple named sections instead of one text block.</summary>
public class ContractTemplate : BaseEntity
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    /// <summary>[{Title, Content}] default clauses, copied into a new AgencyContract's SectionsJson at creation.</summary>
    public string SectionsJson { get; set; } = "[]";
    /// <summary>JSON string[] of ContractClauseBlock.Key values pre-selected as a "Schnellstart" in the wizard when this template is chosen.</summary>
    public string? DefaultBlockKeysJson { get; set; }
}

/// <summary>Admin-managed catalog of reusable clause blocks offered as toggles in the Vertrags-Assistent
/// (Leistungen-Auswahl). Category "kern" is always included; "optionen" are the four optional-clause
/// toggles; the remaining categories (webseiten/marketing/design/wartung) are the Leistungen tiles.</summary>
public class ContractClauseBlock : BaseEntity
{
    public string Key { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    /// <summary>If true, selecting this block also triggers Abnahme/Mängelgewährleistung/Rechteeinräumung clauses (Werkvertrag).</summary>
    public bool IsCreativeWork { get; set; }
}

/// <summary>GoBD-relevant, bilaterally-signed agency contract concluded after a Quote is accepted, or
/// directly for a Serienrechnung/Ratenzahlung (with or without an originating Quote). At least one of
/// QuoteId/SubscriptionId is set. Its own hash chain, independent of Quotes/Invoices.</summary>
public class AgencyContract : GobdEntity
{
    public string ContractNumber { get; set; } = string.Empty;
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public Guid? QuoteId { get; set; }
    public Quote? Quote { get; set; }
    public Guid? SubscriptionId { get; set; }
    public CustomerSubscription? Subscription { get; set; }
    public Guid? ContractTemplateId { get; set; }
    public ContractTemplate? ContractTemplate { get; set; }
    /// <summary>Snapshot of the template's name at creation time (e.g. "Wartungsvertrag").</summary>
    public string ContractTypeName { get; set; } = string.Empty;
    public AgencyContractStatus Status { get; set; } = AgencyContractStatus.Draft;
    /// <summary>[{Title, Content}] editable clause sections, seeded from the chosen ContractTemplate.</summary>
    public string SectionsJson { get; set; } = "[]";
    public string? LegalTextBlocks { get; set; }
    public string? LegalTextBlocksSnapshot { get; set; }
    public decimal? TotalContractValue { get; set; }
    /// <summary>Frozen JSON snapshot of the customer address/contact used at creation (Name/Straße/PLZ/Ort/Land/Ansprechpartner),
    /// so later changes to the customer record don't retroactively alter an already-generated contract PDF. Null for
    /// contracts created before this field existed — PdfService falls back to live customer data in that case.</summary>
    public string? CustomerAddressSnapshot { get; set; }

    // Auftragnehmer (agency) side — one-click confirmation by the logged-in employee.
    public string? RepSignedByName { get; set; }
    public Guid? RepSignedByUserId { get; set; }
    public DateTimeOffset? RepSignedAt { get; set; }

    // Public token link for the customer's signature.
    public string? ApprovalToken { get; set; }
    public string? ApprovalTokenHash { get; set; }
    public DateTimeOffset? ApprovalTokenExpiry { get; set; }
    public DateTimeOffset? SentAt { get; set; }

    // Auftraggeber (customer) side — drawn signature, same mechanism as Quote.
    public string? CustomerSignedByName { get; set; }
    public string? CustomerSignedByEmail { get; set; }
    public string? CustomerSignatureData { get; set; }
    public DateTimeOffset? CustomerSignedAt { get; set; }
    public string? CustomerSignedIpAddress { get; set; }
    public string? DeclineReason { get; set; }
}
