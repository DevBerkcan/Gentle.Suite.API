using GentleSuite.Application.Interfaces;
using GentleSuite.Application.Mappings;
using GentleSuite.Domain.Interfaces;
using GentleSuite.Infrastructure.Data;
using GentleSuite.Infrastructure.Email;
using GentleSuite.Infrastructure.Identity;
using GentleSuite.Infrastructure.Jobs;
using GentleSuite.Infrastructure.Pdf;
using GentleSuite.Infrastructure.Services;
using GentleSuite.API.Hubs;
using Hangfire;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
Log.Logger = new LoggerConfiguration().ReadFrom.Configuration(builder.Configuration).CreateLogger();
builder.Host.UseSerilog();

var connStr = builder.Configuration.GetConnectionString("Default") ?? "Host=localhost;Database=gentlesuite;Username=postgres;Password=postgres";
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(connStr));
builder.Services.AddIdentity<AppUser, IdentityRole>(o => { o.Password.RequireDigit = true; o.Password.RequiredLength = 8; o.Password.RequireNonAlphanumeric = false; }).AddEntityFrameworkStores<AppDbContext>().AddDefaultTokenProviders();

var jwtKey = builder.Configuration["Jwt:Key"] ?? "GentleSuiteSecretKey_MinLength32Chars!!";
builder.Services.AddAuthentication(o => { o.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme; o.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme; })
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateIssuer = false,
            ValidateAudience = false,
            ClockSkew = TimeSpan.Zero
        };
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var accessToken = ctx.Request.Query["access_token"];
                var path = ctx.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/project-board"))
                    ctx.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("AdminOnly", p => p.RequireRole(GentleSuite.Domain.Enums.Roles.Admin));
    o.AddPolicy("AccountingOrAdmin", p => p.RequireRole(GentleSuite.Domain.Enums.Roles.Admin, GentleSuite.Domain.Enums.Roles.Accounting));
});
builder.Services.AddAutoMapper(typeof(MappingProfile));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<ICustomerService, CustomerServiceImpl>();
builder.Services.AddScoped<ICustomerNoteService, CustomerNoteServiceImpl>();
builder.Services.AddScoped<IOnboardingService, OnboardingServiceImpl>();
builder.Services.AddScoped<IQuoteService, QuoteServiceImpl>();
builder.Services.AddScoped<IInvoiceService, InvoiceServiceImpl>();
builder.Services.AddScoped<IExpenseService, ExpenseServiceImpl>();
builder.Services.AddScoped<IProjectService, ProjectServiceImpl>();
builder.Services.AddScoped<ISubscriptionService, SubscriptionServiceImpl>();
builder.Services.AddScoped<IServiceCatalogService, ServiceCatalogServiceImpl>();
builder.Services.AddScoped<IDashboardService, DashboardServiceImpl>();
builder.Services.AddScoped<IActivityLogService, ActivityLogServiceImpl>();
builder.Services.AddScoped<ICompanySettingsService, CompanySettingsServiceImpl>();
builder.Services.AddScoped<ILegalTextService, LegalTextServiceImpl>();
builder.Services.AddScoped<ITimeTrackingService, TimeTrackingServiceImpl>();
builder.Services.AddScoped<IVatService, VatServiceImpl>();
builder.Services.AddScoped<IEmailLogService, EmailLogServiceImpl>();
builder.Services.AddScoped<IUserService, UserServiceImpl>();
builder.Services.AddScoped<IJournalService, JournalServiceImpl>();
builder.Services.AddScoped<IBankTransactionService, BankTransactionServiceImpl>();
builder.Services.AddScoped<IChartOfAccountService, ChartOfAccountServiceImpl>();
builder.Services.AddScoped<IEmailTemplateService, EmailTemplateServiceImpl>();
builder.Services.AddScoped<IContactListService, ContactListServiceImpl>();
builder.Services.AddScoped<ITeamMemberService, TeamMemberServiceImpl>();
builder.Services.AddScoped<IProductService, ProductServiceImpl>();
builder.Services.AddScoped<IPriceListService, PriceListServiceImpl>();
builder.Services.AddScoped<IOpportunityService, OpportunityServiceImpl>();
builder.Services.AddScoped<ISupportTicketService, SupportTicketServiceImpl>();
builder.Services.AddScoped<ICrmActivityService, CrmActivityServiceImpl>();
builder.Services.AddScoped<INumberSequenceService, NumberSequenceServiceImpl>();
builder.Services.AddScoped<IPdfService, PdfService>();
builder.Services.AddScoped<IFileStorageService, LocalFileStorageService>();
builder.Services.Configure<SmtpSettings>(builder.Configuration.GetSection("Smtp"));
builder.Services.AddScoped<IEmailService, EmailServiceImpl>();
builder.Services.AddScoped<ReminderJobs>();

