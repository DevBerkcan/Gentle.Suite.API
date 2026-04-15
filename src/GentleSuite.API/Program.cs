using GentleSuite.Application.Interfaces;
using GentleSuite.Application.Mappings;
using GentleSuite.Infrastructure.Data;
using GentleSuite.Infrastructure.Email;
using GentleSuite.Infrastructure.Identity;
using GentleSuite.Infrastructure.Jobs;
using GentleSuite.Infrastructure.Pdf;
using GentleSuite.Infrastructure.Services;
using GentleSuite.API.Hubs;
using Hangfire;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
Log.Logger = new LoggerConfiguration().ReadFrom.Configuration(builder.Configuration).CreateLogger();
builder.Host.UseSerilog();

var connStr = builder.Configuration.GetConnectionString("Default") ?? "Server=localhost,1433;Database=gentlesuite;User Id=sa;Password=YourStrong!Passw0rd;Encrypt=False;TrustServerCertificate=True";
EnsureSqlServerDatabaseExists(connStr);
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
builder.Services.AddAutoMapper(cfg => cfg.AddMaps(typeof(MappingProfile).Assembly));
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
builder.Services.AddScoped<IIntegrationService, IntegrationServiceImpl>();
builder.Services.AddScoped<ICustomerDocumentService, CustomerDocumentServiceImpl>();
builder.Services.AddScoped<BankSyncJob>();
builder.Services.AddHttpClient();
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
builder.Services.AddScoped<IExportService, ExportServiceImpl>();
builder.Services.Configure<SmtpSettings>(builder.Configuration.GetSection("Smtp"));
builder.Services.AddScoped<IEmailService, EmailServiceImpl>();
builder.Services.AddScoped<ReminderJobs>();
builder.Services.AddScoped<SubscriptionBillingJob>();
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
try
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    // EnsureCreatedAsync() skips creation if ANY tables exist (e.g. Hangfire schema).
    // Use targeted check: only create EF tables if AspNetUsers is missing.
    var efCreator = db.Database.GetInfrastructure().GetRequiredService<IRelationalDatabaseCreator>();
    var dbConn = db.Database.GetDbConnection();
    if (dbConn.State != System.Data.ConnectionState.Open) await dbConn.OpenAsync();
    await using (var chk = dbConn.CreateCommand())
    {
        chk.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='AspNetUsers' AND TABLE_SCHEMA='dbo'";
        var efExists = Convert.ToInt32(await chk.ExecuteScalarAsync()) > 0;
        if (!efExists) await efCreator.CreateTablesAsync();
    }
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

    await db.Database.ExecuteSqlRawAsync("""IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='SubscriptionPlans' AND COLUMN_NAME='Category') ALTER TABLE "SubscriptionPlans" ADD "Category" INT NOT NULL DEFAULT 0;""");
    await db.Database.ExecuteSqlRawAsync("""IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Customers' AND COLUMN_NAME='OnboardingToken') ALTER TABLE "Customers" ADD "OnboardingToken" UNIQUEIDENTIFIER NULL;""");
    await db.Database.ExecuteSqlRawAsync("""IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Customers' AND COLUMN_NAME='OnboardingIntakeDone') ALTER TABLE "Customers" ADD "OnboardingIntakeDone" BIT NOT NULL DEFAULT 0;""");
    await db.Database.ExecuteSqlRawAsync("""IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='CustomerSubscriptions' AND COLUMN_NAME='ContractDurationMonths') ALTER TABLE "CustomerSubscriptions" ADD "ContractDurationMonths" INT NULL;""");
    await db.Database.ExecuteSqlRawAsync("""IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='CustomerSubscriptions' AND COLUMN_NAME='ConfirmedAt') ALTER TABLE "CustomerSubscriptions" ADD "ConfirmedAt" DATETIMEOFFSET NULL;""");
    await db.Database.ExecuteSqlRawAsync("""
    IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='IntegrationSettings')
    CREATE TABLE "IntegrationSettings" (
        "Id" UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        "PayPalClientId" NVARCHAR(512) NULL,
        "PayPalClientSecret" NVARCHAR(512) NULL,
        "PayPalEnabled" BIT NOT NULL DEFAULT 0,
        "PayPalLastSync" DATETIMEOFFSET NULL,
        "GoCardlessSecretId" NVARCHAR(512) NULL,
        "GoCardlessSecretKey" NVARCHAR(512) NULL,
        "BankRequisitionId" NVARCHAR(512) NULL,
        "BankAccountId" NVARCHAR(512) NULL,
        "BankEnabled" BIT NOT NULL DEFAULT 0,
        "BankLastSync" DATETIMEOFFSET NULL,
        "CreatedAt" DATETIMEOFFSET NOT NULL DEFAULT GETUTCDATE(),
        "UpdatedAt" DATETIMEOFFSET NOT NULL DEFAULT GETUTCDATE(),
        "IsDeleted" BIT NOT NULL DEFAULT 0,
        CONSTRAINT "PK_IntegrationSettings" PRIMARY KEY ("Id")
    );
    """);

    await db.Database.ExecuteSqlRawAsync("""
    IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='CustomerDocuments')
    CREATE TABLE "CustomerDocuments" (
        "Id" UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        "CustomerId" UNIQUEIDENTIFIER NOT NULL,
        "FileName" NVARCHAR(512) NOT NULL,
        "ContentType" NVARCHAR(256) NOT NULL,
        "FileSizeBytes" BIGINT NOT NULL DEFAULT 0,
        "StoragePath" NVARCHAR(1024) NOT NULL,
        "Notes" NVARCHAR(MAX) NULL,
        "CreatedAt" DATETIMEOFFSET NOT NULL DEFAULT GETUTCDATE(),
        "CreatedBy" NVARCHAR(MAX) NULL,
        "UpdatedAt" DATETIMEOFFSET NULL,
        "UpdatedBy" NVARCHAR(MAX) NULL,
        "IsDeleted" BIT NOT NULL DEFAULT 0,
        "DeletedAt" DATETIMEOFFSET NULL,
        CONSTRAINT "PK_CustomerDocuments" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_CustomerDocuments_Customers_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES "Customers" ("Id") ON DELETE CASCADE
    );
    """);
    await db.Database.ExecuteSqlRawAsync("""IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_CustomerDocuments_CustomerId' AND object_id=OBJECT_ID('CustomerDocuments')) CREATE INDEX "IX_CustomerDocuments_CustomerId" ON "CustomerDocuments" ("CustomerId");""");

    // Feature: Projektbudget
    await db.Database.ExecuteSqlRawAsync("""IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Projects' AND COLUMN_NAME='BudgetHours') ALTER TABLE "Projects" ADD "BudgetHours" DECIMAL(10,2) NULL;""");

    // Feature: Wiederkehrende Ausgaben
    await db.Database.ExecuteSqlRawAsync("""IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Expenses' AND COLUMN_NAME='IsRecurring') ALTER TABLE "Expenses" ADD "IsRecurring" BIT NOT NULL DEFAULT 0;""");
    await db.Database.ExecuteSqlRawAsync("""IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Expenses' AND COLUMN_NAME='RecurringInterval') ALTER TABLE "Expenses" ADD "RecurringInterval" INT NULL;""");
    await db.Database.ExecuteSqlRawAsync("""IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Expenses' AND COLUMN_NAME='RecurringNextDate') ALTER TABLE "Expenses" ADD "RecurringNextDate" DATETIMEOFFSET NULL;""");
    await db.Database.ExecuteSqlRawAsync("""IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Expenses' AND COLUMN_NAME='RecurringParentId') ALTER TABLE "Expenses" ADD "RecurringParentId" UNIQUEIDENTIFIER NULL;""");

    // Firmendaten aus Referenz-Rechnung immer aktuell halten
    await db.Database.ExecuteSqlRawAsync("""
        UPDATE "CompanySettings" SET
            "CompanyName"       = 'Gentle Group',
            "LegalName"         = 'Berk-Can Atesoglu',
            "Street"            = 'Oberbilker Allee 319',
            "ZipCode"           = '40227',
            "City"              = 'Düsseldorf',
            "Country"           = 'Deutschland',
            "Phone"             = '01754701892',
            "Email"             = 'office@gentlegroup.de',
            "Website"           = 'www.gentlegroup.de',
            "TaxId"             = '133/5008/7238',
            "BankName"          = 'Postbank Ndl der Deutsche Bank',
            "Iban"              = 'DE16 1001 0010 0947 7181 03',
            "ManagingDirector"  = 'Atesoglu'
        WHERE 1=1
    """);

    await SeedData.InitializeAsync(scope.ServiceProvider);
}
catch (Exception ex)
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    logger.LogError(ex, "DB-Initialisierung fehlgeschlagen: {Message}", ex.Message);
}
}



