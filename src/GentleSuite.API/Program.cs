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
builder.Services.AddIdentity<AppUser, IdentityRole>(o =>
{
    o.Password.RequireDigit = true; o.Password.RequiredLength = 8; o.Password.RequireNonAlphanumeric = false;
    o.Lockout.MaxFailedAccessAttempts = 5;
    o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    o.Lockout.AllowedForNewUsers = true;
}).AddEntityFrameworkStores<AppDbContext>().AddDefaultTokenProviders();

var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Length < 32)
    throw new InvalidOperationException("Jwt:Key ist nicht konfiguriert oder zu kurz (mind. 32 Zeichen). Für lokale Entwicklung in appsettings.Development.json setzen, für Produktion in appsettings.Production.json oder als Umgebungsvariable Jwt__Key.");
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
                if (!string.IsNullOrEmpty(accessToken) && (path.StartsWithSegments("/hubs/project-board") || path.StartsWithSegments("/hubs/notifications")))
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
builder.Services.AddSingleton<NoteSecretProtector>();
builder.Services.AddScoped<RenewalService>();
builder.Services.AddSingleton<GentleSuite.API.Hubs.AppNotifier>();
builder.Services.AddScoped<IOnboardingService, OnboardingServiceImpl>();
builder.Services.AddScoped<IQuoteService, QuoteServiceImpl>();
builder.Services.AddScoped<IInvoiceService, InvoiceServiceImpl>();
builder.Services.AddScoped<IExpenseService, ExpenseServiceImpl>();
builder.Services.AddScoped<IProjectService, ProjectServiceImpl>();
builder.Services.AddScoped<ISubscriptionService, SubscriptionServiceImpl>();
builder.Services.AddScoped<IMolliePaymentService, MolliePaymentService>();
builder.Services.AddScoped<IServiceCatalogService, ServiceCatalogServiceImpl>();
builder.Services.AddScoped<IDashboardService, DashboardServiceImpl>();
builder.Services.AddScoped<IActivityLogService, ActivityLogServiceImpl>();
builder.Services.AddScoped<ICompanySettingsService, CompanySettingsServiceImpl>();
builder.Services.AddScoped<ILegalTextService, LegalTextServiceImpl>();
builder.Services.AddScoped<IPaymentTermService, PaymentTermServiceImpl>();
builder.Services.AddScoped<IContractTemplateService, ContractTemplateServiceImpl>();
builder.Services.AddScoped<IAgencyContractService, AgencyContractServiceImpl>();
builder.Services.AddScoped<IContractClauseBlockService, ContractClauseBlockServiceImpl>();
builder.Services.AddScoped<IEstimateService, EstimateServiceImpl>();
builder.Services.AddScoped<ILegalWorkflowService, LegalWorkflowServiceImpl>();
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
builder.Services.AddScoped<MandateReconcileJob>();
builder.Services.AddScoped<IPaymentMonitoringService, PaymentMonitoringService>();
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

var allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>()
    ?? new[] { "https://gentlesuite.vercel.app", "http://localhost:3000" };
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(allowedOrigins).AllowAnyMethod().AllowAnyHeader()));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    // Öffentliche Mandats-Einrichtung: zusätzlich deutlich enger begrenzt (POSTs erzeugen Mollie-Objekte).
    static string Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    o.AddPolicy("public-mandate-read", ctx => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(Ip(ctx),
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // GentleHub-Integration: großzügig für normalen Betrieb, bremst aber Schleifen in einem fehlerhaften Client.
    o.AddPolicy("integration-hub", ctx => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        GentleSuite.API.Middleware.IntegrationApiKeyMiddleware.ProvidedKey(ctx) is { Length: > 12 } k ? k[..12] : Ip(ctx),
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("public-mandate-write", ctx => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(Ip(ctx),
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));
});

var app = builder.Build();

// Lokaler Test des Jahreswechsels: Datum für Geschäftsjahr/Nummernkreise simulieren. In Production nie aktiv.
if (!app.Environment.IsProduction() && DateTimeOffset.TryParse(builder.Configuration["Testing:FiscalYearToday"], out var fiscalTestToday))
    GentleSuite.Infrastructure.Services.FiscalYear.TestTodayOverride = fiscalTestToday;

// Seed
using (var scope = app.Services.CreateScope())
{
try
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await GentleSuite.API.SchemaPatches.ApplyAsync(db);

    await SeedData.InitializeAsync(scope.ServiceProvider);
}
catch (Exception ex)
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    logger.LogError(ex, "DB-Initialisierung fehlgeschlagen: {Message}", ex.Message);
}
// Bestehende Zugangsdaten-Notizen nachverschlüsseln (nur wenn Security:NotesEncryptionKey gesetzt ist; idempotent).
try
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var n = await scope.ServiceProvider.GetRequiredService<NoteSecretProtector>().EncryptExistingAsync(db, logger);
    if (n > 0) logger.LogInformation("{Count} Zugangsdaten-Notizen nachverschlüsselt.", n);
}
catch (Exception ex)
{
    scope.ServiceProvider.GetRequiredService<ILogger<Program>>().LogError(ex, "Nachverschlüsselung der Zugangsdaten-Notizen fehlgeschlagen");
}
}



