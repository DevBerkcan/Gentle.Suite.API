using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;
using GentleSuite.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace GentleSuite.Infrastructure.Data;

public static class SeedData
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var userMgr = services.GetRequiredService<UserManager<AppUser>>();
        var roleMgr = services.GetRequiredService<RoleManager<IdentityRole>>();
        await db.Database.MigrateAsync();
        await SeedNewTables(db);
        await SeedRoles(roleMgr); await SeedUsers(userMgr);
        await SeedCompanySettings(db); await SeedServiceCatalog(db);
        await SeedOnboardingTemplates(db); await SeedEmailTemplates(db);
        await SeedLegalTexts(db); await SeedSubscriptionPlans(db);
        await SeedQuoteTemplates(db); await SeedExpenseCategories(db);
        await SeedAccounts(db); await SeedMissingEmailTemplates(db);
        await SeedDemoData(db);
        await SeedPriceListTemplates(db);
    }

    static async Task SeedRoles(RoleManager<IdentityRole> rm)
    {
        foreach (var r in new[]{Roles.Admin,Roles.ProjectManager,Roles.Designer,Roles.Developer,Roles.Marketing,Roles.Accounting})
            if (!await rm.RoleExistsAsync(r)) await rm.CreateAsync(new IdentityRole(r));
    }

    static async Task SeedUsers(UserManager<AppUser> um)
    {
        var users = new[]{
            ("admin@gentlesuite.local","Admin","User","Password123!",Roles.Admin),
            ("pm@gentlesuite.local","Projekt","Manager","Password123!",Roles.ProjectManager),
            ("berkcan@gentlegroup.de","Berkcan","","Runderbaum12",Roles.Admin),
            ("medin@gentlegroup.de","Medin","","Bosnien12",Roles.ProjectManager),
            ("alanur@gentlegroup.de","Alanur","","Berkcan12",Roles.ProjectManager)
        };
        foreach (var (email,first,last,pass,role) in users)
            if (await um.FindByEmailAsync(email) == null) { var u = new AppUser{UserName=email,Email=email,FirstName=first,LastName=last,EmailConfirmed=true}; if ((await um.CreateAsync(u,pass)).Succeeded) await um.AddToRoleAsync(u,role); }
    }

    static async Task SeedNewTables(AppDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'TeamMembers')
BEGIN
    CREATE TABLE ""TeamMembers"" (
        ""Id"" UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        ""FirstName"" NVARCHAR(MAX) NOT NULL DEFAULT '',
        ""LastName"" NVARCHAR(MAX) NOT NULL DEFAULT '',
        ""Email"" NVARCHAR(MAX),
        ""Phone"" NVARCHAR(MAX),
        ""JobTitle"" NVARCHAR(MAX),
        ""IsActive"" BIT NOT NULL DEFAULT 1,
        ""AppUserId"" NVARCHAR(MAX),
        ""CreatedAt"" DATETIMEOFFSET(7) NOT NULL DEFAULT SYSDATETIMEOFFSET(),
        ""UpdatedAt"" DATETIMEOFFSET(7),
        ""CreatedBy"" NVARCHAR(MAX),
        ""UpdatedBy"" NVARCHAR(MAX),
        ""IsDeleted"" BIT NOT NULL DEFAULT 0,
        ""DeletedAt"" DATETIMEOFFSET(7),
        CONSTRAINT ""PK_TeamMembers"" PRIMARY KEY (""Id"")
    );
END;
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Products')
BEGIN
    CREATE TABLE ""Products"" (
        ""Id"" UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        ""Name"" NVARCHAR(MAX) NOT NULL DEFAULT '',
        ""Description"" NVARCHAR(MAX),
        ""Unit"" NVARCHAR(MAX) NOT NULL DEFAULT 'h',
        ""DefaultPrice"" DECIMAL(18,2) NOT NULL DEFAULT 0,
        ""IsActive"" BIT NOT NULL DEFAULT 1,
        ""CreatedAt"" DATETIMEOFFSET(7) NOT NULL DEFAULT SYSDATETIMEOFFSET(),
        ""UpdatedAt"" DATETIMEOFFSET(7),
        ""CreatedBy"" NVARCHAR(MAX),
        ""UpdatedBy"" NVARCHAR(MAX),
        ""IsDeleted"" BIT NOT NULL DEFAULT 0,
        ""DeletedAt"" DATETIMEOFFSET(7),
        CONSTRAINT ""PK_Products"" PRIMARY KEY (""Id"")
    );
END;
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'ProductTeamMember')
BEGIN
    CREATE TABLE ""ProductTeamMember"" (
        ""ProductId"" UNIQUEIDENTIFIER NOT NULL REFERENCES ""Products""(""Id"") ON DELETE CASCADE,
        ""TeamMemberId"" UNIQUEIDENTIFIER NOT NULL REFERENCES ""TeamMembers""(""Id"") ON DELETE CASCADE,
        CONSTRAINT ""PK_ProductTeamMember"" PRIMARY KEY (""ProductId"", ""TeamMemberId"")
    );
END;
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'PriceLists')
BEGIN
    CREATE TABLE ""PriceLists"" (
        ""Id"" UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        ""CustomerId"" UNIQUEIDENTIFIER NULL,
        ""Name"" NVARCHAR(256) NOT NULL DEFAULT '',
        ""Description"" NVARCHAR(MAX) NULL,
        ""IsTemplate"" BIT NOT NULL DEFAULT 0,
        ""IsActive"" BIT NOT NULL DEFAULT 1,
        ""CreatedAt"" DATETIMEOFFSET(7) NOT NULL DEFAULT SYSDATETIMEOFFSET(),
        ""UpdatedAt"" DATETIMEOFFSET(7),
        ""CreatedBy"" NVARCHAR(MAX),
        ""UpdatedBy"" NVARCHAR(MAX),
        ""IsDeleted"" BIT NOT NULL DEFAULT 0,
        ""DeletedAt"" DATETIMEOFFSET(7),
        CONSTRAINT ""PK_PriceLists"" PRIMARY KEY (""Id"")
    );
END;
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'PriceLists' AND COLUMN_NAME = 'IsTemplate') ALTER TABLE ""PriceLists"" ADD ""IsTemplate"" BIT NOT NULL DEFAULT 0;
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'PriceListItems')
BEGIN
    CREATE TABLE ""PriceListItems"" (
        ""Id"" UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        ""PriceListId"" UNIQUEIDENTIFIER NOT NULL REFERENCES ""PriceLists""(""Id"") ON DELETE CASCADE,
        ""Title"" NVARCHAR(512) NOT NULL DEFAULT '',
        ""Description"" NVARCHAR(MAX) NULL,
        ""Unit"" NVARCHAR(50) NOT NULL DEFAULT 'pauschal',
        ""UnitPrice"" DECIMAL(18,2) NOT NULL DEFAULT 0,
        ""SortOrder"" INT NOT NULL DEFAULT 0,
        ""CreatedAt"" DATETIMEOFFSET(7) NOT NULL DEFAULT SYSDATETIMEOFFSET(),
        ""UpdatedAt"" DATETIMEOFFSET(7),
        ""CreatedBy"" NVARCHAR(MAX),
        ""UpdatedBy"" NVARCHAR(MAX),
        ""IsDeleted"" BIT NOT NULL DEFAULT 0,
        ""DeletedAt"" DATETIMEOFFSET(7),
        CONSTRAINT ""PK_PriceListItems"" PRIMARY KEY (""Id"")
    );
