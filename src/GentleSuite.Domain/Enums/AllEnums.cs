namespace GentleSuite.Domain.Enums;

// === CRM ===
public enum CustomerStatus { Lead, Active, Inactive, Churned }

// === Onboarding ===
public enum OnboardingStatus { NotStarted, InProgress, Completed, Cancelled }
public enum OnboardingStepStatus { Pending, InProgress, Completed, Skipped }
public enum TaskItemStatus { Open, InProgress, Done, Blocked }

// === Quotes ===
public enum QuoteStatus { Draft = 0, Sent = 1, Viewed = 2, Accepted = 3, Rejected = 4, Expired = 5, Ordered = 6 }
public enum QuoteLineType { OneTime, RecurringMonthly }

// === Invoices (GoBD) ===
public enum InvoiceStatus { Draft, Final, Sent, Open, Paid, Overdue, Cancelled }
public enum InvoiceType { Standard, Recurring, CreditNote, Cancellation }
public enum VatRate { Standard19 = 19, Reduced7 = 7, Zero = 0 }
public enum TaxMode { Standard, SmallBusiness, ReverseCharge } // Kleinunternehmer / Reverse Charge

// === Expenses ===
public enum ExpenseStatus { Draft, Booked, Archived }
public enum RecurringInterval { Monthly, Quarterly, Yearly }

// === Projects ===
public enum ProjectStatus { Planning, InProgress, Review, Completed, OnHold, Cancelled }
public enum ProjectBoardTaskStatus { Todo, InProgress, Done }

// === Email ===
public enum EmailStatus { Queued, Sending, Sent, Failed }

// === Subscriptions ===
public enum SubscriptionStatus { Active, Paused, Cancelled, Expired, PendingConfirmation }
public enum BillingCycle { Monthly, Quarterly, Yearly }
public enum SubscriptionPlanCategory { Allgemein, DomainHosting, Wartungsvertrag, SLA, Software, Support, Sonstiges }

// === Reminders ===
public enum ReminderLevel { Level1, Level2, Level3 }

// === Notes ===
public enum NoteType { General, Credential, Technical, Financial, Internal }

// === Signatures ===
public enum SignatureStatus { Pending, Signed, Declined, Expired }

// === Accounting ===
public enum AccountType { Asset, Liability, Income, Expense, Equity }
public enum JournalEntryStatus { Draft, Posted }
public enum AccountingMode { EUR, DoublEntry } // EÜR vs Doppelte Buchführung
public enum BankTransactionStatus { Unmatched, Matched, Ignored }

// === Opportunities ===
public enum OpportunityStage { Qualification, Proposal, Negotiation, ClosedWon, ClosedLost }
public enum OpportunitySource { Inbound, Referral, Campaign, Direct, Other }

// === Support Tickets ===
public enum TicketPriority { Low, Medium, High, Critical }
public enum TicketStatus { Open, InProgress, Resolved, Closed }
public enum TicketCategory { Bug, Request, Complaint, General }

// === CRM Activities ===
public enum CrmActivityType { Call, Meeting, Task, Email }
public enum CrmActivityStatus { Open, Completed, Cancelled }
public enum CallDirection { Inbound, Outbound }

// === SLA ===
public enum SlaLevel { S0_Critical, S1_High, S2_Medium, S3_Low }

// === Roles ===
public static class Roles
{
    public const string Admin = "Admin";
    public const string ProjectManager = "ProjectManager";
    public const string Designer = "Designer";
    public const string Developer = "Developer";
    public const string Marketing = "Marketing";
    public const string Accounting = "Accounting";
}