app.UseCors();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'";
    await next();
});
app.UseRateLimiter();
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
    // Public customer pages (payment setup) never show technical details — the real cause goes to the log.
    if (status == StatusCodes.Status500InternalServerError && ctx.Request.Path.StartsWithSegments("/api/public"))
    {
        ctx.RequestServices.GetRequiredService<ILogger<Program>>().LogError(ex, "Public endpoint {Path} failed", ctx.Request.Path);
        msg = "Das hat gerade leider nicht geklappt. Bitte versuchen Sie es in ein paar Minuten noch einmal.";
    }
    await ctx.Response.WriteAsJsonAsync(new { error = msg });
}));

app.UseSerilogRequestLogging();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseMiddleware<GentleSuite.API.Middleware.GentleBookApiKeyMiddleware>();
app.UseMiddleware<GentleSuite.API.Middleware.IntegrationApiKeyMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<ProjectBoardHub>("/hubs/project-board");
app.MapHub<NotificationHub>("/hubs/notifications");

// Hangfire dashboard
app.MapHangfireDashboard("/hangfire");
// Explicitly remove deactivated jobs from Hangfire DB so they don't keep firing
RecurringJob.RemoveIfExists("check-open-quotes");
// Stündlich reicht: jeder Lesezugriff auf Angebote (Liste, Detail, Freigabe-Link, Dashboard) lässt abgelaufene ohnehin sofort verfallen.
RecurringJob.AddOrUpdate<ReminderJobs>("expire-quotes", j => j.ExpireQuotesAsync(), Cron.Hourly());
RecurringJob.RemoveIfExists("generate-recurring-expenses");
RecurringJob.RemoveIfExists("generate-subscription-invoices");
RecurringJob.AddOrUpdate<BankSyncJob>("sync-bank-transactions", j => j.SyncAllAsync(), "*/30 * * * *");
// Abrechnung/Abgleich nach Berliner Zeit (Fälligkeiten werden ebenfalls in Europe/Berlin berechnet).
var berlinJobOptions = new RecurringJobOptions { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin") };
RecurringJob.AddOrUpdate<ReminderJobs>("quote-follow-up", j => j.CreateQuoteFollowUpsAsync(), Cron.Daily(8, 30), berlinJobOptions);
RecurringJob.AddOrUpdate<MandateReconcileJob>("mandate-reconcile", j => j.RunAsync(), Cron.Daily(5), berlinJobOptions);
RecurringJob.AddOrUpdate<SubscriptionBillingJob>("subscription-billing", j => j.RunAsync(), Cron.Daily(6), berlinJobOptions);
RecurringJob.AddOrUpdate<ReminderJobs>("check-overdue-invoices", j => j.CheckOverdueInvoicesAsync(), Cron.Daily(7));
RecurringJob.AddOrUpdate<ReminderJobs>("send-overdue-reminders", j => j.SendOverdueRemindersAsync(), Cron.Daily(8));
RecurringJob.AddOrUpdate<ReminderJobs>("send-mandate-reminders", j => j.SendMandateRemindersAsync(), Cron.Daily(9));

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