END;
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'PriceListItems' AND COLUMN_NAME = 'Title') ALTER TABLE ""PriceListItems"" ADD ""Title"" NVARCHAR(512) NOT NULL DEFAULT '';
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'PriceListItems' AND COLUMN_NAME = 'UnitPrice') ALTER TABLE ""PriceListItems"" ADD ""UnitPrice"" DECIMAL(18,2) NOT NULL DEFAULT 0;
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'PriceListItems' AND COLUMN_NAME = 'Unit') ALTER TABLE ""PriceListItems"" ADD ""Unit"" NVARCHAR(50) NOT NULL DEFAULT 'pauschal';
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'TeamMembers' AND COLUMN_NAME = 'DeletedAt') ALTER TABLE ""TeamMembers"" ADD ""DeletedAt"" DATETIMEOFFSET(7);
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Products' AND COLUMN_NAME = 'DeletedAt') ALTER TABLE ""Products"" ADD ""DeletedAt"" DATETIMEOFFSET(7);
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'PriceLists' AND COLUMN_NAME = 'DeletedAt') ALTER TABLE ""PriceLists"" ADD ""DeletedAt"" DATETIMEOFFSET(7);
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'PriceListItems' AND COLUMN_NAME = 'DeletedAt') ALTER TABLE ""PriceListItems"" ADD ""DeletedAt"" DATETIMEOFFSET(7);
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Opportunities')
BEGIN
    CREATE TABLE ""Opportunities"" (
        ""Id"" UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        ""CustomerId"" UNIQUEIDENTIFIER NOT NULL REFERENCES ""Customers""(""Id"") ON DELETE CASCADE,
        ""ContactId"" UNIQUEIDENTIFIER REFERENCES ""Contacts""(""Id"") ON DELETE SET NULL,
        ""Title"" NVARCHAR(MAX) NOT NULL DEFAULT '',
        ""Stage"" INT NOT NULL DEFAULT 0,
        ""Probability"" INT NOT NULL DEFAULT 10,
        ""ExpectedRevenue"" DECIMAL(18,2) NOT NULL DEFAULT 0,
        ""CloseDate"" DATETIMEOFFSET(7),
        ""AssignedToTeamMemberId"" UNIQUEIDENTIFIER REFERENCES ""TeamMembers""(""Id"") ON DELETE SET NULL,
        ""Source"" INT,
        ""LostReason"" NVARCHAR(MAX),
        ""QuoteId"" UNIQUEIDENTIFIER REFERENCES ""Quotes""(""Id"") ON DELETE SET NULL,
        ""Notes"" NVARCHAR(MAX),
        ""CreatedAt"" DATETIMEOFFSET(7) NOT NULL DEFAULT SYSDATETIMEOFFSET(),
        ""UpdatedAt"" DATETIMEOFFSET(7),
        ""CreatedBy"" NVARCHAR(MAX),
        ""UpdatedBy"" NVARCHAR(MAX),
        ""IsDeleted"" BIT NOT NULL DEFAULT 0,
        ""DeletedAt"" DATETIMEOFFSET(7),
        CONSTRAINT ""PK_Opportunities"" PRIMARY KEY (""Id"")
    );
END;
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'SupportTickets')
BEGIN
    CREATE TABLE ""SupportTickets"" (
        ""Id"" UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        ""CustomerId"" UNIQUEIDENTIFIER NOT NULL REFERENCES ""Customers""(""Id"") ON DELETE CASCADE,
        ""SubscriptionId"" UNIQUEIDENTIFIER REFERENCES ""CustomerSubscriptions""(""Id"") ON DELETE SET NULL,
        ""Title"" NVARCHAR(MAX) NOT NULL DEFAULT '',
        ""Description"" NVARCHAR(MAX),
        ""Priority"" INT NOT NULL DEFAULT 1,
        ""Status"" INT NOT NULL DEFAULT 0,
        ""Category"" INT NOT NULL DEFAULT 3,
        ""AssignedToTeamMemberId"" UNIQUEIDENTIFIER REFERENCES ""TeamMembers""(""Id"") ON DELETE SET NULL,
        ""SlaDueDate"" DATETIMEOFFSET(7),
        ""ResolvedAt"" DATETIMEOFFSET(7),
        ""CreatedAt"" DATETIMEOFFSET(7) NOT NULL DEFAULT SYSDATETIMEOFFSET(),
        ""UpdatedAt"" DATETIMEOFFSET(7),
        ""CreatedBy"" NVARCHAR(MAX),
        ""UpdatedBy"" NVARCHAR(MAX),
        ""IsDeleted"" BIT NOT NULL DEFAULT 0,
        ""DeletedAt"" DATETIMEOFFSET(7),
        CONSTRAINT ""PK_SupportTickets"" PRIMARY KEY (""Id"")
    );
END;
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'TicketComments')
BEGIN
    CREATE TABLE ""TicketComments"" (
        ""Id"" UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        ""TicketId"" UNIQUEIDENTIFIER NOT NULL REFERENCES ""SupportTickets""(""Id"") ON DELETE CASCADE,
        ""Content"" NVARCHAR(MAX) NOT NULL DEFAULT '',
        ""AuthorName"" NVARCHAR(MAX),
        ""IsInternal"" BIT NOT NULL DEFAULT 0,
        ""CreatedAt"" DATETIMEOFFSET(7) NOT NULL DEFAULT SYSDATETIMEOFFSET(),
        ""UpdatedAt"" DATETIMEOFFSET(7),
        ""CreatedBy"" NVARCHAR(MAX),
        ""UpdatedBy"" NVARCHAR(MAX),
        ""IsDeleted"" BIT NOT NULL DEFAULT 0,
        ""DeletedAt"" DATETIMEOFFSET(7),
        CONSTRAINT ""PK_TicketComments"" PRIMARY KEY (""Id"")
    );
END;
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'CrmActivities')
BEGIN
    CREATE TABLE ""CrmActivities"" (
        ""Id"" UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        ""CustomerId"" UNIQUEIDENTIFIER REFERENCES ""Customers""(""Id"") ON DELETE SET NULL,
        ""ProjectId"" UNIQUEIDENTIFIER REFERENCES ""Projects""(""Id"") ON DELETE SET NULL,
        ""OpportunityId"" UNIQUEIDENTIFIER REFERENCES ""Opportunities""(""Id"") ON DELETE SET NULL,
        ""TicketId"" UNIQUEIDENTIFIER REFERENCES ""SupportTickets""(""Id"") ON DELETE SET NULL,
        ""Type"" INT NOT NULL DEFAULT 0,
        ""Subject"" NVARCHAR(MAX) NOT NULL DEFAULT '',
        ""Description"" NVARCHAR(MAX),
        ""Status"" INT NOT NULL DEFAULT 0,
        ""DueDate"" DATETIMEOFFSET(7),
        ""CompletedAt"" DATETIMEOFFSET(7),
        ""Duration"" INT,
        ""Direction"" INT,
        ""AssignedToTeamMemberId"" UNIQUEIDENTIFIER REFERENCES ""TeamMembers""(""Id"") ON DELETE SET NULL,
        ""CreatedAt"" DATETIMEOFFSET(7) NOT NULL DEFAULT SYSDATETIMEOFFSET(),
        ""UpdatedAt"" DATETIMEOFFSET(7),
        ""CreatedBy"" NVARCHAR(MAX),
        ""UpdatedBy"" NVARCHAR(MAX),
        ""IsDeleted"" BIT NOT NULL DEFAULT 0,
        ""DeletedAt"" DATETIMEOFFSET(7),
        CONSTRAINT ""PK_CrmActivities"" PRIMARY KEY (""Id"")
    );
