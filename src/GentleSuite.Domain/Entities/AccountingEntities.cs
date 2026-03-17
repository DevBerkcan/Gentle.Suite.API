using GentleSuite.Domain.Enums;

namespace GentleSuite.Domain.Entities;

// === Expense (Ausgaben) ===
public class Expense : GobdEntity
{
    public string? ExpenseNumber { get; set; }
    public ExpenseStatus Status { get; set; } = ExpenseStatus.Draft;
    public DateTimeOffset ExpenseDate { get; set; } = DateTimeOffset.UtcNow;
    public string? Supplier { get; set; }           // Lieferant
    public string? SupplierTaxId { get; set; }
    public Guid? ExpenseCategoryId { get; set; }
    public ExpenseCategory? Category { get; set; }
    public string? Description { get; set; }
    public decimal NetAmount { get; set; }
    public int VatPercent { get; set; } = 19;
    public decimal VatAmount { get; set; }          // Vorsteuer
    public decimal GrossAmount { get; set; }
    public string? ReceiptPath { get; set; }        // Beleg Upload
    public Guid? AccountId { get; set; }            // Booking account (SKR03)
    public ChartOfAccount? Account { get; set; }
    public bool IsRecurring { get; set; }
    public RecurringInterval? RecurringInterval { get; set; }
    public DateTimeOffset? RecurringNextDate { get; set; }
    public Guid? RecurringParentId { get; set; }

    public void Recalculate()
    {
        VatAmount = NetAmount * (VatPercent / 100m);
        GrossAmount = NetAmount + VatAmount;
    }
}

public class ExpenseCategory : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? AccountNumber { get; set; }   // SKR03 reference
    public int SortOrder { get; set; }
}

// === Chart of Accounts (Kontenrahmen SKR03) ===
public class ChartOfAccount : BaseEntity
{
    public string AccountNumber { get; set; } = string.Empty; // e.g. "1200"
    public string Name { get; set; } = string.Empty;
    public AccountType Type { get; set; }
    public string? ParentAccountNumber { get; set; }
    public bool IsSystem { get; set; }  // Not deletable
    public bool IsActive { get; set; } = true;
}

// === Journal Entries (Buchungssätze) ===
public class JournalEntry : GobdEntity
{
    public string EntryNumber { get; set; } = string.Empty;
    public DateTimeOffset BookingDate { get; set; }
    public string? Description { get; set; }
    public string? Reference { get; set; }       // Invoice#, Expense# etc.
    public Guid? InvoiceId { get; set; }
    public Guid? ExpenseId { get; set; }
    public JournalEntryStatus Status { get; set; } = JournalEntryStatus.Draft;
    public List<JournalEntryLine> Lines { get; set; } = new();

    public decimal TotalDebit => Lines.Where(l => l.IsDebit).Sum(l => l.Amount);
    public decimal TotalCredit => Lines.Where(l => !l.IsDebit).Sum(l => l.Amount);
    public bool IsBalanced => Math.Abs(TotalDebit - TotalCredit) < 0.01m;
}

public class JournalEntryLine : BaseEntity
{
    public Guid JournalEntryId { get; set; }
    public JournalEntry JournalEntry { get; set; } = null!;
    public string AccountNumber { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public bool IsDebit { get; set; }   // Soll
    public decimal Amount { get; set; }
    public string? Note { get; set; }
}

// === VAT Period (Umsatzsteuer-Voranmeldung) ===
public class VatPeriod : GobdEntity
{
    public int Year { get; set; }
    public int Month { get; set; }      // 1-12, or quarter
    public bool IsQuarterly { get; set; }
    public decimal OutputVat { get; set; }    // Umsatzsteuer
    public decimal InputVat { get; set; }     // Vorsteuer
    public decimal PayableTax { get; set; }   // Zahllast = Output - Input
    public bool IsSubmitted { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
}

// === Bank Transactions ===
public class BankTransaction : BaseEntity
{
    public DateTimeOffset TransactionDate { get; set; }
    public string? Description { get; set; }
    public decimal Amount { get; set; }
    public string? Sender { get; set; }
    public string? Recipient { get; set; }
    public string? Reference { get; set; }
    public string? Iban { get; set; }
    public BankTransactionStatus Status { get; set; } = BankTransactionStatus.Unmatched;
    public Guid? MatchedInvoiceId { get; set; }
    public Guid? MatchedExpenseId { get; set; }
}

// === Company Settings ===
public class CompanySettings : BaseEntity
{
    public string CompanyName { get; set; } = string.Empty;
    public string? LegalName { get; set; }
    public string Street { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Country { get; set; } = "Deutschland";
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }
    public string? TaxId { get; set; }        // Steuernummer
    public string? VatId { get; set; }         // USt-IdNr.
    public string? BankName { get; set; }
    public string? Iban { get; set; }
    public string? Bic { get; set; }
    public string? RegisterCourt { get; set; } // Amtsgericht
    public string? RegisterNumber { get; set; } // HRB ...
    public string? ManagingDirector { get; set; }
    public TaxMode DefaultTaxMode { get; set; } = TaxMode.Standard;
    public AccountingMode AccountingMode { get; set; } = AccountingMode.EUR;
    public string? LogoPath { get; set; }
    public string? InvoiceIntroTemplate { get; set; }
    public string? InvoiceOutroTemplate { get; set; }
    public string? QuoteIntroTemplate { get; set; }
    public string? QuoteOutroTemplate { get; set; }
    public int InvoicePaymentTermDays { get; set; } = 14;
    public int QuoteValidityDays { get; set; } = 30;
}
