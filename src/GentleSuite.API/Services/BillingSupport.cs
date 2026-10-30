using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using GentleSuite.Domain.Enums;

namespace GentleSuite.Infrastructure.Services;

/// <summary>"Heute" für Abrechnung/Einzug immer in Europe/Berlin — unabhängig von der Server-Zeitzone
/// (MonsterASP läuft nicht zwingend in MEZ). Werte sind Berliner Mitternacht mit korrektem Offset.</summary>
public static class BerlinTime
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    public static DateTimeOffset Today => StartOfDay(DateTimeOffset.UtcNow);

    /// <summary>Berlin midnight of the calendar day the given instant falls on (in Berlin).</summary>
    public static DateTimeOffset StartOfDay(DateTimeOffset instant) => Midnight(TimeZoneInfo.ConvertTime(instant, Zone).Date);

    /// <summary>Berlin midnight of a calendar date, e.g. 2026-11-12T00:00:00+01:00.</summary>
    public static DateTimeOffset Midnight(DateTime date)
    {
        var local = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Zone.GetUtcOffset(local));
    }
}

public static class BillingMath
{
    public static int VatPercent(TaxMode companyTaxMode) => companyTaxMode == TaxMode.Standard ? 19 : 0;

    /// <summary>Gross amount exactly as it ends up on the invoice (DECIMAL(18,2) rounds half away from zero).</summary>
    public static decimal Gross(decimal net, TaxMode companyTaxMode)
        => Math.Round(net + net * VatPercent(companyTaxMode) / 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>Per-installment amount — identical rounding to the Preisangebot shown to the customer
    /// (PaymentPlanCalculator: Math.Round(total / months, 2)), the remainder goes on the last installment.</summary>
    public static decimal InstallmentAmount(decimal total, int months)
        => months > 0 ? Math.Round(total / months, 2) : total;

    /// <summary>Net amount of installment number <paramref name="index"/> (0-based).</summary>
    public static decimal InstallmentNet(decimal total, int count, decimal baseAmount, int index)
        => count > 0 && index >= count - 1 ? total - baseAmount * (count - 1) : baseAmount;

    public static int CycleMonths(BillingCycle cycle) => cycle switch { BillingCycle.Quarterly => 3, BillingCycle.Yearly => 12, _ => 1 };

    /// <summary>Next period start after <paramref name="currentPeriodStart"/>, always computed from the anchor
    /// (anchor.AddMonths(n)) so month-end dates don't drift (31.01. → 28.02. → 31.03., not 28.03.).</summary>
    public static DateTimeOffset NextPeriodStart(DateTimeOffset anchor, DateTimeOffset currentPeriodStart, int stepMonths)
    {
        stepMonths = Math.Max(1, stepMonths);
        var monthsFromAnchor = (currentPeriodStart.Year - anchor.Year) * 12 + currentPeriodStart.Month - anchor.Month;
        var periods = Math.Max(0, (int)Math.Floor(monthsFromAnchor / (double)stepMonths));
        var next = anchor.AddMonths((periods + 1) * stepMonths);
        while (next <= currentPeriodStart) next = anchor.AddMonths((++periods + 1) * stepMonths);
        return next;
    }
}

public static class MandateLabels
{
    public static string PaymentMethodLabel(string? method) => method switch
    {
        "directdebit" => "Mollie SEPA-Lastschrift",
        "creditcard" => "Mollie Kreditkarte",
        "paypal" => "Mollie PayPal",
        null or "" => "Mollie",
        _ => $"Mollie ({method})"
    };