END;
");
    }

    static async Task SeedCompanySettings(AppDbContext db)
    {
        if (await db.CompanySettings.AnyAsync()) return;
        db.CompanySettings.Add(new CompanySettings { CompanyName = "Gentle Webdesign", LegalName = "Gentle Webdesign UG (haftungsbeschraenkt)", Street = "Musterstr. 1", ZipCode = "42103", City = "Wuppertal", Country = "Deutschland", Email = "info@gentle-webdesign.de", Website = "https://gentle-webdesign.de", TaxId = "123/456/78901", VatId = "DE123456789", BankName = "Sparkasse Wuppertal", Iban = "DE89 3705 0198 0000 0000 00", Bic = "COLSDE33", ManagingDirector = "Berk-Can", DefaultTaxMode = TaxMode.Standard, AccountingMode = AccountingMode.EUR, InvoicePaymentTermDays = 14, QuoteValidityDays = 30, InvoiceIntroTemplate = "vielen Dank fuer Ihren Auftrag. Hiermit stellen wir Ihnen folgende Leistungen in Rechnung:", InvoiceOutroTemplate = "Vielen Dank fuer die gute Zusammenarbeit!", QuoteIntroTemplate = "vielen Dank fuer Ihr Interesse. Gerne unterbreiten wir Ihnen folgendes Angebot:", QuoteOutroTemplate = "Wir freuen uns auf eine Zusammenarbeit!" });
        await db.SaveChangesAsync();
    }

    static async Task SeedServiceCatalog(AppDbContext db)
    {
        if (await db.ServiceCategories.AnyAsync()) return;
        var cats = new List<ServiceCategory>
        {
            new(){Name="Webdesign",SortOrder=1,Items=new(){new(){Name="Webdesign Projektpaket",ShortCode="WEB",DefaultPrice=4500,DefaultLineType=QuoteLineType.OneTime,SortOrder=1},new(){Name="Landingpage",ShortCode="LP",DefaultPrice=1200,DefaultLineType=QuoteLineType.OneTime,SortOrder=2}}},
            new(){Name="Development",SortOrder=2,Items=new(){new(){Name="Custom Development",ShortCode="DEV",DefaultPrice=120,DefaultLineType=QuoteLineType.OneTime,SortOrder=1},new(){Name="GentleLink Buchungssystem",ShortCode="LINK",DefaultPrice=149,DefaultLineType=QuoteLineType.RecurringMonthly,SortOrder=2}}},
            new(){Name="Branding",SortOrder=3,Items=new(){new(){Name="Logo Design",ShortCode="LOGO",DefaultPrice=800,DefaultLineType=QuoteLineType.OneTime,SortOrder=1},new(){Name="Corporate Identity",ShortCode="CI",DefaultPrice=2500,DefaultLineType=QuoteLineType.OneTime,SortOrder=2}}},
            new(){Name="SEO",SortOrder=4,Items=new(){new(){Name="SEO Audit",ShortCode="SEOAUDIT",DefaultPrice=600,DefaultLineType=QuoteLineType.OneTime,SortOrder=1},new(){Name="SEO Monatlich",ShortCode="SEOMONTH",DefaultPrice=499,DefaultLineType=QuoteLineType.RecurringMonthly,SortOrder=2}}},
            new(){Name="Performance & Tracking",SortOrder=5,Items=new(){new(){Name="Tracking Setup",ShortCode="TRACK",DefaultPrice=450,DefaultLineType=QuoteLineType.OneTime,SortOrder=1}}},
            new(){Name="Content & Social Media",SortOrder=6,Items=new(){new(){Name="Social Media Management",ShortCode="SMM",DefaultPrice=799,DefaultLineType=QuoteLineType.RecurringMonthly,SortOrder=1}}},
            new(){Name="Ads",SortOrder=7,Items=new(){new(){Name="Google Ads Setup",ShortCode="GADS",DefaultPrice=500,DefaultLineType=QuoteLineType.OneTime,SortOrder=1},new(){Name="Ads Management",ShortCode="ADSMGMT",DefaultPrice=399,DefaultLineType=QuoteLineType.RecurringMonthly,SortOrder=2}}},
            new(){Name="Wartung & Hosting",SortOrder=8,Items=new(){new(){Name="GentleSuite Care",ShortCode="CARE",Description="Technische Wartung monatlich",DefaultPrice=79,DefaultLineType=QuoteLineType.RecurringMonthly,SortOrder=1},new(){Name="GentleSuite Unlimited Care",ShortCode="UCARE",Description="Wartung ohne Limit (Fair-Use)",DefaultPrice=199,DefaultLineType=QuoteLineType.RecurringMonthly,SortOrder=2},new(){Name="Hosting",ShortCode="HOST",DefaultPrice=29,DefaultLineType=QuoteLineType.RecurringMonthly,SortOrder=3}}},
            new(){Name="Email-Marketing",SortOrder=9,Items=new(){new(){Name="Newsletter Setup",ShortCode="NL",DefaultPrice=400,DefaultLineType=QuoteLineType.OneTime,SortOrder=1}}},
            new(){Name="CRM & Automations",SortOrder=10,Items=new(){new(){Name="CRM Setup",ShortCode="CRM",DefaultPrice=3000,DefaultLineType=QuoteLineType.OneTime,SortOrder=1}}}
        };
        db.ServiceCategories.AddRange(cats); await db.SaveChangesAsync();
    }

    static async Task SeedOnboardingTemplates(AppDbContext db)
    {
        if (await db.OnboardingWorkflowTemplates.AnyAsync()) return;
        var t = new OnboardingWorkflowTemplate{Name="Webdesign Standard",IsDefault=true,Steps=new()
        {
            new(){Title="Kickoff planen",SortOrder=1,DefaultDurationDays=2,TaskTemplates=new(){new(){Title="Kickoff-Termin vorschlagen",SortOrder=1},new(){Title="Agenda vorbereiten",SortOrder=2}}},
            new(){Title="Brand Assets anfordern",SortOrder=2,DefaultDurationDays=5,TaskTemplates=new(){new(){Title="Logo (SVG/AI) anfordern",SortOrder=1},new(){Title="Farbpalette anfordern",SortOrder=2},new(){Title="Bildmaterial einsammeln",SortOrder=3}}},
            new(){Title="Zugaenge sammeln",SortOrder=3,DefaultDurationDays=3,TaskTemplates=new(){new(){Title="Domain-Zugaenge",SortOrder=1},new(){Title="Hosting-Zugaenge",SortOrder=2},new(){Title="Analytics Zugang",SortOrder=3}}},
            new(){Title="Sitemap & Struktur",SortOrder=4,DefaultDurationDays=3,TaskTemplates=new(){new(){Title="Sitemap erstellen",SortOrder=1},new(){Title="Navigation definieren",SortOrder=2}}},
            new(){Title="Design Entwuerfe",SortOrder=5,DefaultDurationDays=7,TaskTemplates=new(){new(){Title="Wireframes",SortOrder=1},new(){Title="Design Desktop",SortOrder=2},new(){Title="Design Mobile",SortOrder=3}}},
            new(){Title="Feedback Runde",SortOrder=6,DefaultDurationDays=5},
            new(){Title="Development",SortOrder=7,DefaultDurationDays=10,TaskTemplates=new(){new(){Title="Projekt-Setup",SortOrder=1},new(){Title="Seiten implementieren",SortOrder=2},new(){Title="Responsive",SortOrder=3}}},
            new(){Title="QA & Performance",SortOrder=8,DefaultDurationDays=3},
            new(){Title="GoLive",SortOrder=9,DefaultDurationDays=1},
            new(){Title="Uebergabe & Wartung",SortOrder=10,DefaultDurationDays=2}
        }};
        var seo = new OnboardingWorkflowTemplate{Name="SEO / Online Marketing",Steps=new()
        {
            new(){Title="SEO-Audit & Ist-Analyse",SortOrder=1,DefaultDurationDays=3,TaskTemplates=new(){new(){Title="Website-Audit durchfuehren",SortOrder=1},new(){Title="Wettbewerbsanalyse",SortOrder=2}}},
            new(){Title="Keyword-Recherche",SortOrder=2,DefaultDurationDays=5,TaskTemplates=new(){new(){Title="Keyword-Liste erstellen",SortOrder=1},new(){Title="Suchvolumen & Priorisierung",SortOrder=2}}},
            new(){Title="OnPage-Optimierung",SortOrder=3,DefaultDurationDays=7,TaskTemplates=new(){new(){Title="Meta-Tags optimieren",SortOrder=1},new(){Title="Content-Optimierung",SortOrder=2},new(){Title="Technische SEO",SortOrder=3}}},
            new(){Title="Content-Erstellung",SortOrder=4,DefaultDurationDays=10,TaskTemplates=new(){new(){Title="Texte erstellen",SortOrder=1},new(){Title="Bilder & Medien",SortOrder=2},new(){Title="Blog-Artikel",SortOrder=3}}},
            new(){Title="OffPage / Linkbuilding",SortOrder=5,DefaultDurationDays=14,TaskTemplates=new(){new(){Title="Backlink-Strategie",SortOrder=1},new(){Title="Outreach",SortOrder=2}}},
            new(){Title="Ads-Setup",SortOrder=6,DefaultDurationDays=5,TaskTemplates=new(){new(){Title="Google Ads Kampagnen",SortOrder=1},new(){Title="Tracking einrichten",SortOrder=2}}},
            new(){Title="Reporting & Optimierung",SortOrder=7,DefaultDurationDays=3,TaskTemplates=new(){new(){Title="Dashboard einrichten",SortOrder=1},new(){Title="KPIs definieren",SortOrder=2},new(){Title="Bericht erstellen",SortOrder=3}}}
        }};
        var branding = new OnboardingWorkflowTemplate{Name="Branding / Corporate Design",Steps=new()
        {
            new(){Title="Briefing & Analyse",SortOrder=1,DefaultDurationDays=3,TaskTemplates=new(){new(){Title="Briefing-Gespraech",SortOrder=1},new(){Title="Zielgruppe definieren",SortOrder=2},new(){Title="Wettbewerbsanalyse",SortOrder=3}}},
            new(){Title="Moodboard & Inspiration",SortOrder=2,DefaultDurationDays=5,TaskTemplates=new(){new(){Title="Stilrichtung definieren",SortOrder=1},new(){Title="Farbwelten",SortOrder=2},new(){Title="Referenzen sammeln",SortOrder=3}}},
            new(){Title="Logo-Entwuerfe",SortOrder=3,DefaultDurationDays=7,TaskTemplates=new(){new(){Title="3 Konzepte erstellen",SortOrder=1},new(){Title="Praesentation vorbereiten",SortOrder=2}}},
            new(){Title="Feedback & Revision",SortOrder=4,DefaultDurationDays=5,TaskTemplates=new(){new(){Title="Kundenabstimmung",SortOrder=1},new(){Title="Anpassungen umsetzen",SortOrder=2}}},
            new(){Title="Styleguide erstellen",SortOrder=5,DefaultDurationDays=5,TaskTemplates=new(){new(){Title="Farben & Typografie",SortOrder=1},new(){Title="Bildsprache",SortOrder=2},new(){Title="Do/Don'ts",SortOrder=3}}},
            new(){Title="Geschaeftsausstattung",SortOrder=6,DefaultDurationDays=7,TaskTemplates=new(){new(){Title="Visitenkarten",SortOrder=1},new(){Title="Briefpapier",SortOrder=2},new(){Title="E-Mail-Signatur",SortOrder=3}}},
            new(){Title="Druckdaten & Dateien",SortOrder=7,DefaultDurationDays=3,TaskTemplates=new(){new(){Title="Reinzeichnung",SortOrder=1},new(){Title="Formate & Uebergabe",SortOrder=2}}},
            new(){Title="Abschluss & Dokumentation",SortOrder=8,DefaultDurationDays=2,TaskTemplates=new(){new(){Title="Brand-Guide finalisieren",SortOrder=1},new(){Title="Schulung",SortOrder=2}}}
        }};
        var social = new OnboardingWorkflowTemplate{Name="Social Media",Steps=new()
        {
            new(){Title="Strategie & Ziele",SortOrder=1,DefaultDurationDays=3,TaskTemplates=new(){new(){Title="Zielgruppe definieren",SortOrder=1},new(){Title="Plattformen auswaehlen",SortOrder=2},new(){Title="KPIs festlegen",SortOrder=3}}},
            new(){Title="Account-Setup",SortOrder=2,DefaultDurationDays=3,TaskTemplates=new(){new(){Title="Profile erstellen",SortOrder=1},new(){Title="Branding & Bio",SortOrder=2}}},
            new(){Title="Content-Strategie",SortOrder=3,DefaultDurationDays=5,TaskTemplates=new(){new(){Title="Themen definieren",SortOrder=1},new(){Title="Formate festlegen",SortOrder=2},new(){Title="Tonalitaet bestimmen",SortOrder=3}}},
            new(){Title="Redaktionsplan",SortOrder=4,DefaultDurationDays=5,TaskTemplates=new(){new(){Title="Monatsplan erstellen",SortOrder=1},new(){Title="Posting-Frequenz festlegen",SortOrder=2}}},
            new(){Title="Content-Produktion",SortOrder=5,DefaultDurationDays=10,TaskTemplates=new(){new(){Title="Grafiken erstellen",SortOrder=1},new(){Title="Texte schreiben",SortOrder=2},new(){Title="Videos produzieren",SortOrder=3}}},
            new(){Title="Ads-Setup",SortOrder=6,DefaultDurationDays=5,TaskTemplates=new(){new(){Title="Zielgruppen definieren",SortOrder=1},new(){Title="Budget & Creatives",SortOrder=2}}},
            new(){Title="Reporting & Optimierung",SortOrder=7,DefaultDurationDays=3,TaskTemplates=new(){new(){Title="Analytics einrichten",SortOrder=1},new(){Title="Bericht erstellen",SortOrder=2}}}
        }};
        var app = new OnboardingWorkflowTemplate{Name="App-Entwicklung",Steps=new()
        {
            new(){Title="Requirements & Spezifikation",SortOrder=1,DefaultDurationDays=5,TaskTemplates=new(){new(){Title="Anforderungen erfassen",SortOrder=1},new(){Title="User Stories schreiben",SortOrder=2}}},
            new(){Title="UX-Konzept",SortOrder=2,DefaultDurationDays=7,TaskTemplates=new(){new(){Title="Wireframes erstellen",SortOrder=1},new(){Title="User Flows definieren",SortOrder=2},new(){Title="Prototyp bauen",SortOrder=3}}},
            new(){Title="UI-Design",SortOrder=3,DefaultDurationDays=7,TaskTemplates=new(){new(){Title="Screens designen",SortOrder=1},new(){Title="Design System aufbauen",SortOrder=2},new(){Title="Animationen planen",SortOrder=3}}},
            new(){Title="Projekt-Setup",SortOrder=4,DefaultDurationDays=3,TaskTemplates=new(){new(){Title="Repository anlegen",SortOrder=1},new(){Title="CI/CD einrichten",SortOrder=2},new(){Title="Architektur definieren",SortOrder=3}}},
            new(){Title="Frontend-Entwicklung",SortOrder=5,DefaultDurationDays=14,TaskTemplates=new(){new(){Title="Screens implementieren",SortOrder=1},new(){Title="Navigation & Routing",SortOrder=2}}},
            new(){Title="Backend-Entwicklung",SortOrder=6,DefaultDurationDays=14,TaskTemplates=new(){new(){Title="API entwickeln",SortOrder=1},new(){Title="Datenbank aufsetzen",SortOrder=2},new(){Title="Auth implementieren",SortOrder=3}}},
            new(){Title="Testing & QA",SortOrder=7,DefaultDurationDays=7,TaskTemplates=new(){new(){Title="Funktionstests",SortOrder=1},new(){Title="Performance-Tests",SortOrder=2},new(){Title="Bugfixes",SortOrder=3}}},
            new(){Title="App Store Submission",SortOrder=8,DefaultDurationDays=3,TaskTemplates=new(){new(){Title="Screenshots & Beschreibung",SortOrder=1},new(){Title="Review einreichen",SortOrder=2}}},
            new(){Title="Launch & Wartung",SortOrder=9,DefaultDurationDays=2,TaskTemplates=new(){new(){Title="Release durchfuehren",SortOrder=1},new(){Title="Monitoring einrichten",SortOrder=2}}}
        }};
        db.OnboardingWorkflowTemplates.AddRange(t, seo, branding, social, app); await db.SaveChangesAsync();
    }

    static async Task SeedEmailTemplates(AppDbContext db)
    {
        if (await db.EmailTemplates.AnyAsync()) return;
        db.EmailTemplates.AddRange(
            new EmailTemplate{Key="welcome",Subject="Willkommen bei {{ CompanyName }} – Ihre naechsten Schritte",Body="<div style='font-family:Arial,sans-serif;max-width:600px;margin:0 auto'><h2 style='color:#1a1a1a'>Herzlich willkommen, {{ ContactName }}!</h2><p>Vielen Dank, dass sich <b>{{ CustomerName }}</b> fuer eine Zusammenarbeit mit <b>{{ CompanyName }}</b> entschieden hat. Wir freuen uns sehr darauf, gemeinsam Ihr Projekt umzusetzen.</p><h3 style='color:#333;margin-top:24px'>So geht es weiter:</h3><ol style='line-height:1.8'><li><b>Onboarding:</b> Ihr persoenlicher Projektablauf wurde bereits angelegt. Wir werden Sie Schritt fuer Schritt durch den Prozess fuehren.</li><li><b>Kick-off:</b> In Kuerze erhalten Sie eine Einladung zu unserem gemeinsamen Kick-off-Gespraech.</li><li><b>Zugaenge:</b> Bitte halten Sie relevante Zugangsdaten (Domain, Hosting, Analytics etc.) bereit.</li></ol><p style='margin-top:24px'>Bei Fragen stehen wir Ihnen jederzeit gerne zur Verfuegung:</p><table style='margin:12px 0;font-size:14px'><tr><td style='padding:4px 12px 4px 0;color:#666'>E-Mail:</td><td>{{ CompanyEmail }}</td></tr><tr><td style='padding:4px 12px 4px 0;color:#666'>Telefon:</td><td>{{ CompanyPhone }}</td></tr></table><p style='margin-top:24px'>Wir freuen uns auf eine erfolgreiche Zusammenarbeit!</p><p>Mit freundlichen Gruessen<br/><b>{{ CompanyName }}</b></p></div>"},
            new EmailTemplate{Key="quote-sent",Subject="Angebot {{ QuoteNumber }}",Body="<h1>Ihr Angebot</h1><p>Hallo {{ ContactName }},</p><p>Ihr Angebot <b>{{ QuoteNumber }}</b> ueber <b>{{ Total }} EUR</b> ist bereit.</p><p><a href='{{ ApprovalLink }}'>Angebot ansehen & bestaetigen</a></p><p>Gueltig bis: {{ ExpiresAt }}</p>"},
            new EmailTemplate{Key="invoice-sent",Subject="Rechnung {{ InvoiceNumber }}",Body="<h1>Ihre Rechnung</h1><p>Hallo {{ ContactName }},</p><p>anbei Ihre Rechnung <b>{{ InvoiceNumber }}</b> ueber <b>{{ Amount }} EUR</b>.</p><p>Faellig bis: {{ DueDate }}</p>"},
            new EmailTemplate{Key="invoice-reminder-1",Subject="Zahlungserinnerung: {{ InvoiceNumber }}",Body="<p>Hallo {{ ContactName }},</p><p>die Rechnung {{ InvoiceNumber }} ueber {{ Amount }} EUR ist seit dem {{ DueDate }} faellig. Bitte ueberweisen Sie den Betrag.</p>"},
            new EmailTemplate{Key="invoice-reminder-2",Subject="2. Mahnung: {{ InvoiceNumber }}",Body="<p>Hallo {{ ContactName }},</p><p>trotz unserer Erinnerung ist die Rechnung {{ InvoiceNumber }} noch offen. Bitte begleichen Sie {{ Amount }} EUR umgehend.</p>"},
            new EmailTemplate{Key="invoice-reminder-3",Subject="Letzte Mahnung: {{ InvoiceNumber }}",Body="<p>Hallo {{ ContactName }},</p><p>dies ist unsere letzte Mahnung fuer Rechnung {{ InvoiceNumber }}. Bei Nichtzahlung behalten wir uns weitere Schritte vor.</p>"},
            new EmailTemplate{Key="quote-reminder",Subject="Erinnerung: Angebot {{ QuoteNumber }}",Body="<p>Hallo {{ ContactName }},</p><p>Ihr Angebot {{ QuoteNumber }} wartet auf Ihre Rueckmeldung.</p><p><a href='{{ ApprovalLink }}'>Jetzt ansehen</a></p>"},
            new EmailTemplate{Key="invoice-recurring",Subject="Rechnung {{ InvoiceNumber }} – {{ PlanName }}",Body="<div style='font-family:Arial,sans-serif;max-width:600px;margin:0 auto'><h2 style='color:#1a1a1a'>Ihre monatliche Rechnung</h2><p>Hallo {{ ContactName }},</p><p>anbei erhalten Sie Ihre automatische Rechnung <b>{{ InvoiceNumber }}</b> fuer den Wartungsvertrag <b>{{ PlanName }}</b>.</p><p style='font-size:18px;font-weight:bold;color:#027A48'>Betrag: {{ Amount }} EUR</p><p>Zahlbar bis: {{ DueDate }}</p><p style='margin-top:24px'>Bei Fragen stehen wir Ihnen gerne zur Verfuegung.</p><p>Mit freundlichen Gruessen<br/><b>{{ CustomerName }}</b></p></div>"}
        );
        await db.SaveChangesAsync();
    }

    static async Task SeedMissingEmailTemplates(AppDbContext db)
    {
        var existing = await db.EmailTemplates.Select(t => t.Key).ToListAsync();
        if (!existing.Contains("invoice-recurring"))
            db.EmailTemplates.Add(new EmailTemplate{Key="invoice-recurring",Subject="Rechnung {{ InvoiceNumber }} – {{ PlanName }}",Body="<div style='font-family:Arial,sans-serif;max-width:600px;margin:0 auto'><h2 style='color:#1a1a1a'>Ihre monatliche Rechnung</h2><p>Hallo {{ ContactName }},</p><p>anbei erhalten Sie Ihre automatische Rechnung <b>{{ InvoiceNumber }}</b> fuer den Wartungsvertrag <b>{{ PlanName }}</b>.</p><p style='font-size:18px;font-weight:bold;color:#027A48'>Betrag: {{ Amount }} EUR</p><p>Zahlbar bis: {{ DueDate }}</p><p style='margin-top:24px'>Bei Fragen stehen wir Ihnen gerne zur Verfuegung.</p><p>Mit freundlichen Gruessen<br/><b>{{ CustomerName }}</b></p></div>"});
        const string intakeSubject = "Willkommen bei Gentlegroup – Bitte vervollständigen Sie Ihre Angaben";
        const string intakeBody = "<div style='font-family:Arial,sans-serif;max-width:600px;margin:0 auto;padding:32px 24px;background:#f9fafb;border-radius:12px'><div style='background:#ffffff;border-radius:8px;padding:32px;border:1px solid #e5e7eb'><h2 style='color:#1a1a1a;margin-top:0'>Willkommen bei Gentlegroup!</h2><p style='color:#374151;line-height:1.6'>für ein erfolgreiches Onboarding benötigen wir noch einige wichtige Informationen von Ihnen.</p><p style='color:#374151;line-height:1.6'>Der gesamte Vorgang dauert nur wenige Minuten:</p><div style='text-align:center;margin:32px 0'><a href='{{ IntakeUrl }}' style='background:#0f172a;color:#ffffff;padding:14px 28px;border-radius:8px;text-decoration:none;font-weight:600;font-size:16px;display:inline-block'>Hier alle restlichen Infos nachtragen</a></div><p style='color:#6b7280;font-size:13px;line-height:1.6'>Der Link ist personalisiert und nur für Sie bestimmt. Bitte leiten Sie ihn nicht weiter.</p><hr style='border:none;border-top:1px solid #e5e7eb;margin:24px 0'/><p style='color:#374151;font-size:14px;margin:0'>Mit freundlichen Grüßen<br/><b>Berkcan Ünal</b><br/>Gentlegroup – Digital Agency<br/><a href='mailto:office@gentlegroup.de' style='color:#0f172a'>office@gentlegroup.de</a> | <a href='https://www.gentlegroup.de' style='color:#0f172a'>www.gentlegroup.de</a></p></div></div>";
        if (!existing.Contains("customer-intake"))
            db.EmailTemplates.Add(new EmailTemplate{Key="customer-intake",Subject=intakeSubject,Body=intakeBody});
        else
        {
            var t = await db.EmailTemplates.FirstAsync(x => x.Key == "customer-intake");
            t.Subject = intakeSubject;
            t.Body = intakeBody;
        }
        if (!existing.Contains("invoice-reminder"))
            db.EmailTemplates.Add(new EmailTemplate { Key = "invoice-reminder", Subject = "Zahlungserinnerung – Rechnung {{ InvoiceNumber }}", Body = "<div style='font-family:Arial,sans-serif;max-width:600px;margin:0 auto;padding:32px 24px'><div style='background:#fff;border-radius:8px;padding:32px;border:1px solid #e5e7eb'><h2 style='color:#1a1a1a;margin-top:0'>Zahlungserinnerung</h2><p style='color:#374151'>Hallo {{ ContactName }},</p><p style='color:#374151'>wir möchten Sie freundlich daran erinnern, dass folgende Rechnung noch offen ist:</p><table style='width:100%;border-collapse:collapse;margin:16px 0'><tr><td style='padding:8px;border:1px solid #e5e7eb;font-weight:bold'>Rechnungsnummer</td><td style='padding:8px;border:1px solid #e5e7eb'>{{ InvoiceNumber }}</td></tr><tr><td style='padding:8px;border:1px solid #e5e7eb;font-weight:bold'>Betrag</td><td style='padding:8px;border:1px solid #e5e7eb'>{{ Amount }} EUR</td></tr><tr><td style='padding:8px;border:1px solid #e5e7eb;font-weight:bold'>Fälligkeitsdatum</td><td style='padding:8px;border:1px solid #e5e7eb'>{{ DueDate }}</td></tr><tr><td style='padding:8px;border:1px solid #e5e7eb;font-weight:bold'>Überfällig seit</td><td style='padding:8px;border:1px solid #e5e7eb'>{{ DaysOverdue }} Tagen</td></tr></table><p style='color:#374151'>Bitte überweisen Sie den ausstehenden Betrag schnellstmöglich auf unser Konto.</p><p style='color:#374151'>Falls Sie die Zahlung bereits veranlasst haben, bitten wir Sie, diese E-Mail zu ignorieren.</p><hr style='border:none;border-top:1px solid #e5e7eb;margin:24px 0'/><p style='color:#374151;font-size:14px;margin:0'>Mit freundlichen Grüßen<br/><b>Berkcan Ünal</b><br/>Gentlegroup – Digital Agency<br/><a href='mailto:office@gentlegroup.de' style='color:#0f172a'>office@gentlegroup.de</a></p></div></div>" });
        if (!existing.Contains("password-reset"))
            db.EmailTemplates.Add(new EmailTemplate { Key = "password-reset", Subject = "Passwort zurücksetzen – GentleSuite", Body = "<div style='font-family:Arial,sans-serif;max-width:600px;margin:0 auto;padding:32px 24px'><div style='background:#fff;border-radius:8px;padding:32px;border:1px solid #e5e7eb'><h2 style='color:#1a1a1a;margin-top:0'>Passwort zurücksetzen</h2><p style='color:#374151'>Hallo {{ FullName }},</p><p style='color:#374151'>Sie haben eine Anfrage zum Zurücksetzen Ihres Passworts gestellt. Klicken Sie auf den folgenden Button, um ein neues Passwort festzulegen:</p><div style='text-align:center;margin:32px 0'><a href='{{ ResetUrl }}' style='background:#0f172a;color:#ffffff;padding:14px 28px;border-radius:8px;text-decoration:none;font-weight:600;font-size:16px;display:inline-block'>Passwort zurücksetzen</a></div><p style='color:#6b7280;font-size:13px'>Dieser Link ist 24 Stunden gültig. Falls Sie keine Anfrage gestellt haben, können Sie diese E-Mail ignorieren.</p><hr style='border:none;border-top:1px solid #e5e7eb;margin:24px 0'/><p style='color:#374151;font-size:14px;margin:0'>Mit freundlichen Grüßen<br/><b>Berkcan Ünal</b><br/>Gentlegroup – Digital Agency<br/><a href='mailto:office@gentlegroup.de' style='color:#0f172a'>office@gentlegroup.de</a></p></div></div>" });
        await db.SaveChangesAsync();
    }

    static async Task SeedLegalTexts(AppDbContext db)
    {
        if (await db.LegalTextBlocks.AnyAsync()) return;
        db.LegalTextBlocks.AddRange(
            new LegalTextBlock{Key="unlimited-care-fairuse",Title="Wartung & Leistungsumfang (Unlimited Care)",SortOrder=1,Content="Der GentleSuite Unlimited Care Vertrag beinhaltet die technische Wartung und Pflege Ihrer Website im Rahmen einer Fair-Use-Regelung.\n\nInkludiert: Sicherheitsupdates, Plugin-Updates, CMS-Updates, Performance-Monitoring, Backup-Management, SSL-Zertifikat, kleine Textaenderungen, Bildaustausch, Anpassungen bestehender Inhalte, Fehleranalyse und -behebung.\n\nNicht inkludiert: Neuentwicklung von Features/Seiten, Redesign, SEO-Massnahmen, Content-Erstellung, Drittanbieter-Lizenzen, Hosting-Kosten, Marketing-Leistungen.\n\nFair-Use: Die inkludierten Leistungen gelten im Rahmen einer fairen Nutzung. Bei ueberdurchschnittlich hohem Aenderungsaufwand behalten wir uns vor, zusaetzliche Leistungen separat anzubieten."},
            new LegalTextBlock{Key="sla-levels",Title="Service Level Agreement (SLA)",SortOrder=2,Content="S0 - Kritisch: Website nicht erreichbar / Sicherheitsvorfall. Reaktionszeit: 4 Stunden (Geschaeftszeiten).\nS1 - Hoch: Wesentliche Funktion eingeschraenkt. Reaktionszeit: 8 Stunden.\nS2 - Mittel: Nicht-kritische Fehler. Reaktionszeit: 24 Stunden.\nS3 - Niedrig: Optimierungen, Wuensche. Reaktionszeit: 48 Stunden.\n\nGeschaeftszeiten: Mo-Fr 09:00-18:00 Uhr. Notfaelle (S0) auch ausserhalb nach Vereinbarung."}
        );
        await db.SaveChangesAsync();
    }

    static async Task SeedSubscriptionPlans(AppDbContext db)
    {
        if (await db.SubscriptionPlans.AnyAsync()) return;
        var care = new SubscriptionPlan{Name="GentleSuite Care",Description="Technische Wartung & Systempflege",MonthlyPrice=79,IsActive=true};
        var ucare = new SubscriptionPlan{Name="GentleSuite Unlimited Care",Description="Wartung ohne Aenderungslimit (Fair-Use)",MonthlyPrice=199,IsActive=true,
            WorkScopeRule=new WorkScopeRule{FairUseDescription="Unbegrenzte Aenderungen im Fair-Use Rahmen",IncludedItemsJson=JsonSerializer.Serialize(new[]{"Sicherheitsupdates","Plugin-Updates","CMS-Updates","Performance-Monitoring","Backup-Management","Textaenderungen","Bildaustausch","Fehleranalyse"}),ExcludedItemsJson=JsonSerializer.Serialize(new[]{"Neuentwicklung","Redesign","SEO","Content-Erstellung","Marketing"})},
            SupportPolicy=new SupportPolicy{S0ResponseTarget="4 Stunden",S1ResponseTarget="8 Stunden",S2ResponseTarget="24 Stunden",S3ResponseTarget="48 Stunden",S0Description="Website down / Sicherheitsvorfall",S1Description="Wesentliche Funktion eingeschraenkt",S2Description="Nicht-kritische Fehler",S3Description="Optimierungen & Wuensche"}};
        db.SubscriptionPlans.AddRange(care, ucare); await db.SaveChangesAsync();
    }

    static async Task SeedQuoteTemplates(AppDbContext db)
    {
        if (await db.QuoteTemplates.AnyAsync()) return;
        var webCare = await db.ServiceCatalogItems.FirstOrDefaultAsync(i => i.ShortCode == "WEB");
        var ucare = await db.ServiceCatalogItems.FirstOrDefaultAsync(i => i.ShortCode == "UCARE");
        var host = await db.ServiceCatalogItems.FirstOrDefaultAsync(i => i.ShortCode == "HOST");
        db.QuoteTemplates.AddRange(
            new QuoteTemplate{Name="Website + Unlimited Care",Description="Webdesign einmalig + Unlimited Care monatlich",Lines=new(){
                new(){Title="Webdesign Projektpaket",Description="Konzeption, Design & Entwicklung",Quantity=1,UnitPrice=4500,LineType=QuoteLineType.OneTime,ServiceCatalogItemId=webCare?.Id,SortOrder=1},
                new(){Title="GentleSuite Unlimited Care",Description="Monatliche Wartung & Pflege (Fair-Use)",Quantity=1,UnitPrice=199,LineType=QuoteLineType.RecurringMonthly,ServiceCatalogItemId=ucare?.Id,SortOrder=2},
                new(){Title="Managed Hosting",Description="Hosting inkl. SSL & Backups",Quantity=1,UnitPrice=29,LineType=QuoteLineType.RecurringMonthly,ServiceCatalogItemId=host?.Id,SortOrder=3}}},
            new QuoteTemplate{Name="Website + Care",Description="Webdesign einmalig + Care monatlich",Lines=new(){
                new(){Title="Webdesign Projektpaket",Description="Konzeption, Design & Entwicklung",Quantity=1,UnitPrice=4500,LineType=QuoteLineType.OneTime,ServiceCatalogItemId=webCare?.Id,SortOrder=1},
                new(){Title="GentleSuite Care",Description="Monatliche Wartung & Pflege",Quantity=1,UnitPrice=79,LineType=QuoteLineType.RecurringMonthly,SortOrder=2}}}
        );
        await db.SaveChangesAsync();
    }

    static async Task SeedExpenseCategories(AppDbContext db)
    {
        if (await db.ExpenseCategories.AnyAsync()) return;
        db.ExpenseCategories.AddRange(
            new ExpenseCategory{Name="Buerobedarf",AccountNumber="4930",SortOrder=1},
            new ExpenseCategory{Name="Software & Lizenzen",AccountNumber="4964",SortOrder=2},
            new ExpenseCategory{Name="Hosting & Server",AccountNumber="4960",SortOrder=3},
            new ExpenseCategory{Name="Werbung & Marketing",AccountNumber="4600",SortOrder=4},
            new ExpenseCategory{Name="Reisekosten",AccountNumber="4660",SortOrder=5},
            new ExpenseCategory{Name="Telefon & Internet",AccountNumber="4920",SortOrder=6},
            new ExpenseCategory{Name="Fremdleistungen",AccountNumber="4590",SortOrder=7}
        );
        await db.SaveChangesAsync();
    }

    static async Task SeedAccounts(AppDbContext db)
    {
        if (await db.ChartOfAccounts.AnyAsync()) return;
        db.ChartOfAccounts.AddRange(
            new ChartOfAccount{AccountNumber="1200",Name="Bank",Type=AccountType.Asset,IsSystem=true},
            new ChartOfAccount{AccountNumber="1000",Name="Kasse",Type=AccountType.Asset,IsSystem=true},
            new ChartOfAccount{AccountNumber="1400",Name="Forderungen aLuL",Type=AccountType.Asset,IsSystem=true},
            new ChartOfAccount{AccountNumber="1600",Name="Verbindlichkeiten aLuL",Type=AccountType.Liability,IsSystem=true},
            new ChartOfAccount{AccountNumber="1776",Name="Umsatzsteuer 19%",Type=AccountType.Liability,IsSystem=true},
            new ChartOfAccount{AccountNumber="1576",Name="Vorsteuer 19%",Type=AccountType.Asset,IsSystem=true},
            new ChartOfAccount{AccountNumber="8400",Name="Erloese 19% USt",Type=AccountType.Income,IsSystem=true},
            new ChartOfAccount{AccountNumber="8300",Name="Erloese 7% USt",Type=AccountType.Income,IsSystem=true},
            new ChartOfAccount{AccountNumber="4590",Name="Fremdleistungen",Type=AccountType.Expense,IsSystem=true},
            new ChartOfAccount{AccountNumber="4930",Name="Buerobedarf",Type=AccountType.Expense,IsSystem=true},
            new ChartOfAccount{AccountNumber="4960",Name="EDV-Kosten",Type=AccountType.Expense,IsSystem=true}
        );
        await db.SaveChangesAsync();
    }

    static async Task SeedDemoData(AppDbContext db)
    {
        if (await db.Customers.AnyAsync()) return;
        var cust = new Customer{CompanyName="Demo GmbH",CustomerNumber="KD-00001",Industry="IT",Website="https://demo.de",TaxId="111/222/33344",VatId="DE111222333",Status=CustomerStatus.Active,
            Contacts=new(){new(){FirstName="Max",LastName="Mustermann",Email="max@demo.de",Phone="+49 123 456789",Position="Geschaeftsfuehrer",IsPrimary=true}},
            Locations=new(){new(){Label="Hauptsitz",Street="Demostr. 42",City="Wuppertal",ZipCode="42103",IsPrimary=true}}};
        db.Customers.Add(cust); await db.SaveChangesAsync();
    }

    static async Task SeedPriceListTemplates(AppDbContext db)
    {
        if (await db.PriceLists.AnyAsync(p => p.IsTemplate)) return;
        db.PriceLists.AddRange(
            new PriceList { Name = "Webdesign Standard", Description = "Standardpreise für Webdesign-Projekte", IsTemplate = true, Items = new() {
                new() { Title = "Webseite (Landingpage)", Description = "Responsive One-Pager inkl. CMS", Unit = "pauschal", UnitPrice = 1490, SortOrder = 1 },
                new() { Title = "Webseite (5-Seiter)", Description = "Responsive, CMS, Kontaktformular", Unit = "pauschal", UnitPrice = 2490, SortOrder = 2 },
                new() { Title = "Webseite (10-Seiter)", Description = "Responsive, CMS, SEO-Grundoptimierung", Unit = "pauschal", UnitPrice = 3990, SortOrder = 3 },
                new() { Title = "Domain & Hosting", Description = "Inkl. SSL-Zertifikat und E-Mail", Unit = "jährlich", UnitPrice = 199, SortOrder = 4 },
                new() { Title = "SEO-Grundoptimierung", Description = "Meta-Tags, Seitenstruktur, Sitemap", Unit = "pauschal", UnitPrice = 390, SortOrder = 5 },
                new() { Title = "Pflegestunde", Description = "Nachträgliche Änderungen und Erweiterungen", Unit = "h", UnitPrice = 95, SortOrder = 6 }
            }},
            new PriceList { Name = "Softwareentwicklung", Description = "Stundensätze und Pauschalen für Software-Projekte", IsTemplate = true, Items = new() {
                new() { Title = "Anforderungsanalyse & Konzept", Description = "Workshops, Spezifikation, User Stories", Unit = "h", UnitPrice = 95, SortOrder = 1 },
                new() { Title = "Backend-Entwicklung", Description = "API, Datenbankentwicklung, Integrationen", Unit = "h", UnitPrice = 95, SortOrder = 2 },
                new() { Title = "Frontend-Entwicklung", Description = "UI-Implementierung, Responsive, Animationen", Unit = "h", UnitPrice = 90, SortOrder = 3 },
                new() { Title = "Testing & QA", Description = "Funktions-, Integrations- und Regressionstests", Unit = "h", UnitPrice = 75, SortOrder = 4 },
                new() { Title = "Deployment & DevOps", Description = "Server-Setup, CI/CD, Monitoring", Unit = "pauschal", UnitPrice = 390, SortOrder = 5 }
            }},
            new PriceList { Name = "Wartungsvertrag", Description = "Monatliche Wartung und Support-Pakete", IsTemplate = true, Items = new() {
                new() { Title = "Wartung Basis", Description = "Updates, Backup, Security-Monitoring", Unit = "Monat", UnitPrice = 49, SortOrder = 1 },
                new() { Title = "Wartung Standard", Description = "Basis + 2h Support/Monat", Unit = "Monat", UnitPrice = 99, SortOrder = 2 },
                new() { Title = "Wartung Premium", Description = "Standard + 5h Support + Entwicklung", Unit = "Monat", UnitPrice = 199, SortOrder = 3 },
                new() { Title = "Zusatz-Supportstunde", Description = "Über das Paket hinaus", Unit = "h", UnitPrice = 95, SortOrder = 4 }
            }}
        );
        await db.SaveChangesAsync();
    }
}