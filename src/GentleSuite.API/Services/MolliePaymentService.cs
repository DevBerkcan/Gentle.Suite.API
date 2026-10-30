using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GentleSuite.Application.DTOs;
using GentleSuite.Application.Interfaces;
using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;
using GentleSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GentleSuite.Infrastructure.Services;

/// <summary>Non-2xx answer from the Mollie API. Still an InvalidOperationException (→ HTTP 400 in the
/// global handler), but callers can inspect the status code / Mollie's own error detail.</summary>
public sealed class MollieApiException : InvalidOperationException
{
    public int StatusCode { get; }
    public string? Detail { get; }
    public MollieApiException(int statusCode, string? detail)
        : base($"Mollie-Anfrage fehlgeschlagen ({statusCode}).") { StatusCode = statusCode; Detail = detail; }
}

public sealed class MolliePaymentService : IMolliePaymentService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IEmailService _email;
    private readonly ILogger<MolliePaymentService> _logger;

    private const int MaxCollectionAttempts = 3;
    private const int CollectionRetryDelayDays = 3;
    // Fehlschlag-Mail mit dem (dauerhaften) Einrichtungslink höchstens einmal pro 24 h je Abo.
    private static readonly TimeSpan FailureMailThrottle = TimeSpan.FromHours(24);
    private static readonly string[] InFlightStatuses = { "open", "pending", "authorized" };
    private static readonly string[] TerminalFailureStatuses = { "failed", "expired", "canceled" };

    public MolliePaymentService(AppDbContext db, IHttpClientFactory httpClientFactory, IConfiguration configuration, IEmailService email, ILogger<MolliePaymentService> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _email = email;
        _logger = logger;
    }

    private string FrontendBaseUrl => (_configuration["FrontendBaseUrl"] ?? "https://gentlesuite.vercel.app").TrimEnd('/');
    private string PublicBaseUrl => (_configuration["PublicBaseUrl"] ?? "https://gentlesuite.runasp.net").TrimEnd('/');
    private bool IbanMandatesEnabled => _configuration.GetValue<bool?>("Mollie:IbanMandatesEnabled") ?? true;
    private string SetupUrl(CustomerSubscription s) => $"{FrontendBaseUrl}/payment/setup/{s.MandateSetupToken}";

    /// <summary>Subscription incl. plan/customer/contacts. Ignores the soft-delete filter on the plan on
    /// purpose: a (formerly) deleted plan must never make a running subscription invisible to billing.</summary>
    private IQueryable<CustomerSubscription> SubscriptionsWithDetails() => _db.CustomerSubscriptions.IgnoreQueryFilters()
        .Where(s => !s.IsDeleted && !s.Customer.IsDeleted)
        .Include(s => s.Plan)
        .Include(s => s.Customer).ThenInclude(c => c.Contacts.Where(x => !x.IsDeleted));

    private static Contact? PrimaryContact(CustomerSubscription s)
        => s.Customer.Contacts.FirstOrDefault(c => c.IsPrimary) ?? s.Customer.Contacts.FirstOrDefault();

    private static bool IsValid(string? mandateStatus) => string.Equals(mandateStatus, "valid", StringComparison.OrdinalIgnoreCase);
    private static bool IsEnded(CustomerSubscription s) => s.Status is SubscriptionStatus.Cancelled or SubscriptionStatus.Expired or SubscriptionStatus.Completed;

    private static void EnsureSetupToken(CustomerSubscription s) => s.MandateSetupToken ??= IbanTools.NewToken();

    public async Task<string> EnsureSetupUrlAsync(Guid subscriptionId, CancellationToken ct)
    {
        var subscription = await _db.CustomerSubscriptions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == subscriptionId && !s.IsDeleted, ct)
            ?? throw new KeyNotFoundException("Abonnement wurde nicht gefunden.");
        if (subscription.MandateSetupToken == null)
        {
            EnsureSetupToken(subscription);
            await _db.SaveChangesAsync(ct);
        }
        return SetupUrl(subscription);
    }

    /// <summary>Admin action "Mollie-Checkout erzeugen". Reuses a still-open first payment; never touches
    /// MollieFirstPaymentId once the customer already has a valid mandate (own or adopted).</summary>
    public async Task<MollieMandateCheckoutDto> StartMandateCheckoutAsync(Guid subscriptionId, CancellationToken ct)
    {
        var subscription = await SubscriptionsWithDetails().FirstOrDefaultAsync(s => s.Id == subscriptionId, ct)
            ?? throw new KeyNotFoundException("Abonnement wurde nicht gefunden.");
        if (subscription.Status is SubscriptionStatus.Cancelled or SubscriptionStatus.Expired)
            throw new InvalidOperationException("Für ein beendetes Abonnement kann kein Mandat angefordert werden.");
        EnsureB2bContractEvidence(subscription);
        if (IsValid(subscription.MollieMandateStatus) || await TryAdoptExistingMandateAsync(subscription, ct))
        {
            await _db.SaveChangesAsync(ct);
            throw new InvalidOperationException("Für dieses Abonnement besteht bereits ein gültiges Mollie-Mandat.");
        }

        if (!string.IsNullOrWhiteSpace(subscription.MollieFirstPaymentId))
        {
            var existing = await GetAsync($"payments/{subscription.MollieFirstPaymentId}", ct);
            var existingStatus = RequiredString(existing, "status");
            var existingCheckout = existing["_links"]?["checkout"]?["href"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(existingCheckout) && existingStatus is "open" or "pending")
                return new MollieMandateCheckoutDto(existingCheckout, subscription.MollieFirstPaymentId, existingStatus);
        }

        var checkoutUrl = await CreateFirstPaymentAsync(subscription, ct);
        return new MollieMandateCheckoutDto(checkoutUrl, subscription.MollieFirstPaymentId!, subscription.MollieFirstPaymentStatus ?? "open");
    }

    /// <summary>Creates a FRESH 0,01 € first payment (card/PayPal mandate). Every call is a new attempt with
    /// its own idempotency key, so an expired checkout can never be handed out again.</summary>
    private async Task<string> CreateFirstPaymentAsync(CustomerSubscription subscription, CancellationToken ct)
    {
        var mollieCustomerId = await EnsureMollieCustomerAsync(subscription, ct);
        EnsureSetupToken(subscription);
        subscription.MollieMandateAttempt = Math.Max(0, subscription.MollieMandateAttempt) + 1;
        await _db.SaveChangesAsync(ct);

        var payment = await PostAsync("payments", new
        {
            amount = new { currency = "EUR", value = "0.01" },
            customerId = mollieCustomerId,
            sequenceType = "first",
            description = $"Zahlungseinrichtung {subscription.ContractReference} - {subscription.InstallmentSourceTitle ?? subscription.Plan.Name}",
            redirectUrl = $"{SetupUrl(subscription)}?return=1",
            webhookUrl = $"{PublicBaseUrl}/api/mollie/webhook",
            metadata = new { kind = "mandate", subscriptionId = subscription.Id }
        }, $"mandate-{subscription.Id:N}-{subscription.MollieMandateAttempt}", ct);

        subscription.MollieFirstPaymentId = RequiredString(payment, "id");
        subscription.MollieFirstPaymentStatus = RequiredString(payment, "status");
        await _db.SaveChangesAsync(ct);

        return payment["_links"]?["checkout"]?["href"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Mollie hat keine Checkout-URL zurückgegeben.");
    }

    /// <summary>One Mollie customer per GentleSuite customer: reuses the id stored on the customer or on a
    /// sibling subscription before creating a new one.</summary>
    private async Task<string> EnsureMollieCustomerAsync(CustomerSubscription subscription, CancellationToken ct)
    {
        var id = await ResolveMollieCustomerIdAsync(subscription, ct);
        if (string.IsNullOrWhiteSpace(id))
        {
            var contact = PrimaryContact(subscription);
            var customer = await PostAsync("customers", new
            {
                name = subscription.Customer.CompanyName,
                email = string.IsNullOrWhiteSpace(contact?.Email) ? null : contact!.Email.Trim(),
                locale = "de_DE",
                metadata = new { gentleSuiteCustomerId = subscription.CustomerId }
            }, $"customer-{subscription.CustomerId:N}", ct);
            id = RequiredString(customer, "id");
        }
        subscription.MollieCustomerId = id;
        subscription.Customer.MollieCustomerId ??= id;
        await _db.SaveChangesAsync(ct);
        return id;
    }

    private async Task<string?> ResolveMollieCustomerIdAsync(CustomerSubscription subscription, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(subscription.MollieCustomerId)) return subscription.MollieCustomerId;
        if (!string.IsNullOrWhiteSpace(subscription.Customer.MollieCustomerId)) return subscription.Customer.MollieCustomerId;
        return await _db.CustomerSubscriptions.IgnoreQueryFilters()
            .Where(s => s.CustomerId == subscription.CustomerId && s.Id != subscription.Id && s.MollieCustomerId != null)
            .OrderByDescending(s => s.MollieMandateStatus == "valid").ThenByDescending(s => s.CreatedAt)
            .Select(s => s.MollieCustomerId).FirstOrDefaultAsync(ct);
    }

    /// <summary>Looks for an already valid mandate of the same Mollie customer (e.g. from another
    /// subscription of this customer) and adopts it. Returns true if the subscription is now valid.</summary>
    private async Task<bool> TryAdoptExistingMandateAsync(CustomerSubscription subscription, CancellationToken ct)
    {
        var mollieCustomerId = await ResolveMollieCustomerIdAsync(subscription, ct);
        if (string.IsNullOrWhiteSpace(mollieCustomerId)) return false;
        var mandates = await ListMandatesAsync(mollieCustomerId, ct);
        var valid = PickMandate(mandates, "valid", subscription.Customer.MollieMandateId, subscription.MollieMandateId);
        if (valid != null)
        {
            subscription.MollieCustomerId = mollieCustomerId;
            ApplyMandate(subscription, valid);
            return true;
        }
        var pending = PickMandate(mandates, "pending", subscription.MollieMandateId);
        if (pending != null && !IsValid(subscription.MollieMandateStatus))
        {
            subscription.MollieCustomerId = mollieCustomerId;
            ApplyMandate(subscription, pending);
        }
        return false;
    }

    private async Task<List<JsonNode>> ListMandatesAsync(string mollieCustomerId, CancellationToken ct)
    {
        var list = await GetAsync($"customers/{Uri.EscapeDataString(mollieCustomerId)}/mandates?limit=250", ct);
        return list["_embedded"]?["mandates"]?.AsArray().Where(m => m != null).Select(m => m!).ToList() ?? new List<JsonNode>();
    }

    private static JsonNode? PickMandate(List<JsonNode> mandates, string status, params string?[] preferredIds)
    {
        var candidates = mandates.Where(m => string.Equals(m["status"]?.GetValue<string>(), status, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var id in preferredIds.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var preferred = candidates.FirstOrDefault(m => m["id"]?.GetValue<string>() == id);
            if (preferred != null) return preferred;
        }
        return candidates.OrderByDescending(m => m["createdAt"]?.GetValue<string>()).FirstOrDefault();
    }

    /// <summary>Copies a Mollie mandate onto the subscription (id/status/real method/last 4 digits — never the
    /// full IBAN) and activates a pending subscription once the mandate is valid.</summary>
    private static void ApplyMandate(CustomerSubscription subscription, JsonNode mandate)
    {
        subscription.MollieMandateId = RequiredString(mandate, "id");
        subscription.MollieMandateStatus = RequiredString(mandate, "status");
        subscription.MollieMandateMethod = mandate["method"]?.GetValue<string>() ?? subscription.MollieMandateMethod;
        var details = mandate["details"];
        var last4 = subscription.MollieMandateMethod switch
        {
            "directdebit" => IbanTools.Last4(details?["consumerAccount"]?.GetValue<string>()),
            "creditcard" => IbanTools.Last4(details?["cardNumber"]?.GetValue<string>()),
            _ => null
        };
        if (last4 != null) subscription.MandateAccountLast4 = last4;

        if (IsValid(subscription.MollieMandateStatus))
        {
            subscription.Customer.MollieCustomerId ??= subscription.MollieCustomerId;
            subscription.Customer.MollieMandateId = subscription.MollieMandateId;
            if (subscription.Status == SubscriptionStatus.PendingConfirmation && subscription.BusinessCustomerConfirmed)
            {
                subscription.Status = SubscriptionStatus.Active;
                subscription.ConfirmedAt = DateTimeOffset.UtcNow;
            }
        }
    }

    public async Task<MandateEmailResultDto> SendMandateEmailAsync(Guid subscriptionId, CancellationToken ct)
    {
        var subscription = await SubscriptionsWithDetails().FirstOrDefaultAsync(s => s.Id == subscriptionId, ct)
            ?? throw new KeyNotFoundException("Abonnement wurde nicht gefunden.");

        var contact = PrimaryContact(subscription);
        var recipient = contact?.Email?.Trim();

        // Hat der Kunde bereits ein gültiges Mandat (eigenes oder aus einem anderen Vertrag), braucht er keine
        // Einrichtungs-Mail — das Mandat wird übernommen statt den Kunden ein zweites Mal einrichten zu lassen.
        if (!IsValid(subscription.MollieMandateStatus) && !IsEnded(subscription))
        {
            try
            {
                if (await TryAdoptExistingMandateAsync(subscription, ct))
                {
                    await _db.SaveChangesAsync(ct);
                    _logger.LogInformation("Existing Mollie mandate adopted for subscription {SubscriptionId}; no setup email needed", subscriptionId);
                }
                else await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Mandate adoption check failed for subscription {SubscriptionId}; sending setup email anyway", subscriptionId);
            }
        }
        if (IsValid(subscription.MollieMandateStatus))
            return new MandateEmailResultDto(false, recipient, subscription.MandateEmailSentAt, "NotRequired", "Für diesen Kunden besteht bereits ein gültiges Mollie-Mandat; es wird für diesen Vertrag verwendet.");

        subscription.MandateEmailAttemptCount++;
        subscription.MandateEmailRecipient = recipient;
        subscription.MandateEmailStatus = "Sending";
        subscription.MandateEmailLastError = null;
        EnsureSetupToken(subscription);
        await _db.SaveChangesAsync(ct);

        if (string.IsNullOrWhiteSpace(recipient))
            return await MarkMandateEmailFailedAsync(subscription, "Beim Kunden ist keine E-Mail-Adresse hinterlegt.", ct);

        try
        {
            EnsureB2bContractEvidence(subscription);
            var startedAt = DateTimeOffset.UtcNow;
            var contactName = ContactName(subscription, contact);

            // Dauerhafter Link statt Mollie-Checkout-URL: die Checkout-URL läuft nach ~15 Minuten ab, der
            // Kunde öffnet die Mail aber oft erst Tage später. Die Seite erzeugt bei Bedarf einen frischen Checkout.
            await _email.SendTemplatedEmailAsync(
                recipient,
                "subscription-mandate",
                new Dictionary<string, object>
                {
                    ["ContactName"] = contactName,
                    ["CustomerName"] = subscription.Customer.CompanyName,
                    ["PlanName"] = subscription.InstallmentSourceTitle ?? subscription.Plan.Name,
                    ["MonthlyPrice"] = (subscription.AgreedMonthlyPrice ?? subscription.Plan.MonthlyPrice).ToString("0.00", CultureInfo.GetCultureInfo("de-DE")),
                    ["StartDate"] = subscription.StartDate.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("de-DE")),
                    ["ContractReference"] = subscription.ContractReference ?? "-",
                    ["ContractAcceptedAt"] = subscription.ContractAcceptedAt?.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("de-DE")) ?? "-",
                    ["CheckoutUrl"] = SetupUrl(subscription)
                },
                subscription.CustomerId,
                ct: ct);

            var emailLog = await _db.EmailLogs
                .Where(e => e.TemplateKey == "subscription-mandate" && e.CustomerId == subscription.CustomerId && e.To == recipient && e.CreatedAt >= startedAt.AddMinutes(-1))
                .OrderByDescending(e => e.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (emailLog?.Status != EmailStatus.Sent)
                return await MarkMandateEmailFailedAsync(subscription, emailLog?.Error ?? "Die Einrichtungs-E-Mail konnte nicht versendet werden.", ct);

            subscription.MandateEmailStatus = "Sent";
            subscription.MandateEmailSentAt = emailLog.SentAt ?? DateTimeOffset.UtcNow;
            subscription.MandateEmailLastError = null;
            await _db.SaveChangesAsync(ct);
            return new MandateEmailResultDto(true, recipient, subscription.MandateEmailSentAt, "Sent", null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Mandate email failed for subscription {SubscriptionId}", subscriptionId);
            return await MarkMandateEmailFailedAsync(subscription, ex.Message, ct);
        }
    }

    private static string ContactName(CustomerSubscription subscription, Contact? contact)
    {
        var name = contact == null ? "" : string.Join(" ", new[] { contact.FirstName, contact.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        return string.IsNullOrWhiteSpace(name) ? subscription.Customer.CompanyName : name;
    }

    private async Task<MandateEmailResultDto> MarkMandateEmailFailedAsync(CustomerSubscription subscription, string error, CancellationToken ct)
    {
        subscription.MandateEmailStatus = "Failed";
        subscription.MandateEmailLastError = error.Length > 1000 ? error[..1000] : error;
        await _db.SaveChangesAsync(ct);
        return new MandateEmailResultDto(false, subscription.MandateEmailRecipient, subscription.MandateEmailSentAt, "Failed", subscription.MandateEmailLastError);
    }

    /// <summary>Nudges a customer who hasn't completed the mandate yet, days after the initial
    /// setup email (`SendMandateEmailAsync`). Tracked separately via MandateReminderSentAt/-Count so the
    /// "Mandats-E-Mail" column keeps describing only the first email. Called by ReminderJobs on a cron.
    /// Uses the stable setup link and never creates a Mollie payment itself.</summary>
    public async Task<MandateEmailResultDto> SendMandateReminderEmailAsync(Guid subscriptionId, CancellationToken ct)
    {
        var subscription = await SubscriptionsWithDetails().FirstOrDefaultAsync(s => s.Id == subscriptionId, ct)
            ?? throw new KeyNotFoundException("Abonnement wurde nicht gefunden.");

        var contact = PrimaryContact(subscription);
        var recipient = contact?.Email?.Trim();

        if (string.IsNullOrWhiteSpace(recipient))
            throw new InvalidOperationException("Beim Kunden ist keine E-Mail-Adresse hinterlegt.");
        if (IsValid(subscription.MollieMandateStatus))
            throw new InvalidOperationException("Für dieses Abonnement besteht bereits ein gültiges Mollie-Mandat.");
        if (await TryAdoptExistingMandateAsync(subscription, ct))
        {
            await _db.SaveChangesAsync(ct);
            throw new InvalidOperationException("Für diesen Kunden besteht bereits ein gültiges Mollie-Mandat; es wurde übernommen.");
        }

        EnsureB2bContractEvidence(subscription);
        EnsureSetupToken(subscription);
        await _db.SaveChangesAsync(ct);
        var startedAt = DateTimeOffset.UtcNow;
        var daysWaiting = subscription.MandateEmailSentAt != null
            ? Math.Max(1, (int)Math.Round((DateTimeOffset.UtcNow - subscription.MandateEmailSentAt.Value).TotalDays))
            : 2;

        await _email.SendTemplatedEmailAsync(
            recipient,
            "subscription-mandate-reminder",
            new Dictionary<string, object>
            {
                ["ContactName"] = ContactName(subscription, contact),
                ["CustomerName"] = subscription.Customer.CompanyName,
                ["PlanName"] = subscription.InstallmentSourceTitle ?? subscription.Plan.Name,
                ["MonthlyPrice"] = (subscription.AgreedMonthlyPrice ?? subscription.Plan.MonthlyPrice).ToString("0.00", CultureInfo.GetCultureInfo("de-DE")),
                ["ContractReference"] = subscription.ContractReference ?? "-",
                ["DaysWaiting"] = daysWaiting,
                ["ReminderNumber"] = subscription.MandateReminderCount + 1,
                ["CheckoutUrl"] = SetupUrl(subscription)
            },
            subscription.CustomerId,
            ct: ct);

        var emailLog = await _db.EmailLogs
            .Where(e => e.TemplateKey == "subscription-mandate-reminder" && e.CustomerId == subscription.CustomerId && e.To == recipient && e.CreatedAt >= startedAt.AddMinutes(-1))
            .OrderByDescending(e => e.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (emailLog?.Status != EmailStatus.Sent)
            throw new InvalidOperationException(emailLog?.Error ?? "Die Erinnerungs-E-Mail konnte nicht versendet werden.");

        subscription.MandateReminderSentAt = emailLog.SentAt ?? DateTimeOffset.UtcNow;
        subscription.MandateReminderCount++;
        await _db.SaveChangesAsync(ct);
        return new MandateEmailResultDto(true, recipient, subscription.MandateReminderSentAt, "Sent", null);
    }

    // === Öffentliche Mandats-Einrichtung (Token-Link aus den Mails) ===

    private async Task<CustomerSubscription> GetByTokenAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 32 or > 64)
            throw new KeyNotFoundException("Der Link ist ungültig.");
        return await SubscriptionsWithDetails().FirstOrDefaultAsync(s => s.MandateSetupToken == token, ct)
            ?? throw new KeyNotFoundException("Der Link ist ungültig.");
    }

    public async Task<MandateSetupInfoDto> GetSetupInfoAsync(string token, CancellationToken ct)
    {
        var subscription = await GetByTokenAsync(token, ct);

        // Rückkehr vom Mollie-Checkout: Webhook kann verzögert sein — Live-Status der aktuellen Erstzahlung abfragen.
        if (!IsValid(subscription.MollieMandateStatus) && !IsEnded(subscription) &&
            !string.IsNullOrWhiteSpace(subscription.MollieFirstPaymentId) &&
            subscription.MollieFirstPaymentStatus is not ("failed" or "canceled" or "expired"))
        {
            try
            {
                var payment = await GetAsync($"payments/{Uri.EscapeDataString(subscription.MollieFirstPaymentId)}", ct);
                await ApplyPaymentStatusAsync(payment, ct);
                subscription = await GetByTokenAsync(token, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Live mandate refresh failed for subscription {SubscriptionId}", subscription.Id);
            }
        }

        var co = await _db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var creditor = string.IsNullOrWhiteSpace(co?.CompanyName) ? "Gentle Group" : co!.CompanyName;
        var status = IsEnded(subscription) ? "invalid"
            : IsValid(subscription.MollieMandateStatus) ? "valid"
            : string.Equals(subscription.MollieMandateStatus, "pending", StringComparison.OrdinalIgnoreCase) || subscription.MollieFirstPaymentStatus is "pending" or "authorized" ? "processing"
            : "pending";

        var taxMode = co?.DefaultTaxMode ?? TaxMode.Standard;
        string description, amountText;
        if (subscription.IsInstallmentPlan)
        {
            var count = subscription.ContractDurationMonths ?? 1;
            var total = subscription.TotalInstallmentAmount ?? 0m;
            var baseAmount = subscription.AgreedMonthlyPrice ?? BillingMath.InstallmentAmount(total, count);
            var gross = BillingMath.Gross(baseAmount, taxMode);
            description = $"Ratenzahlung ({subscription.InstallmentSourceTitle ?? subscription.Plan.Name}) – {count} Raten à {Euro(gross)}";
            amountText = $"monatlich {Euro(gross)}";
        }
        else
        {
            var months = BillingMath.CycleMonths(subscription.ContractBillingCycle);
            var gross = BillingMath.Gross((subscription.AgreedMonthlyPrice ?? subscription.Plan.MonthlyPrice) * months, taxMode);
            var cycle = months switch { 3 => "vierteljährlich", 12 => "jährlich", _ => "monatlich" };
            description = $"Serienrechnung – {subscription.Plan.Name}";
            amountText = $"{cycle} {Euro(gross)}";
        }

        var contact = PrimaryContact(subscription);
        var contactName = contact == null ? null : string.Join(" ", new[] { contact.FirstName, contact.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        var mandateText = $"Ich ermächtige {creditor} (Zahlungsempfänger, Einzug über den Zahlungsdienstleister Mollie B.V.), Zahlungen von meinem Konto mittels Lastschrift einzuziehen. " +
            $"Zugleich weise ich mein Kreditinstitut an, die von {creditor} bzw. Mollie B.V. auf mein Konto gezogenen Lastschriften einzulösen. " +
            "Hinweis: Ich kann innerhalb von acht Wochen, beginnend mit dem Belastungsdatum, die Erstattung des belasteten Betrages verlangen. Es gelten dabei die mit meinem Kreditinstitut vereinbarten Bedingungen.";

        var preNotificationDays = Math.Max(1, _configuration.GetValue<int?>("Mollie:PreNotificationDays") ?? 14);
        var open = await NextCollection.LoadOpenCollectionsAsync(_db, new List<Guid> { subscription.Id }, ct);
        var next = NextCollection.Compute(subscription, open.GetValueOrDefault(subscription.Id), taxMode, preNotificationDays, BerlinTime.Today);

        return new MandateSetupInfoDto(
            status,
            subscription.Customer.CompanyName,
            string.IsNullOrWhiteSpace(contactName) ? null : contactName,
            creditor,
            string.IsNullOrWhiteSpace(_configuration["Mollie:CreditorId"]) ? null : _configuration["Mollie:CreditorId"],
            description,
            amountText,
            new MandateSetupMethodsDto(IbanMandatesEnabled, true),
            mandateText,
            status == "valid" ? new MandateSetupExistingDto(subscription.MollieMandateMethod ?? "unknown", subscription.MandateAccountLast4) : null,
            next.Date,
            next.Amount);
    }

    private static string Euro(decimal amount) => amount.ToString("N2", CultureInfo.GetCultureInfo("de-DE")) + " €";

    private static void EnsureSetupAllowed(CustomerSubscription subscription)
    {
        if (IsEnded(subscription))
            throw new InvalidOperationException("Dieser Vertrag ist beendet; eine Zahlungseinrichtung ist nicht mehr möglich.");
        if (IsValid(subscription.MollieMandateStatus))
            throw new InvalidOperationException("Für diesen Vertrag ist bereits eine gültige Zahlungsart hinterlegt.");
        EnsureB2bContractEvidence(subscription);
    }

    /// <summary>SEPA-Lastschriftmandat direkt per IBAN (Mollie "Create mandate"). Requires the Mollie account
    /// feature to be activated — if Mollie refuses, the customer gets a clear German 400 so the page can
    /// offer the card checkout instead. The IBAN is only forwarded to Mollie, never stored or logged in full.</summary>
    public async Task<MandateSetupStatusDto> CreateIbanMandateAsync(string token, MandateSetupIbanRequest req, CancellationToken ct)
    {
        var subscription = await GetByTokenAsync(token, ct);
        EnsureSetupAllowed(subscription);
        if (!IbanMandatesEnabled)
            throw new InvalidOperationException("Die Einrichtung per IBAN ist derzeit nicht verfügbar. Bitte nutzen Sie die Zahlung per Karte.");
        if (!req.Confirm)
            throw new InvalidOperationException("Bitte bestätigen Sie das SEPA-Lastschriftmandat.");
        var holder = (req.AccountHolder ?? string.Empty).Trim();
        if (holder.Length is < 2 or > 70)
            throw new InvalidOperationException("Bitte geben Sie den Namen des Kontoinhabers an.");
        var iban = IbanTools.Normalize(req.Iban);
        if (!IbanTools.IsValid(iban))
            throw new InvalidOperationException("Die IBAN ist ungültig. Bitte prüfen Sie Ihre Eingabe.");

        if (await TryAdoptExistingMandateAsync(subscription, ct))
        {
            await _db.SaveChangesAsync(ct);
            return new MandateSetupStatusDto("valid");
        }

        var mollieCustomerId = await EnsureMollieCustomerAsync(subscription, ct);
        var reference = $"GS-{subscription.Id:N}"[..15].ToUpperInvariant();
        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{iban}|{holder}")))[..16];
        JsonNode mandate;
        try
        {
            mandate = await PostAsync($"customers/{Uri.EscapeDataString(mollieCustomerId)}/mandates", new
            {
                method = "directdebit",
                consumerName = holder,
                consumerAccount = iban,
                signatureDate = BerlinTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                mandateReference = reference
            }, $"iban-mandate-{subscription.Id:N}-{keyHash}", ct);
        }
        catch (MollieApiException ex)
        {
            _logger.LogWarning("Mollie rejected IBAN mandate for subscription {SubscriptionId}: {StatusCode} {Detail}", subscription.Id, ex.StatusCode, IbanTools.MaskIbans(ex.Detail ?? ""));
            throw new InvalidOperationException("Die Einrichtung des Lastschriftmandats wurde von unserem Zahlungsdienstleister abgelehnt. Bitte prüfen Sie Ihre Angaben oder nutzen Sie die Zahlung per Karte.");
        }

        ApplyMandate(subscription, mandate);
        subscription.MollieMandateMethod ??= "directdebit";
        subscription.MandateAccountLast4 ??= iban[^4..];
        await _db.SaveChangesAsync(ct);

        var status = RequiredString(mandate, "status");
        if (IsValid(status))
        {
            await NotifyMandateOutcomeAsync(subscription, true, null, ct);
            return new MandateSetupStatusDto("valid");
        }
        if (status == "pending") return new MandateSetupStatusDto("processing");
        throw new InvalidOperationException("Das Lastschriftmandat konnte nicht eingerichtet werden. Bitte nutzen Sie die Zahlung per Karte.");
    }

    public async Task<MandateSetupCheckoutDto> CreateSetupCheckoutAsync(string token, CancellationToken ct)
    {
        var subscription = await GetByTokenAsync(token, ct);
        EnsureSetupAllowed(subscription);
        if (await TryAdoptExistingMandateAsync(subscription, ct))
        {
            await _db.SaveChangesAsync(ct);
            throw new InvalidOperationException("Für diesen Vertrag ist bereits eine gültige Zahlungsart hinterlegt.");
        }
        return new MandateSetupCheckoutDto(await CreateFirstPaymentAsync(subscription, ct));
    }

    // === Einzug ===

    public async Task CollectInvoiceAsync(Guid invoiceId, CancellationToken ct)
    {
        var invoice = await _db.Invoices
            .FirstOrDefaultAsync(i => i.Id == invoiceId, ct)
            ?? throw new KeyNotFoundException("Rechnung wurde nicht gefunden.");
        if (invoice.Status == InvoiceStatus.Paid) return;
        var subscription = (invoice.SubscriptionId == null ? null : await SubscriptionsWithDetails().FirstOrDefaultAsync(s => s.Id == invoice.SubscriptionId, ct))
            ?? throw new InvalidOperationException("Die Rechnung gehört zu keinem Abonnement.");

        if (string.IsNullOrWhiteSpace(subscription.MollieCustomerId) ||
            string.IsNullOrWhiteSpace(subscription.MollieMandateId) ||
            !IsValid(subscription.MollieMandateStatus))
            throw new InvalidOperationException("Für das Abonnement liegt kein gültiges Mollie-Mandat vor.");

        var currentMandate = await GetAsync($"customers/{subscription.MollieCustomerId}/mandates/{subscription.MollieMandateId}", ct);
        ApplyMandate(subscription, currentMandate);
        if (!IsValid(subscription.MollieMandateStatus) && !await TryAdoptExistingMandateAsync(subscription, ct))
        {
            subscription.Status = SubscriptionStatus.Paused;
            subscription.PausedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            throw new InvalidOperationException("Das Mollie-Mandat ist nicht mehr gültig; das Abonnement wurde pausiert.");
        }
        await _db.SaveChangesAsync(ct);

        if (!string.IsNullOrWhiteSpace(invoice.ExternalPaymentReference))
        {
            var existing = await GetAsync($"payments/{invoice.ExternalPaymentReference}", ct);
            await ApplyPaymentStatusAsync(existing, ct);
            return;
        }

        var priorPayment = await FindPaymentForInvoiceAsync(subscription.MollieCustomerId!, invoice.Id, ct);
        if (priorPayment != null)
        {
            // Only reuse a prior payment if it's still in flight (or already settled) — a terminal
            // failure/expiry/cancellation is handled (idempotently) and then retried with a new payment.
            await ApplyPaymentStatusAsync(priorPayment, ct);
            if (RequiredString(priorPayment, "status") is "open" or "pending" or "authorized" or "paid")
                return;
            invoice = await _db.Invoices.FirstAsync(i => i.Id == invoiceId, ct);
            if (invoice.Status == InvoiceStatus.Paid || invoice.PaymentCollectionStatus != "scheduled" ||
                invoice.PaymentCollectionDueDate > BerlinTime.Today.AddDays(1))
                return;
        }

        var payment = await PostAsync("payments", new
        {
            amount = new { currency = "EUR", value = invoice.GrossTotal.ToString("0.00", CultureInfo.InvariantCulture) },
            customerId = subscription.MollieCustomerId,
            mandateId = subscription.MollieMandateId,
            sequenceType = "recurring",
            description = $"Rechnung {invoice.InvoiceNumber}",
            webhookUrl = $"{PublicBaseUrl}/api/mollie/webhook",
            metadata = new { kind = "invoice", invoiceId = invoice.Id, subscriptionId = subscription.Id }
        }, $"invoice-{invoice.Id:N}-{invoice.CollectionAttemptCount}", ct);

        await ApplyPaymentStatusAsync(payment, ct);
    }

    public async Task HandlePaymentWebhookAsync(string paymentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(paymentId) || !paymentId.StartsWith("tr_", StringComparison.Ordinal) || paymentId.Length > 64)
            throw new ArgumentException("Ungültige Mollie-Zahlungs-ID.");
        try
        {
            JsonNode payment;
            try { payment = await GetAsync($"payments/{Uri.EscapeDataString(paymentId)}", ct); }
            catch (MollieApiException ex) when (ex.StatusCode == 404)
            {
                // Unknown payment (not ours / test noise): acknowledge so nobody can probe ids via the response.
                _logger.LogWarning("Mollie webhook for unknown payment {PaymentId} ignored", paymentId);
                return;
            }
            await ApplyPaymentStatusAsync(payment, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RecordWebhookErrorAsync(paymentId, ex, ct);
            throw;
        }
    }

    private async Task RecordWebhookErrorAsync(string paymentId, Exception error, CancellationToken ct)
    {
        try
        {
            _db.ChangeTracker.Clear();
            var now = DateTimeOffset.UtcNow;
            var message = error.Message + (error.InnerException != null ? " => " + error.InnerException.Message : "");
            _db.JobRunLogs.Add(new JobRunLog { JobName = "mollie-webhook", StartedAt = now, FinishedAt = now, Succeeded = false, Failures = 1, Reference = paymentId, Error = message.Length > 2000 ? message[..2000] : message });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record Mollie webhook error for {PaymentId}", paymentId);
        }
    }

    public async Task<MollieMandateStatusDto> GetMandateStatusAsync(Guid subscriptionId, CancellationToken ct)
    {
        var subscription = await _db.CustomerSubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == subscriptionId, ct)
            ?? throw new KeyNotFoundException("Abonnement wurde nicht gefunden.");

        if (IsValid(subscription.MollieMandateStatus))
            return new MollieMandateStatusDto("valid", null);

        if (string.IsNullOrWhiteSpace(subscription.MollieFirstPaymentId))
            return new MollieMandateStatusDto("pending", null);

        // Fetch the live status from Mollie rather than only the cached DB value — a webhook can be
        // delayed or missed, and this page is exactly where an out-of-date "pending" would mislead the customer.
        var payment = await GetAsync($"payments/{Uri.EscapeDataString(subscription.MollieFirstPaymentId)}", ct);
        await ApplyPaymentStatusAsync(payment, ct);

        // Re-read: ApplyPaymentStatusAsync works on a fresh, row-locked instance.
        var mandateStatus = await _db.CustomerSubscriptions.AsNoTracking().Where(s => s.Id == subscriptionId).Select(s => s.MollieMandateStatus).FirstOrDefaultAsync(ct);
        if (IsValid(mandateStatus))
            return new MollieMandateStatusDto("valid", null);

        var status = RequiredString(payment, "status");
        var failureMessage = payment["details"]?["failureMessage"]?.GetValue<string>();
        return status switch
        {
            "open" or "pending" or "authorized" => new MollieMandateStatusDto("pending", null),
            "paid" => new MollieMandateStatusDto(mandateStatus == "pending" ? "pending" : "failed", mandateStatus == "pending" ? null : "Die Zahlung wurde verarbeitet, aber es konnte kein gültiges Lastschrift-/Kartenmandat eingerichtet werden."),
            _ => new MollieMandateStatusDto("failed", failureMessage),
        };
    }

    private async Task ApplyPaymentStatusAsync(JsonNode payment, CancellationToken ct)
    {
        var paymentId = RequiredString(payment, "id");
        ValidateProfile(payment);
        var status = RequiredString(payment, "status");
        var metadata = payment["metadata"];
        var kind = MetadataString(metadata, "kind");

        if (kind == "mandate" && Guid.TryParse(MetadataString(metadata, "subscriptionId"), out var subscriptionId))
        {
            await ApplyMandatePaymentStatusAsync(subscriptionId, paymentId, status, payment, ct);
            return;
        }

        if (kind == "invoice" && Guid.TryParse(MetadataString(metadata, "invoiceId"), out var invoiceId))
        {
            await ApplyInvoicePaymentStatusAsync(invoiceId, paymentId, status, payment, ct);
            return;
        }
    }

    // Mollie can deliver the same payment webhook more than once, and a webhook delivery can also
    // land while the customer's setup page is independently polling for the same payment. A pessimistic
    // row lock (UPDLOCK/HOLDLOCK, held for the lifetime of this transaction) on the exact subscription/
    // invoice row makes the read-modify-write atomic across calls: the loser blocks on the SELECT until
    // the winner commits, then sees the already-applied status and correctly no-ops.
    //
    // The lock only works if the row is actually re-read: when the entity is already tracked by this
    // DbContext (e.g. CollectInvoiceAsync loaded it before), EF's identity resolution silently hands back
    // the stale tracked instance instead of the freshly locked values. Hence pending changes are saved and
    // the change tracker is cleared before every locked read.
    //
    // (Previously this used sp_getapplock via SqlQueryRaw<int>+SingleAsync — that throws "'FromSql' or
    // 'SqlQuery' was called with non-composable SQL and with a query composing over it" on every call,
    // because EF Core refuses to compose SingleAsync over a multi-statement DECLARE/EXEC/SELECT batch.)
    private async Task ResetTrackingAsync(CancellationToken ct)
    {
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    private async Task ApplyMandatePaymentStatusAsync(Guid subscriptionId, string paymentId, string status, JsonNode payment, CancellationToken ct)
    {
        CustomerSubscription? subscription;
        var shouldNotifySuccess = false;
        var shouldNotifyFailure = false;
        string? failureMessage = null;

        await ResetTrackingAsync(ct);
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            subscription = await _db.CustomerSubscriptions
                .FromSqlInterpolated($"SELECT * FROM CustomerSubscriptions WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE Id = {subscriptionId}")
                .IgnoreQueryFilters()
                .Include(s => s.Plan)
                .Include(s => s.Customer).ThenInclude(c => c.Contacts.Where(x => !x.IsDeleted))
                .FirstOrDefaultAsync(ct);
            var isCurrent = subscription != null && subscription.MollieFirstPaymentId == paymentId;
            // An older checkout of the same subscription that still got paid (customer had two tabs open)
            // must still produce the mandate; any other status change of a stale payment is irrelevant.
            if (subscription == null || subscription.IsDeleted || (!isCurrent && status != "paid"))
            {
                await tx.CommitAsync(ct);
                return;
            }

            var previousStatus = subscription.MollieFirstPaymentStatus;
            if (isCurrent) subscription.MollieFirstPaymentStatus = status;
            var wasValid = IsValid(subscription.MollieMandateStatus);

            var mollieCustomerId = payment["customerId"]?.GetValue<string>() ?? subscription.MollieCustomerId;
            if (status == "paid" && !string.IsNullOrWhiteSpace(mollieCustomerId))
            {
                subscription.MollieCustomerId ??= mollieCustomerId;
                var mandateId = payment["mandateId"]?.GetValue<string>();
                JsonNode? mandate = null;
                if (!string.IsNullOrWhiteSpace(mandateId))
                    mandate = await GetAsync($"customers/{Uri.EscapeDataString(mollieCustomerId)}/mandates/{Uri.EscapeDataString(mandateId)}", ct);
                if (mandate == null || !IsValid(mandate["status"]?.GetValue<string>()))
                {
                    var mandates = await ListMandatesAsync(mollieCustomerId, ct);
                    mandate = PickMandate(mandates, "valid", mandateId) ?? PickMandate(mandates, "pending", mandateId) ?? mandate;
                }
                if (mandate != null && (!wasValid || IsValid(mandate["status"]?.GetValue<string>())))
                    ApplyMandate(subscription, mandate);
            }
            var becameValid = !wasValid && IsValid(subscription.MollieMandateStatus);

            // Proaktive Rückmeldung an den Kunden, sobald sich das Ergebnis dieses Zahlungsversuchs erstmals
            // klärt. Fehlschlag-Mails enthalten den dauerhaften Einrichtungslink und dürfen daher wiederholt
            // kommen (kein Endlos-Kreislauf mehr, weil keine neue Zahlung erzeugt wird) — gedrosselt auf 1x/24 h.
            if (becameValid)
            {
                shouldNotifySuccess = true;
            }
            else if (isCurrent && previousStatus != status && TerminalFailureStatuses.Contains(status) &&
                     !IsValid(subscription.MollieMandateStatus) && !IsEnded(subscription) &&
                     (subscription.MandateFailureNotifiedAt == null || subscription.MandateFailureNotifiedAt < DateTimeOffset.UtcNow - FailureMailThrottle))
            {
                failureMessage = payment["details"]?["failureMessage"]?.GetValue<string>();
                subscription.MandateFailureNotifiedAt = DateTimeOffset.UtcNow;
                shouldNotifyFailure = true;
            }

            EnsureSetupToken(subscription);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        // Sent after the commit (and outside the lock) so the transaction isn't held open across a
        // slow SMTP call.
        if (shouldNotifySuccess)
            await NotifyMandateOutcomeAsync(subscription, true, null, ct);
        else if (shouldNotifyFailure)
            await NotifyMandateOutcomeAsync(subscription, false, failureMessage, ct);
    }

    private async Task ApplyInvoicePaymentStatusAsync(Guid invoiceId, string paymentId, string status, JsonNode payment, CancellationToken ct)
    {
        Invoice? invoice;
        var isNewlyPaid = false;
        var newChargeback = false;
        var exhaustedRetries = false;
        var stalePaidPayment = false;
        decimal chargedBack = 0;

        await ResetTrackingAsync(ct);
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            invoice = await _db.Invoices
                .FromSqlInterpolated($"SELECT * FROM Invoices WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE Id = {invoiceId}")
                .Include(i => i.Payments)
                .Include(i => i.Customer).ThenInclude(c => c.Contacts)
                .FirstOrDefaultAsync(ct);
            if (invoice == null)
            {
                await tx.CommitAsync(ct);
                return;
            }

            // Webhook about a payment that is no longer the invoice's current one (e.g. a late delivery for a
            // failed attempt that was already retried): acknowledge with 200 instead of erroring, otherwise
            // Mollie keeps retrying for days. A *paid* stale payment is real money → alert the team.
            if (!string.IsNullOrWhiteSpace(invoice.ExternalPaymentReference) && invoice.ExternalPaymentReference != paymentId)
            {
                stalePaidPayment = status == "paid" && !invoice.Payments.Any(p => p.Reference == paymentId);
                _logger.LogWarning("Mollie payment {PaymentId} is not the current payment {Current} of invoice {InvoiceId} (status {Status}); ignored",
                    paymentId, invoice.ExternalPaymentReference, invoiceId, status);
                await tx.CommitAsync(ct);
                if (stalePaidPayment)
                    await NotifyStaffAsync("Mollie-Zahlung manuell prüfen",
                        $"Für Rechnung {invoice.InvoiceNumber} (Kunde: {invoice.Customer.CompanyName}) meldet Mollie die nicht mehr aktuelle Zahlung {paymentId} als bezahlt. Bitte prüfen, ob doppelt eingezogen wurde.", ct);
                return;
            }

            if (TerminalFailureStatuses.Contains(status))
            {
                // Idempotent per Mollie payment: a repeated "failed" webhook must not count a second attempt.
                var alreadyCounted = await _db.InvoicePaymentAttempts.AnyAsync(a => a.MolliePaymentId == paymentId, ct);
                if (alreadyCounted)
                {
                    await tx.CommitAsync(ct);
                    return;
                }
                var reason = payment["details"]?["failureMessage"]?.GetValue<string>()
                    ?? payment["details"]?["bankReason"]?.GetValue<string>()
                    ?? status switch { "expired" => "Zahlung abgelaufen", "canceled" => "Zahlung abgebrochen", _ => "Einzug fehlgeschlagen" };
                _db.InvoicePaymentAttempts.Add(new InvoicePaymentAttempt { InvoiceId = invoice.Id, MolliePaymentId = paymentId, Status = status, FailureReason = reason.Length > 1000 ? reason[..1000] : reason });

                invoice.CollectionAttemptCount++;
                if (invoice.CollectionAttemptCount < MaxCollectionAttempts)
                {
                    // Retry: clear the dead payment reference and put the invoice back into
                    // SubscriptionBillingJob's daily collection queue with a short delay so a
                    // fresh Mollie payment (new idempotency key per attempt) is created later.
                    invoice.ExternalPaymentReference = null;
                    invoice.PaymentCollectionStatus = "scheduled";
                    invoice.PaymentCollectionDueDate = BerlinTime.Today.AddDays(CollectionRetryDelayDays);
                }
                else
                {
                    invoice.ExternalPaymentReference = paymentId;
                    invoice.PaymentCollectionStatus = status;
                    invoice.Status = InvoiceStatus.Overdue;
                    invoice.PaidAt = null;
                    exhaustedRetries = true;
                }
            }
            else
            {
                invoice.ExternalPaymentReference = paymentId;
                if (status == "paid")
                {
                    var method = payment["method"]?.GetValue<string>();
                    var paidAmount = ParseAmount(payment["amount"]) ?? invoice.GrossTotal;
                    if (!invoice.Payments.Any(p => p.Reference == paymentId))
                    {
                        isNewlyPaid = true;
                        // Added via the DbSet (not invoice.Payments.Add) with the FK set explicitly: adding
                        // to a collection navigation that was populated by .Include() on a FromSqlInterpolated
                        // root query can get mis-tracked as Modified instead of Added.
                        _db.InvoicePayments.Add(new InvoicePayment
                        {
                            InvoiceId = invoice.Id,
                            Amount = paidAmount,
                            PaymentDate = DateTimeOffset.UtcNow,
                            PaymentMethod = MandateLabels.PaymentMethodLabel(method),
                            Reference = paymentId,
                            Note = "Automatisch über Mollie eingezogen"
                        });
                    }

                    // Mollie v2 kennt keinen Status "charged_back": eine Rücklastschrift bleibt "paid" und
                    // erscheint nur als amountChargedBack (> 0) an der Zahlung.
                    chargedBack = ParseAmount(payment["amountChargedBack"]) ?? 0m;
                    if (chargedBack > 0)
                    {
                        var reversalReference = $"{paymentId}:chargeback";
                        if (!invoice.Payments.Any(p => p.Reference == reversalReference))
                        {
                            newChargeback = true;
                            _db.InvoicePayments.Add(new InvoicePayment
                            {
                                InvoiceId = invoice.Id,
                                Amount = -chargedBack,
                                PaymentDate = DateTimeOffset.UtcNow,
                                PaymentMethod = method == "directdebit" ? "Mollie SEPA-Rücklastschrift" : "Mollie Rückbuchung",
                                Reference = reversalReference,
                                Note = "Rücklastschrift/Rückbuchung von Mollie gemeldet"
                            });
                        }
                        invoice.ChargedBackAt ??= DateTimeOffset.UtcNow;
                        invoice.ChargebackAmount = chargedBack;
                        invoice.PaymentCollectionStatus = "charged_back";
                        invoice.Status = InvoiceStatus.Overdue;
                        invoice.PaidAt = null;
                        isNewlyPaid = false;
                    }
                    else
                    {
                        invoice.PaymentCollectionStatus = "paid";
                        invoice.Status = InvoiceStatus.Paid;
                        invoice.PaidAt ??= DateTimeOffset.UtcNow;
                    }
                }
                else if (invoice.PaymentCollectionStatus != "charged_back")
                {
                    invoice.PaymentCollectionStatus = status;
                }
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        if (isNewlyPaid)
        {
            await SendPaymentReceivedEmailAsync(invoice, ct);
            await CompleteInstallmentPlanIfFullyPaidAsync(invoice.SubscriptionId, ct);
        }
        if (newChargeback)
        {
            await ReopenInstallmentPlanAsync(invoice.SubscriptionId, ct);
            await NotifyStaffAsync(
                "Mollie: Rücklastschrift eingegangen",
                $"Für Rechnung {invoice.InvoiceNumber} (Kunde: {invoice.Customer.CompanyName}) wurden {chargedBack:N2} € zurückgebucht (Mollie-Zahlung {paymentId}). Die Rechnung ist wieder offen ('Überfällig') und wird nicht automatisch erneut eingezogen — bitte Kontakt mit dem Kunden aufnehmen.",
                ct);
        }
        if (exhaustedRetries)
            await NotifyStaffAsync(
                "Mollie-Einzug endgültig fehlgeschlagen",
                $"Der automatische Einzug für Rechnung {invoice.InvoiceNumber} (Kunde: {invoice.Customer.CompanyName}) ist nach {invoice.CollectionAttemptCount} Versuchen fehlgeschlagen (letzter Status: {status}). Die Rechnung wurde auf 'Überfällig' gesetzt und muss manuell nachverfolgt werden.",
                ct);
    }

    private static decimal? ParseAmount(JsonNode? amount)
    {
        var raw = amount?["value"]?.GetValue<string>();
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>Ein Ratenplan gilt erst als abgeschlossen, wenn alle Raten fakturiert UND bezahlt sind —
    /// nicht schon beim Erstellen der letzten Rechnung.</summary>
    private async Task CompleteInstallmentPlanIfFullyPaidAsync(Guid? subscriptionId, CancellationToken ct)
    {
        if (subscriptionId == null) return;
        try
        {
            var sub = await SubscriptionsWithDetails().FirstOrDefaultAsync(s => s.Id == subscriptionId, ct);
            if (sub == null || !sub.IsInstallmentPlan || sub.Status is SubscriptionStatus.Completed or SubscriptionStatus.Cancelled) return;
            var count = sub.ContractDurationMonths ?? 1;
            if (sub.InstallmentsCompleted < count) return;
            var anyUnpaid = await _db.Invoices.AnyAsync(i => i.SubscriptionId == sub.Id && i.Status != InvoiceStatus.Paid &&
                i.Status != InvoiceStatus.Cancelled && i.Status != InvoiceStatus.Draft &&
                i.Type != InvoiceType.Cancellation && i.Type != InvoiceType.CreditNote, ct);
            if (anyUnpaid) return;
            sub.Status = SubscriptionStatus.Completed;
            sub.EndDate = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            await NotifyStaffAsync(
                "Ratenzahlung abgeschlossen – Wartungsvertrag anbieten",
                $"{sub.Customer.CompanyName} hat alle {count} Raten ({sub.InstallmentSourceTitle ?? sub.Plan.Name}) bezahlt. Gesamtbetrag {sub.TotalInstallmentAmount:N2} € vollständig beglichen (Angebot {sub.ContractReference ?? "-"}). Jetzt Wartungsvertrag/Folgeangebot anbieten.",
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Installment completion check failed for subscription {SubscriptionId}", subscriptionId);
        }
    }

    private async Task ReopenInstallmentPlanAsync(Guid? subscriptionId, CancellationToken ct)
    {
        if (subscriptionId == null) return;
        var sub = await _db.CustomerSubscriptions.FirstOrDefaultAsync(s => s.Id == subscriptionId, ct);
        if (sub is { IsInstallmentPlan: true, Status: SubscriptionStatus.Completed })
        {
            sub.Status = SubscriptionStatus.Active;
            sub.EndDate = null;
            await _db.SaveChangesAsync(ct);
        }
    }

    // === Mandats-Abgleich ===

    public async Task<MandateReconcileRunDto> ReconcileMandatesAsync(CancellationToken ct)
    {
        var log = new JobRunLog { JobName = "mandate-reconcile", StartedAt = DateTimeOffset.UtcNow };
        _db.JobRunLogs.Add(log);
        await _db.SaveChangesAsync(ct);

        var candidateIds = await _db.CustomerSubscriptions.IgnoreQueryFilters()
            .Where(s => !s.IsDeleted && !s.Customer.IsDeleted &&
                        s.Status != SubscriptionStatus.Cancelled && s.Status != SubscriptionStatus.Expired && s.Status != SubscriptionStatus.Completed &&
                        (s.MollieMandateStatus == null || s.MollieMandateStatus != "valid") &&
                        (s.MollieCustomerId != null || s.Customer.MollieCustomerId != null ||
                         _db.CustomerSubscriptions.IgnoreQueryFilters().Any(o => o.CustomerId == s.CustomerId && o.MollieCustomerId != null)))
            .Select(s => s.Id).ToListAsync(ct);

        int updated = 0, failures = 0;
        var errors = new List<string>();
        foreach (var id in candidateIds)
        {
            try
            {
                _db.ChangeTracker.Clear();
                if (await ReconcileCoreAsync(id, includeValid: false, ct)) updated++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures++;
                errors.Add($"Abo {id}: {ex.Message}");
                _logger.LogError(ex, "Mandate reconcile failed for subscription {SubscriptionId}", id);
                _db.ChangeTracker.Clear();
            }
        }

        var entry = await _db.JobRunLogs.FirstAsync(x => x.Id == log.Id, ct);
        entry.FinishedAt = DateTimeOffset.UtcNow;
        entry.Succeeded = failures == 0;
        entry.Updated = updated;
        entry.Failures = failures;
        entry.Error = errors.Count == 0 ? null : string.Join("\n", errors.Take(20));
        await _db.SaveChangesAsync(ct);
        return new MandateReconcileRunDto(entry.StartedAt, entry.FinishedAt, entry.Succeeded, entry.Updated, entry.Error);
    }

    public async Task<bool> ReconcileSubscriptionAsync(Guid subscriptionId, CancellationToken ct)
    {
        if (!await _db.CustomerSubscriptions.AsNoTracking().AnyAsync(s => s.Id == subscriptionId, ct))
            throw new KeyNotFoundException("Abonnement wurde nicht gefunden.");
        return await ReconcileCoreAsync(subscriptionId, includeValid: true, ct);
    }

    private async Task<bool> ReconcileCoreAsync(Guid subscriptionId, bool includeValid, CancellationToken ct)
    {
        var sub = await SubscriptionsWithDetails().FirstOrDefaultAsync(s => s.Id == subscriptionId, ct);
        if (sub == null) return false;
        var before = (sub.MollieMandateId, sub.MollieMandateStatus, sub.MollieMandateMethod, sub.MandateAccountLast4, sub.Status);

        if (IsValid(sub.MollieMandateStatus))
        {
            if (!includeValid || string.IsNullOrWhiteSpace(sub.MollieCustomerId) || string.IsNullOrWhiteSpace(sub.MollieMandateId)) return false;
            var current = await GetAsync($"customers/{Uri.EscapeDataString(sub.MollieCustomerId)}/mandates/{Uri.EscapeDataString(sub.MollieMandateId)}", ct);
            ApplyMandate(sub, current);
            if (!IsValid(sub.MollieMandateStatus)) await TryAdoptExistingMandateAsync(sub, ct);
        }
        else
        {
            await TryAdoptExistingMandateAsync(sub, ct);
        }

        var changed = before != (sub.MollieMandateId, sub.MollieMandateStatus, sub.MollieMandateMethod, sub.MandateAccountLast4, sub.Status);
        await _db.SaveChangesAsync(ct);
        return changed;
    }

    // === Benachrichtigungen ===

    private async Task NotifyStaffAsync(string subject, string message, CancellationToken ct)
    {
        try
        {
            var co = await _db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(ct);
            if (co == null || string.IsNullOrWhiteSpace(co.Email)) return;
            await _email.SendEmailAsync(co.Email, subject, $"<p>{System.Net.WebUtility.HtmlEncode(message)}</p>", ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Staff alert email failed: {Subject}", subject);
        }
    }

    private async Task SendPaymentReceivedEmailAsync(Invoice invoice, CancellationToken ct)
    {
        var contact = invoice.Customer.Contacts.FirstOrDefault(c => c.IsPrimary)
            ?? invoice.Customer.Contacts.FirstOrDefault();
        if (contact == null || string.IsNullOrWhiteSpace(contact.Email)) return;

        try
        {
            await _email.SendTemplatedEmailAsync(
                contact.Email,
                "payment-received",
                new Dictionary<string, object>
                {
                    ["ContactName"] = contact.FirstName,
                    ["CustomerName"] = invoice.Customer.CompanyName,
                    ["InvoiceNumber"] = invoice.InvoiceNumber,
                    ["GrossTotal"] = invoice.GrossTotal.ToString("N2"),
                    ["PaidAt"] = (invoice.PaidAt ?? DateTimeOffset.UtcNow).ToString("dd.MM.yyyy"),
                },
                invoice.CustomerId,
                ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Payment-received email failed for invoice {InvoiceId}", invoice.Id);
        }
    }

    /// <summary>Proactively tells the customer whether the mandate setup they just attempted worked or not,
    /// so they don't have to rely on still having the browser tab open. On failure the mail carries the
    /// stable setup link (never an expiring Mollie checkout URL).</summary>
    private async Task NotifyMandateOutcomeAsync(CustomerSubscription subscription, bool succeeded, string? failureMessage, CancellationToken ct)
    {
        var contact = PrimaryContact(subscription);
        if (contact == null || string.IsNullOrWhiteSpace(contact.Email)) return;
        var contactName = ContactName(subscription, contact);

        try
        {
            if (succeeded)
            {
                await _email.SendTemplatedEmailAsync(contact.Email, "subscription-mandate-confirmed", new Dictionary<string, object>
                {
                    ["ContactName"] = contactName,
                    ["PlanName"] = subscription.InstallmentSourceTitle ?? subscription.Plan.Name,
                    ["MonthlyPrice"] = (subscription.AgreedMonthlyPrice ?? subscription.Plan.MonthlyPrice).ToString("0.00", CultureInfo.GetCultureInfo("de-DE")),
                }, subscription.CustomerId, ct: ct);
            }
            else
            {
                await _email.SendTemplatedEmailAsync(contact.Email, "subscription-mandate-failed", new Dictionary<string, object>
                {
                    ["ContactName"] = contactName,
                    ["PlanName"] = subscription.InstallmentSourceTitle ?? subscription.Plan.Name,
                    ["FailureReason"] = string.IsNullOrWhiteSpace(failureMessage) ? "Die Zahlung wurde nicht angenommen." : failureMessage,
                    ["CheckoutUrl"] = SetupUrl(subscription),
                }, subscription.CustomerId, ct: ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mandate outcome email failed for subscription {SubscriptionId}", subscription.Id);
        }
    }

    private async Task<JsonNode?> FindPaymentForInvoiceAsync(string customerId, Guid invoiceId, CancellationToken ct)
    {
        var list = await GetAsync($"customers/{customerId}/payments?limit=250", ct);
        var matches = list["_embedded"]?["payments"]?.AsArray()
            .Where(p => p != null && string.Equals(MetadataString(p["metadata"], "invoiceId"), invoiceId.ToString(), StringComparison.OrdinalIgnoreCase))
            .Select(p => p!).ToList() ?? new List<JsonNode>();
        // Prefer a settled/in-flight payment over older terminal failures.
        return matches.FirstOrDefault(p => p["status"]?.GetValue<string>() is "paid" or "open" or "pending" or "authorized")
            ?? matches.FirstOrDefault();
    }

    private static void EnsureB2bContractEvidence(CustomerSubscription subscription)
    {
        var hasQuoteEvidence = subscription.ContractQuoteId != null && !string.IsNullOrWhiteSpace(subscription.ContractReference) &&
            subscription.ContractAcceptedAt != null;
        // Manuell angelegte Ratenzahlungspläne (Zusage außerhalb des Systems) haben kein Angebot als Nachweis —
        // hier ersetzt die Pflicht-Bestätigung beim Anlegen (PaymentPlanOptionKey "manual") den Vertragsnachweis.
        var hasManualEvidence = subscription.PaymentPlanOptionKey == "manual" && subscription.BusinessCustomerConfirmedAt != null &&
            !string.IsNullOrWhiteSpace(subscription.InstallmentSourceTitle);
        // Normale Serienrechnungen ohne verknüpftes Angebot (das Angebot ist beim Anlegen optional) — die
        // Pflicht-B2B-Bestätigung beim Anlegen ersetzt hier den Vertragsnachweis, analog zu "manual" oben.
        var hasNoQuoteEvidence = subscription.ContractQuoteId == null && !subscription.IsInstallmentPlan &&
            subscription.PaymentPlanOptionKey != "manual" && subscription.BusinessCustomerConfirmedAt != null;
        if ((!hasQuoteEvidence && !hasManualEvidence && !hasNoQuoteEvidence) || !subscription.BusinessCustomerConfirmed ||
            subscription.AgreedMonthlyPrice is null or <= 0)
            throw new InvalidOperationException("Die Mollie-Zahlungseinrichtung ist ohne vollständigen B2B-Vertragsnachweis gesperrt.");
    }

    // === Mollie HTTP ===

    private static readonly JsonSerializerOptions SerializerOptions = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private HttpClient CreateClient()
    {
        var apiKey = _configuration["Mollie:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Mollie ist noch nicht konfiguriert.");
        var baseUrl = _configuration["Mollie:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = "https://api.mollie.com/v2/";
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    private async Task<JsonNode> GetAsync(string path, CancellationToken ct)
    {
        using var client = CreateClient();
        using var response = await client.GetAsync(path, ct);
        return await ReadResponseAsync(response, ct);
    }

    private async Task<JsonNode> PostAsync(string path, object body, string idempotencyKey, CancellationToken ct)
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, SerializerOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await client.SendAsync(request, ct);
        return await ReadResponseAsync(response, ct);
    }

    private async Task<JsonNode> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            string? detail = null;
            try { detail = JsonNode.Parse(json)?["detail"]?.GetValue<string>(); } catch (JsonException) { }
            // Error bodies can echo request fields — never write a full IBAN to the log.
            _logger.LogError("Mollie API returned {StatusCode}: {Body}", (int)response.StatusCode, IbanTools.MaskIbans(json));
            throw new MollieApiException((int)response.StatusCode, detail);
        }
        return JsonNode.Parse(json) ?? throw new InvalidOperationException("Mollie hat eine leere Antwort geliefert.");
    }

    private void ValidateProfile(JsonNode payment)
    {
        var expectedProfile = _configuration["Mollie:ProfileId"];
        var actualProfile = payment["profileId"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(expectedProfile) && !string.Equals(expectedProfile, actualProfile, StringComparison.Ordinal))
            throw new InvalidOperationException("Mollie-Zahlung gehört nicht zum konfigurierten GentleSuite-Profil.");
    }

    private static string RequiredString(JsonNode node, string property)
        => node[property]?.GetValue<string>() ?? throw new InvalidOperationException($"Mollie-Antwort enthält kein Feld '{property}'.");

    private static string? MetadataString(JsonNode? metadata, string property)
    {
        if (metadata is JsonObject obj) return obj[property]?.ToString();
        if (metadata is JsonValue value && value.TryGetValue<string>(out var raw) && !string.IsNullOrWhiteSpace(raw))
            return JsonNode.Parse(raw)?[property]?.ToString();
        return null;
    }
}
