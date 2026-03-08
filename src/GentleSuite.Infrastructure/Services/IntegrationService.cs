using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GentleSuite.Application.DTOs;
using GentleSuite.Application.Interfaces;
using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;
using GentleSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GentleSuite.Infrastructure.Services;

public class IntegrationServiceImpl : IIntegrationService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<IntegrationServiceImpl> _log;

    public IntegrationServiceImpl(AppDbContext db, IHttpClientFactory http, ILogger<IntegrationServiceImpl> log)
    { _db = db; _http = http; _log = log; }

    private async Task<IntegrationSettings> EnsureSettingsAsync(CancellationToken ct)
    {
        var s = await _db.IntegrationSettings.FirstOrDefaultAsync(ct);
        if (s == null) { s = new IntegrationSettings(); _db.IntegrationSettings.Add(s); await _db.SaveChangesAsync(ct); }
        return s;
    }

    public async Task<IntegrationSettingsDto> GetAsync(CancellationToken ct)
    {
        var s = await EnsureSettingsAsync(ct);
        return new IntegrationSettingsDto(
            s.PayPalEnabled, s.PayPalClientId, !string.IsNullOrEmpty(s.PayPalClientSecret), s.PayPalLastSync,
            s.BankEnabled, s.BankAccountId, s.BankRequisitionId, s.BankLastSync);
    }

    public async Task UpdatePayPalAsync(UpdatePayPalRequest req, CancellationToken ct)
    {
        var s = await EnsureSettingsAsync(ct);
        s.PayPalClientId = req.ClientId;
        s.PayPalClientSecret = req.ClientSecret;
        s.PayPalEnabled = true;
        await _db.SaveChangesAsync(ct);
    }

    public async Task DisconnectPayPalAsync(CancellationToken ct)
    {
        var s = await EnsureSettingsAsync(ct);
        s.PayPalClientId = null; s.PayPalClientSecret = null;
        s.PayPalEnabled = false; s.PayPalLastSync = null;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<string> SetupBankAsync(SetupBankRequest req, CancellationToken ct)
    {
        var s = await EnsureSettingsAsync(ct);
        s.GoCardlessSecretId = req.GoCardlessSecretId;
        s.GoCardlessSecretKey = req.GoCardlessSecretKey;
        await _db.SaveChangesAsync(ct);

        var token = await GetGoCardlessTokenAsync(req.GoCardlessSecretId, req.GoCardlessSecretKey, ct);

        // Find institution for IBAN country
        var iban = req.Iban.Replace(" ", "");
        var country = iban.Length >= 2 ? iban[..2].ToUpper() : "DE";

        var client = _http.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Try to find Fyrst or Deutsche Bank institution
        var instResp = await client.GetAsync($"https://bankaccountdata.gocardless.com/api/v2/institutions/?country={country}", ct);
        var instJson = await instResp.Content.ReadAsStringAsync(ct);
        var institutions = JsonDocument.Parse(instJson).RootElement;

        string? institutionId = null;
        foreach (var inst in institutions.EnumerateArray())
        {
            var id = inst.GetProperty("id").GetString() ?? "";
            // Fyrst is built on Deutsche Bank infrastructure
            if (id.Contains("FYRST") || id.Contains("DEUTSCHE_BANK") || id.Contains("DBDE"))
            {
                institutionId = id;
                break;
            }
        }
        // Fallback: use first institution if none matched
        if (institutionId == null && institutions.GetArrayLength() > 0)
            institutionId = institutions[0].GetProperty("id").GetString();
        if (institutionId == null)
            throw new InvalidOperationException("Keine passende Bank gefunden.");

        // Create end user agreement
        var euaPayload = JsonSerializer.Serialize(new { institution_id = institutionId, max_historical_days = 90, access_valid_for_days = 90 });
        var euaResp = await client.PostAsync("https://bankaccountdata.gocardless.com/api/v2/agreements/enduser/",
            new StringContent(euaPayload, Encoding.UTF8, "application/json"), ct);
        var euaJson = await euaResp.Content.ReadAsStringAsync(ct);
        var euaId = JsonDocument.Parse(euaJson).RootElement.GetProperty("id").GetString();

        // Create requisition
        var reqPayload = JsonSerializer.Serialize(new
        {
            redirect = "https://gentlesuite.vercel.app/settings",
            institution_id = institutionId,
            agreement = euaId,
            reference = $"gentlesuite-{Guid.NewGuid():N}"
        });
        var reqResp = await client.PostAsync("https://bankaccountdata.gocardless.com/api/v2/requisitions/",
            new StringContent(reqPayload, Encoding.UTF8, "application/json"), ct);
        var reqJson = await reqResp.Content.ReadAsStringAsync(ct);
        var reqDoc = JsonDocument.Parse(reqJson).RootElement;
        var requisitionId = reqDoc.GetProperty("id").GetString()!;
        var authLink = reqDoc.GetProperty("link").GetString()!;

        s.BankRequisitionId = requisitionId;
        await _db.SaveChangesAsync(ct);

        return authLink;
    }

    public async Task ConfirmBankAsync(ConfirmBankRequest req, CancellationToken ct)
    {
        var s = await EnsureSettingsAsync(ct);
        if (string.IsNullOrEmpty(s.GoCardlessSecretId) || string.IsNullOrEmpty(s.GoCardlessSecretKey))
            throw new InvalidOperationException("GoCardless-Zugangsdaten nicht konfiguriert.");

        var token = await GetGoCardlessTokenAsync(s.GoCardlessSecretId, s.GoCardlessSecretKey, ct);
        var client = _http.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var reqResp = await client.GetAsync($"https://bankaccountdata.gocardless.com/api/v2/requisitions/{req.RequisitionId}/", ct);
        var reqJson = await reqResp.Content.ReadAsStringAsync(ct);
        var accounts = JsonDocument.Parse(reqJson).RootElement.GetProperty("accounts");

        if (accounts.GetArrayLength() == 0)
            throw new InvalidOperationException("Noch keine Konten autorisiert. Bitte erst die Bank-Autorisierung abschließen.");

        s.BankAccountId = accounts[0].GetString();
        s.BankRequisitionId = req.RequisitionId;
        s.BankEnabled = true;
        await _db.SaveChangesAsync(ct);
    }

    public async Task DisconnectBankAsync(CancellationToken ct)
    {
        var s = await EnsureSettingsAsync(ct);
        s.GoCardlessSecretId = null; s.GoCardlessSecretKey = null;
        s.BankRequisitionId = null; s.BankAccountId = null;
        s.BankEnabled = false; s.BankLastSync = null;
        await _db.SaveChangesAsync(ct);
    }

    public async Task SyncPayPalAsync(CancellationToken ct)
    {
        var s = await EnsureSettingsAsync(ct);
        if (!s.PayPalEnabled || string.IsNullOrEmpty(s.PayPalClientId) || string.IsNullOrEmpty(s.PayPalClientSecret))
        { _log.LogDebug("PayPal nicht konfiguriert, überspringe Sync."); return; }

        try
        {
            var client = _http.CreateClient();
            // OAuth2 Token
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{s.PayPalClientId}:{s.PayPalClientSecret}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            var tokenResp = await client.PostAsync("https://api-m.paypal.com/v1/oauth2/token",
                new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("grant_type", "client_credentials") }), ct);
            if (!tokenResp.IsSuccessStatusCode) { _log.LogWarning("PayPal OAuth fehlgeschlagen: {Status}", tokenResp.StatusCode); return; }
            var tokenJson = JsonDocument.Parse(await tokenResp.Content.ReadAsStringAsync(ct));
            var accessToken = tokenJson.RootElement.GetProperty("access_token").GetString()!;

            // Fetch transactions
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var startDate = (s.PayPalLastSync ?? DateTimeOffset.UtcNow.AddDays(-30)).ToString("yyyy-MM-ddTHH:mm:sszzz");
            var endDate = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz");
            var txResp = await client.GetAsync(
                $"https://api-m.paypal.com/v1/reporting/transactions?start_date={Uri.EscapeDataString(startDate)}&end_date={Uri.EscapeDataString(endDate)}&fields=all&page_size=500", ct);
            if (!txResp.IsSuccessStatusCode) { _log.LogWarning("PayPal Transaktionen fehlgeschlagen: {Status}", txResp.StatusCode); return; }
            var txJson = JsonDocument.Parse(await txResp.Content.ReadAsStringAsync(ct));

            var transactions = txJson.RootElement.GetProperty("transaction_details");
            int imported = 0;
            foreach (var tx in transactions.EnumerateArray())
            {
                var info = tx.GetProperty("transaction_info");
                var txId = info.GetProperty("transaction_id").GetString()!;
                if (await _db.BankTransactions.AnyAsync(t => t.Reference == txId, ct)) continue;

                var amountStr = info.TryGetProperty("transaction_amount", out var amtEl)
                    ? amtEl.TryGetProperty("value", out var valEl) ? valEl.GetString() : "0" : "0";
                var amount = decimal.TryParse(amountStr, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var amt) ? amt : 0;

                var date = info.TryGetProperty("transaction_initiation_date", out var dateEl)
                    ? DateTimeOffset.TryParse(dateEl.GetString(), out var d) ? d : DateTimeOffset.UtcNow
                    : DateTimeOffset.UtcNow;

                var name = info.TryGetProperty("transaction_subject", out var subj) ? subj.GetString()
                    : info.TryGetProperty("transaction_note", out var note) ? note.GetString() : null;

                _db.BankTransactions.Add(new BankTransaction
                {
                    TransactionDate = date,
                    Description = name ?? $"PayPal {txId}",
                    Amount = amount,
                    Reference = txId,
                    Iban = "PAYPAL",
                    Sender = amount > 0 ? "PayPal Eingang" : null,
                    Recipient = amount < 0 ? "PayPal Ausgang" : null,
                    Status = BankTransactionStatus.Unmatched,
                });
                imported++;
            }

            s.PayPalLastSync = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("PayPal Sync: {Count} neue Transaktionen importiert.", imported);
        }
        catch (Exception ex) { _log.LogError(ex, "Fehler beim PayPal-Sync."); }
    }

    public async Task SyncBankAsync(CancellationToken ct)
    {
        var s = await EnsureSettingsAsync(ct);
        if (!s.BankEnabled || string.IsNullOrEmpty(s.BankAccountId) || string.IsNullOrEmpty(s.GoCardlessSecretId))
        { _log.LogDebug("Bank nicht konfiguriert, überspringe Sync."); return; }

        try
        {
            var token = await GetGoCardlessTokenAsync(s.GoCardlessSecretId!, s.GoCardlessSecretKey!, ct);
            var client = _http.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var dateFrom = (s.BankLastSync ?? DateTimeOffset.UtcNow.AddDays(-90)).ToString("yyyy-MM-dd");
            var txResp = await client.GetAsync(
                $"https://bankaccountdata.gocardless.com/api/v2/accounts/{s.BankAccountId}/transactions/?date_from={dateFrom}", ct);
            if (!txResp.IsSuccessStatusCode) { _log.LogWarning("GoCardless Transaktionen fehlgeschlagen: {Status}", txResp.StatusCode); return; }
            var txJson = JsonDocument.Parse(await txResp.Content.ReadAsStringAsync(ct));

            var allTx = txJson.RootElement.GetProperty("transactions");
            int imported = 0;
            foreach (var group in new[] { "booked", "pending" })
            {
                if (!allTx.TryGetProperty(group, out var list)) continue;
                foreach (var tx in list.EnumerateArray())
                {
                    var txId = tx.TryGetProperty("transactionId", out var idEl) ? idEl.GetString()
                        : tx.TryGetProperty("internalTransactionId", out var intId) ? intId.GetString() : null;
                    if (txId == null || await _db.BankTransactions.AnyAsync(t => t.Reference == txId, ct)) continue;

                    var amountStr = tx.TryGetProperty("transactionAmount", out var amtEl)
                        ? amtEl.TryGetProperty("amount", out var valEl) ? valEl.GetString() : "0" : "0";
                    var amount = decimal.TryParse(amountStr, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var amt) ? amt : 0;

                    var dateStr = tx.TryGetProperty("bookingDate", out var bdEl) ? bdEl.GetString()
                        : tx.TryGetProperty("valueDate", out var vdEl) ? vdEl.GetString() : null;
                    var date = DateTimeOffset.TryParse(dateStr, out var d) ? d : DateTimeOffset.UtcNow;

                    var desc = tx.TryGetProperty("remittanceInformationUnstructured", out var descEl) ? descEl.GetString()
                        : tx.TryGetProperty("additionalInformation", out var addEl) ? addEl.GetString() : null;
                    var creditorName = tx.TryGetProperty("creditorName", out var cEl) ? cEl.GetString() : null;
                    var debtorName = tx.TryGetProperty("debtorName", out var dEl) ? dEl.GetString() : null;
                    var creditorIban = tx.TryGetProperty("creditorAccount", out var caEl)
                        ? caEl.TryGetProperty("iban", out var ciEl) ? ciEl.GetString() : null : null;

                    _db.BankTransactions.Add(new BankTransaction
                    {
                        TransactionDate = date,
                        Description = desc ?? creditorName ?? debtorName ?? txId,
                        Amount = amount,
                        Reference = txId,
                        Iban = creditorIban ?? "FYRST",
                        Sender = amount > 0 ? debtorName : null,
                        Recipient = amount < 0 ? creditorName : null,
                        Status = BankTransactionStatus.Unmatched,
                    });
                    imported++;
                }
            }

            s.BankLastSync = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Bank-Sync: {Count} neue Transaktionen importiert.", imported);
        }
        catch (Exception ex) { _log.LogError(ex, "Fehler beim Bank-Sync."); }
    }

    private async Task<string> GetGoCardlessTokenAsync(string secretId, string secretKey, CancellationToken ct)
    {
        var client = _http.CreateClient();
        var payload = JsonSerializer.Serialize(new { secret_id = secretId, secret_key = secretKey });
        var resp = await client.PostAsync("https://bankaccountdata.gocardless.com/api/v2/token/new/",
            new StringContent(payload, Encoding.UTF8, "application/json"), ct);
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return json.RootElement.GetProperty("access").GetString()!;
    }
}
