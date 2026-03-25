using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;
using GentleSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QRCoder;
using System.Text.Json;
using GentleSuite.Application.Interfaces;

namespace GentleSuite.Infrastructure.Pdf;

public class PdfService : IPdfService
{
    private readonly AppDbContext _db;
    private readonly IFileStorageService _storage;
    public PdfService(AppDbContext db, IFileStorageService storage) { _db = db; _storage = storage; QuestPDF.Settings.License = LicenseType.Community; }

    private async Task<byte[]?> LoadLogoAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            using var stream = await _storage.DownloadAsync(path);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    public async Task<byte[]> GenerateQuotePdfAsync(Quote quote, CompanySettings co, CancellationToken ct = default)
    {
        var logo = await LoadLogoAsync(co.LogoPath);
        var contact = quote.Customer.Contacts.FirstOrDefault(c => c.IsPrimary) ?? quote.Customer.Contacts.FirstOrDefault();
        var loc = quote.Customer.Locations.FirstOrDefault(l => l.IsPrimary) ?? quote.Customer.Locations.FirstOrDefault();
        var oneTime = quote.Lines.Where(l => l.LineType == QuoteLineType.OneTime).OrderBy(l => l.SortOrder).ToList();
        var recurring = quote.Lines.Where(l => l.LineType == QuoteLineType.RecurringMonthly).OrderBy(l => l.SortOrder).ToList();
        List<LegalTextBlock>? legal = null;
        if (!string.IsNullOrEmpty(quote.LegalTextBlocks))
        {
            var keys = JsonSerializer.Deserialize<List<string>>(quote.LegalTextBlocks);
            if (keys?.Any() == true) legal = await _db.LegalTextBlocks.Where(b => keys.Contains(b.Key) && b.IsActive).OrderBy(b => b.SortOrder).ToListAsync(ct);
        }

        return Document.Create(c => c.Page(p =>
        {
            p.Size(PageSizes.A4); p.MarginTop(30); p.MarginBottom(30); p.MarginHorizontal(45);
            p.DefaultTextStyle(x => x.FontSize(9.5f).FontColor("#101828"));
            p.Header().Element(h => BuildDocHeader(h, co, logo, "ANGEBOT", $"Nr. {quote.QuoteNumber} · V{quote.Version}", quote.Customer, contact, loc, $"Datum: {quote.CreatedAt:dd.MM.yyyy}", quote.ExpiresAt.HasValue ? $"Gültig bis: {quote.ExpiresAt:dd.MM.yyyy}" : null));
            p.Content().PaddingTop(16).Column(col =>
            {
                if (!string.IsNullOrEmpty(quote.Subject)) col.Item().Text($"Betr.: {quote.Subject}").Bold().FontSize(11);
                if (!string.IsNullOrEmpty(quote.IntroText)) col.Item().PaddingTop(8).Text(quote.IntroText).FontSize(9);
                if (oneTime.Any()) { col.Item().PaddingTop(14).Text("Einmalige Leistungen").Bold().FontSize(10).FontColor("#344054"); col.Item().PaddingTop(6).Element(e => BuildQuoteTable(e, oneTime, false)); col.Item().AlignRight().PaddingTop(4).Text($"Summe: {quote.SubtotalOneTime:N2} €").Bold(); }
                if (recurring.Any()) { col.Item().PaddingTop(14).Text("Monatliche Leistungen").Bold().FontSize(10).FontColor("#344054"); col.Item().PaddingTop(6).Element(e => BuildQuoteTable(e, recurring, true)); col.Item().AlignRight().PaddingTop(4).Text($"Monatlich: {quote.SubtotalMonthly:N2} €/Mon.").Bold(); }
                BuildTotalsBox(col, quote.Subtotal, quote.TaxRate, quote.TaxAmount, quote.GrandTotal, quote.TaxMode);
                if (!string.IsNullOrEmpty(quote.OutroText)) col.Item().PaddingTop(14).Text(quote.OutroText).FontSize(9);
                if (legal?.Any() == true) foreach (var b in legal) { col.Item().PaddingTop(12).Text(b.Title).Bold().FontSize(9).FontColor("#344054"); col.Item().PaddingTop(3).Text(b.Content).FontSize(7.5f).FontColor("#667085"); }
                BuildSignatureArea(col, co, quote.SignatureData, quote.SignedByName, quote.SignedAt);
            });
            p.Footer().Element(f => BuildDocFooter(f, co));
        })).GeneratePdf();
    }