    public static string CollectionSentence(string? method, decimal gross, DateTimeOffset date)
    {
        var amount = gross.ToString("N2", CultureInfo.GetCultureInfo("de-DE"));
        var basis = method switch
        {
            "directdebit" => "auf Grundlage des erteilten SEPA-Lastschriftmandats",
            "creditcard" => "von der hinterlegten Kreditkarte",
            "paypal" => "über das hinterlegte PayPal-Konto",
            _ => "über die hinterlegte Zahlungsart"
        };
        return $"Der Rechnungsbetrag von {amount} € wird am {date:dd.MM.yyyy} {basis} automatisch über Mollie eingezogen.";
    }
}

public static class IbanTools
{
    public static string Normalize(string? iban) => new string((iban ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    /// <summary>ISO 13616 structure + mod-97 check.</summary>
    public static bool IsValid(string normalized)
    {
        if (normalized.Length is < 15 or > 34) return false;
        if (!char.IsLetter(normalized[0]) || !char.IsLetter(normalized[1]) || !char.IsDigit(normalized[2]) || !char.IsDigit(normalized[3])) return false;
        if (normalized.Any(c => !(c is >= 'A' and <= 'Z' or >= '0' and <= '9'))) return false;
        if (normalized.StartsWith("DE") && normalized.Length != 22) return false;
        var rearranged = normalized[4..] + normalized[..4];
        var digits = string.Concat(rearranged.Select(c => char.IsLetter(c) ? (c - 'A' + 10).ToString(CultureInfo.InvariantCulture) : c.ToString()));
        return BigInteger.Parse(digits, CultureInfo.InvariantCulture) % 97 == 1;
    }

    public static string? Last4(string? account)
    {
        var n = Normalize(account);
        return n.Length >= 4 ? n[^4..] : null;
    }

    /// <summary>Masks anything that looks like an IBAN before it reaches a log line.</summary>
    public static string MaskIbans(string text) => System.Text.RegularExpressions.Regex.Replace(text ?? string.Empty,
        @"\b([A-Z]{2}\d{2})[A-Z0-9 ]{8,30}([A-Z0-9]{4})\b", "$1********$2");

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>"Nächster Einzug" für Abo-DTO und öffentliche Einrichtungsseite: Termin/Betrag der tatsächlich
/// offenen Rechnung (PaymentCollectionDueDate, brutto), sonst Prognose max(NextBillingDate, heute + Vorabankündigung).</summary>
public static class NextCollection
{
    public static readonly string[] OpenStatuses = { "scheduled", "open", "pending", "authorized" };

    public record OpenInvoice(Guid SubscriptionId, string InvoiceNumber, decimal GrossTotal, DateTimeOffset? DueDate);
    public record Info(DateTimeOffset? Date, decimal? Amount, string? InvoiceNumber, bool IsForecast, string? BlockedReason);

    public static async Task<Dictionary<Guid, OpenInvoice>> LoadOpenCollectionsAsync(GentleSuite.Infrastructure.Data.AppDbContext db, List<Guid> subscriptionIds, CancellationToken ct)
    {
        if (subscriptionIds.Count == 0) return new Dictionary<Guid, OpenInvoice>();
        var rows = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.Invoices
            .Where(i => i.SubscriptionId != null && subscriptionIds.Contains(i.SubscriptionId.Value) &&
                        i.Status != InvoiceStatus.Paid && i.Status != InvoiceStatus.Cancelled &&
                        i.PaymentCollectionStatus != null && OpenStatuses.Contains(i.PaymentCollectionStatus))
            .Select(i => new OpenInvoice(i.SubscriptionId!.Value, i.InvoiceNumber, i.GrossTotal, i.PaymentCollectionDueDate)), ct);
        return rows.GroupBy(r => r.SubscriptionId).ToDictionary(g => g.Key, g => g.OrderBy(r => r.DueDate).First());
    }

    public static Info Compute(GentleSuite.Domain.Entities.CustomerSubscription s, OpenInvoice? open, TaxMode taxMode, int preNotificationDays, DateTimeOffset today)
    {
        if (open != null)
            return new Info(open.DueDate, Math.Round(open.GrossTotal, 2), open.InvoiceNumber, false, null);

        var stage = DashboardAmounts.Stage(s);
        if (stage == "completed") return new Info(null, null, null, false, null);
        var count = s.ContractDurationMonths ?? 1;
        if (s.IsInstallmentPlan && s.InstallmentsCompleted >= count) return new Info(null, null, null, false, null);

        decimal net;
        if (s.IsInstallmentPlan)
        {
            var total = s.TotalInstallmentAmount ?? 0m;
            net = BillingMath.InstallmentNet(total, count, s.AgreedMonthlyPrice ?? BillingMath.InstallmentAmount(total, count), s.InstallmentsCompleted);
        }
        else net = (s.AgreedMonthlyPrice ?? s.Plan?.MonthlyPrice ?? 0m) * BillingMath.CycleMonths(s.ContractBillingCycle);
        var amount = BillingMath.Gross(net, taxMode);

        string? blocked = stage switch
        {
            "mandate" => string.Equals(s.MollieMandateStatus, "pending", StringComparison.OrdinalIgnoreCase) ? "Mandat wird geprüft" : "Mandat fehlt",
            "authorization" => "Freigabe fehlt",
            "review" => "Vertragsdaten prüfen",
            "paused" => "Abo pausiert",
            "closed" => "Abo beendet",
            _ => null
        };
        if (blocked != null) return new Info(null, amount, null, true, blocked);

        var earliest = today.AddDays(preNotificationDays);
        var planned = BerlinTime.StartOfDay(s.NextBillingDate);
        return new Info(planned > earliest ? planned : earliest, amount, null, true, null);
    }
}
