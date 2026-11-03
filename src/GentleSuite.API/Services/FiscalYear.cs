namespace GentleSuite.Infrastructure.Services;

/// <summary>
/// Geschäftsjahr = Kalenderjahr nach Berliner Zeit. Ein Jahr ist abgeschlossen, sobald das nächste begonnen hat:
/// Angebote, Rechnungen und Schätzungen mit Datum in einem abgeschlossenen Jahr können nur noch gelesen werden.
/// Laufende Verträge, Abos und Ratenpläne sind nicht an ein Jahr gebunden und laufen weiter; Zahlungen, Stornos
/// und Folgedokumente (mit neuer Nummer im laufenden Jahr) bleiben möglich.
/// </summary>
public static class FiscalYear
{
    /// <summary>Ab diesem Jahr tragen Angebots- und Rechnungsnummern das Jahr (RE-2027-0001) und beginnen jährlich neu.</summary>
    public const int FirstYearWithYearInNumber = 2027;

    /// <summary>Nur für lokale Tests (Jahreswechsel simulieren); in Production wird es beim Start nie gesetzt.</summary>
    public static DateTimeOffset? TestTodayOverride { get; set; }

    public static DateTimeOffset Today => TestTodayOverride is { } t ? BerlinTime.StartOfDay(t) : BerlinTime.Today;
    public static int Current => Today.Year;
    public static int YearOf(DateTimeOffset date) => BerlinTime.LocalDate(date).Year;
    public static bool IsClosed(DateTimeOffset date) => YearOf(date) < Current;

    /// <summary>[1. Januar, 1. Januar des Folgejahres) als Berliner Mitternacht — für Listenfilter.</summary>
    public static (DateTimeOffset From, DateTimeOffset To) Range(int year) =>
        (BerlinTime.Midnight(new DateTime(year, 1, 1)), BerlinTime.Midnight(new DateTime(year + 1, 1, 1)));

    /// <summary>Wirft, wenn das Dokument zu einem abgeschlossenen Geschäftsjahr gehört (→ 400 mit verständlicher Meldung).</summary>
    public static void EnsureOpen(DateTimeOffset documentDate, string document)
    {
        if (!IsClosed(documentDate)) return;
        var year = YearOf(documentDate);
        throw new InvalidOperationException(
            $"Das Geschäftsjahr {year} ist abgeschlossen – {document} aus {year} kann nur noch angesehen werden. " +
            $"Für Änderungen bitte ein neues Dokument in {Current} anlegen (z. B. „Duplizieren“).");
    }
}