    public async Task<byte[]> GenerateInvoicePdfAsync(Invoice inv, CompanySettings co, CancellationToken ct = default)
    {
        var logo = await LoadLogoAsync(co.LogoPath);
        var contact = inv.Customer.Contacts.FirstOrDefault(c => c.IsPrimary) ?? inv.Customer.Contacts.FirstOrDefault();
        var loc = inv.Customer.Locations.FirstOrDefault(l => l.IsPrimary) ?? inv.Customer.Locations.FirstOrDefault();
        var lines = inv.Lines.OrderBy(l => l.SortOrder).ToList();
        var vatGroups = lines.GroupBy(l => l.VatPercent).Select(g => new { Pct = g.Key, Net = g.Sum(l => l.NetTotal), Vat = g.Sum(l => l.VatAmount) }).ToList();
        var label = inv.Type == InvoiceType.Cancellation ? "Stornorechnung" : inv.Type == InvoiceType.CreditNote ? "Gutschrift" : "Rechnung";
        var isSmallBusiness = inv.TaxMode == TaxMode.SmallBusiness;

        return Document.Create(c => c.Page(p =>
        {
            p.Size(PageSizes.A4);
            p.MarginTop(40);
            p.MarginBottom(30);
            p.MarginHorizontal(50);
            p.DefaultTextStyle(x => x.FontSize(9.5f).FontColor("#1a1a1a"));

            // === HEADER ===
            p.Header().Column(header =>
            {
                // Row 1: Company name/logo left, "Rechnung" right
                header.Item().Row(row =>
                {
                    row.RelativeItem(3).AlignBottom().Column(lc =>
                    {
                        if (logo != null)
                            lc.Item().MaxHeight(45).MaxWidth(180).Image(logo, ImageScaling.FitArea);
                        else
                            lc.Item().Text(co.CompanyName.ToUpperInvariant()).FontSize(20).Bold().FontColor("#1a1a1a").LetterSpacing(0.05f);
                    });
                    row.RelativeItem(2).AlignRight().AlignBottom().Text(label).FontSize(22).Bold().FontColor("#1a1a1a");
                });

                // Thin separator line
                header.Item().PaddingTop(8).LineHorizontal(1.5f).LineColor("#1a1a1a");

                // Row 2: Metadata right-aligned (Rechnungsnr, Kundennr, Datum, Lieferdatum)
                header.Item().PaddingTop(6).AlignRight().Column(meta =>
                {
                    meta.Item().AlignRight().Text($"Rechnungsnr.: {inv.InvoiceNumber}").FontSize(9);
                    if (!string.IsNullOrEmpty(inv.Customer.CustomerNumber))
                        meta.Item().AlignRight().Text($"Kundennr.: {inv.Customer.CustomerNumber}").FontSize(9);
                    meta.Item().AlignRight().Text($"Datum: {inv.InvoiceDate:dd.MM.yyyy}").FontSize(9);
                    meta.Item().AlignRight().Text($"Lieferdatum: {inv.ServiceDateFrom:dd.MM.yyyy} – {inv.ServiceDateTo:dd.MM.yyyy}").FontSize(9);
                });

                // Row 3: Sender line (small) + Address block left / Company contact right
                header.Item().PaddingTop(16).Column(addr =>
                {
                    // Small sender return address line
                    addr.Item().Text($"{co.CompanyName} · {co.Street} · {co.ZipCode} {co.City}").FontSize(7).FontColor("#888888").Underline();

                    addr.Item().PaddingTop(6).Row(row =>
                    {
                        // Recipient address left
                        row.RelativeItem(3).Column(recipient =>
                        {
                            recipient.Item().Text(inv.Customer.CompanyName).Bold().FontSize(10);
                            if (contact != null) recipient.Item().Text(contact.FullName).FontSize(10);
                            if (loc != null)
                            {
                                recipient.Item().Text(loc.Street).FontSize(10);
                                recipient.Item().Text($"{loc.ZipCode} {loc.City}").FontSize(10);
                            }
                        });

                        // Company contact details right
                        row.RelativeItem(2).AlignRight().Column(company =>
                        {
                            company.Item().AlignRight().Text(co.CompanyName).Bold().FontSize(9).FontColor("#1a1a1a");
                            company.Item().AlignRight().Text(co.Street).FontSize(9).FontColor("#444444");
                            company.Item().AlignRight().Text($"{co.ZipCode} {co.City}").FontSize(9).FontColor("#444444");
                            if (!string.IsNullOrEmpty(co.Phone))
                                company.Item().AlignRight().Text($"Tel.: {co.Phone}").FontSize(9).FontColor("#444444");
                            if (!string.IsNullOrEmpty(co.Email))
                                company.Item().AlignRight().Text(co.Email).FontSize(9).FontColor("#444444");
                            if (!string.IsNullOrEmpty(co.Website))
                                company.Item().AlignRight().Text(co.Website).FontSize(9).FontColor("#444444");
                        });
                    });
                });
            });

            // === CONTENT ===
            p.Content().PaddingTop(24).Column(col =>
            {
                // Intro text
                if (!string.IsNullOrEmpty(inv.Subject))
                    col.Item().Text($"Betr.: {inv.Subject}").Bold().FontSize(11);
                col.Item().PaddingTop(8).Text(inv.IntroText ?? "Unsere Lieferungen/Leistungen stellen wir Ihnen wie folgt in Rechnung.").FontSize(9);

                // === POSITIONS TABLE ===
                col.Item().PaddingTop(14).Table(table =>
                {
                    if (isSmallBusiness)
                    {
                        // SmallBusiness: Pos | Bezeichnung | Menge | Einheit | Einzelpreis | Gesamt
                        table.ColumnsDefinition(cd =>
                        {
                            cd.ConstantColumn(30);   // Pos.
                            cd.RelativeColumn(4);     // Bezeichnung
                            cd.RelativeColumn(0.8f);  // Menge
                            cd.RelativeColumn(0.8f);  // Einheit
                            cd.RelativeColumn(1.2f);  // Einzelpreis
                            cd.RelativeColumn(1.2f);  // Gesamt
                        });

                        var hs = TextStyle.Default.FontSize(8.5f).Bold().FontColor("#1a1a1a");
                        table.Header(hd =>
                        {
                            hd.Cell().Background("#F2F2F2").Border(0.5f).BorderColor("#BBBBBB").Padding(6).Text("Pos.").Style(hs);
                            hd.Cell().Background("#F2F2F2").Border(0.5f).BorderColor("#BBBBBB").Padding(6).Text("Bezeichnung").Style(hs);
                            hd.Cell().Background("#F2F2F2").Border(0.5f).BorderColor("#BBBBBB").Padding(6).AlignRight().Text("Menge").Style(hs);
                            hd.Cell().Background("#F2F2F2").Border(0.5f).BorderColor("#BBBBBB").Padding(6).Text("Einheit").Style(hs);
                            hd.Cell().Background("#F2F2F2").Border(0.5f).BorderColor("#BBBBBB").Padding(6).AlignRight().Text("Einzel €").Style(hs);
                            hd.Cell().Background("#F2F2F2").Border(0.5f).BorderColor("#BBBBBB").Padding(6).AlignRight().Text("Gesamt €").Style(hs);
                        });

                        for (int i = 0; i < lines.Count; i++)
                        {
                            var l = lines[i];
                            table.Cell().Border(0.5f).BorderColor("#DDDDDD").Padding(6).Text($"{i + 1}").FontSize(9);
                            table.Cell().Border(0.5f).BorderColor("#DDDDDD").Padding(6).Column(cc =>
                            {
                                cc.Item().Text(l.Title).Bold().FontSize(9);
                                if (!string.IsNullOrEmpty(l.Description))
                                    cc.Item().Text(l.Description).FontSize(8).FontColor("#666666");
                            });
                            table.Cell().Border(0.5f).BorderColor("#DDDDDD").Padding(6).AlignRight().Text(l.Quantity.ToString("N2")).FontSize(9);
                            table.Cell().Border(0.5f).BorderColor("#DDDDDD").Padding(6).Text(l.Unit ?? "Stk.").FontSize(9);
                            table.Cell().Border(0.5f).BorderColor("#DDDDDD").Padding(6).AlignRight().Text($"{l.UnitPrice:N2} €").FontSize(9);
                            table.Cell().Border(0.5f).BorderColor("#DDDDDD").Padding(6).AlignRight().Text($"{l.NetTotal:N2} €").FontSize(9);
                        }
                    }
                    else
                    {
                        // Standard: Pos | Bezeichnung | Menge | Einheit | Einzelpreis | MwSt | Gesamt
                        table.ColumnsDefinition(cd =>
                        {
                            cd.ConstantColumn(30);   // Pos.
                            cd.RelativeColumn(3.5f);  // Bezeichnung
                            cd.RelativeColumn(0.7f);  // Menge
                            cd.RelativeColumn(0.7f);  // Einheit
                            cd.RelativeColumn(1.1f);  // Einzelpreis
                            cd.RelativeColumn(0.6f);  // MwSt
                            cd.RelativeColumn(1.1f);  // Gesamt
                        });

                        var hs = TextStyle.Default.FontSize(8.5f).Bold().FontColor("#1a1a1a");
                        table.Header(hd =>
                        {
                            hd.Cell().Background("#F2F2F2").Border(0.5f).BorderColor("#BBBBBB").Padding(6).Text("Pos.").Style(hs);
                            hd.Cell().Background("#F2F2F2").Border(0.5f).BorderColor("#BBBBBB").Padding(6).Text("Bezeichnung").Style(hs);
                            hd.Cell().Background("#F2F2F2").Border(0.5f).BorderColor("#BBBBBB").Padding(6).AlignRight().Text("Menge").Style(hs);
                            hd.Cell().Background("#F2F2F2").Border(0.5f).BorderColor("#BBBBBB").Padding(6).Text("Einheit").Style(hs);
                            hd.Cell().Background("#F2F2F2").Border(0.5f).BorderColor("#BBBBBB").Padding(6).AlignRight().Text("Einzelpreis").Style(hs);
                            hd.Cell().Background("#F2F2F2").Border(0.5f).BorderColor("#BBBBBB").Padding(6).AlignRight().Text("MwSt").Style(hs);
                            hd.Cell().Background("#F2F2F2").Border(0.5f).BorderColor("#BBBBBB").Padding(6).AlignRight().Text("Gesamt €").Style(hs);
                        });

                        for (int i = 0; i < lines.Count; i++)
                        {
                            var l = lines[i];
                            table.Cell().Border(0.5f).BorderColor("#DDDDDD").Padding(6).Text($"{i + 1}").FontSize(9);
                            table.Cell().Border(0.5f).BorderColor("#DDDDDD").Padding(6).Column(cc =>
                            {
                                cc.Item().Text(l.Title).Bold().FontSize(9);
                                if (!string.IsNullOrEmpty(l.Description))
                                    cc.Item().Text(l.Description).FontSize(8).FontColor("#666666");
                            });
                            table.Cell().Border(0.5f).BorderColor("#DDDDDD").Padding(6).AlignRight().Text(l.Quantity.ToString("N2")).FontSize(9);
                            table.Cell().Border(0.5f).BorderColor("#DDDDDD").Padding(6).Text(l.Unit ?? "Stk.").FontSize(9);
                            table.Cell().Border(0.5f).BorderColor("#DDDDDD").Padding(6).AlignRight().Text($"{l.UnitPrice:N2} €").FontSize(9);
                            table.Cell().Border(0.5f).BorderColor("#DDDDDD").Padding(6).AlignRight().Text($"{l.VatPercent}%").FontSize(9);
                            table.Cell().Border(0.5f).BorderColor("#DDDDDD").Padding(6).AlignRight().Text($"{l.NetTotal:N2} €").FontSize(9);
                        }
                    }
                });

                // === TOTALS ===
                if (isSmallBusiness)
                {
                    // SmallBusiness: Gesamtbetrag full-width mit Border (wie Referenz-Rechnung)
                    col.Item().PaddingTop(4).Border(0.5f).BorderColor("#BBBBBB").Background("#F9F9F9").Padding(8).Row(tr =>
                    {
                        tr.RelativeItem().Text("Gesamtbetrag*").Bold().FontSize(11);
                        tr.ConstantItem(120).AlignRight().Text($"{inv.GrossTotal:N2} €").Bold().FontSize(11);
                    });
                }
                else
                {
                    // Standard: Netto / MwSt / Brutto
                    col.Item().PaddingTop(10).AlignRight().Row(r =>
                    {
                        r.RelativeItem();
                        r.ConstantItem(260).Background("#F5F5F5").Border(1).BorderColor("#dddddd").Padding(10).Column(tc =>
                        {
                            tc.Item().Row(tr =>
                            {
                                tr.RelativeItem().Text("Nettobetrag").FontSize(10);
                                tr.RelativeItem().AlignRight().Text($"{inv.NetTotal:N2} €").FontSize(10);
                            });
                            foreach (var vg in vatGroups)
                            {
                                tc.Item().PaddingTop(3).Row(tr =>
                                {
                                    tr.RelativeItem().Text($"zzgl. {vg.Pct}% MwSt.").FontSize(10);
                                    tr.RelativeItem().AlignRight().Text($"{vg.Vat:N2} €").FontSize(10);
                                });
                            }
                            tc.Item().PaddingTop(6).LineHorizontal(1).LineColor("#cccccc");
                            tc.Item().PaddingTop(6).Row(tr =>
                            {
                                tr.RelativeItem().Text("Gesamtbetrag").Bold().FontSize(12);
                                tr.RelativeItem().AlignRight().Text($"{inv.GrossTotal:N2} €").Bold().FontSize(12);
                            });
                            if (inv.TaxMode == TaxMode.ReverseCharge)
                                tc.Item().PaddingTop(4).Text("Reverse Charge – Steuerschuldnerschaft des Leistungsempfängers.").FontSize(8).FontColor("#666666");
                        });
                    });
                }

                // === §19 UStG NOTE (SmallBusiness) ===
                if (isSmallBusiness)
                {
                    col.Item().PaddingTop(14).Text(inv.SmallBusinessNote ?? "Umsatzsteuerfreie Leistungen gemäß § 19 UStG.").FontSize(9).FontColor("#444444");
                }

                // === PAYMENT NOTE ===
                col.Item().PaddingTop(10).Text(inv.PaymentTerms ?? $"Bitte überweisen Sie den Betrag bis zum {inv.DueDate:dd.MM.yyyy} auf das unten angegebene Konto.").FontSize(9);

                // === OUTRO TEXT ===
                col.Item().PaddingTop(8).Text(inv.OutroText ?? "Vielen Dank für die gute Zusammenarbeit.").FontSize(9);

                // === QR CODE + BANK INFO ===
                if (!string.IsNullOrEmpty(co.Iban))
                {
                    col.Item().PaddingTop(16).Border(0.5f).BorderColor("#cccccc").Padding(10).Row(qrRow =>
                    {
                        // EPC QR Code left
                        qrRow.ConstantItem(120).Column(qrCol =>
                        {
                            qrCol.Item().Text("Überweisen per Code").Bold().FontSize(8).FontColor("#444444");
                            qrCol.Item().PaddingTop(2).Text("Ganz bequem Code mit der Banking-App scannen.").FontSize(7).FontColor("#666666");
                            qrCol.Item().PaddingTop(6).Width(90).Height(90).Image(GenerateEpcQrCode(co, inv));
                        });

                        qrRow.ConstantItem(16); // Spacer

                        // Bank details right
                        qrRow.RelativeItem().Column(bank =>
                        {
                            bank.Item().Text("Bankverbindung").Bold().FontSize(9);
                            bank.Item().PaddingTop(4).Table(bt =>
                            {
                                bt.ColumnsDefinition(cd => { cd.ConstantColumn(110); cd.RelativeColumn(); });
                                if (!string.IsNullOrEmpty(co.BankName))
                                {
                                    bt.Cell().Text("Bank:").FontSize(9).FontColor("#666666");
                                    bt.Cell().Text(co.BankName).FontSize(9);
                                }
                                bt.Cell().Text("IBAN:").FontSize(9).FontColor("#666666");
                                bt.Cell().Text(co.Iban).FontSize(9);
                                if (!string.IsNullOrEmpty(co.Bic))
                                {
                                    bt.Cell().Text("BIC:").FontSize(9).FontColor("#666666");
                                    bt.Cell().Text(co.Bic).FontSize(9);
                                }
                                bt.Cell().Text("Verwendungszweck:").FontSize(9).FontColor("#666666");
                                bt.Cell().Text(inv.InvoiceNumber).FontSize(9);
                            });
                        });
                    });
                }

            });

            // === FOOTER ===
            p.Footer().Column(fc =>
            {
                fc.Item().LineHorizontal(0.5f).LineColor("#BBBBBB");
                fc.Item().PaddingTop(5).Row(r =>
                {
                    // Links: Firmen-Kontaktblock
                    r.RelativeItem().Column(c =>
                    {
                        c.Item().Text(co.CompanyName).FontSize(7).FontColor("#444444");
                        c.Item().Text(co.Street).FontSize(7).FontColor("#444444");
                        c.Item().Text($"{co.ZipCode} {co.City}").FontSize(7).FontColor("#444444");
                        if (!string.IsNullOrEmpty(co.Phone))
                            c.Item().Text($"Tel.: {co.Phone}").FontSize(7).FontColor("#444444");
                        if (!string.IsNullOrEmpty(co.Email))
                            c.Item().Text(co.Email).FontSize(7).FontColor("#444444");
                        if (!string.IsNullOrEmpty(co.Website))
                            c.Item().Text(co.Website).FontSize(7).FontColor("#444444");
                    });
                    // Mitte: Steuernummer + Stadt + Ansprechpartner
                    r.RelativeItem().AlignCenter().Column(c =>
                    {
                        if (!string.IsNullOrEmpty(co.TaxId))
                            c.Item().AlignCenter().Text($"Steuernummer: {co.TaxId}").FontSize(7).FontColor("#444444");
                        if (!string.IsNullOrEmpty(co.VatId))
                            c.Item().AlignCenter().Text($"USt-IdNr.: {co.VatId}").FontSize(7).FontColor("#444444");
                        c.Item().AlignCenter().Text(co.City).FontSize(7).FontColor("#444444");
                        if (!string.IsNullOrEmpty(co.ManagingDirector))
                            c.Item().AlignCenter().Text($"Herr {co.ManagingDirector}").FontSize(7).FontColor("#444444");
                    });
                    // Rechts: Kontoinhaber + Bank + IBAN
                    r.RelativeItem().AlignRight().Column(c =>
                    {
                        if (!string.IsNullOrEmpty(co.LegalName))
                            c.Item().AlignRight().Text(co.LegalName).FontSize(7).FontColor("#444444");
                        if (!string.IsNullOrEmpty(co.BankName))
                            c.Item().AlignRight().Text(co.BankName).FontSize(7).FontColor("#444444");
                        if (!string.IsNullOrEmpty(co.Iban))
                            c.Item().AlignRight().Text($"IBAN: {co.Iban}").FontSize(7).FontColor("#444444");
                        c.Item().AlignRight().Text(t =>
                        {
                            t.Span("Seite ").FontSize(7).FontColor("#888888");
                            t.CurrentPageNumber().FontSize(7).FontColor("#888888");
                            t.Span(" / ").FontSize(7).FontColor("#888888");
                            t.TotalPages().FontSize(7).FontColor("#888888");
                        });
                    });
                });
            });
        })).GeneratePdf();
    }

