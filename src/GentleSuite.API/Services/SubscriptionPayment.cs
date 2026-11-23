using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;
using GentleSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GentleSuite.Infrastructure.Services;

/// <summary>Zahlungsart einer Serienrechnung: Mollie-Einzug (braucht ein gültiges Mandat) oder Überweisung durch
/// den Kunden (Rechnung wird nur erstellt und versendet, kein Mandat, kein Einzug).</summary>
public static class SubscriptionPayment
{
    public static bool IsTransfer(CustomerSubscription s) => s.PaymentMethod == SubscriptionPaymentMethod.Transfer;

    /// <summary>Darf abgerechnet/aktiviert werden: bei Überweisung immer, bei Mollie erst mit gültigem Mandat.</summary>
    public static bool IsPaymentReady(CustomerSubscription s) =>
        IsTransfer(s) || string.Equals(s.MollieMandateStatus, "valid", StringComparison.OrdinalIgnoreCase);

    /// <summary>Setzt die Zahlungsart. Ein noch ausstehendes Abo mit B2B-Bestätigung wartet bei Überweisung auf
    /// kein Mandat mehr und wird daher aktiv (die Abrechnung startet trotzdem erst mit der Freigabe).</summary>
    public static void Apply(CustomerSubscription s, SubscriptionPaymentMethod method)
    {
        s.PaymentMethod = method;
        if (method == SubscriptionPaymentMethod.Transfer && s.Status == SubscriptionStatus.PendingConfirmation && s.BusinessCustomerConfirmed)
        {
            s.Status = SubscriptionStatus.Active;
            s.ConfirmedAt ??= DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Text für die Zahlungsbedingungen einer Überweisungs-Rechnung.</summary>
    public static string TransferTerms(decimal grossTotal, DateTimeOffset dueDate, string invoiceNumber) =>
        $"Bitte überweisen Sie den Rechnungsbetrag von {grossTotal.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("de-DE"))} € bis zum {dueDate:dd.MM.yyyy} " +
        $"unter Angabe der Rechnungsnummer {invoiceNumber} auf das unten genannte Konto.";
}
