using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Interfaces;
using GentleSuite.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace GentleSuite.Infrastructure.Data;

public class AppDbContext : IdentityDbContext<AppUser>, IUnitOfWork
{
    private readonly ICurrentUserService? _currentUser;
    public AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUserService? currentUser = null) : base(options) { _currentUser = currentUser; }

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<CustomerNote> CustomerNotes => Set<CustomerNote>();
    public DbSet<CustomerService> CustomerServices => Set<CustomerService>();
    public DbSet<ActivityLog> ActivityLogs => Set<ActivityLog>();
    public DbSet<ServiceCategory> ServiceCategories => Set<ServiceCategory>();
    public DbSet<ServiceCatalogItem> ServiceCatalogItems => Set<ServiceCatalogItem>();
    public DbSet<OnboardingWorkflowTemplate> OnboardingWorkflowTemplates => Set<OnboardingWorkflowTemplate>();
    public DbSet<OnboardingStepTemplate> OnboardingStepTemplates => Set<OnboardingStepTemplate>();
    public DbSet<TaskItemTemplate> TaskItemTemplates => Set<TaskItemTemplate>();
    public DbSet<OnboardingWorkflow> OnboardingWorkflows => Set<OnboardingWorkflow>();
    public DbSet<OnboardingStep> OnboardingSteps => Set<OnboardingStep>();
    public DbSet<TaskItem> TaskItems => Set<TaskItem>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Milestone> Milestones => Set<Milestone>();
    public DbSet<ProjectComment> ProjectComments => Set<ProjectComment>();
    public DbSet<ProjectBoardTask> ProjectBoardTasks => Set<ProjectBoardTask>();
    public DbSet<ProjectTeamMember> ProjectTeamMembers => Set<ProjectTeamMember>();
    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<QuoteLine> QuoteLines => Set<QuoteLine>();
    public DbSet<QuoteTemplate> QuoteTemplates => Set<QuoteTemplate>();
    public DbSet<QuoteTemplateLine> QuoteTemplateLines => Set<QuoteTemplateLine>();
    public DbSet<LegalTextBlock> LegalTextBlocks => Set<LegalTextBlock>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<InvoicePayment> InvoicePayments => Set<InvoicePayment>();
    public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();
    public DbSet<SubscriptionPlanService> SubscriptionPlanServices => Set<SubscriptionPlanService>();
    public DbSet<CustomerSubscription> CustomerSubscriptions => Set<CustomerSubscription>();
    public DbSet<WorkScopeRule> WorkScopeRules => Set<WorkScopeRule>();
    public DbSet<SupportPolicy> SupportPolicies => Set<SupportPolicy>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<ExpenseCategory> ExpenseCategories => Set<ExpenseCategory>();
    public DbSet<ChartOfAccount> ChartOfAccounts => Set<ChartOfAccount>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalEntryLine> JournalEntryLines => Set<JournalEntryLine>();
    public DbSet<VatPeriod> VatPeriods => Set<VatPeriod>();
    public DbSet<BankTransaction> BankTransactions => Set<BankTransaction>();
    public DbSet<CompanySettings> CompanySettings => Set<CompanySettings>();
    public DbSet<ReminderSettings> ReminderSettings => Set<ReminderSettings>();
    public DbSet<EmailTemplate> EmailTemplates => Set<EmailTemplate>();
    public DbSet<EmailLog> EmailLogs => Set<EmailLog>();
    public DbSet<FileUpload> FileUploads => Set<FileUpload>();
    public DbSet<TimeEntry> TimeEntries => Set<TimeEntry>();
    public DbSet<NumberSequence> NumberSequences => Set<NumberSequence>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<PriceList> PriceLists => Set<PriceList>();
    public DbSet<PriceListItem> PriceListItems => Set<PriceListItem>();
    public DbSet<Opportunity> Opportunities => Set<Opportunity>();
    public DbSet<SupportTicket> SupportTickets => Set<SupportTicket>();
    public DbSet<TicketComment> TicketComments => Set<TicketComment>();
    public DbSet<CrmActivity> CrmActivities => Set<CrmActivity>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        base.OnModelCreating(mb);
        // Soft delete filter
       foreach (var et in mb.Model.GetEntityTypes().Where(e => typeof(BaseEntity).IsAssignableFrom(e.ClrType)))
{
    var parameter = Expression.Parameter(et.ClrType, "e");
    var isDeletedProp = Expression.Property(parameter, nameof(BaseEntity.IsDeleted));
    var body = Expression.Equal(isDeletedProp, Expression.Constant(false));
    var lambda = Expression.Lambda(body, parameter);

    mb.Entity(et.ClrType).HasQueryFilter(lambda);
}
        // Relationships
        mb.Entity<Contact>().HasOne(x => x.Customer).WithMany(x => x.Contacts).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<Location>().HasOne(x => x.Customer).WithMany(x => x.Locations).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<CustomerNote>().HasOne(x => x.Customer).WithMany(x => x.Notes).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<CustomerService>().HasOne(x => x.Customer).WithMany(x => x.DesiredServices).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<ActivityLog>().HasOne(x => x.Customer).WithMany(x => x.Activities).OnDelete(DeleteBehavior.SetNull);
        mb.Entity<OnboardingStepTemplate>().HasOne(x => x.WorkflowTemplate).WithMany(x => x.Steps).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<TaskItemTemplate>().HasOne(x => x.StepTemplate).WithMany(x => x.TaskTemplates).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<OnboardingWorkflow>().HasOne(x => x.Customer).WithMany(x => x.OnboardingWorkflows).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<OnboardingWorkflow>().HasOne(x => x.Project).WithMany(x => x.OnboardingWorkflows).OnDelete(DeleteBehavior.SetNull);
        mb.Entity<OnboardingWorkflow>().HasOne(x => x.Template).WithMany().OnDelete(DeleteBehavior.Restrict);
        mb.Entity<OnboardingStep>().HasOne(x => x.Workflow).WithMany(x => x.Steps).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<TaskItem>().HasOne(x => x.Step).WithMany(x => x.Tasks).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<Project>().HasOne(x => x.Customer).WithMany(x => x.Projects).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<Quote>().HasOne(x => x.Customer).WithMany(x => x.Quotes).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<Quote>().HasIndex(x => x.QuoteNumber).IsUnique();
        mb.Entity<Quote>().HasIndex(x => new { x.QuoteGroupId, x.Version }).IsUnique();
        mb.Entity<Quote>().HasIndex(x => new { x.QuoteGroupId, x.IsCurrentVersion }).HasFilter("[IsCurrentVersion] = 1");
        mb.Entity<QuoteLine>().HasOne(x => x.Quote).WithMany(x => x.Lines).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<QuoteTemplateLine>().HasOne(x => x.QuoteTemplate).WithMany(x => x.Lines).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<Invoice>().HasOne(x => x.Customer).WithMany(x => x.Invoices).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<Invoice>().HasIndex(x => x.InvoiceNumber).IsUnique();
        mb.Entity<Invoice>()
            .HasIndex(x => new { x.SubscriptionId, x.BillingPeriodStart, x.BillingPeriodEnd })
            .IsUnique()
            .HasFilter("[SubscriptionId] IS NOT NULL");
        mb.Entity<InvoiceLine>().HasOne(x => x.Invoice).WithMany(x => x.Lines).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<InvoicePayment>().HasOne(x => x.Invoice).WithMany(x => x.Payments).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<NumberSequence>().HasIndex(x => new { x.EntityType, x.Year }).IsUnique();
        mb.Entity<ServiceCatalogItem>().HasOne(x => x.Category).WithMany(x => x.Items).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<SubscriptionPlanService>().HasOne(x => x.Plan).WithMany(x => x.IncludedServices).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<WorkScopeRule>().HasOne(x => x.Plan).WithOne(x => x.WorkScopeRule).HasForeignKey<WorkScopeRule>(x => x.PlanId);
        mb.Entity<SupportPolicy>().HasOne(x => x.Plan).WithOne(x => x.SupportPolicy).HasForeignKey<SupportPolicy>(x => x.PlanId);
        mb.Entity<CustomerSubscription>().HasOne(x => x.Customer).WithMany(x => x.Subscriptions).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<JournalEntryLine>().HasOne(x => x.JournalEntry).WithMany(x => x.Lines).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<Milestone>().HasOne(x => x.Project).WithMany(x => x.Milestones).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<ProjectComment>().HasOne(x => x.Project).WithMany(x => x.Comments).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<ProjectBoardTask>().HasOne(x => x.Project).WithMany(x => x.BoardTasks).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<ProjectBoardTask>().HasIndex(x => new { x.ProjectId, x.Status, x.SortOrder });
        mb.Entity<ProjectTeamMember>().HasKey(x => new { x.ProjectId, x.TeamMemberId });
        mb.Entity<ProjectTeamMember>().HasOne(x => x.Project).WithMany(x => x.TeamMembers).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<ProjectTeamMember>().HasOne(x => x.TeamMember).WithMany().OnDelete(DeleteBehavior.Cascade);
        mb.Entity<TimeEntry>().HasOne(x => x.Project).WithMany().OnDelete(DeleteBehavior.SetNull);
        mb.Entity<TimeEntry>().HasOne(x => x.Customer).WithMany().OnDelete(DeleteBehavior.SetNull);

        // ProductTeamMember (join table, no owned Id)
        mb.Entity<ProductTeamMember>().HasKey(x => new { x.ProductId, x.TeamMemberId });
        mb.Entity<ProductTeamMember>().HasOne(x => x.Product).WithMany(x => x.TeamMembers).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<ProductTeamMember>().HasOne(x => x.TeamMember).WithMany(x => x.ProductAssignments).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<PriceList>().HasOne(x => x.Customer).WithMany().OnDelete(DeleteBehavior.Cascade);
        mb.Entity<PriceListItem>().HasOne(x => x.PriceList).WithMany(x => x.Items).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<PriceListItem>().HasOne(x => x.Product).WithMany(x => x.PriceListItems).OnDelete(DeleteBehavior.Restrict);
        mb.Entity<PriceListItem>().HasOne(x => x.TeamMember).WithMany().OnDelete(DeleteBehavior.SetNull);
        mb.Entity<Opportunity>().HasOne(x => x.Customer).WithMany(x => x.Opportunities).HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<Opportunity>().HasOne(x => x.Contact).WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.SetNull);
        mb.Entity<Opportunity>().HasOne(x => x.AssignedTo).WithMany().HasForeignKey(x => x.AssignedToTeamMemberId).OnDelete(DeleteBehavior.SetNull);
        mb.Entity<Opportunity>().HasOne(x => x.Quote).WithMany().HasForeignKey(x => x.QuoteId).OnDelete(DeleteBehavior.SetNull);
        mb.Entity<SupportTicket>().HasOne(x => x.Customer).WithMany(x => x.SupportTickets).HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<SupportTicket>().HasOne(x => x.AssignedTo).WithMany().HasForeignKey(x => x.AssignedToTeamMemberId).OnDelete(DeleteBehavior.SetNull);
        mb.Entity<SupportTicket>().HasOne(x => x.Subscription).WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.SetNull);
        mb.Entity<TicketComment>().HasOne(x => x.Ticket).WithMany(x => x.Comments).HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<CrmActivity>().HasOne(x => x.Customer).WithMany(x => x.CrmActivities).HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.SetNull);
        mb.Entity<CrmActivity>().HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.SetNull);
        mb.Entity<CrmActivity>().HasOne(x => x.Opportunity).WithMany().HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.SetNull);
        mb.Entity<CrmActivity>().HasOne(x => x.Ticket).WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.SetNull);
        mb.Entity<CrmActivity>().HasOne(x => x.AssignedTo).WithMany().HasForeignKey(x => x.AssignedToTeamMemberId).OnDelete(DeleteBehavior.SetNull);

        // Decimal precision
        foreach (var prop in mb.Model.GetEntityTypes().SelectMany(t => t.GetProperties()).Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))
        { prop.SetPrecision(18); prop.SetScale(2); }
    }

    public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            if (entry.State == EntityState.Added) { entry.Entity.CreatedAt = DateTimeOffset.UtcNow; entry.Entity.CreatedBy = _currentUser?.UserId; }
            if (entry.State == EntityState.Modified) { entry.Entity.UpdatedAt = DateTimeOffset.UtcNow; entry.Entity.UpdatedBy = _currentUser?.UserId; }
        }
        return await base.SaveChangesAsync(ct);
    }
}