    /// <summary>
    /// Generates an EPC QR Code (European Payments Council) for SEPA bank transfers.
    /// </summary>
    private static byte[] GenerateEpcQrCode(CompanySettings co, Invoice inv)
    {
        // EPC QR Code format (EPC069-12)
        var epcData = string.Join("\n",
            "BCD",                                              // Service Tag
            "002",                                              // Version
            "1",                                                // Encoding (UTF-8)
            "SCT",                                              // Identification
            co.Bic ?? "",                                       // BIC
            co.LegalName ?? co.CompanyName,                     // Beneficiary Name (max 70 chars)
            co.Iban?.Replace(" ", "") ?? "",                    // IBAN
            $"EUR{inv.GrossTotal:F2}",                          // Amount
            "",                                                 // Purpose
            inv.InvoiceNumber,                                  // Remittance Reference
            "",                                                 // Remittance Text
            ""                                                  // Beneficiary to Originator Info
        );

        using var qrGen = new QRCodeGenerator();
        using var qrData = qrGen.CreateQrCode(epcData, QRCodeGenerator.ECCLevel.M);
        using var qrCode = new PngByteQRCode(qrData);
        return qrCode.GetGraphic(5);
    }

    // === Helpers ===
    private static void BuildDocHeader(IContainer container, CompanySettings co, byte[]? logo, string docType, string docNumber, Customer cust, Contact? contact, Location? loc, params string?[] meta)
    {
        container.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem(3).Column(lc =>
                {
                    if (logo != null)
                        lc.Item().MaxHeight(50).MaxWidth(180).Image(logo, ImageScaling.FitArea);
                    else
                        lc.Item().Text(co.CompanyName).FontSize(18).Bold().FontColor("#344054");
                });
                row.RelativeItem(2).AlignRight().Column(c => { c.Item().Text(docType).FontSize(22).Bold().FontColor("#344054"); c.Item().Text(docNumber).FontColor("#667085"); });
            });
            col.Item().PaddingTop(8).PaddingBottom(12).LineHorizontal(2).LineColor("#344054");
            col.Item().Row(row =>
            {
                row.RelativeItem(3).Column(c =>
                {
                    c.Item().Text($"{co.CompanyName} · {co.Street} · {co.ZipCode} {co.City}").FontSize(7).FontColor("#667085");
                    c.Item().PaddingTop(8).Text(cust.CompanyName).Bold().FontSize(11);
                    if (contact != null) c.Item().Text(contact.FullName);
                    if (loc != null) { c.Item().Text(loc.Street); c.Item().Text($"{loc.ZipCode} {loc.City}"); }
                });
                row.RelativeItem(2).AlignRight().PaddingTop(8).Column(c => { foreach (var m in meta.Where(x => x != null)) c.Item().Text(m!).FontSize(9); });
            });
        });
    }

    private static void BuildQuoteTable(IContainer container, List<QuoteLine> items, bool monthly)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(c => { c.ConstantColumn(25); c.RelativeColumn(4); c.RelativeColumn(1); c.RelativeColumn(1.2f); c.RelativeColumn(0.8f); c.RelativeColumn(1.2f); });
            var hs = TextStyle.Default.FontSize(8).Bold().FontColor("#FFF");
            table.Header(h =>
            {
                h.Cell().Background("#344054").Padding(5).Text("Pos").Style(hs);
                h.Cell().Background("#344054").Padding(5).Text("Leistung").Style(hs);
                h.Cell().Background("#344054").Padding(5).AlignRight().Text("Menge").Style(hs);
                h.Cell().Background("#344054").Padding(5).AlignRight().Text(monthly ? "Monat" : "Preis").Style(hs);
                h.Cell().Background("#344054").Padding(5).AlignRight().Text("MwSt").Style(hs);
                h.Cell().Background("#344054").Padding(5).AlignRight().Text("Gesamt").Style(hs);
            });
            for (int i = 0; i < items.Count; i++)
            {
                var l = items[i]; var bg = i % 2 == 0 ? "#FFF" : "#F7F8FA";
                table.Cell().Background(bg).Padding(5).Text($"{i+1}").FontSize(8);
                table.Cell().Background(bg).Padding(5).Column(c =>
                {
                    c.Item().Text(l.Title).Bold().FontSize(9);
                    if (!string.IsNullOrEmpty(l.Description)) c.Item().Text(l.Description).FontSize(8).FontColor("#667085");
                    if (l.DiscountPercent > 0) c.Item().Text($"Rabatt: {l.DiscountPercent:N0}%").FontSize(8).FontColor("#B54708");
                });
                table.Cell().Background(bg).Padding(5).AlignRight().Text(l.Quantity.ToString("N0")).FontSize(8);
                table.Cell().Background(bg).Padding(5).AlignRight().Text($"{l.UnitPrice:N2} €").FontSize(8);
                table.Cell().Background(bg).Padding(5).AlignRight().Text($"{l.VatPercent}%").FontSize(8);
                table.Cell().Background(bg).Padding(5).AlignRight().Text($"{l.Total:N2} €").FontSize(8);
            }
        });
    }

    private static void BuildTotalsBox(ColumnDescriptor col, decimal net, decimal taxRate, decimal tax, decimal gross, TaxMode mode)
    {
        col.Item().PaddingTop(16).AlignRight().MinWidth(240).Background("#F7F8FA").Padding(12).Column(tc =>
        {
            tc.Item().Row(r => { r.RelativeItem().Text("Netto:"); r.RelativeItem().AlignRight().Text($"{net:N2} €"); });
            if (mode == TaxMode.SmallBusiness) tc.Item().PaddingTop(4).Text("Gem. § 19 UStG – keine MwSt.").FontSize(8).FontColor("#667085");
            else tc.Item().Row(r => { r.RelativeItem().Text($"MwSt. ({taxRate:N0}%):"); r.RelativeItem().AlignRight().Text($"{tax:N2} €"); });
            tc.Item().PaddingTop(6).LineHorizontal(1).LineColor("#EAECF0");
            tc.Item().PaddingTop(6).Row(r => { r.RelativeItem().Text("Gesamt:").Bold().FontSize(12); r.RelativeItem().AlignRight().Text($"{gross:N2} €").Bold().FontSize(12); });
        });
    }

    private static void BuildSignatureArea(ColumnDescriptor col, CompanySettings co, string? sigData, string? sigName, DateTimeOffset? sigDate)
    {
        col.Item().PaddingTop(30).Row(row =>
        {
            row.RelativeItem().Column(c =>
            {
                c.Item().LineHorizontal(0.5f).LineColor("#EAECF0");
                c.Item().PaddingTop(4).Text("Auftragnehmer").FontSize(8).FontColor("#667085");
                c.Item().PaddingTop(8).Text(co.CompanyName).FontSize(9).FontColor("#344054");
                if (!string.IsNullOrEmpty(co.ManagingDirector))
                    c.Item().Text(co.ManagingDirector).FontSize(8).FontColor("#667085");
            });

            row.ConstantItem(40);

            row.RelativeItem().Column(c =>
            {
                if (!string.IsNullOrEmpty(sigData))
                {
                    try
                    {
                        var base64 = sigData.Contains(",") ? sigData.Split(',')[1] : sigData;
                        var imgBytes = Convert.FromBase64String(base64);
                        c.Item().MaxHeight(60).MaxWidth(200).Image(imgBytes, ImageScaling.FitArea);
                    }
                    catch { }

                    c.Item().PaddingTop(4).LineHorizontal(0.5f).LineColor("#EAECF0");
                    c.Item().PaddingTop(4).Text($"✓ Signiert von: {sigName}").FontSize(8).FontColor("#027A48");
                    c.Item().Text($"Datum: {sigDate:dd.MM.yyyy HH:mm} Uhr").FontSize(8).FontColor("#667085");
                }
                else
                {
                    c.Item().LineHorizontal(0.5f).LineColor("#EAECF0");
                    c.Item().PaddingTop(4).Text("Auftraggeber (Unterschrift)").FontSize(8).FontColor("#667085");
                }
            });
        });
    }


    private static void BuildDocFooter(IContainer container, CompanySettings co)
    {
        container.Column(fc =>
        {
            fc.Item().LineHorizontal(0.5f).LineColor("#EAECF0");
            fc.Item().PaddingTop(3).Row(r =>
            {
                r.RelativeItem().Text($"{co.CompanyName} · {co.Street} · {co.ZipCode} {co.City}").FontSize(7).FontColor("#667085");
                r.RelativeItem().AlignCenter().Text($"St.Nr.: {co.TaxId} · USt-IdNr.: {co.VatId}").FontSize(7).FontColor("#667085");
                r.RelativeItem().AlignRight().Text(t =>
                {
                    t.CurrentPageNumber().FontSize(7).FontColor("#667085");
                    t.Span("/").FontSize(7).FontColor("#667085");
                    t.TotalPages().FontSize(7).FontColor("#667085");
                });
            });
            if (!string.IsNullOrEmpty(co.Iban)) fc.Item().AlignCenter().Text($"{co.BankName} · IBAN: {co.Iban} · BIC: {co.Bic}").FontSize(7).FontColor("#667085");
            if (!string.IsNullOrEmpty(co.RegisterCourt)) fc.Item().AlignCenter().Text($"{co.RegisterCourt} · {co.RegisterNumber} · GF: {co.ManagingDirector}").FontSize(7).FontColor("#667085");
        });
    }
}
