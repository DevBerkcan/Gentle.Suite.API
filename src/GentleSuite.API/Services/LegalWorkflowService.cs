using GentleSuite.Application.DTOs;
using GentleSuite.Application.Interfaces;
using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;
using GentleSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GentleSuite.Infrastructure.Services;

/// <summary>
/// Legal-Cockpit-style legal checklist per project (anchored on the quote group):
/// 1 Angebot · 2 Agenturvertrag · 3 AV-Vertrag · 4 Rechtstexte der Website · 5 Urheberrechte · 6 Abnahme.
/// Steps 1/2 are derived live from quote + agency contract, 3/4 are manual, 5/6 run through public token links.
/// </summary>
public class LegalWorkflowServiceImpl : ILegalWorkflowService
{
    private readonly AppDbContext _db;
    private readonly IEmailService _email;
    private readonly IActivityLogService _activity;
    private readonly IConfiguration _config;
    private readonly ILogger<LegalWorkflowServiceImpl> _logger;
    private string FrontendBaseUrl => _config["FrontendBaseUrl"] ?? "http://localhost:3000";

    public LegalWorkflowServiceImpl(AppDbContext db, IEmailService email, IActivityLogService activity, IConfiguration config, ILogger<LegalWorkflowServiceImpl> logger)
    { _db = db; _email = email; _activity = activity; _config = config; _logger = logger; }

    public const string RightsTextKey = "urheberrechte-freigabe";
    public const string AcceptanceTextKey = "abnahmeprotokoll";

    static readonly string[] ManualStates = { "open", "done", "not-required" };

    /// <summary>Manual checklist items: key → (step, title, optional, hint).</summary>
    static readonly (string Key, string Step, string Title, bool Optional, string? Hint)[] ManualItems =
    {
        ("offer-attachment", "offer", "Anlage hinzufügen (z. B. Leistungsbeschreibung, Schätzung)", true, null),
        ("av-contract", "av", "AV-Vertrag nach Art. 28 DSGVO abschließen", false, "Nötig, wenn wir personenbezogene Daten des Kunden verarbeiten (Hosting, Wartung mit Zugriff, Formulare, Newsletter …). Sonst \"nicht erforderlich\"."),
        ("site-scan", "website", "Webseite scannen (Cookies, Tracking, eingebundene Dienste)", false, null),
        ("privacy", "website", "Datenschutzerklärung erstellen und einbinden", false, "Mit Generator erstellt — die rechtliche Prüfung liegt laut Vertrag beim Kunden."),
        ("imprint", "website", "Impressum erstellen und einbinden", false, null),
        ("cookie", "website", "Cookie-Consent-Tool einrichten", false, "Nur nötig, wenn nicht notwendige Cookies/Tracking eingesetzt werden."),
        ("consent", "website", "Einwilligungstexte (Formulare, Newsletter)", false, null),
        ("accessibility", "website", "Barrierefreiheitserklärung (BFSG)", false, "Pflicht für viele B2C-Angebote seit 28.06.2025 — Kleinstunternehmen mit Dienstleistungen sind ausgenommen."),
    };

    record ManualState(string State, string? Note, DateTimeOffset? UpdatedAt);

