using GentleSuite.Domain.Enums;

namespace GentleSuite.Domain.Entities;

/// <summary>
/// GoBD-konforme Rechnung nach §14 UStG
/// - Fortlaufende, nicht änderbare Rechnungsnummer
/// - Hash-Chain für Revisionssicherheit
/// - Nach Finalisierung: nur Storno möglich
/// </summary>
public class Invoice : GobdEntity
{
    // === Pflichtangaben §14 UStG ===
    public string InvoiceNumber { get; set; } = string.Empty;  // Fortlaufend, unveränderbar
    public InvoiceType Type { get; set; } = InvoiceType.Standard;
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public Guid? QuoteId { get; set; }
    public Quote? Quote { get; set; }
    public Guid? SubscriptionId { get; set; }
    public CustomerSubscription? Subscription { get; set; }
    public DateTimeOffset? BillingPeriodStart { get; set; } // fuer wiederkehrende Rechnungen
    public DateTimeOffset? BillingPeriodEnd { get; set; }   // fuer wiederkehrende Rechnungen
    public Guid? CancellationOfInvoiceId { get; set; }  // For Storno-Rechnung
    public Invoice? CancellationOfInvoice { get; set; }

    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;
    public TaxMode TaxMode { get; set; } = TaxMode.Standard;

    // === Dates ===
    public DateTimeOffset InvoiceDate { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset DueDate { get; set; }
    public DateTimeOffset? PaidAt { get; set; }

    // === Tax (§14 UStG) ===
    public string? SellerTaxId { get; set; }      // Our Steuernummer
    public string? SellerVatId { get; set; }       // Our USt-IdNr.
    public string? BuyerVatId { get; set; }        // Customer USt-IdNr (for reverse charge)

    // === Amounts ===
    public decimal NetTotal { get; set; }           // Netto
    public decimal VatAmount { get; set; }          // MwSt Betrag
    public decimal GrossTotal { get; set; }         // Brutto
    public decimal VatRate { get; set; } = 19m;

    // === Text ===
    public string? Subject { get; set; }
    public string? IntroText { get; set; }
    public string? OutroText { get; set; }
    public string? Notes { get; set; }
    public string? PaymentTerms { get; set; }

    // === Reminder ===
    public bool ReminderStop { get; set; }       // Mahnstopp auf Rechnungsebene
    public ReminderLevel? LastReminderLevel { get; set; }
    public DateTimeOffset? LastReminderSentAt { get; set; }

    // === SmallBusiness / Reverse Charge ===
    public string? SmallBusinessNote { get; set; }  // "Gemäß § 19 UStG wird keine Umsatzsteuer berechnet."
    public string? ReverseChargeNote { get; set; }  // "Steuerschuldnerschaft des Leistungsempfängers."

    public List<InvoiceLine> Lines { get; set; } = new();
    public List<InvoicePayment> Payments { get; set; } = new();

    public void RecalculateTotals()
    {
        NetTotal = Lines.Sum(l => l.NetTotal);
        if (TaxMode == TaxMode.SmallBusiness || TaxMode == TaxMode.ReverseCharge)
        {
            VatAmount = 0;
            GrossTotal = NetTotal;
        }
        else
        {
            VatAmount = Lines.Sum(l => l.VatAmount);
            GrossTotal = NetTotal + VatAmount;
        }
    }
}

public class InvoiceLine : BaseEntity
{
    public Guid InvoiceId { get; set; }
    public Invoice Invoice { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Unit { get; set; } = "Stk.";
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }      // Netto-Einzelpreis
    public int VatPercent { get; set; } = 19;   // 19, 7, or 0
    public int SortOrder { get; set; }
    public int LineType { get; set; }
    public decimal DiscountPercent { get; set; } = 0;

    public decimal NetTotal => Quantity * UnitPrice;
    public decimal VatAmount => NetTotal * (VatPercent / 100m);
    public decimal GrossTotal => NetTotal + VatAmount;
}

/// <summary>Payment tracking for partial/full payments</summary>
public class InvoicePayment : BaseEntity
{
    public Guid InvoiceId { get; set; }
    public Invoice Invoice { get; set; } = null!;
    public decimal Amount { get; set; }
    public DateTimeOffset PaymentDate { get; set; }
    public string? PaymentMethod { get; set; }  // Überweisung, PayPal, Bar, etc.
    public string? Reference { get; set; }       // Transaktions-ID
    public string? Note { get; set; }
    public Guid? BankTransactionId { get; set; }
}

/// <summary>Separate VAT summary for multi-rate invoices</summary>
public class InvoiceVatSummary
{
    public int VatPercent { get; set; }
    public decimal NetAmount { get; set; }
    public decimal VatAmount { get; set; }
    public decimal GrossAmount { get; set; }
}
