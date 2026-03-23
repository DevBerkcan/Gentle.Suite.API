using GentleSuite.Application.DTOs;
using GentleSuite.Application.Interfaces;
using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;
using GentleSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace GentleSuite.Infrastructure.Services;

public class VatServiceImpl : IVatService
{
    private readonly AppDbContext _db;
    public VatServiceImpl(AppDbContext db) { _db = db; }

    public async Task<VatReportDto> GetVatReportAsync(int year, int month, CancellationToken ct)
    {
        var from = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddMonths(1);

        // Finalized invoices in period
        var invoices = await _db.Invoices.Include(i => i.Lines)
            .Where(i => i.IsFinalized && i.InvoiceDate >= from && i.InvoiceDate < to && i.Type != InvoiceType.Cancellation)
            .ToListAsync(ct);

        // Cancellation invoices reduce output
        var cancellations = await _db.Invoices.Include(i => i.Lines)
            .Where(i => i.IsFinalized && i.InvoiceDate >= from && i.InvoiceDate < to && i.Type == InvoiceType.Cancellation)
            .ToListAsync(ct);

        var invoiceLines = new List<VatReportLineDto>();
        decimal outputVat19 = 0, outputVat7 = 0, netBase19 = 0, netBase7 = 0;

        foreach (var inv in invoices)
        {
            foreach (var line in inv.Lines)
            {
                if (line.VatPercent == 19) { outputVat19 += line.VatAmount; netBase19 += line.NetTotal; }
                else if (line.VatPercent == 7) { outputVat7 += line.VatAmount; netBase7 += line.NetTotal; }
                invoiceLines.Add(new VatReportLineDto(inv.InvoiceNumber, inv.InvoiceDate, line.Title, line.NetTotal, line.VatAmount, line.VatPercent));
            }
        }

        // Subtract cancellations
        foreach (var inv in cancellations)
        {
            foreach (var line in inv.Lines)
            {
                if (line.VatPercent == 19) { outputVat19 -= line.VatAmount; netBase19 -= line.NetTotal; }
                else if (line.VatPercent == 7) { outputVat7 -= line.VatAmount; netBase7 -= line.NetTotal; }
                invoiceLines.Add(new VatReportLineDto(inv.InvoiceNumber, inv.InvoiceDate, $"[Storno] {line.Title}", -line.NetTotal, -line.VatAmount, line.VatPercent));
            }
        }

        // Booked expenses in period (Vorsteuer)
        var expenses = await _db.Expenses
            .Where(e => e.Status == ExpenseStatus.Booked && e.ExpenseDate >= from && e.ExpenseDate < to)
            .ToListAsync(ct);

        decimal inputVat = 0;
        var expenseLines = new List<VatReportLineDto>();
        foreach (var exp in expenses)
        {
            inputVat += exp.VatAmount;
            expenseLines.Add(new VatReportLineDto(exp.ExpenseNumber ?? "", exp.ExpenseDate, exp.Description ?? exp.Supplier ?? "", exp.NetAmount, exp.VatAmount, exp.VatPercent));
        }

        var totalOutputVat = outputVat19 + outputVat7;
        var payableTax = totalOutputVat - inputVat;

        return new VatReportDto(year, month, outputVat19, outputVat7, totalOutputVat, inputVat, payableTax, netBase19, netBase7, invoiceLines, expenseLines);
    }

