using AutoMapper;
using GentleSuite.Application.DTOs;
using GentleSuite.Domain.Entities;
using System.Text.Json;

namespace GentleSuite.Application.Mappings;

public class MappingProfile : Profile
{
    public MappingProfile()
    {
        CreateMap<Customer, CustomerListDto>()
            .ConstructUsing(s => new CustomerListDto(
                s.Id,
                s.CompanyName,
                s.CustomerNumber,
                s.Industry,
                s.Status,
                s.Contacts.Where(c => c.IsPrimary).Select(c => c.FullName).FirstOrDefault(),
                s.Contacts.Where(c => c.IsPrimary).Select(c => c.Email).FirstOrDefault(),
                s.Projects.Count,
                s.CreatedAt,
                null,
                null,
                0m
            ));
        CreateMap<Customer, CustomerDetailDto>()
            .ForMember(d => d.DesiredServiceIds, o => o.MapFrom(s => s.DesiredServices.Select(ds => ds.ServiceCatalogItemId).ToList()))
            .ForMember(d => d.IntakePending, o => o.MapFrom(s => s.OnboardingToken != null && !s.OnboardingIntakeDone));
        CreateMap<Contact, ContactDto>();
        CreateMap<Location, LocationDto>();
        CreateMap<CustomerNote, CustomerNoteDto>();

        CreateMap<OnboardingWorkflow, OnboardingWorkflowDto>().ForMember(d => d.TemplateName, o => o.MapFrom(s => s.Template.Name));
        CreateMap<OnboardingStep, OnboardingStepDto>();
        CreateMap<TaskItem, TaskItemDto>();

        CreateMap<Project, ProjectListDto>()
            .ConstructUsing(s => new ProjectListDto(
                s.Id, s.Name, s.Status, s.Customer.CompanyName, s.StartDate, s.DueDate
            ));
        CreateMap<Project, ProjectDetailDto>().ForMember(d => d.TotalLoggedHours, o => o.Ignore());
        CreateMap<Milestone, MilestoneDto>();
        CreateMap<ProjectComment, ProjectCommentDto>();
        CreateMap<ProjectBoardTask, ProjectBoardTaskDto>();

        CreateMap<Quote, QuoteListDto>()
            .ConstructUsing(s => new QuoteListDto(
                s.Id,
                s.QuoteNumber,
                s.Customer.CompanyName,
                s.Status,
                s.SignatureStatus,
                s.GrandTotal,
                s.Version,
                s.CreatedAt,
                s.ExpiresAt
            ));
        CreateMap<Quote, QuoteDetailDto>()
            .ForMember(d => d.CustomerName, o => o.MapFrom(s => s.Customer.CompanyName))
            .ForMember(d => d.PrimaryContactEmail, o => o.MapFrom(s => s.Customer.Contacts.Where(c => c.IsPrimary).Select(c => c.Email).FirstOrDefault()))
            .ForMember(d => d.LegalTextBlockKeys, o => o.MapFrom(s => string.IsNullOrEmpty(s.LegalTextBlocks) ? new List<string>() : JsonSerializer.Deserialize<List<string>>(s.LegalTextBlocks, (JsonSerializerOptions?)null)));
        CreateMap<Quote, QuoteVersionDto>()
            .ConstructUsing(s => new QuoteVersionDto(s.Id, s.QuoteGroupId, s.QuoteNumber, s.Version, s.IsCurrentVersion, s.Status, s.CreatedAt, s.SentAt));
        CreateMap<QuoteLine, QuoteLineDto>();
        CreateMap<QuoteTemplate, QuoteTemplateDto>();
        CreateMap<QuoteTemplateLine, QuoteTemplateLineDto>();

        CreateMap<Invoice, InvoiceListDto>()
            .ConstructUsing(s => new InvoiceListDto(
                s.Id, s.InvoiceNumber, s.Type, s.Customer.CompanyName, s.Status, s.GrossTotal, s.InvoiceDate, s.DueDate, s.PaidAt
            ));
        CreateMap<Invoice, InvoiceDetailDto>()
            .ForMember(d => d.CustomerName, o => o.MapFrom(s => s.Customer.CompanyName))
            .ForMember(d => d.PaidAmount, o => o.MapFrom(s => s.Payments.Sum(p => p.Amount)))
            .ForMember(d => d.OpenAmount, o => o.MapFrom(s => s.GrossTotal - s.Payments.Sum(p => p.Amount)))
            .ForMember(d => d.VatSummary, o => o.MapFrom(s => s.Lines.GroupBy(l => l.VatPercent).Select(g => new InvoiceVatSummaryDto(g.Key, g.Sum(l => l.NetTotal), g.Sum(l => l.VatAmount), g.Sum(l => l.GrossTotal))).ToList()));
        CreateMap<InvoiceLine, InvoiceLineDto>();
        CreateMap<InvoicePayment, InvoicePaymentDto>();

        CreateMap<Expense, ExpenseListDto>().ForMember(d => d.CategoryName, o => o.MapFrom(s => s.Category != null ? s.Category.Name : null));
        CreateMap<Expense, ExpenseDetailDto>().ForMember(d => d.CategoryName, o => o.MapFrom(s => s.Category != null ? s.Category.Name : null)).ForMember(d => d.AccountNumber, o => o.MapFrom(s => s.Account != null ? s.Account.AccountNumber : null));
        CreateMap<ExpenseCategory, ExpenseCategoryDto>();

        CreateMap<JournalEntry, JournalEntryDto>();
        CreateMap<JournalEntryLine, JournalEntryLineDto>();
        CreateMap<VatPeriod, VatPeriodDto>();
        CreateMap<BankTransaction, BankTransactionDto>();
        CreateMap<ChartOfAccount, ChartOfAccountDto>();
        CreateMap<CompanySettings, CompanySettingsDto>();

        CreateMap<SubscriptionPlan, SubscriptionPlanDto>()
            .ConstructUsing((s, ctx) => new SubscriptionPlanDto(
                s.Id,
                s.Name,
                s.Description,
                s.MonthlyPrice,
                s.BillingCycle,
                s.Category,
                s.IsActive,
                s.WorkScopeRule == null ? null : ctx.Mapper.Map<WorkScopeRuleDto>(s.WorkScopeRule),
                s.SupportPolicy == null ? null : ctx.Mapper.Map<SupportPolicyDto>(s.SupportPolicy)
            ));
        CreateMap<WorkScopeRule, WorkScopeRuleDto>()
            .ConstructUsing(s => new WorkScopeRuleDto(
                s.FairUseDescription,
                JsonSerializer.Deserialize<List<string>>(s.IncludedItemsJson, (JsonSerializerOptions?)null) ?? new List<string>(),
                JsonSerializer.Deserialize<List<string>>(s.ExcludedItemsJson, (JsonSerializerOptions?)null) ?? new List<string>(),
                s.MaxHoursPerMonth
            ));
        CreateMap<SupportPolicy, SupportPolicyDto>()
            .ConstructUsing(s => new SupportPolicyDto(
                s.S0ResponseTarget,
                s.S1ResponseTarget,
                s.S2ResponseTarget,
                s.S3ResponseTarget
            ));
        CreateMap<CustomerSubscription, CustomerSubscriptionDto>()
            .ConstructUsing(s => new CustomerSubscriptionDto(
                s.Id,
                s.PlanId,
                s.Plan.Name,
                s.CustomerId,
                s.Customer != null ? s.Customer.CompanyName : null,
                s.Status,
                s.StartDate,
                s.NextBillingDate,
                s.Plan.MonthlyPrice,
                s.ContractDurationMonths,
                s.ConfirmedAt
            ));

        CreateMap<TimeEntry, TimeEntryDto>()
            .ForMember(d => d.ProjectName, o => o.MapFrom(s => s.Project != null ? s.Project.Name : null))
            .ForMember(d => d.CustomerName, o => o.MapFrom(s => s.Customer != null ? s.Customer.CompanyName : null));

        CreateMap<ServiceCategory, ServiceCategoryDto>();
        CreateMap<ServiceCatalogItem, ServiceCatalogItemDto>();
        CreateMap<EmailLog, EmailLogDto>();
        CreateMap<LegalTextBlock, LegalTextBlockDto>();
        CreateMap<ActivityLog, ActivityLogDto>();
        CreateMap<FileUpload, FileUploadDto>();
    }
}
