using GentleSuite.Domain.Enums;

namespace GentleSuite.Domain.Entities;

public class Opportunity : BaseEntity
{
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public Guid? ContactId { get; set; }
    public Contact? Contact { get; set; }
    public string Title { get; set; } = string.Empty;
    public OpportunityStage Stage { get; set; } = OpportunityStage.Qualification;
    public int Probability { get; set; } = 10;
    public decimal ExpectedRevenue { get; set; }
    public DateTimeOffset? CloseDate { get; set; }
    public Guid? AssignedToTeamMemberId { get; set; }
    public TeamMember? AssignedTo { get; set; }
    public OpportunitySource? Source { get; set; }
    public string? LostReason { get; set; }
    public Guid? QuoteId { get; set; }
    public Quote? Quote { get; set; }
    public string? Notes { get; set; }
}

public class SupportTicket : BaseEntity
{
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public Guid? SubscriptionId { get; set; }
    public CustomerSubscription? Subscription { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public TicketPriority Priority { get; set; } = TicketPriority.Medium;
    public TicketStatus Status { get; set; } = TicketStatus.Open;
    public TicketCategory Category { get; set; } = TicketCategory.General;
    public Guid? AssignedToTeamMemberId { get; set; }
    public TeamMember? AssignedTo { get; set; }
    public DateTimeOffset? SlaDueDate { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public List<TicketComment> Comments { get; set; } = new();
}

public class TicketComment : BaseEntity
{
    public Guid TicketId { get; set; }
    public SupportTicket Ticket { get; set; } = null!;
    public string Content { get; set; } = string.Empty;
    public string? AuthorName { get; set; }
    public bool IsInternal { get; set; }
}

public class CrmActivity : BaseEntity
{
    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }
    public Guid? OpportunityId { get; set; }
    public Opportunity? Opportunity { get; set; }
    public Guid? TicketId { get; set; }
    public SupportTicket? Ticket { get; set; }
    public CrmActivityType Type { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string? Description { get; set; }
    public CrmActivityStatus Status { get; set; } = CrmActivityStatus.Open;
    public DateTimeOffset? DueDate { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int? Duration { get; set; }
    public CallDirection? Direction { get; set; }
    public Guid? AssignedToTeamMemberId { get; set; }
    public TeamMember? AssignedTo { get; set; }
}

public class Customer : BaseEntity
{
    public string CompanyName { get; set; } = string.Empty;
    public string? Industry { get; set; }
    public string? Website { get; set; }
    public string? TaxId { get; set; }         // Steuernummer
    public string? VatId { get; set; }          // USt-IdNr.
    public CustomerStatus Status { get; set; } = CustomerStatus.Lead;
    public string? CustomerNumber { get; set; } // Kundennummer (Debitor)
    public bool ReminderStop { get; set; }      // Mahnstopp auf Kundenebene
    public Guid? OnboardingToken { get; set; }          // Einmaliger Link-Token für Kunden-Intake-Formular
    public bool OnboardingIntakeDone { get; set; }      // true nach Formular-Ausfüllung durch Kunden

    public List<Contact> Contacts { get; set; } = new();
    public List<Location> Locations { get; set; } = new();
    public List<CustomerService> DesiredServices { get; set; } = new();
    public List<OnboardingWorkflow> OnboardingWorkflows { get; set; } = new();
    public List<Project> Projects { get; set; } = new();
    public List<Quote> Quotes { get; set; } = new();
    public List<Invoice> Invoices { get; set; } = new();
    public List<CustomerSubscription> Subscriptions { get; set; } = new();
    public List<CustomerNote> Notes { get; set; } = new();
    public List<ActivityLog> Activities { get; set; } = new();
    public List<Opportunity> Opportunities { get; set; } = new();
    public List<SupportTicket> SupportTickets { get; set; } = new();
    public List<CrmActivity> CrmActivities { get; set; } = new();
    public List<CustomerDocument> Documents { get; set; } = new();
}

public class Contact : BaseEntity
{
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Position { get; set; }
    public bool IsPrimary { get; set; }
    public string FullName => $"{FirstName} {LastName}";
}

public class Location : BaseEntity
{
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public string Label { get; set; } = string.Empty;
    public string Street { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
    public string Country { get; set; } = "Deutschland";
    public bool IsPrimary { get; set; }
}

public class CustomerNote : BaseEntity
{
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public NoteType Type { get; set; } = NoteType.General;
    public bool IsPinned { get; set; }
}

public class CustomerService : BaseEntity
{
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public Guid ServiceCatalogItemId { get; set; }
    public ServiceCatalogItem ServiceCatalogItem { get; set; } = null!;
}

public class CustomerDocument : BaseEntity
{
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public string StoragePath { get; set; } = "";
    public string? Notes { get; set; }
}

public class ActivityLog : BaseEntity
{
    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
}