app.UseCors();
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
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<ProjectBoardHub>("/hubs/project-board");

// Hangfire dashboard
app.MapHangfireDashboard("/hangfire");
RecurringJob.AddOrUpdate<ReminderJobs>("check-overdue-invoices", j => j.CheckOverdueInvoicesAsync(), Cron.Daily(8));
RecurringJob.AddOrUpdate<ReminderJobs>("check-open-quotes", j => j.CheckOpenQuotesAsync(), Cron.Daily(9));
RecurringJob.AddOrUpdate<ReminderJobs>("generate-subscription-invoices", j => j.GenerateSubscriptionInvoicesAsync(), Cron.Daily(6));
RecurringJob.AddOrUpdate<BankSyncJob>("sync-bank-transactions", j => j.SyncAllAsync(), "*/30 * * * *");
RecurringJob.AddOrUpdate<ReminderJobs>("generate-recurring-expenses", j => j.GenerateRecurringExpensesAsync(), Cron.Daily(7));
RecurringJob.AddOrUpdate<SubscriptionBillingJob>(
    "generate-subscription-invoices",
    job => job.RunAsync(),
    Cron.Daily(7));

app.Run();

static void EnsureSqlServerDatabaseExists(string connectionString)
{
    var csb = new SqlConnectionStringBuilder(connectionString);
    if (string.IsNullOrWhiteSpace(csb.InitialCatalog))
        return;

    var dbName = csb.InitialCatalog;
    csb.InitialCatalog = "master";

    using var conn = new SqlConnection(csb.ConnectionString);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        IF DB_ID(@db) IS NULL
        BEGIN
            DECLARE @sql NVARCHAR(MAX) = N'CREATE DATABASE ' + QUOTENAME(@db);
            EXEC(@sql);
        END
        """;
    cmd.Parameters.AddWithValue("@db", dbName);
    cmd.ExecuteNonQuery();
}