    static string HashToken(string token)
    {
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(token)));
    }

    static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace("+", "").Replace("/", "").Replace("=", "");

    static Dictionary<string, ManualState> ParseManual(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, ManualState>>(json) ?? new(); } catch { return new(); }
    }

    // ---------- loading ----------

    sealed class ProjectData
    {
        public Quote Quote = null!;
        public AgencyContract? Contract;
        public ProjectLegalWorkflow? Workflow;
        public bool AgbAttached;
    }

    async Task<Quote> LoadCurrentQuoteAsync(Guid quoteId, CancellationToken ct)
    {
        var any = await _db.Quotes.AsNoTracking().FirstOrDefaultAsync(q => q.Id == quoteId, ct) ?? throw new KeyNotFoundException("Angebot nicht gefunden.");
        // Always work on the current version of the quote group so the checklist follows new versions.
        return await _db.Quotes.Include(q => q.Customer).ThenInclude(c => c.Contacts).Include(q => q.Lines)
            .Where(q => q.QuoteGroupId == any.QuoteGroupId).OrderByDescending(q => q.IsCurrentVersion).ThenByDescending(q => q.Version)
            .FirstAsync(ct);
    }

    async Task<HashSet<string>> AgbKeysAsync(CancellationToken ct) =>
        (await _db.LegalTextBlocks.Where(b => b.Type == LegalDocumentType.Agb).Select(b => b.Key).ToListAsync(ct)).ToHashSet();

    static bool SnapshotHasAgb(string? snapshotJson, HashSet<string> agbKeys)
    {
        if (string.IsNullOrEmpty(snapshotJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(snapshotJson);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var key = el.TryGetProperty("Key", out var k) ? k.GetString() : null;
                var title = el.TryGetProperty("Title", out var t) ? t.GetString() : null;
                if ((key != null && agbKeys.Contains(key)) || (title != null && title.Contains("Geschäftsbedingungen", StringComparison.OrdinalIgnoreCase)) || (title != null && title.StartsWith("AGB", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        catch { }
        return false;
    }

    async Task<ProjectData> LoadAsync(Guid quoteId, CancellationToken ct)
    {
        var quote = await LoadCurrentQuoteAsync(quoteId, ct);
        var contract = await _db.AgencyContracts.Where(c => c.Quote != null && c.Quote.QuoteGroupId == quote.QuoteGroupId)
            .OrderByDescending(c => c.Status == AgencyContractStatus.FullyExecuted).ThenByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct);
        var wf = await _db.Set<ProjectLegalWorkflow>().FirstOrDefaultAsync(w => w.QuoteGroupId == quote.QuoteGroupId, ct);
        var agbKeys = await AgbKeysAsync(ct);
        return new ProjectData { Quote = quote, Contract = contract, Workflow = wf, AgbAttached = SnapshotHasAgb(quote.LegalTextBlocksSnapshot, agbKeys) };
    }

    async Task<ProjectLegalWorkflow> EnsureWorkflowAsync(ProjectData d, CancellationToken ct)
    {
        if (d.Workflow != null) return d.Workflow;
        d.Workflow = new ProjectLegalWorkflow { QuoteGroupId = d.Quote.QuoteGroupId, CustomerId = d.Quote.CustomerId };
        _db.Set<ProjectLegalWorkflow>().Add(d.Workflow);
        await _db.SaveChangesAsync(ct);
        return d.Workflow;
    }

    // ---------- step computation ----------

    static bool AcceptanceDeemed(ProjectLegalWorkflow? w) =>
        w != null && w.AcceptanceResult == null && w.AcceptanceDeadline.HasValue && w.AcceptanceDeadline.Value < DateTimeOffset.UtcNow;

    static List<LegalWorkflowStepDto> BuildSteps(ProjectData d)
    {
        var q = d.Quote; var c = d.Contract; var w = d.Workflow;
        var manual = ParseManual(w?.ManualStepsJson);
        LegalWorkflowSubStepDto Manual(string key)
        {
            var def = ManualItems.First(m => m.Key == key);
            manual.TryGetValue(key, out var s);
            return new(key, def.Title, s?.State ?? "open", s?.Note, s?.UpdatedAt, true, def.Optional, def.Hint);
        }
        static string Auto(bool done) => done ? "done" : "open";
        var accepted = q.Status is QuoteStatus.Accepted or QuoteStatus.Ordered || q.SignedAt.HasValue;

        var steps = new List<LegalWorkflowStepDto>
        {
            new("offer", 1, "Angebot abgeben", "Angebot mit AGB versenden und auf die Annahme warten.", "", new()
            {
                new("offer-prepared", "Angebot vorbereiten", Auto(q.Lines.Any()), null, q.CreatedAt, false, false),
                Manual("offer-attachment"),
                new("offer-agb", "AGB beifügen", Auto(d.AgbAttached), d.AgbAttached ? "Mit dem Angebot versendet" : (q.SentAt.HasValue ? "Beim Versand waren keine AGB dabei" : "Wird beim Versand automatisch angehängt"), null, false, false),
                new("offer-sent", "Angebot versenden", Auto(q.SentAt.HasValue), null, q.SentAt, false, false),
                new("offer-accepted", "Auf Annahme warten", q.Status == QuoteStatus.Rejected ? "problem" : Auto(accepted), q.Status == QuoteStatus.Rejected ? "Angebot wurde abgelehnt" : null, q.SignedAt ?? q.RespondedAt, false, false),
            }),
            new("contract", 2, "Agenturvertrag abschließen", "Vertrag mit den gebuchten Leistungsmodulen beidseitig unterschreiben.", "", new()
            {
                new("contract-created", "Vertrag aus dem Angebot erstellen", Auto(c != null), c?.ContractNumber, c?.CreatedAt, false, false),
                new("contract-sent", "Unterschreiben und an den Kunden senden", Auto(c?.SentAt != null), null, c?.SentAt, false, false),
                new("contract-signed", "Unterschrift des Kunden", c?.Status == AgencyContractStatus.Declined ? "problem" : Auto(c?.Status == AgencyContractStatus.FullyExecuted),
                    c?.Status == AgencyContractStatus.Declined ? $"Abgelehnt: {c.DeclineReason}" : c?.CustomerSignedByName, c?.CustomerSignedAt, false, false),
            }),
            new("av", 3, "AV-Vertrag", "Auftragsverarbeitung nach Art. 28 DSGVO, falls wir Daten des Kunden verarbeiten.", "", new() { Manual("av-contract") }),
            new("website", 4, "Rechtstexte der Kunden-Website", "Pflichttexte und Einwilligungen auf der Website des Kunden.", "",
                ManualItems.Where(m => m.Step == "website").Select(m => Manual(m.Key)).ToList()),
            new("rights", 5, "Urheberrechte freigeben lassen", "Der Kunde bestätigt die Rechte an gelieferten Inhalten (Texte, Bilder, Logos).", "", new()
            {
                new("rights-requested", "Freigabe beim Kunden anfordern", Auto(w?.RightsRequestedAt != null), w?.RightsRequestedTo, w?.RightsRequestedAt, false, false),
                new("rights-granted", "Freigabe durch den Kunden", Auto(w?.RightsGrantedAt != null), w?.RightsGrantedByName, w?.RightsGrantedAt, false, false),
            }),
            new("acceptance", 6, "Rechtssichere Abnahme", "Abnahmeprotokoll mit Frist — ohne Rückmeldung gilt die Leistung nach Fristablauf als abgenommen.", "", new()
            {
                new("acceptance-requested", "Abnahme anfordern (mit Frist)", Auto(w?.AcceptanceRequestedAt != null), w?.AcceptanceDeadline is { } dl ? $"Frist bis {BerlinTime.LocalDate(dl):dd.MM.yyyy}" : null, w?.AcceptanceRequestedAt, false, false),
                new("acceptance-done", "Abnahme durch den Kunden",
                    w?.AcceptanceResult == "declined" ? "problem" : Auto(w?.AcceptanceResult is "accepted" or "accepted-with-reservations" || AcceptanceDeemed(w)),
                    w?.AcceptanceResult switch
                    {
                        "accepted" => $"Abgenommen von {w.AcceptanceByName}",
                        "accepted-with-reservations" => $"Abgenommen mit Vorbehalten ({w.AcceptanceByName})",
                        "declined" => $"Verweigert: {w.AcceptanceRemarks}",
                        _ => AcceptanceDeemed(w) ? "Frist abgelaufen ohne Rückmeldung — gilt als abgenommen" : null
                    }, w?.AcceptanceRespondedAt ?? (AcceptanceDeemed(w) ? w!.AcceptanceDeadline : null), false, false),
            }),
        };

        return steps.Select(s =>
        {
            var required = s.SubSteps.Where(x => !x.Optional).ToList();
            var state = s.SubSteps.Any(x => x.State == "problem") ? "problem"
                : required.All(x => x.State == "not-required") ? "not-required"
                : required.All(x => x.State is "done" or "not-required") ? "done"
                : s.SubSteps.Any(x => x.State == "done") ? "in-progress" : "open";
            return s with { State = state };
        }).ToList();
    }

    static (int Done, int Total, string? Next, bool Problem) Summarize(List<LegalWorkflowStepDto> steps) =>
        (steps.Count(s => s.State is "done" or "not-required"), steps.Count,
         steps.FirstOrDefault(s => s.State is not ("done" or "not-required"))?.Title,
         steps.Any(s => s.State == "problem"));

    static LegalWorkflowDto ToDto(ProjectData d)
    {
        var steps = BuildSteps(d);
        var (done, total, _, _) = Summarize(steps);
        var q = d.Quote; var w = d.Workflow;
        var contact = q.Customer.Contacts.FirstOrDefault(c => c.IsPrimary) ?? q.Customer.Contacts.FirstOrDefault();
        return new LegalWorkflowDto(q.Id, q.QuoteNumber, q.Subject, q.CustomerId, q.Customer.CompanyName, q.Status, q.GrandTotal,
            d.Contract?.Id, d.Contract?.ContractNumber, contact?.Email, done, total, steps,
            w?.RightsRequestedAt, w?.RightsRequestedTo, w?.RightsGrantedAt, w?.RightsGrantedByName, w?.RightsReferenceConsent ?? false,
            w?.AcceptanceRequestedAt, w?.AcceptanceRequestedTo, w?.AcceptanceDeadline, w?.AcceptanceScope, w?.AcceptanceResult,
            w?.AcceptanceRespondedAt, w?.AcceptanceByName, w?.AcceptanceRemarks, AcceptanceDeemed(w));
    }

    // ---------- admin API ----------

    public async Task<List<LegalWorkflowListItemDto>> GetListAsync(CancellationToken ct)
    {
        var workflows = await _db.Set<ProjectLegalWorkflow>().ToListAsync(ct);
        var wfGroups = workflows.Select(w => w.QuoteGroupId).ToHashSet();
        // Live projects: every current quote except dead ones — those only stay listed once a workflow was started.
        var quotes = await _db.Quotes.Include(q => q.Customer).Include(q => q.Lines)
            .Where(q => q.IsCurrentVersion && q.Status != QuoteStatus.Inactive
                && ((q.Status != QuoteStatus.Rejected && q.Status != QuoteStatus.Expired) || wfGroups.Contains(q.QuoteGroupId)))
            .OrderByDescending(q => q.UpdatedAt ?? q.CreatedAt).Take(300).ToListAsync(ct);
        var groupIds = quotes.Select(q => q.QuoteGroupId).ToList();
        var contracts = await _db.AgencyContracts.Where(c => c.Quote != null && groupIds.Contains(c.Quote.QuoteGroupId))
            .Select(c => new { c, c.Quote!.QuoteGroupId }).ToListAsync(ct);
        var agbKeys = await AgbKeysAsync(ct);

        return quotes.Select(q =>
        {
            var d = new ProjectData
            {
                Quote = q,
                Contract = contracts.Where(x => x.QuoteGroupId == q.QuoteGroupId).Select(x => x.c)
                    .OrderByDescending(c => c.Status == AgencyContractStatus.FullyExecuted).ThenByDescending(c => c.CreatedAt).FirstOrDefault(),
                Workflow = workflows.FirstOrDefault(w => w.QuoteGroupId == q.QuoteGroupId),
                AgbAttached = SnapshotHasAgb(q.LegalTextBlocksSnapshot, agbKeys),
            };
            var (done, total, next, problem) = Summarize(BuildSteps(d));
            var last = new[] { q.UpdatedAt ?? q.CreatedAt, d.Contract?.UpdatedAt ?? d.Contract?.CreatedAt ?? DateTimeOffset.MinValue, d.Workflow?.UpdatedAt ?? d.Workflow?.CreatedAt ?? DateTimeOffset.MinValue }.Max();
            return new LegalWorkflowListItemDto(q.Id, q.QuoteNumber, q.Subject, q.CustomerId, q.Customer.CompanyName, q.Status, done, total, next, problem, last);
        }).OrderByDescending(x => x.LastActivity).ToList();
    }

    public async Task<LegalWorkflowDto> GetAsync(Guid quoteId, CancellationToken ct) => ToDto(await LoadAsync(quoteId, ct));

    public async Task<LegalWorkflowDto> UpdateItemAsync(Guid quoteId, UpdateLegalWorkflowItemRequest req, CancellationToken ct)
    {
        if (ManualItems.All(m => m.Key != req.Key)) throw new ArgumentException("Unbekannter Prüfpunkt.");
        if (!ManualStates.Contains(req.State)) throw new ArgumentException("Ungültiger Status.");
        var d = await LoadAsync(quoteId, ct);
        var w = await EnsureWorkflowAsync(d, ct);
        var manual = ParseManual(w.ManualStepsJson);
        manual[req.Key] = new ManualState(req.State, string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(), DateTimeOffset.UtcNow);
        w.ManualStepsJson = JsonSerializer.Serialize(manual);
        w.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToDto(d);
    }

    (string Email, string FirstName) ResolveRecipient(Quote q, string? requested)
    {
        var contact = q.Customer.Contacts.FirstOrDefault(c => c.IsPrimary) ?? q.Customer.Contacts.FirstOrDefault();
        var email = !string.IsNullOrWhiteSpace(requested) ? requested.Trim() : contact?.Email;
        if (string.IsNullOrWhiteSpace(email)) throw new ArgumentException("Für diesen Kunden ist keine E-Mail-Adresse hinterlegt.");
        var name = contact != null && string.Equals(contact.Email, email, StringComparison.OrdinalIgnoreCase) ? contact.FirstName : "";
        return (email, name);
    }

    static string ProjectTitle(Quote q) => string.IsNullOrWhiteSpace(q.Subject) ? $"Angebot {q.QuoteNumber}" : q.Subject!;

    /// <summary>Sends a templated mail and throws if it could not be delivered. EmailService swallows SMTP errors and only
    /// records them in EmailLogs, so we read the log back — a request must not show as "angefordert" when no mail went out.</summary>
    async Task SendOrThrowAsync(string to, string templateKey, Dictionary<string, object> vars, Guid customerId, CancellationToken ct)
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await _email.SendTemplatedEmailAsync(to, templateKey, vars, customerId, ct: ct);
        var log = await _db.EmailLogs.AsNoTracking().Where(l => l.TemplateKey == templateKey && l.To == to && l.CreatedAt >= before)
            .OrderByDescending(l => l.CreatedAt).FirstOrDefaultAsync(ct);
        if (log == null) throw new InvalidOperationException($"E-Mail-Vorlage \"{templateKey}\" fehlt oder ist deaktiviert.");
        if (log.Status != EmailStatus.Sent) throw new InvalidOperationException($"Die E-Mail an {to} konnte nicht gesendet werden: {log.Error}");
    }

    public async Task<LegalWorkflowDto> SendRightsRequestAsync(Guid quoteId, SendLegalRequestRequest req, CancellationToken ct)
    {
        var d = await LoadAsync(quoteId, ct);
        if (d.Workflow?.RightsGrantedAt != null) throw new InvalidOperationException("Die Urheberrechts-Freigabe liegt bereits vor.");
        var (email, firstName) = ResolveRecipient(d.Quote, req.RecipientEmail);
        var w = await EnsureWorkflowAsync(d, ct);
        var token = NewToken();
        var co = await _db.CompanySettings.FirstOrDefaultAsync(ct);
        await SendOrThrowAsync(email, "legal-rights-release", new Dictionary<string, object>
        {
            ["ContactName"] = string.IsNullOrWhiteSpace(firstName) ? "zusammen" : firstName,
            ["ProjectTitle"] = ProjectTitle(d.Quote),
            ["QuoteNumber"] = d.Quote.QuoteNumber,
            ["Url"] = $"{FrontendBaseUrl}/legal/rights/{token}",
            ["CompanyName"] = co?.CompanyName ?? "",
        }, d.Quote.CustomerId, ct);

        // A new link replaces the previous one.
        w.RightsTokenHash = HashToken(token);
        w.RightsTokenExpiry = DateTimeOffset.UtcNow.AddDays(60);
        w.RightsRequestedAt = DateTimeOffset.UtcNow;
        w.RightsRequestedTo = email;
        w.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _activity.LogAsync(d.Quote.CustomerId, "LegalWorkflow", w.Id, "RightsRequested", $"Urheberrechts-Freigabe für {d.Quote.QuoteNumber} an {email} gesendet", ct: ct);
        return ToDto(d);
    }

    public async Task<LegalWorkflowDto> SendAcceptanceRequestAsync(Guid quoteId, SendLegalRequestRequest req, CancellationToken ct)
    {
        if (req.DeadlineDays < 7 || req.DeadlineDays > 60) throw new ArgumentException("Die Frist muss zwischen 7 und 60 Tagen liegen.");
        var d = await LoadAsync(quoteId, ct);
        if (d.Workflow?.AcceptanceResult is "accepted" or "accepted-with-reservations") throw new InvalidOperationException("Die Abnahme wurde bereits erklärt.");
        var (email, firstName) = ResolveRecipient(d.Quote, req.RecipientEmail);
        var w = await EnsureWorkflowAsync(d, ct);
        var token = NewToken();
        // End of the deadline day in Berlin (e.g. 14 days → until 23:59:59 local time).
        var deadline = BerlinTime.Midnight(BerlinTime.LocalDate(DateTimeOffset.UtcNow).AddDays(req.DeadlineDays + 1)).AddTicks(-1);
        var scope = string.IsNullOrWhiteSpace(req.Scope) ? $"Alle Leistungen aus dem Angebot {d.Quote.QuoteNumber}" : req.Scope.Trim();
        var co = await _db.CompanySettings.FirstOrDefaultAsync(ct);
        await SendOrThrowAsync(email, "legal-acceptance-request", new Dictionary<string, object>
        {
            ["ContactName"] = string.IsNullOrWhiteSpace(firstName) ? "zusammen" : firstName,
            ["ProjectTitle"] = ProjectTitle(d.Quote),
            ["QuoteNumber"] = d.Quote.QuoteNumber,
            ["Scope"] = scope,
            ["Deadline"] = BerlinTime.LocalDate(deadline).ToString("dd.MM.yyyy"),
            ["Url"] = $"{FrontendBaseUrl}/legal/acceptance/{token}",
            ["CompanyName"] = co?.CompanyName ?? "",
        }, d.Quote.CustomerId, ct);

        w.AcceptanceTokenHash = HashToken(token);
        w.AcceptanceTokenExpiry = deadline.AddDays(60);
        w.AcceptanceRequestedAt = DateTimeOffset.UtcNow;
        w.AcceptanceRequestedTo = email;
        w.AcceptanceDeadline = deadline;
        w.AcceptanceScope = scope;
        // A new request after a refusal starts a fresh round.
        w.AcceptanceResult = null; w.AcceptanceRespondedAt = null; w.AcceptanceByName = null; w.AcceptanceByEmail = null;
        w.AcceptanceRemarks = null; w.AcceptanceIp = null; w.AcceptanceSignatureData = null; w.AcceptanceTextSnapshot = null;
        w.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _activity.LogAsync(d.Quote.CustomerId, "LegalWorkflow", w.Id, "AcceptanceRequested", $"Abnahme für {d.Quote.QuoteNumber} angefordert (Frist {BerlinTime.LocalDate(deadline):dd.MM.yyyy})", ct: ct);
        return ToDto(d);
    }

    // ---------- public token pages ----------

    async Task<string> ResolveTextAsync(string key, string fallback, Quote q, string provider, CancellationToken ct)
    {
        var block = await _db.LegalTextBlocks.FirstOrDefaultAsync(b => b.Key == key && b.IsActive, ct);
        var text = block != null && !string.IsNullOrWhiteSpace(block.Content) ? block.Content : fallback;
        return text.Replace("{{Kunde}}", q.Customer.CompanyName).Replace("{{Anbieter}}", provider)
                   .Replace("{{Projekt}}", ProjectTitle(q)).Replace("{{Angebot}}", q.QuoteNumber);
    }

    const string DefaultRightsText =
        "Der Kunde {{Kunde}} bestätigt gegenüber {{Anbieter}} für das Projekt \"{{Projekt}}\" (Angebot {{Angebot}}):\n\n" +
        "1. Der Kunde ist Inhaber der erforderlichen Rechte an allen Inhalten, die er für das Projekt zur Verfügung stellt oder stellen wird (insbesondere Texte, Bilder, Fotos, Logos, Grafiken, Videos, Schriften und Marken), oder hat die Erlaubnis der Rechteinhaber zu deren Nutzung eingeholt. Abgebildete Personen haben in die Veröffentlichung eingewilligt.\n\n" +
        "2. Der Kunde räumt dem Anbieter an diesen Inhalten die für die Durchführung des Projekts erforderlichen, einfachen Nutzungsrechte ein (insbesondere Vervielfältigung, Bearbeitung und öffentliche Zugänglichmachung im Rahmen des Projekts).\n\n" +
        "3. Der Anbieter prüft die gelieferten Inhalte nicht auf Rechte Dritter. Werden Ansprüche Dritter wegen vom Kunden gelieferter Inhalte geltend gemacht, stellt der Kunde den Anbieter nach Maßgabe des Vertrags hiervon frei.";

    const string DefaultAcceptanceText =
        "Der Kunde {{Kunde}} hat die vom Anbieter {{Anbieter}} erbrachten Leistungen für das Projekt \"{{Projekt}}\" (Angebot {{Angebot}}) geprüft.\n\n" +
        "Mit der Abnahme bestätigt der Kunde, dass die Leistungen im Wesentlichen vertragsgemäß erbracht wurden. Unwesentliche Mängel berechtigen nicht zur Verweigerung der Abnahme; sie können unten als Vorbehalt angegeben werden und werden im Rahmen der Gewährleistung behoben.\n\n" +
        "Eine Verweigerung der Abnahme ist nur unter Angabe der wesentlichen Mängel möglich. Erfolgt bis zum Ablauf der Frist keine Rückmeldung, gilt die Leistung gemäß Vertrag als abgenommen.";

    async Task<string> ProviderNameAsync(CancellationToken ct) => (await _db.CompanySettings.FirstOrDefaultAsync(ct))?.CompanyName ?? "Anbieter";

    async Task<(ProjectLegalWorkflow W, Quote Q)?> FindByRightsTokenAsync(string token, CancellationToken ct)
    {
        var hash = HashToken(token);
        var w = await _db.Set<ProjectLegalWorkflow>().FirstOrDefaultAsync(x => x.RightsTokenHash == hash, ct);
        if (w == null || w.RightsTokenExpiry <= DateTimeOffset.UtcNow) return null;
        var q = await _db.Quotes.Include(x => x.Customer).ThenInclude(c => c.Contacts).Where(x => x.QuoteGroupId == w.QuoteGroupId)
            .OrderByDescending(x => x.IsCurrentVersion).ThenByDescending(x => x.Version).FirstAsync(ct);
        return (w, q);
    }

    async Task<(ProjectLegalWorkflow W, Quote Q)?> FindByAcceptanceTokenAsync(string token, CancellationToken ct)
    {
        var hash = HashToken(token);
        var w = await _db.Set<ProjectLegalWorkflow>().FirstOrDefaultAsync(x => x.AcceptanceTokenHash == hash, ct);
        if (w == null || w.AcceptanceTokenExpiry <= DateTimeOffset.UtcNow) return null;
        var q = await _db.Quotes.Include(x => x.Customer).ThenInclude(c => c.Contacts).Where(x => x.QuoteGroupId == w.QuoteGroupId)
            .OrderByDescending(x => x.IsCurrentVersion).ThenByDescending(x => x.Version).FirstAsync(ct);
        return (w, q);
    }

    static (string? Name, string? Email) PrimaryContact(Quote q)
    {
        var c = q.Customer.Contacts.FirstOrDefault(x => x.IsPrimary) ?? q.Customer.Contacts.FirstOrDefault();
        return (c == null ? null : $"{c.FirstName} {c.LastName}".Trim(), c?.Email);
    }

    public async Task<PublicRightsReleaseDto?> GetRightsByTokenAsync(string token, CancellationToken ct)
    {
        var found = await FindByRightsTokenAsync(token, ct);
        if (found == null) return null;
        var (w, q) = found.Value;
        var provider = await ProviderNameAsync(ct);
        var text = w.RightsTextSnapshot ?? await ResolveTextAsync(RightsTextKey, DefaultRightsText, q, provider, ct);
        var (cn, ce) = PrimaryContact(q);
        return new PublicRightsReleaseDto(provider, q.Customer.CompanyName, ProjectTitle(q), q.QuoteNumber, text, w.RightsGrantedAt != null, w.RightsGrantedAt, w.RightsGrantedByName, cn, ce);
    }

    public async Task ConfirmRightsAsync(string token, ConfirmRightsReleaseRequest req, string? ip, CancellationToken ct)
    {
        var found = await FindByRightsTokenAsync(token, ct) ?? throw new KeyNotFoundException("Der Link ist ungültig oder abgelaufen.");
        var (w, q) = found;
        if (w.RightsGrantedAt != null) throw new InvalidOperationException("Die Freigabe wurde bereits erteilt.");
        if (!req.Confirm) throw new InvalidOperationException("Bitte bestätigen Sie die Erklärung.");
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Email)) throw new InvalidOperationException("Name und E-Mail sind erforderlich.");
        var provider = await ProviderNameAsync(ct);
        w.RightsTextSnapshot = await ResolveTextAsync(RightsTextKey, DefaultRightsText, q, provider, ct);
        w.RightsGrantedAt = DateTimeOffset.UtcNow;
        w.RightsGrantedByName = req.Name.Trim();
        w.RightsGrantedByEmail = req.Email.Trim();
        w.RightsGrantedIp = ip;
        w.RightsReferenceConsent = req.ReferenceConsent;
        w.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _activity.LogAsync(q.CustomerId, "LegalWorkflow", w.Id, "RightsGranted", $"Urheberrechts-Freigabe für {q.QuoteNumber} erteilt von {w.RightsGrantedByName}", ct: ct);
    }

    public async Task<PublicAcceptanceDto?> GetAcceptanceByTokenAsync(string token, CancellationToken ct)
    {
        var found = await FindByAcceptanceTokenAsync(token, ct);
        if (found == null) return null;
        var (w, q) = found.Value;
        var provider = await ProviderNameAsync(ct);
        var text = w.AcceptanceTextSnapshot ?? await ResolveTextAsync(AcceptanceTextKey, DefaultAcceptanceText, q, provider, ct);
        var (cn, ce) = PrimaryContact(q);
        return new PublicAcceptanceDto(provider, q.Customer.CompanyName, ProjectTitle(q), q.QuoteNumber, w.AcceptanceScope, text, w.AcceptanceDeadline,
            w.AcceptanceResult, w.AcceptanceRespondedAt, w.AcceptanceByName, cn, ce);
    }

    public async Task ProcessAcceptanceAsync(string token, ProcessAcceptanceRequest req, string? ip, CancellationToken ct)
    {
        var found = await FindByAcceptanceTokenAsync(token, ct) ?? throw new KeyNotFoundException("Der Link ist ungültig oder abgelaufen.");
        var (w, q) = found;
        if (w.AcceptanceResult != null) throw new InvalidOperationException("Über die Abnahme wurde bereits entschieden.");
        if (req.Result is not ("accepted" or "accepted-with-reservations" or "declined")) throw new InvalidOperationException("Ungültige Auswahl.");
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Email)) throw new InvalidOperationException("Name und E-Mail sind erforderlich.");
        if (req.Result != "accepted" && string.IsNullOrWhiteSpace(req.Remarks))
            throw new InvalidOperationException(req.Result == "declined" ? "Bitte nennen Sie die wesentlichen Mängel, wegen derer Sie die Abnahme verweigern." : "Bitte beschreiben Sie Ihre Vorbehalte.");
        if (req.Result != "declined" && string.IsNullOrWhiteSpace(req.SignatureData)) throw new InvalidOperationException("Bitte unterschreiben Sie das Abnahmeprotokoll.");
        var provider = await ProviderNameAsync(ct);
        w.AcceptanceTextSnapshot = await ResolveTextAsync(AcceptanceTextKey, DefaultAcceptanceText, q, provider, ct);
        w.AcceptanceResult = req.Result;
        w.AcceptanceRespondedAt = DateTimeOffset.UtcNow;
        w.AcceptanceByName = req.Name.Trim();
        w.AcceptanceByEmail = req.Email.Trim();
        w.AcceptanceRemarks = string.IsNullOrWhiteSpace(req.Remarks) ? null : req.Remarks.Trim();
        w.AcceptanceSignatureData = req.SignatureData;
        w.AcceptanceIp = ip;
        w.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        var label = req.Result switch { "accepted" => "abgenommen", "accepted-with-reservations" => "mit Vorbehalten abgenommen", _ => "Abnahme verweigert" };
        await _activity.LogAsync(q.CustomerId, "LegalWorkflow", w.Id, "Acceptance", $"{q.QuoteNumber}: {label} von {w.AcceptanceByName}", ct: ct);
    }
}