    public async Task<VatPeriodDto> SubmitVatPeriodAsync(int year, int month, CancellationToken ct)
    {
        var report = await GetVatReportAsync(year, month, ct);

        var period = await _db.VatPeriods.FirstOrDefaultAsync(v => v.Year == year && v.Month == month, ct);
        if (period == null)
        {
            period = new VatPeriod { Year = year, Month = month };
            _db.VatPeriods.Add(period);
        }

        period.OutputVat = report.TotalOutputVat;
        period.InputVat = report.InputVat;
        period.PayableTax = report.PayableTax;
        period.IsSubmitted = true;
        period.SubmittedAt = DateTimeOffset.UtcNow;
        period.IsFinalized = true;
        period.FinalizedAt = DateTimeOffset.UtcNow;
        period.RetentionUntil = DateTimeOffset.UtcNow.AddYears(10);

        // GoBD hash
        var hashInput = $"{year}|{month}|{period.OutputVat:F2}|{period.InputVat:F2}|{period.PayableTax:F2}|{period.SubmittedAt:O}";
        using var sha = SHA256.Create();
        period.DocumentHash = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(hashInput)));

        await _db.SaveChangesAsync(ct);

        return new VatPeriodDto(period.Id, period.Year, period.Month, period.IsQuarterly, period.OutputVat, period.InputVat, period.PayableTax, period.IsSubmitted);
    }

    public async Task<byte[]> ExportDatevAsync(DatevExportRequest req, CancellationToken ct)
    {
        var from = new DateTimeOffset(req.Year, req.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddMonths(1);
        var co = await _db.CompanySettings.FirstOrDefaultAsync(ct);
        var sb = new StringBuilder();

        // DATEV Header (simplified Buchungsstapel format)
        sb.AppendLine("\"EXTF\";700;21;\"Buchungsstapel\";7;;;;;\"\";\"\";\"\";" +
            $"\"{co?.TaxId ?? ""}\";;;;;{req.Year}0101;4;;;;;;\"\";\"\"");
        sb.AppendLine("Umsatz (ohne Soll/Haben-Kz);Soll/Haben-Kz;WKZ Umsatz;Kurs;Basisumsatz;WKZ Basisumsatz;" +
            "Konto;Gegenkonto (ohne BU-Schluessel);BU-Schluessel;Belegdatum;Belegfeld 1;Belegfeld 2;" +
            "Skonto;Buchungstext");

        if (req.IncludeInvoices)
        {
            var invoices = await _db.Invoices.Include(i => i.Lines)
                .Where(i => i.IsFinalized && i.InvoiceDate >= from && i.InvoiceDate < to)
                .ToListAsync(ct);

            foreach (var inv in invoices)
            {
                var sign = inv.Type == InvoiceType.Cancellation ? -1 : 1;
                foreach (var line in inv.Lines)
                {
                    var account = line.VatPercent == 19 ? "8400" : line.VatPercent == 7 ? "8300" : "8100";
                    var amount = (sign * line.GrossTotal).ToString("F2").Replace(".", ",");
                    var date = inv.InvoiceDate.ToString("ddMM");
                    sb.AppendLine($"{amount};S;EUR;;;;\"{account}\";\"1400\";;{date};\"{inv.InvoiceNumber}\";;;\"{line.Title}\"");
                }
            }
        }

        if (req.IncludeExpenses)
        {
            var expenses = await _db.Expenses.Include(e => e.Category)
                .Where(e => e.Status == ExpenseStatus.Booked && e.ExpenseDate >= from && e.ExpenseDate < to)
                .ToListAsync(ct);

            foreach (var exp in expenses)
            {
                var account = exp.Category?.AccountNumber ?? "4590";
                var amount = exp.GrossAmount.ToString("F2").Replace(".", ",");
                var date = exp.ExpenseDate.ToString("ddMM");
                sb.AppendLine($"{amount};S;EUR;;;;\"{account}\";\"1600\";;{date};\"{exp.ExpenseNumber ?? ""}\";;;\"{exp.Supplier ?? exp.Description ?? ""}\"");
            }
        }

        return Encoding.GetEncoding("iso-8859-1").GetBytes(sb.ToString());
    }

    public async Task<byte[]> GenerateElsterXmlAsync(int year, int month, CancellationToken ct)
    {
        var report = await GetVatReportAsync(year, month, ct);
        var co = await _db.CompanySettings.FirstOrDefaultAsync(ct);
        var inv = new System.Globalization.CultureInfo("de-DE");

        string Fmt(decimal v) => v.ToString("F2", inv);

        var xml = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XComment("GentleSuite – ELSTER UStVA Ausfüllhilfe. Diese Datei ist KEIN offizielles ELSTER-Dokument, sondern eine Ausfüllhilfe für das ELSTER Online Portal (www.elster.de)."),
            new XElement("UStVA_Ausfuellhilfe",
                new XAttribute("Jahr", year),
                new XAttribute("Zeitraum", month.ToString("00")),
                new XAttribute("Erstellt", DateTime.Now.ToString("dd.MM.yyyy")),
                new XElement("Steuerpflichtiger",
                    new XElement("Name", co?.LegalName ?? co?.CompanyName ?? ""),
                    new XElement("Steuernummer", co?.TaxId ?? ""),
                    new XElement("UStIdNr", co?.VatId ?? "")
                ),
                new XElement("Kennzahlen",
                    new XComment("KZ 81 + 83: Steuerpflichtige Umsätze 19%"),
                    new XElement("Kz81", new XAttribute("Bezeichnung", "Lieferungen/Leistungen 19% – Nettobetrag"), Fmt(report.NetBase19)),
                    new XElement("Kz83", new XAttribute("Bezeichnung", "Umsatzsteuer 19%"), Fmt(report.OutputVat19)),
                    new XComment("KZ 86 + 85: Steuerpflichtige Umsätze 7%"),
                    new XElement("Kz86", new XAttribute("Bezeichnung", "Lieferungen/Leistungen 7% – Nettobetrag"), Fmt(report.NetBase7)),
                    new XElement("Kz85", new XAttribute("Bezeichnung", "Umsatzsteuer 7%"), Fmt(report.OutputVat7)),
                    new XComment("KZ 66: Vorsteuer aus Eingangsrechnungen"),
                    new XElement("Kz66", new XAttribute("Bezeichnung", "Abziehbare Vorsteuerbeträge"), Fmt(report.InputVat)),
                    new XComment("KZ 69 oder KZ 67: Zahllast / Erstattung"),
                    new XElement("Kz69", new XAttribute("Bezeichnung", "Verbleibende USt-Zahllast (wenn positiv)"), Fmt(Math.Max(0, report.PayableTax))),
                    new XElement("Kz67", new XAttribute("Bezeichnung", "Erstattungsbetrag (wenn Überschuss)"), Fmt(Math.Max(0, -report.PayableTax)))
                )
            )
        );

        using var ms = new MemoryStream();
        xml.Save(ms);
        return ms.ToArray();
    }
}
