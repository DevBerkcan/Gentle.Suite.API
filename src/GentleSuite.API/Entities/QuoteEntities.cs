using GentleSuite.Domain.Enums;

namespace GentleSuite.Domain.Entities;

// === Quote Templates ===
public class QuoteTemplate : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public List<QuoteTemplateLine> Lines { get; set; } = new();
}

public class QuoteTemplateLine : BaseEntity
{
    public Guid QuoteTemplateId { get; set; }
    public QuoteTemplate QuoteTemplate { get; set; } = null!;
    public Guid? ServiceCatalogItemId { get; set; }
    public ServiceCatalogItem? ServiceCatalogItem { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public QuoteLineType LineType { get; set; }
    public int SortOrder { get; set; }
}

// === Quote ===
public class Quote : GobdEntity
{
    public string QuoteNumber { get; set; } = string.Empty;
    public Guid QuoteGroupId { get; set; }        // stable id for all versions
    public bool IsCurrentVersion { get; set; } = true;
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public Guid? ContactId { get; set; }
    public Contact? Contact { get; set; }
    public int Version { get; set; } = 1;
    public QuoteStatus Status { get; set; } = QuoteStatus.Draft;
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? ViewedAt { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? Subject { get; set; }
    public string? IntroText { get; set; }
    public string? OutroText { get; set; }
    public string? Notes { get; set; }
    public string? InternalNotes { get; set; }
    public string? CustomerComment { get; set; }
    public decimal TaxRate { get; set; } = 19m;
    public TaxMode TaxMode { get; set; } = TaxMode.Standard;

    // Approval / Signature
    public string? ApprovalToken { get; set; }
    public string? ApprovalTokenHash { get; set; }
    public DateTimeOffset? ApprovalTokenExpiry { get; set; }
    public SignatureStatus SignatureStatus { get; set; } = SignatureStatus.Pending;
    public string? SignatureData { get; set; }  // Base64 SVG/PNG of signature
    public string? SignedByName { get; set; }
    public string? SignedByEmail { get; set; }
    public DateTimeOffset? SignedAt { get; set; }
    public string? SignedIpAddress { get; set; }
    public bool B2bAuthorityConfirmed { get; set; }

    // Legal text blocks (JSON array of keys)
    public string? LegalTextBlocks { get; set; }
    /// <summary>Frozen title+content of the referenced LegalTextBlocks at send time, so later edits to the master texts don't silently change what an already-sent/signed quote shows.</summary>
    public string? LegalTextBlocksSnapshot { get; set; }

    // Payment terms offered to the customer (JSON array of keys) + the one the customer picked
    public string? PaymentTermKeys { get; set; }
    public string? ChosenPaymentTermKey { get; set; }

    // Ratenzahlung: optional installment periods (in months) offered for the OneTime-line total, and the
    // one the customer picked at signing. Null/absent ChosenInstallmentMonths = pay the full amount now.
    public string? InstallmentPeriodOptionsMonths { get; set; }
    public int? ChosenInstallmentMonths { get; set; }

    public List<QuoteLine> Lines { get; set; } = new();

    public decimal SubtotalOneTime => Lines.Where(l => l.LineType == QuoteLineType.OneTime).Sum(l => l.Total);
    public decimal SubtotalMonthly => Lines.Where(l => l.LineType == QuoteLineType.RecurringMonthly).Sum(l => l.Total);
    public decimal Subtotal => SubtotalOneTime + SubtotalMonthly;
    public decimal TaxAmount => TaxMode == TaxMode.SmallBusiness ? 0 : Subtotal * (TaxRate / 100);
    public decimal GrandTotal => Subtotal + TaxAmount;
}

public class QuoteLine : BaseEntity
{
    public Guid QuoteId { get; set; }
    public Quote Quote { get; set; } = null!;
    public Guid? ServiceCatalogItemId { get; set; }
    public ServiceCatalogItem? ServiceCatalogItem { get; set; }
    public Guid? SubscriptionPlanId { get; set; }
    public SubscriptionPlan? SubscriptionPlan { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; } // Rabatt in %
    public QuoteLineType LineType { get; set; }
    public int VatPercent { get; set; } = 19;
    public int SortOrder { get; set; }
    public decimal Total => Quantity * UnitPrice * (1 - (DiscountPercent / 100m));
}

public class LegalTextBlock : BaseEntity
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    public LegalDocumentType Type { get; set; } = LegalDocumentType.Sonstiges;
    /// <summary>If true, this block is included on every quote sent, without needing to be picked manually (AGB/Datenschutz).</summary>
    public bool AutoAttachToQuotes { get; set; }
    /// <summary>Storage path if this document was uploaded as a file (e.g. a signed AGB PDF) instead of/in addition to Content.</summary>
    public string? AttachmentPath { get; set; }
    public string? AttachmentFileName { get; set; }
    public string? AttachmentContentType { get; set; }
}

public class PaymentTermOption : BaseEntity
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}
