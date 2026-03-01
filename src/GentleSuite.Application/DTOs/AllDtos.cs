using GentleSuite.Domain.Enums;

namespace GentleSuite.Application.DTOs;

// === Auth ===
public record LoginRequest(string Email, string Password);
public record LoginResponse(string Token, string Email, string FullName, List<string> Roles, DateTimeOffset Expiry);

// === Pagination ===
public record PaginationParams(int Page = 1, int PageSize = 25, string? Search = null, string? SortBy = null, bool SortDesc = false);
public record PagedResult<T>(List<T> Items, int TotalCount, int Page, int PageSize)
{
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
}

// === Customer ===
public record CustomerListDto(Guid Id, string CompanyName, string? CustomerNumber, string? Industry, CustomerStatus Status, string? PrimaryContactName, string? PrimaryContactEmail, int ProjectCount, DateTimeOffset CreatedAt, string? Street = null, string? City = null, decimal TotalRevenue = 0);
public class CustomerDetailDto
{
    public Guid Id { get; set; }
    public string CompanyName { get; set; } = "";
    public string? CustomerNumber { get; set; }
    public string? Industry { get; set; }
    public string? Website { get; set; }
    public string? TaxId { get; set; }
    public string? VatId { get; set; }
    public CustomerStatus Status { get; set; }
    public bool ReminderStop { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<ContactDto> Contacts { get; set; } = new();
    public List<LocationDto> Locations { get; set; } = new();
    public List<Guid> DesiredServiceIds { get; set; } = new();
}
public record CreateCustomerRequest(string CompanyName, string? Industry, string? Website, string? TaxId, string? VatId, CreateContactRequest PrimaryContact, CreateLocationRequest? PrimaryLocation, List<Guid>? DesiredServiceIds);
public record UpdateCustomerRequest(string CompanyName, string? Industry, string? Website, string? TaxId, string? VatId, CustomerStatus Status);
public record DuplicateCheckRequest(string CompanyName, string? Email, string? Phone, Guid? ExcludeCustomerId = null);
public record DuplicateHitDto(Guid CustomerId, string CompanyName, string? ContactName, string? Email, string? Phone, string MatchType);
public record DuplicateCheckResultDto(bool HasDuplicate, List<DuplicateHitDto> Matches);
public record GdprExportDto(Guid CustomerId, string CompanyName, string? CustomerNumber, string? Industry, string? Website, string? TaxId, string? VatId, List<ContactDto> Contacts, List<LocationDto> Locations, List<CustomerNoteDto> Notes, DateTimeOffset ExportedAt);
public record GdprEraseRequest(string? Reason = null);
public record ContactDto(Guid Id, string FirstName, string LastName, string Email, string? Phone, string? Position, bool IsPrimary);
public record CreateContactRequest(string FirstName, string LastName, string Email, string? Phone, string? Position, bool IsPrimary = false);
public record LocationDto(Guid Id, string Label, string Street, string City, string ZipCode, string Country, bool IsPrimary);
public record CreateLocationRequest(string Label, string Street, string City, string ZipCode, string Country = "Deutschland", bool IsPrimary = true);

// === Notes ===
public record CustomerNoteDto(Guid Id, string Title, string Content, NoteType Type, bool IsPinned, DateTimeOffset CreatedAt, string? CreatedBy);
public record CreateNoteRequest(string Title, string Content, NoteType Type = NoteType.General, bool IsPinned = false);
public record UpdateNoteRequest(string Title, string Content, NoteType Type, bool IsPinned);

// === Onboarding ===
public class OnboardingWorkflowDto
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Guid? ProjectId { get; set; }
    public string TemplateName { get; set; } = string.Empty;
    public OnboardingStatus Status { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public List<OnboardingStepDto> Steps { get; set; } = new();
}
public record OnboardingStepDto(Guid Id, string Title, string? Description, int SortOrder, OnboardingStepStatus Status, DateTimeOffset? DueDate, List<TaskItemDto> Tasks);
public record TaskItemDto(Guid Id, string Title, string? Description, int SortOrder, TaskItemStatus Status, DateTimeOffset? DueDate, string? AssigneeId);
public record UpdateStepStatusRequest(OnboardingStepStatus Status);
public record UpdateTaskStatusRequest(TaskItemStatus Status);
public record OnboardingTemplateListDto(Guid Id, string Name, bool IsDefault, int StepCount);

// === Project ===
public record ProjectListDto(Guid Id, string Name, ProjectStatus Status, string CustomerName, DateTimeOffset? StartDate, DateTimeOffset? DueDate);
public record ProjectDetailDto(Guid Id, Guid CustomerId, string Name, string? Description, ProjectStatus Status, DateTimeOffset? StartDate, DateTimeOffset? DueDate, List<MilestoneDto> Milestones, List<ProjectCommentDto> Comments);
public record CreateProjectRequest(Guid CustomerId, string Name, string? Description, DateTimeOffset? StartDate, DateTimeOffset? DueDate, string? ManagerId, Guid OnboardingTemplateId);
public record MilestoneDto(Guid Id, string Title, int SortOrder, DateTimeOffset? DueDate, bool IsCompleted);
public record ProjectCommentDto(Guid Id, string Content, string? AuthorName, DateTimeOffset CreatedAt);
public record CreateMilestoneRequest(string Title, int SortOrder = 0, DateTimeOffset? DueDate = null, bool IsCompleted = false);
public record CreateProjectCommentRequest(string Content);
public record ProjectBoardTaskDto(Guid Id, Guid ProjectId, string Title, string? Description, ProjectBoardTaskStatus Status, int SortOrder, string? AssigneeName, DateTimeOffset? DueDate, DateTimeOffset CreatedAt, string? CreatedBy);
public record CreateProjectBoardTaskRequest(string Title, string? Description, string? AssigneeName, DateTimeOffset? DueDate = null, ProjectBoardTaskStatus Status = ProjectBoardTaskStatus.Todo);
public record UpdateProjectBoardTaskRequest(string Title, string? Description, string? AssigneeName, DateTimeOffset? DueDate = null);
public record MoveProjectBoardTaskRequest(ProjectBoardTaskStatus Status, int SortOrder);

// === Quote ===
public record QuoteListDto(Guid Id, string QuoteNumber, string CustomerName, QuoteStatus Status, SignatureStatus SignatureStatus, decimal GrandTotal, int Version, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);
public class QuoteDetailDto
{
    public Guid Id { get; set; }
    public string QuoteNumber { get; set; } = "";
    public Guid QuoteGroupId { get; set; }
    public bool IsCurrentVersion { get; set; }
    public Guid CustomerId { get; set; }
    public string CustomerName { get; set; } = "";
    public Guid? ContactId { get; set; }
    public int Version { get; set; }
    public QuoteStatus Status { get; set; }
    public string? Subject { get; set; }
    public string? IntroText { get; set; }
    public string? OutroText { get; set; }
    public string? Notes { get; set; }
    public string? InternalNotes { get; set; }
    public string? CustomerComment { get; set; }
    public decimal TaxRate { get; set; }
    public TaxMode TaxMode { get; set; }
    public decimal SubtotalOneTime { get; set; }
    public decimal SubtotalMonthly { get; set; }
    public decimal Subtotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal GrandTotal { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public SignatureStatus SignatureStatus { get; set; }
    public string? SignedByName { get; set; }
    public DateTimeOffset? SignedAt { get; set; }
    public string? PrimaryContactEmail { get; set; }
    public List<QuoteLineDto> Lines { get; set; } = new();
    public List<string>? LegalTextBlockKeys { get; set; }
}
public record QuoteVersionDto(Guid Id, Guid QuoteGroupId, string QuoteNumber, int Version, bool IsCurrentVersion, QuoteStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? SentAt);
public record QuoteLineDto(Guid Id, Guid? ServiceCatalogItemId, string Title, string? Description, decimal Quantity, decimal UnitPrice, decimal DiscountPercent, QuoteLineType LineType, int VatPercent, int SortOrder, decimal Total);
public record CreateQuoteRequest(Guid CustomerId, Guid? ContactId, string? Subject, string? IntroText, string? OutroText, string? Notes, decimal TaxRate = 19m, TaxMode TaxMode = TaxMode.Standard, List<CreateQuoteLineRequest>? Lines = null, Guid? TemplateId = null, List<string>? LegalTextBlockKeys = null);
public record CreateQuoteLineRequest(Guid? ServiceCatalogItemId, string Title, string? Description, decimal Quantity, decimal UnitPrice, decimal DiscountPercent = 0, QuoteLineType LineType = QuoteLineType.OneTime, int VatPercent = 19, int SortOrder = 0);
public record SendQuoteRequest(string? RecipientEmail = null, string? Message = null, int ExpirationDays = 30, bool RequireSignature = true);
public record ApprovalRequest(bool Accepted, string? Comment, string? SignatureData, string? SignedByName, string? SignedByEmail);
public record UpdateQuoteRequest(string? Subject, string? IntroText, string? OutroText, string? Notes, decimal? TaxRate, TaxMode? TaxMode);
public record QuoteTemplateDto(Guid Id, string Name, string? Description, List<QuoteTemplateLineDto> Lines);
public record QuoteTemplateLineDto(Guid Id, string Title, string? Description, decimal Quantity, decimal UnitPrice, QuoteLineType LineType, int SortOrder);

// === Invoice (GoBD) ===
public record InvoiceListDto(Guid Id, string InvoiceNumber, InvoiceType Type, string CustomerName, InvoiceStatus Status, decimal GrossTotal, DateTimeOffset InvoiceDate, DateTimeOffset DueDate, DateTimeOffset? PaidAt);
public class InvoiceDetailDto
{
    public Guid Id { get; set; }
    public string InvoiceNumber { get; set; } = "";
    public InvoiceType Type { get; set; }
    public Guid CustomerId { get; set; }
    public string CustomerName { get; set; } = "";
    public InvoiceStatus Status { get; set; }
    public TaxMode TaxMode { get; set; }
    public DateTimeOffset InvoiceDate { get; set; }
    public DateTimeOffset DueDate { get; set; }
    public DateTimeOffset ServiceDateFrom { get; set; }
    public DateTimeOffset ServiceDateTo { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public decimal NetTotal { get; set; }
    public decimal VatAmount { get; set; }
    public decimal GrossTotal { get; set; }
    public decimal VatRate { get; set; }
    public string? Subject { get; set; }
    public string? IntroText { get; set; }
    public string? OutroText { get; set; }
    public string? Notes { get; set; }
    public string? PaymentTerms { get; set; }
    public bool IsFinalized { get; set; }
    public bool ReminderStop { get; set; }
    public List<InvoiceLineDto> Lines { get; set; } = new();
    public List<InvoiceVatSummaryDto> VatSummary { get; set; } = new();
    public List<InvoicePaymentDto> Payments { get; set; } = new();
    public decimal PaidAmount { get; set; }
    public decimal OpenAmount { get; set; }
}
public record InvoiceLineDto(Guid Id, string Title, string? Description, string? Unit, decimal Quantity, decimal UnitPrice, int VatPercent, int SortOrder, decimal NetTotal, decimal VatAmount, decimal GrossTotal);
public record InvoicePaymentDto(Guid Id, decimal Amount, DateTimeOffset PaymentDate, string? PaymentMethod, string? Reference);
public record InvoiceVatSummaryDto(int VatPercent, decimal NetAmount, decimal VatAmount, decimal GrossAmount);
public record CreateInvoiceRequest(Guid CustomerId, Guid? QuoteId, string? Subject, string? IntroText, string? OutroText, string? Notes, TaxMode TaxMode = TaxMode.Standard, DateTimeOffset? ServiceDateFrom = null, DateTimeOffset? ServiceDateTo = null, int PaymentTermDays = 14, List<CreateInvoiceLineRequest>? Lines = null);
public record CreateInvoiceLineRequest(string Title, string? Description, string? Unit, decimal Quantity, decimal UnitPrice, int VatPercent = 19, int SortOrder = 0);
public record UpdateInvoiceLineRequest(string Title, string? Description, string? Unit, decimal Quantity, decimal UnitPrice, int VatPercent = 19, int SortOrder = 0);
public record UpdateInvoiceRequest(string? Subject, string? IntroText, string? OutroText, string? Notes, TaxMode TaxMode, DateTimeOffset InvoiceDate, DateTimeOffset DueDate, DateTimeOffset ServiceDateFrom, DateTimeOffset ServiceDateTo, List<UpdateInvoiceLineRequest> Lines);
public record FinalizeInvoiceRequest(bool SendEmail = true);
public record RecordPaymentRequest(decimal Amount, DateTimeOffset? PaymentDate, string? PaymentMethod, string? Reference, string? Note);
public record CreateCancellationRequest(string? Reason);

// === Expense ===
public record ExpenseListDto(Guid Id, string? ExpenseNumber, string? Supplier, string? CategoryName, decimal GrossAmount, int VatPercent, DateTimeOffset ExpenseDate, ExpenseStatus Status);
public class ExpenseDetailDto
{
    public Guid Id { get; set; }
    public string? ExpenseNumber { get; set; }
    public string? Supplier { get; set; }
    public string? SupplierTaxId { get; set; }
    public Guid? ExpenseCategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string? Description { get; set; }
    public decimal NetAmount { get; set; }
    public int VatPercent { get; set; }
    public decimal VatAmount { get; set; }
    public decimal GrossAmount { get; set; }
    public string? ReceiptPath { get; set; }
    public DateTimeOffset ExpenseDate { get; set; }
    public ExpenseStatus Status { get; set; }
    public string? AccountNumber { get; set; }
}
public record CreateExpenseRequest(string? Supplier, string? SupplierTaxId, Guid? ExpenseCategoryId, string? Description, decimal NetAmount, int VatPercent = 19, DateTimeOffset? ExpenseDate = null, string? AccountNumber = null);
public record ExpenseCategoryDto(Guid Id, string Name, string? AccountNumber, int SortOrder);

// === Journal ===
public record JournalEntryDto(Guid Id, string EntryNumber, DateTimeOffset BookingDate, string? Description, string? Reference, JournalEntryStatus Status, decimal TotalDebit, decimal TotalCredit, bool IsBalanced, List<JournalEntryLineDto> Lines);
public record JournalEntryLineDto(Guid Id, string AccountNumber, string AccountName, bool IsDebit, decimal Amount, string? Note);
public record CreateJournalEntryRequest(DateTimeOffset BookingDate, string? Description, string? Reference, List<CreateJournalEntryLineRequest> Lines);
public record CreateJournalEntryLineRequest(string AccountNumber, string AccountName, bool IsDebit, decimal Amount, string? Note);

// === Tax ===
public record VatPeriodDto(Guid Id, int Year, int Month, bool IsQuarterly, decimal OutputVat, decimal InputVat, decimal PayableTax, bool IsSubmitted);
public record VatReportDto(int Year, int Month, decimal OutputVat19, decimal OutputVat7, decimal TotalOutputVat, decimal InputVat, decimal PayableTax, List<VatReportLineDto> InvoiceLines, List<VatReportLineDto> ExpenseLines);
public record VatReportLineDto(string DocumentNumber, DateTimeOffset Date, string? Description, decimal NetAmount, decimal VatAmount, int VatPercent);

// === DATEV ===
public record DatevExportRequest(int Year, int Month, bool IncludeInvoices = true, bool IncludeExpenses = true, bool IncludeJournal = true);

// === Bank ===
public record BankTransactionDto(Guid Id, DateTimeOffset TransactionDate, string? Description, decimal Amount, string? Sender, string? Recipient, string? Reference, BankTransactionStatus Status, Guid? MatchedInvoiceId);
public record MatchBankTransactionRequest(Guid? InvoiceId, Guid? ExpenseId);

// === Subscription ===
public record SubscriptionPlanDto(Guid Id, string Name, string? Description, decimal MonthlyPrice, BillingCycle BillingCycle, WorkScopeRuleDto? WorkScopeRule, SupportPolicyDto? SupportPolicy);
public record WorkScopeRuleDto(string? FairUseDescription, List<string> IncludedItems, List<string> ExcludedItems, int? MaxHoursPerMonth);
public record SupportPolicyDto(string? S0ResponseTarget, string? S1ResponseTarget, string? S2ResponseTarget, string? S3ResponseTarget);
public record CustomerSubscriptionDto(Guid Id, Guid PlanId, string PlanName, Guid CustomerId, string? CustomerName, SubscriptionStatus Status, DateTimeOffset StartDate, DateTimeOffset NextBillingDate, decimal MonthlyPrice);
public record CreateSubscriptionRequest(Guid CustomerId, Guid PlanId, DateTimeOffset? StartDate);
public record UpdateSubscriptionStatusRequest(SubscriptionStatus Status, string? Reason = null);

// === Time Tracking ===
public record TimeEntryDto(Guid Id, Guid? ProjectId, string? ProjectName, Guid? CustomerId, string? CustomerName, string Description, DateTimeOffset Date, decimal Hours, bool IsBillable, bool IsInvoiced, decimal? HourlyRate, DateTimeOffset CreatedAt);
public record CreateTimeEntryRequest(Guid? ProjectId, Guid? CustomerId, string Description, DateTimeOffset Date, decimal Hours, bool IsBillable = true, decimal? HourlyRate = null);
public record TimeEntrySummaryDto(decimal TotalHours, decimal BillableHours, decimal NonBillableHours, decimal BillableAmount);

// === Service Catalog ===
public record ServiceCategoryDto(Guid Id, string Name, string? Description, int SortOrder, List<ServiceCatalogItemDto> Items);
public record ServiceCatalogItemDto(Guid Id, Guid CategoryId, string Name, string? Description, string? ShortCode, decimal? DefaultPrice, QuoteLineType DefaultLineType, int SortOrder);

// === Email ===
public record EmailLogDto(Guid Id, string To, string Subject, EmailStatus Status, string? Error, DateTimeOffset CreatedAt, DateTimeOffset? SentAt, string? TemplateKey);

// === Legal Text ===
public record LegalTextBlockDto(Guid Id, string Key, string Title, string Content, int SortOrder);
public record CreateLegalTextRequest(string Key, string Title, string Content, int SortOrder = 0);

// === Activity ===
public record ActivityLogDto(Guid Id, string EntityType, Guid EntityId, string Action, string? Description, string? UserName, DateTimeOffset CreatedAt);

// === Company Settings ===
public record CompanySettingsDto(Guid Id, string CompanyName, string? LegalName, string Street, string ZipCode, string City, string Country, string? Phone, string? Email, string? Website, string? TaxId, string? VatId, string? BankName, string? Iban, string? Bic, string? RegisterCourt, string? RegisterNumber, string? ManagingDirector, TaxMode DefaultTaxMode, AccountingMode AccountingMode, string? LogoPath, string? InvoiceIntroTemplate, string? InvoiceOutroTemplate, string? QuoteIntroTemplate, string? QuoteOutroTemplate, int InvoicePaymentTermDays, int QuoteValidityDays);
public record UpdateCompanySettingsRequest(string CompanyName, string? LegalName, string Street, string ZipCode, string City, string? Phone, string? Email, string? Website, string? TaxId, string? VatId, string? BankName, string? Iban, string? Bic, string? RegisterCourt, string? RegisterNumber, string? ManagingDirector, TaxMode DefaultTaxMode, AccountingMode AccountingMode, string? InvoiceIntroTemplate, string? InvoiceOutroTemplate, string? QuoteIntroTemplate, string? QuoteOutroTemplate, int InvoicePaymentTermDays, int QuoteValidityDays);
public record NumberRangeDto(string EntityType, int Year, string Prefix, int NextValue, int Padding);
public record UpdateNumberRangeRequest(string EntityType, int Year, string Prefix, int NextValue, int Padding);
public record ReminderSettingsDto(int Level1Days, int Level2Days, int Level3Days, decimal Level1Fee, decimal Level2Fee, decimal Level3Fee, decimal AnnualInterestPercent);
public record UpdateReminderSettingsRequest(int Level1Days, int Level2Days, int Level3Days, decimal Level1Fee, decimal Level2Fee, decimal Level3Fee, decimal AnnualInterestPercent);
public record UpdateReminderStopRequest(bool ReminderStop);

// === Dashboard ===
public record DashboardKpis(int OpenOnboardings, int OverdueTasks, int OpenQuotes, int OverdueInvoices, int ActiveCustomers, int ActiveSubscriptions, decimal MonthlyRecurringRevenue);
public record FinanceDashboardDto(decimal RevenueThisMonth, decimal RevenueLastMonth, decimal OpenReceivables, decimal OverdueReceivables, int OverdueInvoiceCount, decimal ExpensesThisMonth, decimal VatPayable, decimal TaxReserveRecommendation, List<MonthlyRevenueDto> RevenueChart);
public record MonthlyRevenueDto(int Year, int Month, decimal Revenue, decimal Expenses);

// === File ===
public record FileUploadDto(Guid Id, string FileName, string ContentType, long FileSize, DateTimeOffset CreatedAt);

// === Account ===
public record ChartOfAccountDto(Guid Id, string AccountNumber, string Name, AccountType Type, bool IsSystem, bool IsActive);

// === User Management ===
public record UserListDto(string Id, string Email, string FirstName, string LastName, bool IsActive, List<string> Roles);
public record CreateUserRequest(string Email, string FirstName, string LastName, string Password, List<string> Roles);
public record UpdateUserRequest(string FirstName, string LastName, bool IsActive, List<string> Roles);
public record ResetPasswordRequest(string NewPassword);

// === Expense Update ===
public record UpdateExpenseRequest(string? Supplier, string? SupplierTaxId, Guid? ExpenseCategoryId, string? Description, decimal NetAmount, int VatPercent, DateTimeOffset? ExpenseDate);

// === Expense Category CRUD ===
public record CreateExpenseCategoryRequest(string Name, string? AccountNumber, int SortOrder = 0);
public record UpdateExpenseCategoryRequest(string Name, string? AccountNumber, int SortOrder);

// === Project Update ===
public record UpdateProjectRequest(string Name, string? Description, ProjectStatus Status, DateTimeOffset? StartDate, DateTimeOffset? DueDate);

// === Service Catalog CRUD ===
public record CreateServiceCategoryRequest(string Name, string? Description, int SortOrder = 0);
public record UpdateServiceCategoryRequest(string Name, string? Description, int SortOrder);
public record CreateServiceItemRequest(Guid CategoryId, string Name, string? Description, string? ShortCode, decimal? DefaultPrice, QuoteLineType DefaultLineType = QuoteLineType.OneTime, int SortOrder = 0);
public record UpdateServiceItemRequest(string Name, string? Description, string? ShortCode, decimal? DefaultPrice, QuoteLineType DefaultLineType, int SortOrder);

// === Quote Template CRUD ===
public record CreateQuoteTemplateRequest(string Name, string? Description, List<CreateQuoteTemplateLineRequest> Lines);
public record CreateQuoteTemplateLineRequest(string Title, string? Description, decimal Quantity, decimal UnitPrice, QuoteLineType LineType, int SortOrder = 0);
public record UpdateQuoteTemplateRequest(string Name, string? Description, List<CreateQuoteTemplateLineRequest> Lines);

// === Onboarding Template CRUD ===
public record CreateOnboardingTemplateRequest(string Name, bool IsDefault, List<CreateOnboardingStepTemplateRequest> Steps);
public record CreateOnboardingStepTemplateRequest(string Title, string? Description, int SortOrder, int DefaultDurationDays = 7, List<CreateOnboardingTaskTemplateRequest>? Tasks = null);
public record CreateOnboardingTaskTemplateRequest(string Title, string? Description, int SortOrder = 0);
public record OnboardingTemplateDetailDto(Guid Id, string Name, bool IsDefault, List<OnboardingStepTemplateDto> Steps);
public record OnboardingStepTemplateDto(Guid Id, string Title, string? Description, int SortOrder, int DefaultDurationDays, List<OnboardingTaskTemplateDto> Tasks);
public record OnboardingTaskTemplateDto(Guid Id, string Title, string? Description, int SortOrder);

// === Email Template ===
public record EmailTemplateDto(Guid Id, string Key, string Subject, string Body, string? VariablesSchema, bool IsActive);
public record CreateEmailTemplateRequest(string Key, string Subject, string Body, string? VariablesSchema, bool IsActive = true);

// === Contact List ===
public record ContactListDto(Guid Id, string FirstName, string LastName, string Email, string? Phone, string? Position, bool IsPrimary, Guid CustomerId, string CompanyName);

// === Contact/Location Update ===
public record UpdateContactRequest(string FirstName, string LastName, string Email, string? Phone, string? Position, bool IsPrimary);
public record UpdateLocationRequest(string Label, string Street, string City, string ZipCode, string Country, bool IsPrimary);

// === Team Members ===
public record TeamMemberDto(Guid Id, string FirstName, string LastName, string FullName, string? Email, string? Phone, string? JobTitle, bool IsActive, string? AppUserId);
public record CreateTeamMemberRequest(string FirstName, string LastName, string? Email, string? Phone, string? JobTitle, string? AppUserId);
public record UpdateTeamMemberRequest(string FirstName, string LastName, string? Email, string? Phone, string? JobTitle, bool IsActive, string? AppUserId);

// === Products ===
public record ProductDto(Guid Id, string Name, string? Description, string Unit, decimal DefaultPrice, bool IsActive, List<TeamMemberDto> TeamMembers);
public record CreateProductRequest(string Name, string? Description, string Unit, decimal DefaultPrice);
public record UpdateProductRequest(string Name, string? Description, string Unit, decimal DefaultPrice, bool IsActive);

// === Price Lists ===
public record PriceListDto(Guid Id, Guid CustomerId, string Name, string? Description, bool IsActive, List<PriceListItemDto> Items);
public record PriceListItemDto(Guid Id, Guid ProductId, string ProductName, string Unit, decimal CustomPrice, Guid? TeamMemberId, string? TeamMemberName, string? Note, int SortOrder);
public record CreatePriceListRequest(Guid CustomerId, string Name, string? Description);
public record UpdatePriceListRequest(string Name, string? Description, bool IsActive);
public record UpsertPriceListItemRequest(Guid ProductId, Guid? TeamMemberId, decimal CustomPrice, string? Note, int SortOrder = 0);

// === Opportunities ===
public record OpportunityListDto(Guid Id, Guid CustomerId, string CustomerName, string Title, OpportunityStage Stage, int Probability, decimal ExpectedRevenue, DateTimeOffset? CloseDate, string? AssignedToName, DateTimeOffset CreatedAt);
public record OpportunityDetailDto(Guid Id, Guid CustomerId, string CustomerName, Guid? ContactId, string? ContactName, string Title, OpportunityStage Stage, int Probability, decimal ExpectedRevenue, DateTimeOffset? CloseDate, Guid? AssignedToTeamMemberId, string? AssignedToName, OpportunitySource? Source, string? LostReason, Guid? QuoteId, string? Notes, DateTimeOffset CreatedAt);
public record CreateOpportunityRequest(Guid CustomerId, Guid? ContactId, string Title, OpportunityStage Stage = OpportunityStage.Qualification, int Probability = 10, decimal ExpectedRevenue = 0, DateTimeOffset? CloseDate = null, Guid? AssignedToTeamMemberId = null, OpportunitySource? Source = null, string? Notes = null);
public record UpdateOpportunityRequest(Guid? ContactId, string Title, int Probability, decimal ExpectedRevenue, DateTimeOffset? CloseDate, Guid? AssignedToTeamMemberId, OpportunitySource? Source, string? LostReason, Guid? QuoteId, string? Notes);
public record UpdateOpportunityStageRequest(OpportunityStage Stage, string? LostReason = null);

// === Support Tickets ===
public record TicketListDto(Guid Id, Guid CustomerId, string CustomerName, string Title, TicketPriority Priority, TicketStatus Status, TicketCategory Category, string? AssignedToName, DateTimeOffset? SlaDueDate, DateTimeOffset CreatedAt, int CommentCount);
public record TicketDetailDto(Guid Id, Guid CustomerId, string CustomerName, Guid? SubscriptionId, string Title, string? Description, TicketPriority Priority, TicketStatus Status, TicketCategory Category, Guid? AssignedToTeamMemberId, string? AssignedToName, DateTimeOffset? SlaDueDate, DateTimeOffset? ResolvedAt, DateTimeOffset CreatedAt, List<TicketCommentDto> Comments);
public record TicketCommentDto(Guid Id, string Content, string? AuthorName, bool IsInternal, DateTimeOffset CreatedAt);
public record CreateTicketRequest(Guid CustomerId, Guid? SubscriptionId, string Title, string? Description, TicketPriority Priority = TicketPriority.Medium, TicketCategory Category = TicketCategory.General, Guid? AssignedToTeamMemberId = null);
public record UpdateTicketRequest(string Title, string? Description, TicketPriority Priority, TicketCategory Category, Guid? AssignedToTeamMemberId, Guid? SubscriptionId);
public record UpdateTicketStatusRequest(TicketStatus Status);
public record AddTicketCommentRequest(string Content, bool IsInternal = false);

// === CRM Activities ===
public record CrmActivityListDto(Guid Id, Guid? CustomerId, string? CustomerName, CrmActivityType Type, string Subject, CrmActivityStatus Status, DateTimeOffset? DueDate, DateTimeOffset? CompletedAt, string? AssignedToName, DateTimeOffset CreatedAt);
public record CrmActivityDetailDto(Guid Id, Guid? CustomerId, string? CustomerName, Guid? ProjectId, Guid? OpportunityId, Guid? TicketId, CrmActivityType Type, string Subject, string? Description, CrmActivityStatus Status, DateTimeOffset? DueDate, DateTimeOffset? CompletedAt, int? Duration, CallDirection? Direction, Guid? AssignedToTeamMemberId, string? AssignedToName, DateTimeOffset CreatedAt);
public record CreateCrmActivityRequest(Guid? CustomerId, Guid? ProjectId, Guid? OpportunityId, Guid? TicketId, CrmActivityType Type, string Subject, string? Description, DateTimeOffset? DueDate, int? Duration, CallDirection? Direction, Guid? AssignedToTeamMemberId);
public record UpdateCrmActivityRequest(string Subject, string? Description, DateTimeOffset? DueDate, int? Duration, CallDirection? Direction, Guid? AssignedToTeamMemberId);
public record CompleteCrmActivityRequest(DateTimeOffset? CompletedAt = null);