builder.Services.AddHangfire(c => c.UseSqlServerStorage(connStr));
builder.Services.AddHangfireServer();
builder.Services.AddSignalR();

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "GentleSuite API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { In = ParameterLocation.Header, Description = "Enter your JWT token", Name = "Authorization", Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement { { new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }, Array.Empty<string>() } });
});

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

// Seed
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    // Create schema if it doesn't exist (includes AspNetRoles, AspNetUsers, etc.)
    await db.Database.EnsureCreatedAsync();
    // Lightweight schema evolution for existing volumes without migrations.
    await db.Database.ExecuteSqlRawAsync("""IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='OnboardingWorkflows' AND COLUMN_NAME='ProjectId') ALTER TABLE "OnboardingWorkflows" ADD "ProjectId" UNIQUEIDENTIFIER NULL;""");
    await db.Database.ExecuteSqlRawAsync("""IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_OnboardingWorkflows_ProjectId' AND object_id=OBJECT_ID('OnboardingWorkflows')) CREATE INDEX "IX_OnboardingWorkflows_ProjectId" ON "OnboardingWorkflows" ("ProjectId");""");
    await db.Database.ExecuteSqlRawAsync("""
    IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='NumberSequences')
    BEGIN
        CREATE TABLE "NumberSequences" (
            "Id" UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
            "EntityType" NVARCHAR(MAX) NOT NULL,
            "Year" INT NOT NULL,
            "Prefix" NVARCHAR(MAX) NOT NULL,
            "LastValue" INT NOT NULL DEFAULT 0,
            "Padding" INT NOT NULL DEFAULT 4,
            "CreatedAt" DATETIMEOFFSET(7) NOT NULL,
            "CreatedBy" NVARCHAR(MAX) NULL,
            "UpdatedAt" DATETIMEOFFSET(7) NULL,
            "UpdatedBy" NVARCHAR(MAX) NULL,
            "IsDeleted" BIT NOT NULL DEFAULT 0,
            "DeletedAt" DATETIMEOFFSET(7) NULL,
            CONSTRAINT "PK_NumberSequences" PRIMARY KEY ("Id")
        );
    END
    """);
    await db.Database.ExecuteSqlRawAsync("""IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_NumberSequences_EntityType_Year' AND object_id=OBJECT_ID('NumberSequences')) CREATE UNIQUE INDEX "IX_NumberSequences_EntityType_Year" ON "NumberSequences" ("EntityType", "Year");""");
    await db.Database.ExecuteSqlRawAsync("""IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Invoices' AND COLUMN_NAME='BillingPeriodStart') ALTER TABLE "Invoices" ADD "BillingPeriodStart" DATETIMEOFFSET(7) NULL;""");
    await db.Database.ExecuteSqlRawAsync("""IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Invoices' AND COLUMN_NAME='BillingPeriodEnd') ALTER TABLE "Invoices" ADD "BillingPeriodEnd" DATETIMEOFFSET(7) NULL;""");
    await db.Database.ExecuteSqlRawAsync("""IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Customers' AND COLUMN_NAME='ReminderStop') ALTER TABLE "Customers" ADD "ReminderStop" BIT NOT NULL DEFAULT 0;""");
    await db.Database.ExecuteSqlRawAsync("""IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Invoices' AND COLUMN_NAME='ReminderStop') ALTER TABLE "Invoices" ADD "ReminderStop" BIT NOT NULL DEFAULT 0;""");
    await db.Database.ExecuteSqlRawAsync("""
    IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='ProjectBoardTasks')
    BEGIN
        CREATE TABLE "ProjectBoardTasks" (
            "Id" UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
            "ProjectId" UNIQUEIDENTIFIER NOT NULL,
            "Title" NVARCHAR(MAX) NOT NULL,
            "Description" NVARCHAR(MAX) NULL,
            "Status" INT NOT NULL,
            "SortOrder" INT NOT NULL DEFAULT 0,
            "AssigneeName" NVARCHAR(MAX) NULL,
            "DueDate" DATETIMEOFFSET(7) NULL,
            "CreatedAt" DATETIMEOFFSET(7) NOT NULL,
            "CreatedBy" NVARCHAR(MAX) NULL,
            "UpdatedAt" DATETIMEOFFSET(7) NULL,
            "UpdatedBy" NVARCHAR(MAX) NULL,
            "IsDeleted" BIT NOT NULL DEFAULT 0,
            "DeletedAt" DATETIMEOFFSET(7) NULL,
            CONSTRAINT "PK_ProjectBoardTasks" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_ProjectBoardTasks_Projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "Projects" ("Id") ON DELETE CASCADE
        );
    END
    """);
    await db.Database.ExecuteSqlRawAsync("""IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ProjectBoardTasks_ProjectId_Status_SortOrder' AND object_id=OBJECT_ID('ProjectBoardTasks')) CREATE INDEX "IX_ProjectBoardTasks_ProjectId_Status_SortOrder" ON "ProjectBoardTasks" ("ProjectId", "Status", "SortOrder");""");
    await db.Database.ExecuteSqlRawAsync("""
    IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='ProjectTeamMembers')
    BEGIN
        CREATE TABLE "ProjectTeamMembers" (
            "ProjectId" UNIQUEIDENTIFIER NOT NULL,
            "TeamMemberId" UNIQUEIDENTIFIER NOT NULL,
            CONSTRAINT "PK_ProjectTeamMembers" PRIMARY KEY ("ProjectId", "TeamMemberId"),
            CONSTRAINT "FK_ProjectTeamMembers_Projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "Projects" ("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_ProjectTeamMembers_TeamMembers_TeamMemberId" FOREIGN KEY ("TeamMemberId") REFERENCES "TeamMembers" ("Id") ON DELETE CASCADE
        );
    END
    """);
    await db.Database.ExecuteSqlRawAsync("""IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ProjectTeamMembers_TeamMemberId' AND object_id=OBJECT_ID('ProjectTeamMembers')) CREATE INDEX "IX_ProjectTeamMembers_TeamMemberId" ON "ProjectTeamMembers" ("TeamMemberId");""");
    await db.Database.ExecuteSqlRawAsync("""IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Quotes' AND COLUMN_NAME='QuoteGroupId') ALTER TABLE "Quotes" ADD "QuoteGroupId" UNIQUEIDENTIFIER NULL;""");
    await db.Database.ExecuteSqlRawAsync("""IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Quotes' AND COLUMN_NAME='IsCurrentVersion') ALTER TABLE "Quotes" ADD "IsCurrentVersion" BIT NOT NULL DEFAULT 1;""");
    await db.Database.ExecuteSqlRawAsync("""IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='QuoteLines' AND COLUMN_NAME='DiscountPercent') ALTER TABLE "QuoteLines" ADD "DiscountPercent" DECIMAL(18,2) NOT NULL DEFAULT 0;""");
    await db.Database.ExecuteSqlRawAsync("""UPDATE "Quotes" SET "QuoteGroupId" = "Id" WHERE "QuoteGroupId" IS NULL;""");
    await db.Database.ExecuteSqlRawAsync("""IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Quotes_QuoteGroupId_Version' AND object_id=OBJECT_ID('Quotes')) CREATE UNIQUE INDEX "IX_Quotes_QuoteGroupId_Version" ON "Quotes" ("QuoteGroupId", "Version");""");
    await db.Database.ExecuteSqlRawAsync("""IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Quotes_QuoteGroupId_IsCurrentVersion' AND object_id=OBJECT_ID('Quotes')) CREATE INDEX "IX_Quotes_QuoteGroupId_IsCurrentVersion" ON "Quotes" ("QuoteGroupId", "IsCurrentVersion") WHERE "IsCurrentVersion" = 1;""");
    await db.Database.ExecuteSqlRawAsync("""
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Invoices_SubscriptionId_BillingPeriodStart_BillingPeriodEnd' AND object_id=OBJECT_ID('Invoices'))
        CREATE UNIQUE INDEX "IX_Invoices_SubscriptionId_BillingPeriodStart_BillingPeriodEnd"
        ON "Invoices" ("SubscriptionId", "BillingPeriodStart", "BillingPeriodEnd")
        WHERE "SubscriptionId" IS NOT NULL;
    """);
    await db.Database.ExecuteSqlRawAsync("""
    IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='ReminderSettings')
    BEGIN
        CREATE TABLE "ReminderSettings" (
            "Id" UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
            "Level1Days" INT NOT NULL DEFAULT 7,
            "Level2Days" INT NOT NULL DEFAULT 14,
            "Level3Days" INT NOT NULL DEFAULT 21,
            "Level1Fee" DECIMAL(18,2) NOT NULL DEFAULT 0,
            "Level2Fee" DECIMAL(18,2) NOT NULL DEFAULT 0,
            "Level3Fee" DECIMAL(18,2) NOT NULL DEFAULT 0,
            "AnnualInterestPercent" DECIMAL(18,2) NOT NULL DEFAULT 0,
            "CreatedAt" DATETIMEOFFSET(7) NOT NULL,
            "CreatedBy" NVARCHAR(MAX) NULL,
            "UpdatedAt" DATETIMEOFFSET(7) NULL,
            "UpdatedBy" NVARCHAR(MAX) NULL,
            "IsDeleted" BIT NOT NULL DEFAULT 0,
            "DeletedAt" DATETIMEOFFSET(7) NULL,
            CONSTRAINT "PK_ReminderSettings" PRIMARY KEY ("Id")
        );
    END
    """);
    await db.Database.ExecuteSqlRawAsync("""
    IF NOT EXISTS (SELECT 1 FROM "ReminderSettings")
        INSERT INTO "ReminderSettings" ("Id","Level1Days","Level2Days","Level3Days","Level1Fee","Level2Fee","Level3Fee","AnnualInterestPercent","CreatedAt","IsDeleted")
        VALUES ('00000000-0000-0000-0000-000000000001',7,14,21,0,0,0,0,SYSDATETIMEOFFSET(),0);
    """);

    await SeedData.InitializeAsync(scope.ServiceProvider);
}



