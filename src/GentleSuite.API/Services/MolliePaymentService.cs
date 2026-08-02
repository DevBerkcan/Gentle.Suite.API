using System.Globalization;
using System.Net.Http.Headers;
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

public sealed class MolliePaymentService : IMolliePaymentService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IEmailService _email;
    private readonly ILogger<MolliePaymentService> _logger;

    public MolliePaymentService(AppDbContext db, IHttpClientFactory httpClientFactory, IConfiguration configuration, IEmailService email, ILogger<MolliePaymentService> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _email = email;
        _logger = logger;
    }

    public async Task<MollieMandateCheckoutDto> StartMandateCheckoutAsync(Guid subscriptionId, CancellationToken ct)
    {
        var subscription = await _db.CustomerSubscriptions
            .Include(s => s.Plan)
            .Include(s => s.Customer).ThenInclude(c => c.Contacts)
            .FirstOrDefaultAsync(s => s.Id == subscriptionId, ct)
            ?? throw new KeyNotFoundException("Abonnement wurde nicht gefunden.");

        if (subscription.Status is SubscriptionStatus.Cancelled or SubscriptionStatus.Expired)
            throw new InvalidOperationException("Für ein beendetes Abonnement kann kein Mandat angefordert werden.");
        EnsureB2bContractEvidence(subscription);

        var contact = subscription.Customer.Contacts.FirstOrDefault(c => c.IsPrimary)
            ?? subscription.Customer.Contacts.FirstOrDefault();
        if (contact == null || string.IsNullOrWhiteSpace(contact.Email))
            throw new InvalidOperationException("Beim Kunden ist keine E-Mail-Adresse für Mollie hinterlegt.");

        if (string.IsNullOrWhiteSpace(subscription.MollieCustomerId))
        {
            var customer = await PostAsync("customers", new
            {
                name = subscription.Customer.CompanyName,
                email = contact.Email,
                locale = "de_DE",
                metadata = new { gentleSuiteCustomerId = subscription.CustomerId }
            }, $"customer-{subscription.CustomerId:N}", ct);
            subscription.MollieCustomerId = RequiredString(customer, "id");
            await _db.SaveChangesAsync(ct);
        }

        var mandateAttempt = subscription.MollieMandateAttempt;
        if (!string.IsNullOrWhiteSpace(subscription.MollieFirstPaymentId))
        {
            var existing = await GetAsync($"payments/{subscription.MollieFirstPaymentId}", ct);
            var existingStatus = RequiredString(existing, "status");
            var existingCheckout = existing["_links"]?["checkout"]?["href"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(existingCheckout) && existingStatus is "open" or "pending")
                return new MollieMandateCheckoutDto(existingCheckout, subscription.MollieFirstPaymentId, existingStatus);
            mandateAttempt = Math.Max(1, mandateAttempt) + 1;
        }
        else if (mandateAttempt <= 0)
        {
            mandateAttempt = 1;
        }

        subscription.MollieMandateAttempt = mandateAttempt;
        await _db.SaveChangesAsync(ct);

        var publicBaseUrl = (_configuration["PublicBaseUrl"] ?? "https://gentlesuite.runasp.net").TrimEnd('/');
        var frontendBaseUrl = (_configuration["FrontendBaseUrl"] ?? "https://gentlesuite.vercel.app").TrimEnd('/');
        var payment = await PostAsync("payments", new
        {
            amount = new { currency = "EUR", value = "0.01" },
            customerId = subscription.MollieCustomerId,
            sequenceType = "first",
            description = $"Zahlungseinrichtung {subscription.ContractReference} - {subscription.Plan.Name}",
            redirectUrl = $"{frontendBaseUrl}/payment/mandate-result",
            webhookUrl = $"{publicBaseUrl}/api/mollie/webhook",
            metadata = new { kind = "mandate", subscriptionId = subscription.Id }
        }, $"mandate-{subscription.Id:N}-{mandateAttempt}", ct);

        subscription.MollieFirstPaymentId = RequiredString(payment, "id");
        subscription.MollieFirstPaymentStatus = RequiredString(payment, "status");
        await _db.SaveChangesAsync(ct);

        var checkoutUrl = payment["_links"]?["checkout"]?["href"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Mollie hat keine Checkout-URL zurückgegeben.");
        return new MollieMandateCheckoutDto(checkoutUrl, subscription.MollieFirstPaymentId, subscription.MollieFirstPaymentStatus);
    }

    public async Task<MandateEmailResultDto> SendMandateEmailAsync(Guid subscriptionId, CancellationToken ct)
    {
        var subscription = await _db.CustomerSubscriptions
            .Include(s => s.Plan)
            .Include(s => s.Customer).ThenInclude(c => c.Contacts)
            .FirstOrDefaultAsync(s => s.Id == subscriptionId, ct)
            ?? throw new KeyNotFoundException("Abonnement wurde nicht gefunden.");

        var contact = subscription.Customer.Contacts.FirstOrDefault(c => c.IsPrimary)
            ?? subscription.Customer.Contacts.FirstOrDefault();
        var recipient = contact?.Email?.Trim();
        subscription.MandateEmailAttemptCount++;
        subscription.MandateEmailRecipient = recipient;
        subscription.MandateEmailStatus = "Sending";
        subscription.MandateEmailLastError = null;
        await _db.SaveChangesAsync(ct);

        if (string.IsNullOrWhiteSpace(recipient))
            return await MarkMandateEmailFailedAsync(subscription, "Beim Kunden ist keine E-Mail-Adresse hinterlegt.", ct);
        if (string.Equals(subscription.MollieMandateStatus, "valid", StringComparison.OrdinalIgnoreCase))
            return await MarkMandateEmailFailedAsync(subscription, "Für dieses Abonnement besteht bereits ein gültiges Mollie-Mandat.", ct);

        try
        {
            EnsureB2bContractEvidence(subscription);
            var checkout = await StartMandateCheckoutAsync(subscriptionId, ct);
            var startedAt = DateTimeOffset.UtcNow;
            var contactName = string.Join(" ", new[] { contact!.FirstName, contact.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
            if (string.IsNullOrWhiteSpace(contactName)) contactName = subscription.Customer.CompanyName;

            await _email.SendTemplatedEmailAsync(
                recipient,
                "subscription-mandate",
                new Dictionary<string, object>
                {
                    ["ContactName"] = contactName,
                    ["CustomerName"] = subscription.Customer.CompanyName,
                    ["PlanName"] = subscription.Plan.Name,
                    ["MonthlyPrice"] = (subscription.AgreedMonthlyPrice ?? subscription.Plan.MonthlyPrice).ToString("0.00", CultureInfo.GetCultureInfo("de-DE")),
                    ["StartDate"] = subscription.StartDate.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("de-DE")),
                    ["ContractReference"] = subscription.ContractReference ?? "-",
                    ["ContractAcceptedAt"] = subscription.ContractAcceptedAt?.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("de-DE")) ?? "-",
                    ["CheckoutUrl"] = checkout.CheckoutUrl
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

    private async Task<MandateEmailResultDto> MarkMandateEmailFailedAsync(CustomerSubscription subscription, string error, CancellationToken ct)
    {
        subscription.MandateEmailStatus = "Failed";
        subscription.MandateEmailLastError = error.Length > 1000 ? error[..1000] : error;
        await _db.SaveChangesAsync(ct);
        return new MandateEmailResultDto(false, subscription.MandateEmailRecipient, subscription.MandateEmailSentAt, "Failed", subscription.MandateEmailLastError);
    }

    public async Task CollectInvoiceAsync(Guid invoiceId, CancellationToken ct)
    {
        var invoice = await _db.Invoices
            .Include(i => i.Subscription).ThenInclude(s => s!.Plan)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, ct)
            ?? throw new KeyNotFoundException("Rechnung wurde nicht gefunden.");
        var subscription = invoice.Subscription
            ?? throw new InvalidOperationException("Die Rechnung gehört zu keinem Abonnement.");

        if (invoice.Status == InvoiceStatus.Paid) return;
        if (string.IsNullOrWhiteSpace(subscription.MollieCustomerId) ||
            string.IsNullOrWhiteSpace(subscription.MollieMandateId) ||
            !string.Equals(subscription.MollieMandateStatus, "valid", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Für das Abonnement liegt kein gültiges Mollie-Mandat vor.");

        var currentMandate = await GetAsync($"customers/{subscription.MollieCustomerId}/mandates/{subscription.MollieMandateId}", ct);
        subscription.MollieMandateStatus = RequiredString(currentMandate, "status");
        if (!string.Equals(subscription.MollieMandateStatus, "valid", StringComparison.OrdinalIgnoreCase))
        {
            subscription.Status = SubscriptionStatus.Paused;
            subscription.PausedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            throw new InvalidOperationException("Das Mollie-Mandat ist nicht mehr gültig; das Abonnement wurde pausiert.");
        }

        if (!string.IsNullOrWhiteSpace(invoice.ExternalPaymentReference))
        {
            var existing = await GetAsync($"payments/{invoice.ExternalPaymentReference}", ct);
            await ApplyPaymentStatusAsync(existing, ct);
            return;
        }

        var priorPayment = await FindPaymentForInvoiceAsync(subscription.MollieCustomerId, invoice.Id, ct);
        if (priorPayment != null)
        {
            invoice.ExternalPaymentReference = RequiredString(priorPayment, "id");
            await ApplyPaymentStatusAsync(priorPayment, ct);
            return;
        }

        var publicBaseUrl = (_configuration["PublicBaseUrl"] ?? "https://gentlesuite.runasp.net").TrimEnd('/');
        var payment = await PostAsync("payments", new
        {
            amount = new { currency = "EUR", value = invoice.GrossTotal.ToString("0.00", CultureInfo.InvariantCulture) },
            customerId = subscription.MollieCustomerId,
            mandateId = subscription.MollieMandateId,
            sequenceType = "recurring",
            description = $"Rechnung {invoice.InvoiceNumber}",
            webhookUrl = $"{publicBaseUrl}/api/mollie/webhook",
            metadata = new { kind = "invoice", invoiceId = invoice.Id, subscriptionId = subscription.Id }
        }, $"invoice-{invoice.Id:N}", ct);

        invoice.ExternalPaymentReference = RequiredString(payment, "id");
        await ApplyPaymentStatusAsync(payment, ct);
    }

    public async Task HandlePaymentWebhookAsync(string paymentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(paymentId) || !paymentId.StartsWith("tr_", StringComparison.Ordinal))
            throw new ArgumentException("Ungültige Mollie-Zahlungs-ID.");
        var payment = await GetAsync($"payments/{Uri.EscapeDataString(paymentId)}", ct);
        await ApplyPaymentStatusAsync(payment, ct);
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
            var subscription = await _db.CustomerSubscriptions.FirstOrDefaultAsync(s => s.Id == subscriptionId, ct);
            if (subscription == null || subscription.MollieFirstPaymentId != paymentId) return;
            subscription.MollieFirstPaymentStatus = status;

            if (status == "paid" && !string.IsNullOrWhiteSpace(subscription.MollieCustomerId))
            {
                var mandates = await GetAsync($"customers/{subscription.MollieCustomerId}/mandates", ct);
                var mandate = mandates["_embedded"]?["mandates"]?.AsArray()
                    .FirstOrDefault(m => string.Equals(m?["status"]?.GetValue<string>(), "valid", StringComparison.OrdinalIgnoreCase));
                if (mandate != null)
                {
                    subscription.MollieMandateId = RequiredString(mandate, "id");
                    subscription.MollieMandateStatus = RequiredString(mandate, "status");
                    if (subscription.Status == SubscriptionStatus.PendingConfirmation &&
                        subscription.ContractQuoteId != null && subscription.BusinessCustomerConfirmed)
                    {
                        subscription.Status = SubscriptionStatus.Active;
                        subscription.ConfirmedAt = DateTimeOffset.UtcNow;
                    }
                }
            }
            await _db.SaveChangesAsync(ct);
            return;
        }

        if (kind == "invoice" && Guid.TryParse(MetadataString(metadata, "invoiceId"), out var invoiceId))
        {
            var invoice = await _db.Invoices.Include(i => i.Payments).FirstOrDefaultAsync(i => i.Id == invoiceId, ct);
            if (invoice == null) return;
            if (!string.IsNullOrWhiteSpace(invoice.ExternalPaymentReference) && invoice.ExternalPaymentReference != paymentId)
                throw new InvalidOperationException("Mollie-Zahlung stimmt nicht mit der Rechnung überein.");
            invoice.ExternalPaymentReference = paymentId;
            invoice.PaymentCollectionStatus = status;

            if (status == "paid")
            {
                if (!invoice.Payments.Any(p => p.Reference == paymentId))
                    invoice.Payments.Add(new InvoicePayment
                    {
                        Amount = invoice.GrossTotal,
                        PaymentDate = DateTimeOffset.UtcNow,
                        PaymentMethod = "Mollie SEPA-Lastschrift",
                        Reference = paymentId,
                        Note = "Automatisch über Mollie eingezogen"
                    });
                invoice.Status = InvoiceStatus.Paid;
                invoice.PaidAt ??= DateTimeOffset.UtcNow;
            }
            else if (status == "charged_back")
            {
                var reversalReference = $"{paymentId}:chargeback";
                if (!invoice.Payments.Any(p => p.Reference == reversalReference))
                    invoice.Payments.Add(new InvoicePayment
                    {
                        Amount = -invoice.GrossTotal,
                        PaymentDate = DateTimeOffset.UtcNow,
                        PaymentMethod = "Mollie SEPA-Rücklastschrift",
                        Reference = reversalReference,
                        Note = "Rücklastschrift von Mollie gemeldet"
                    });
                invoice.Status = InvoiceStatus.Overdue;
                invoice.PaidAt = null;
            }
            await _db.SaveChangesAsync(ct);
        }
    }

    private async Task<JsonNode?> FindPaymentForInvoiceAsync(string customerId, Guid invoiceId, CancellationToken ct)
    {
        var list = await GetAsync($"customers/{customerId}/payments?limit=250", ct);
        return list["_embedded"]?["payments"]?.AsArray()
            .FirstOrDefault(p => string.Equals(MetadataString(p?["metadata"], "invoiceId"), invoiceId.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureB2bContractEvidence(CustomerSubscription subscription)
    {
        if (subscription.ContractQuoteId == null || string.IsNullOrWhiteSpace(subscription.ContractReference) ||
            subscription.ContractAcceptedAt == null || !subscription.BusinessCustomerConfirmed ||
            subscription.AgreedMonthlyPrice is null or <= 0)
            throw new InvalidOperationException("Die Mollie-Zahlungseinrichtung ist ohne vollständigen B2B-Vertragsnachweis gesperrt.");
    }

    private HttpClient CreateClient()
    {
        var apiKey = _configuration["Mollie:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Mollie ist noch nicht konfiguriert.");
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri("https://api.mollie.com/v2/");
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
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
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
            _logger.LogError("Mollie API returned {StatusCode}: {Body}", (int)response.StatusCode, json);
            throw new InvalidOperationException($"Mollie-Anfrage fehlgeschlagen ({(int)response.StatusCode}).");
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
