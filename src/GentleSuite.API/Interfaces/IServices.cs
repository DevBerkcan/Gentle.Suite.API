using GentleSuite.Application.DTOs;
using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;

namespace GentleSuite.Application.Interfaces;

public interface ICustomerService
{
    Task<PagedResult<CustomerListDto>> GetCustomersAsync(PaginationParams p, CustomerStatus? status = null, Guid? serviceId = null, CancellationToken ct = default);
    Task<CustomerDetailDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<CustomerDetailDto> CreateAsync(CreateCustomerRequest req, CancellationToken ct = default);
    Task<CustomerDetailDto> UpdateAsync(Guid id, UpdateCustomerRequest req, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<ContactDto> AddContactAsync(Guid customerId, CreateContactRequest req, CancellationToken ct = default);
    Task<ContactDto> UpdateContactAsync(Guid customerId, Guid contactId, UpdateContactRequest req, CancellationToken ct = default);
    Task DeleteContactAsync(Guid customerId, Guid contactId, CancellationToken ct = default);
    Task<LocationDto> AddLocationAsync(Guid customerId, CreateLocationRequest req, CancellationToken ct = default);
    Task<LocationDto> UpdateLocationAsync(Guid customerId, Guid locationId, UpdateLocationRequest req, CancellationToken ct = default);
    Task DeleteLocationAsync(Guid customerId, Guid locationId, CancellationToken ct = default);
    Task<DuplicateCheckResultDto> CheckDuplicateAsync(DuplicateCheckRequest req, CancellationToken ct = default);
    Task<GdprExportDto> ExportGdprAsync(Guid customerId, CancellationToken ct = default);
    Task EraseGdprAsync(Guid customerId, GdprEraseRequest req, CancellationToken ct = default);
    Task<CustomerDetailDto> CreateQuickAsync(CreateCustomerQuickRequest req, CancellationToken ct = default);
    Task<CustomerIntakeInfoDto?> GetIntakeInfoAsync(Guid token, CancellationToken ct = default);
    Task CompleteIntakeAsync(Guid token, CustomerIntakeSubmitRequest req, CancellationToken ct = default);
    Task ResendIntakeAsync(Guid customerId, CancellationToken ct = default);
}

public interface ICustomerNoteService
{
    Task<List<CustomerNoteDto>> GetNotesAsync(Guid customerId, CancellationToken ct = default);
    Task<CustomerNoteDto> CreateAsync(Guid customerId, CreateNoteRequest req, CancellationToken ct = default);
    Task<CustomerNoteDto> UpdateAsync(Guid customerId, Guid noteId, UpdateNoteRequest req, CancellationToken ct = default);
    Task DeleteAsync(Guid customerId, Guid noteId, CancellationToken ct = default);
}

public interface IOnboardingService
{
    Task<List<OnboardingWorkflowDto>> GetByCustomerAsync(Guid customerId, CancellationToken ct = default);
    Task<List<OnboardingWorkflowDto>> GetByProjectAsync(Guid projectId, CancellationToken ct = default);
    Task<List<OnboardingTemplateListDto>> GetTemplatesAsync(CancellationToken ct = default);
    Task<OnboardingTemplateDetailDto> GetTemplateByIdAsync(Guid id, CancellationToken ct = default);
    Task<OnboardingTemplateDetailDto> CreateTemplateAsync(CreateOnboardingTemplateRequest req, CancellationToken ct = default);
    Task<OnboardingTemplateDetailDto> UpdateTemplateAsync(Guid id, CreateOnboardingTemplateRequest req, CancellationToken ct = default);
    Task DeleteTemplateAsync(Guid id, CancellationToken ct = default);
    Task<OnboardingWorkflowDto> StartWorkflowAsync(Guid customerId, Guid? templateId = null, Guid? projectId = null, CancellationToken ct = default);
    Task UpdateStepStatusAsync(Guid stepId, UpdateStepStatusRequest req, CancellationToken ct = default);
    Task UpdateTaskStatusAsync(Guid taskId, UpdateTaskStatusRequest req, CancellationToken ct = default);
}

public interface IQuoteService
{
    Task<PagedResult<QuoteListDto>> GetQuotesAsync(PaginationParams p, QuoteStatus? status = null, Guid? customerId = null, CancellationToken ct = default);
    Task<QuoteDetailDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<QuoteDetailDto> CreateAsync(CreateQuoteRequest req, CancellationToken ct = default);
    Task<QuoteDetailDto> CreateFromTemplateAsync(Guid customerId, Guid templateId, CancellationToken ct = default);
    Task<QuoteDetailDto> UpdateLinesAsync(Guid id, List<CreateQuoteLineRequest> lines, CancellationToken ct = default);
    Task SendAsync(Guid id, SendQuoteRequest req, CancellationToken ct = default);
    Task<QuoteDetailDto?> GetByApprovalTokenAsync(string token, CancellationToken ct = default);
    Task ProcessApprovalAsync(string token, ApprovalRequest req, string? ipAddress = null, CancellationToken ct = default);
    Task<byte[]> GeneratePdfAsync(Guid id, CancellationToken ct = default);
    Task<List<QuoteTemplateDto>> GetTemplatesAsync(CancellationToken ct = default);
    Task<QuoteTemplateDto> CreateTemplateAsync(CreateQuoteTemplateRequest req, CancellationToken ct = default);
    Task<QuoteTemplateDto> UpdateTemplateAsync(Guid id, UpdateQuoteTemplateRequest req, CancellationToken ct = default);
    Task DeleteTemplateAsync(Guid id, CancellationToken ct = default);
    Task<QuoteDetailDto> MarkAsOrderedAsync(Guid quoteId, CancellationToken ct = default);
    Task<InvoiceDetailDto> ConvertToInvoiceAsync(Guid quoteId, CancellationToken ct = default);
    Task<QuoteDetailDto> UpdateAsync(Guid id, UpdateQuoteRequest req, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<QuoteDetailDto> DuplicateAsync(Guid id, CancellationToken ct = default);
    Task<QuoteDetailDto> CreateNewVersionAsync(Guid id, CancellationToken ct = default);
    Task<List<QuoteVersionDto>> GetVersionsAsync(Guid id, CancellationToken ct = default);
    Task<byte[]> GeneratePdfByTokenAsync(string token, CancellationToken ct = default);

}

public interface IInvoiceService
{
    Task<PagedResult<InvoiceListDto>> GetInvoicesAsync(PaginationParams p, InvoiceStatus? status = null, Guid? customerId = null, CancellationToken ct = default);
    Task<InvoiceDetailDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<InvoiceDetailDto> CreateAsync(CreateInvoiceRequest req, CancellationToken ct = default);
    Task<InvoiceDetailDto> UpdateAsync(Guid id, UpdateInvoiceRequest req, CancellationToken ct = default);
    Task<InvoiceDetailDto> FinalizeAsync(Guid id, FinalizeInvoiceRequest req, CancellationToken ct = default);
    Task<InvoiceDetailDto> RecordPaymentAsync(Guid id, RecordPaymentRequest req, CancellationToken ct = default);
    Task<InvoiceDetailDto> CreateCancellationAsync(Guid id, CreateCancellationRequest req, CancellationToken ct = default);
    Task<byte[]> GeneratePdfAsync(Guid id, CancellationToken ct = default);
    Task<byte[]> GenerateXRechnungXmlAsync(Guid id, CancellationToken ct = default);
    Task SendAsync(Guid id, CancellationToken ct = default);
    Task<InvoiceDetailDto> CreateFromTimeEntriesAsync(CreateInvoiceFromTimeEntriesRequest req, CancellationToken ct = default);
    Task SendReminderAsync(Guid id, CancellationToken ct = default);
}

public interface IExpenseService
{
    Task<PagedResult<ExpenseListDto>> GetExpensesAsync(PaginationParams p, Guid? categoryId = null, CancellationToken ct = default);
    Task<ExpenseDetailDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<ExpenseDetailDto> CreateAsync(CreateExpenseRequest req, CancellationToken ct = default);
    Task<ExpenseDetailDto> UpdateAsync(Guid id, UpdateExpenseRequest req, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task BookAsync(Guid id, CancellationToken ct = default);
    Task<List<ExpenseCategoryDto>> GetCategoriesAsync(CancellationToken ct = default);
    Task<ExpenseCategoryDto> CreateCategoryAsync(CreateExpenseCategoryRequest req, CancellationToken ct = default);
    Task<ExpenseCategoryDto> UpdateCategoryAsync(Guid id, UpdateExpenseCategoryRequest req, CancellationToken ct = default);
    Task DeleteCategoryAsync(Guid id, CancellationToken ct = default);
    Task UploadReceiptAsync(Guid id, Stream stream, string fileName, string contentType, CancellationToken ct = default);
    Task<(Stream Stream, string ContentType, string FileName)?> DownloadReceiptAsync(Guid id, CancellationToken ct = default);
}

public interface IJournalService
{
    Task<PagedResult<JournalEntryDto>> GetEntriesAsync(PaginationParams p, CancellationToken ct = default);
    Task<JournalEntryDto> CreateAsync(CreateJournalEntryRequest req, CancellationToken ct = default);
    Task PostAsync(Guid id, CancellationToken ct = default);
}

public interface IVatService
{
    Task<VatReportDto> GetVatReportAsync(int year, int month, CancellationToken ct = default);
    Task<VatPeriodDto> SubmitVatPeriodAsync(int year, int month, CancellationToken ct = default);
    Task<byte[]> ExportDatevAsync(DatevExportRequest req, CancellationToken ct = default);
    Task<byte[]> GenerateElsterXmlAsync(int year, int month, CancellationToken ct = default);
}

public interface IExportService
{
    Task<ExportYearStatsDto> GetYearStatsAsync(int year, CancellationToken ct = default);
    Task<byte[]> ExportYearZipAsync(int year, bool includeInvoices, bool includeExpenses, CancellationToken ct = default);
}

public interface IProjectService
{
    Task<PagedResult<ProjectListDto>> GetProjectsAsync(PaginationParams p, ProjectStatus? status = null, Guid? customerId = null, CancellationToken ct = default);
    Task<ProjectDetailDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<ProjectDetailDto> CreateAsync(CreateProjectRequest req, CancellationToken ct = default);
    Task<ProjectDetailDto> UpdateAsync(Guid id, UpdateProjectRequest req, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<MilestoneDto> AddMilestoneAsync(Guid projectId, CreateMilestoneRequest req, CancellationToken ct = default);
    Task<MilestoneDto> UpdateMilestoneAsync(Guid milestoneId, CreateMilestoneRequest req, CancellationToken ct = default);
    Task DeleteMilestoneAsync(Guid milestoneId, CancellationToken ct = default);
    Task<ProjectCommentDto> AddCommentAsync(Guid projectId, CreateProjectCommentRequest req, CancellationToken ct = default);
    Task<List<ProjectBoardTaskDto>> GetBoardTasksAsync(Guid projectId, CancellationToken ct = default);
    Task<ProjectBoardTaskDto> CreateBoardTaskAsync(Guid projectId, CreateProjectBoardTaskRequest req, CancellationToken ct = default);
    Task<ProjectBoardTaskDto> UpdateBoardTaskAsync(Guid projectId, Guid taskId, UpdateProjectBoardTaskRequest req, CancellationToken ct = default);
    Task<ProjectBoardTaskDto> MoveBoardTaskAsync(Guid projectId, Guid taskId, MoveProjectBoardTaskRequest req, CancellationToken ct = default);
    Task DeleteBoardTaskAsync(Guid projectId, Guid taskId, CancellationToken ct = default);
    Task<List<TeamMemberDto>> GetMembersAsync(Guid projectId, CancellationToken ct = default);
    Task AddMemberAsync(Guid projectId, Guid teamMemberId, CancellationToken ct = default);
    Task RemoveMemberAsync(Guid projectId, Guid teamMemberId, CancellationToken ct = default);
}

public interface ISubscriptionService
{
    Task<List<SubscriptionPlanDto>> GetPlansAsync(CancellationToken ct = default);
    Task<SubscriptionPlanDto> CreatePlanAsync(CreatePlanRequest req, CancellationToken ct = default);
    Task<SubscriptionPlanDto> UpdatePlanAsync(Guid id, UpdatePlanRequest req, CancellationToken ct = default);
    Task DeletePlanAsync(Guid id, CancellationToken ct = default);
    Task<List<CustomerSubscriptionDto>> GetAllAsync(CancellationToken ct = default);
    Task<List<CustomerSubscriptionDto>> GetCustomerSubscriptionsAsync(Guid customerId, CancellationToken ct = default);
    Task<CustomerSubscriptionDto> CreateAsync(CreateSubscriptionRequest req, CancellationToken ct = default);
    Task UpdateStatusAsync(Guid subscriptionId, UpdateSubscriptionStatusRequest req, CancellationToken ct = default);
    Task ConfirmAsync(Guid subscriptionId, CancellationToken ct = default);
    Task<List<SubscriptionInvoiceDto>> GetInvoicesAsync(Guid subscriptionId, CancellationToken ct = default);
}

public interface ITimeTrackingService
{
    Task<List<TimeEntryDto>> GetEntriesAsync(DateTimeOffset? from, DateTimeOffset? to, Guid? projectId, Guid? customerId, CancellationToken ct = default);
    Task<TimeEntryDto> CreateAsync(CreateTimeEntryRequest req, CancellationToken ct = default);
    Task<TimeEntryDto> UpdateAsync(Guid id, CreateTimeEntryRequest req, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<TimeEntrySummaryDto> GetSummaryAsync(DateTimeOffset from, DateTimeOffset to, Guid? projectId, CancellationToken ct = default);
}

public interface IServiceCatalogService
{
    Task<List<ServiceCategoryDto>> GetCategoriesAsync(CancellationToken ct = default);
    Task<ServiceCategoryDto> CreateCategoryAsync(CreateServiceCategoryRequest req, CancellationToken ct = default);
    Task<ServiceCategoryDto> UpdateCategoryAsync(Guid id, UpdateServiceCategoryRequest req, CancellationToken ct = default);
    Task DeleteCategoryAsync(Guid id, CancellationToken ct = default);
    Task<ServiceCatalogItemDto> CreateItemAsync(CreateServiceItemRequest req, CancellationToken ct = default);
    Task<ServiceCatalogItemDto> UpdateItemAsync(Guid id, UpdateServiceItemRequest req, CancellationToken ct = default);
    Task DeleteItemAsync(Guid id, CancellationToken ct = default);
}

public interface IDashboardService
{
    Task<DashboardKpis> GetKpisAsync(CancellationToken ct = default);
    Task<FinanceDashboardDto> GetFinanceDashboardAsync(CancellationToken ct = default);
}

public interface ICompanySettingsService
{
    Task<CompanySettingsDto> GetAsync(CancellationToken ct = default);
    Task<CompanySettingsDto> UpdateAsync(UpdateCompanySettingsRequest req, CancellationToken ct = default);
}

public interface IActivityLogService
{
    Task<PagedResult<ActivityLogDto>> GetByCustomerAsync(Guid customerId, PaginationParams p, CancellationToken ct = default);
    Task LogAsync(Guid? customerId, string entityType, Guid entityId, string action, string? description = null, CancellationToken ct = default);
}

public interface ILegalTextService
{
    Task<List<LegalTextBlockDto>> GetAllAsync(CancellationToken ct = default);
    Task<LegalTextBlockDto> CreateAsync(CreateLegalTextRequest req, CancellationToken ct = default);
    Task<LegalTextBlockDto> UpdateAsync(Guid id, CreateLegalTextRequest req, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
public interface IEmailLogService { Task<PagedResult<EmailLogDto>> GetLogsAsync(PaginationParams p, Guid? customerId = null, CancellationToken ct = default); }

public interface IBankTransactionService
{
    Task<PagedResult<BankTransactionDto>> GetTransactionsAsync(PaginationParams p, CancellationToken ct = default);
    Task MatchAsync(Guid id, MatchBankTransactionRequest req, CancellationToken ct = default);
}

public interface ICustomerDocumentService
{
    Task<List<CustomerDocumentDto>> GetDocumentsAsync(Guid customerId, CancellationToken ct = default);
    Task<CustomerDocumentDto> UploadAsync(Guid customerId, Stream stream, string fileName, string contentType, long fileSize, string? notes, CancellationToken ct = default);
    Task<(Stream Stream, string FileName, string ContentType)> DownloadAsync(Guid customerId, Guid docId, CancellationToken ct = default);
    Task DeleteAsync(Guid customerId, Guid docId, CancellationToken ct = default);
}

public interface IIntegrationService
{
    Task<IntegrationSettingsDto> GetAsync(CancellationToken ct = default);
    Task UpdatePayPalAsync(UpdatePayPalRequest req, CancellationToken ct = default);
    Task<string> SetupBankAsync(SetupBankRequest req, CancellationToken ct = default);
    Task ConfirmBankAsync(ConfirmBankRequest req, CancellationToken ct = default);
    Task DisconnectPayPalAsync(CancellationToken ct = default);
    Task DisconnectBankAsync(CancellationToken ct = default);
    Task SyncPayPalAsync(CancellationToken ct = default);
    Task SyncBankAsync(CancellationToken ct = default);
}

public interface IChartOfAccountService
{
    Task<List<ChartOfAccountDto>> GetAllAsync(CancellationToken ct = default);
}

public interface IEmailTemplateService
{
    Task<List<EmailTemplateDto>> GetAllAsync(CancellationToken ct = default);
    Task<EmailTemplateDto> CreateAsync(CreateEmailTemplateRequest req, CancellationToken ct = default);
    Task<EmailTemplateDto> UpdateAsync(Guid id, CreateEmailTemplateRequest req, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface IContactListService
{
    Task<PagedResult<ContactListDto>> GetAllContactsAsync(PaginationParams p, CancellationToken ct = default);
}

public interface IUserService
{
    Task<List<UserListDto>> GetAllAsync(CancellationToken ct = default);
    Task<UserListDto> CreateAsync(CreateUserRequest req, CancellationToken ct = default);
    Task<UserListDto> UpdateAsync(string id, UpdateUserRequest req, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task ResetPasswordAsync(string id, ResetPasswordRequest req, CancellationToken ct = default);
}

public interface ITeamMemberService
{
    Task<List<TeamMemberDto>> GetAllAsync(CancellationToken ct = default);
    Task<TeamMemberDto> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<TeamMemberDto> CreateAsync(CreateTeamMemberRequest req, CancellationToken ct = default);
    Task<TeamMemberDto> UpdateAsync(Guid id, UpdateTeamMemberRequest req, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface IProductService
{
    Task<List<ProductDto>> GetAllAsync(CancellationToken ct = default);
    Task<ProductDto> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<ProductDto> CreateAsync(CreateProductRequest req, CancellationToken ct = default);
    Task<ProductDto> UpdateAsync(Guid id, UpdateProductRequest req, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task AddTeamMemberAsync(Guid productId, Guid teamMemberId, CancellationToken ct = default);
    Task RemoveTeamMemberAsync(Guid productId, Guid teamMemberId, CancellationToken ct = default);
}

public interface IPriceListService
{
    Task<List<PriceListDto>> GetByCustomerAsync(Guid customerId, CancellationToken ct = default);
    Task<List<PriceListDto>> GetTemplatesAsync(CancellationToken ct = default);
    Task<PriceListDto> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<PriceListDto> CreateAsync(CreatePriceListRequest req, CancellationToken ct = default);
    Task<PriceListDto> UpdateAsync(Guid id, UpdatePriceListRequest req, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<PriceListDto> CloneToCustomerAsync(ClonePriceListRequest req, CancellationToken ct = default);
    Task<PriceListItemDto> AddItemAsync(Guid priceListId, UpsertPriceListItemRequest req, CancellationToken ct = default);
    Task<PriceListItemDto> UpdateItemAsync(Guid priceListId, Guid itemId, UpsertPriceListItemRequest req, CancellationToken ct = default);
    Task RemoveItemAsync(Guid priceListId, Guid itemId, CancellationToken ct = default);
}

public interface IOpportunityService
{
    Task<List<OpportunityListDto>> GetAllAsync(Guid? customerId = null, OpportunityStage? stage = null, CancellationToken ct = default);
    Task<OpportunityDetailDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<OpportunityDetailDto> CreateAsync(CreateOpportunityRequest req, CancellationToken ct = default);
    Task<OpportunityDetailDto> UpdateAsync(Guid id, UpdateOpportunityRequest req, CancellationToken ct = default);
    Task<OpportunityDetailDto> UpdateStageAsync(Guid id, UpdateOpportunityStageRequest req, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface ISupportTicketService
{
    Task<List<TicketListDto>> GetAllAsync(Guid? customerId = null, TicketStatus? status = null, TicketPriority? priority = null, CancellationToken ct = default);
    Task<TicketDetailDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<TicketDetailDto> CreateAsync(CreateTicketRequest req, CancellationToken ct = default);
    Task<TicketDetailDto> UpdateAsync(Guid id, UpdateTicketRequest req, CancellationToken ct = default);
    Task<TicketDetailDto> UpdateStatusAsync(Guid id, UpdateTicketStatusRequest req, CancellationToken ct = default);
    Task<TicketCommentDto> AddCommentAsync(Guid id, AddTicketCommentRequest req, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface ICrmActivityService
{
    Task<List<CrmActivityListDto>> GetAllAsync(Guid? customerId = null, Guid? opportunityId = null, Guid? ticketId = null, CancellationToken ct = default);
    Task<CrmActivityDetailDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<CrmActivityDetailDto> CreateAsync(CreateCrmActivityRequest req, CancellationToken ct = default);
    Task<CrmActivityDetailDto> UpdateAsync(Guid id, UpdateCrmActivityRequest req, CancellationToken ct = default);
    Task<CrmActivityDetailDto> CompleteAsync(Guid id, CompleteCrmActivityRequest req, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface IRepository<T> where T : BaseEntity
{
    Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<List<T>> GetAllAsync(CancellationToken ct = default);
    Task<T> AddAsync(T entity, CancellationToken ct = default);
    Task UpdateAsync(T entity, CancellationToken ct = default);
    Task DeleteAsync(T entity, CancellationToken ct = default);
    IQueryable<T> Query();
}

public interface IUnitOfWork { Task<int> SaveChangesAsync(CancellationToken ct = default); }

public interface IEmailService
{
    Task SendEmailAsync(string to, string subject, string body, string? cc = null, List<string>? attachments = null, CancellationToken ct = default);
    Task SendTemplatedEmailAsync(
        string to,
        string templateKey,
        Dictionary<string, object> variables,
        Guid? customerId = null,
        IEnumerable<EmailAttachment>? attachments = null,
        CancellationToken ct = default);
}


public record EmailAttachment(string FileName, byte[] Content, string ContentType);


public interface IFileStorageService
{
    Task<string> UploadAsync(Stream stream, string fileName, string contentType, CancellationToken ct = default);
    Task<Stream> DownloadAsync(string path, CancellationToken ct = default);
    Task DeleteAsync(string path, CancellationToken ct = default);
}

public interface IPdfService
{
    Task<byte[]> GenerateQuotePdfAsync(Quote quote, CompanySettings company, CancellationToken ct = default);
    Task<byte[]> GenerateInvoicePdfAsync(Invoice invoice, CompanySettings company, CancellationToken ct = default);
}

public interface ICurrentUserService
{
    string? UserId { get; }
    string? UserName { get; }
    string? Email { get; }
    bool IsAuthenticated { get; }
    bool IsInRole(string role);
}

public interface IGobdService
{
    string ComputeHash(string content);
    string CreateChainedHash(string content, string? previousHash);
    Task<string?> GetLastInvoiceHashAsync(CancellationToken ct = default);
}

public interface INumberSequenceService
{
    Task<string> NextNumberAsync(string entityType, int year, string defaultPrefix, int defaultPadding, CancellationToken ct = default, bool includeYear = true);
    Task<List<NumberRangeConfig>> GetRangesAsync(int year, CancellationToken ct = default);
    Task<NumberRangeConfig> UpsertRangeAsync(NumberRangeConfig config, CancellationToken ct = default);
}

public record NumberRangeConfig(string EntityType, int Year, string Prefix, int NextValue, int Padding);