app.UseExceptionHandler(a => a.Run(async ctx =>
{
    var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var status = ex switch
    {
        KeyNotFoundException => StatusCodes.Status404NotFound,
        ArgumentException => StatusCodes.Status400BadRequest,
        InvalidOperationException => StatusCodes.Status400BadRequest,
        UnauthorizedAccessException => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status500InternalServerError
    };

    ctx.Response.StatusCode = status;
    ctx.Response.ContentType = "application/json";
    var msg = ex?.Message ?? "Internal server error";
    if (status == StatusCodes.Status500InternalServerError && ex?.InnerException != null) msg += " => " + ex.InnerException.Message;
    await ctx.Response.WriteAsJsonAsync(new { error = msg });
}));

app.UseSerilogRequestLogging();
app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<ProjectBoardHub>("/hubs/project-board");

// Hangfire dashboard
app.MapHangfireDashboard("/hangfire");
RecurringJob.AddOrUpdate<ReminderJobs>("check-overdue-invoices", j => j.CheckOverdueInvoicesAsync(), Cron.Daily(8));
RecurringJob.AddOrUpdate<ReminderJobs>("check-open-quotes", j => j.CheckOpenQuotesAsync(), Cron.Daily(9));
RecurringJob.AddOrUpdate<ReminderJobs>("generate-subscription-invoices", j => j.GenerateSubscriptionInvoicesAsync(), Cron.Daily(6));

app.Run();
