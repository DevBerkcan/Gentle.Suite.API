using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;
using GentleSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QRCoder;
using System.Text.Json;
using GentleSuite.Application.DTOs;
using GentleSuite.Application.Interfaces;
using GentleSuite.Infrastructure.Services;

namespace GentleSuite.Infrastructure.Pdf;

public record LegalTextSnapshotItem(string Key, string Title, string Content, string? AttachmentFileName = null);

/// <summary>Gentle Group Studios brand tokens, taken from the Word templates (Aufwandsschätzung, Angebot, Rechnung).</summary>
internal static class Brand
{
    public const string Navy = "#010A30";
    public const string Text = "#1B2140";
    public const string Muted = "#5B6180";
    public const string Line = "#DADDE8";
    public const string Fill = "#EEF0F6";
    public const string Success = "#027A48";

    // Space Grotesk (headings) / Public Sans (body) are used when their TTFs are dropped into Pdf/Fonts;
    // otherwise QuestPDF falls back to its bundled Lato.
    public static readonly string[] Display;
    public static readonly string[] Body;

    static Brand()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Pdf", "Fonts");
        if (Directory.Exists(dir))
            foreach (var f in Directory.EnumerateFiles(dir, "*.ttf"))
            {
                using var s = File.OpenRead(f);
                QuestPDF.Drawing.FontManager.RegisterFont(s);
            }
        bool Has(string family) => Directory.Exists(dir) && Directory.EnumerateFiles(dir, family.Replace(" ", "") + "*.ttf").Any();
        Display = Has("Space Grotesk") ? new[] { "Space Grotesk", "Lato" } : new[] { "Lato" };
        Body = Has("Public Sans") ? new[] { "Public Sans", "Lato" } : new[] { "Lato" };
    }

    /// <summary>Navy, bold, upper-case section heading with a navy rule above — "01 — Leistungsumfang".</summary>
    public static void Section(ColumnDescriptor col, string text, float paddingTop = 18)
    {
        col.Item().PaddingTop(paddingTop).LineHorizontal(1.2f).LineColor(Navy);
        col.Item().PaddingTop(7).Text(text.ToUpperInvariant()).FontFamily(Display).Bold().FontSize(11).FontColor(Navy).LetterSpacing(0.04f);
    }

    /// <summary>A heading plus the content added to the returned column move to the next page together when less than
    /// <paramref name="minHeight"/> would be left for them — keeps headings from ending up alone at the bottom of a page.
    /// (EnsureSpace only acts when its content would be split, so the heading must share one item with its content.)</summary>
    public static ColumnDescriptor Group(ColumnDescriptor col, float minHeight = 90)
    {
        ColumnDescriptor group = null!;
        col.Item().EnsureSpace(minHeight).Column(c => group = c); // the handler runs immediately; items added later still render
        return group;
    }

    /// <summary>Header cell of a table: navy fill, small white upper-case label.</summary>
    public static IContainer HeadCell(IContainer c) => c.Background(Navy).PaddingVertical(6).PaddingHorizontal(4);
    public static TextStyle HeadText => TextStyle.Default.FontFamily(Display).FontSize(7).Bold().FontColor("#FFFFFF").LetterSpacing(0.05f);

    /// <summary>Body cell of a table: hairline below, no vertical lines.</summary>
    public static IContainer BodyCell(IContainer c) => c.BorderBottom(0.5f).BorderColor(Line).PaddingVertical(6).PaddingHorizontal(4);
}

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
        List<(string Title, string Content)>? legal = null;
        if (!string.IsNullOrEmpty(quote.LegalTextBlocksSnapshot))
        {
            // Frozen at send time, so a later edit to the master text doesn't change what an already-sent quote shows.
            var snapshot = JsonSerializer.Deserialize<List<LegalTextSnapshotItem>>(quote.LegalTextBlocksSnapshot);
            if (snapshot?.Any() == true) legal = snapshot.Select(s => (s.Title, string.IsNullOrEmpty(s.AttachmentFileName) ? s.Content : $"Dieses Dokument liegt als Anhang bei ({s.AttachmentFileName}).")).ToList();
        }
        else
        {
            // Draft preview before send: show manually chosen blocks plus anything marked "automatisch anhängen".
            var keys = string.IsNullOrEmpty(quote.LegalTextBlocks) ? new List<string>() : (JsonSerializer.Deserialize<List<string>>(quote.LegalTextBlocks) ?? new List<string>());
            var autoKeys = await _db.LegalTextBlocks.Where(b => b.IsActive && b.AutoAttachToQuotes).Select(b => b.Key).ToListAsync(ct);
            var allKeys = keys.Union(autoKeys).ToList();
            if (allKeys.Count > 0) legal = (await _db.LegalTextBlocks.Where(b => allKeys.Contains(b.Key) && b.IsActive).OrderBy(b => b.SortOrder).ToListAsync(ct))
                .Select(b => (b.Title, string.IsNullOrEmpty(b.AttachmentFileName) ? b.Content : $"Dieses Dokument liegt als Anhang bei ({b.AttachmentFileName}).")).ToList();
        }
        List<PaymentTermOption>? paymentTerms = null;
        if (!string.IsNullOrEmpty(quote.PaymentTermKeys))
        {
            var keys = JsonSerializer.Deserialize<List<string>>(quote.PaymentTermKeys);
            if (keys?.Any() == true) paymentTerms = await _db.PaymentTermOptions.Where(o => keys.Contains(o.Key) && o.IsActive).OrderBy(o => o.SortOrder).ToListAsync(ct);
        }
        List<PaymentPlanOptionResolvedDto>? planOptions = null;
        if (!string.IsNullOrEmpty(quote.PaymentPlanConfig))
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<PaymentPlanConfigDto>(quote.PaymentPlanConfig);
                if (cfg != null) planOptions = PaymentPlanCalculator.Resolve(cfg, quote.SubtotalOneTime);
            }
            catch (JsonException) { /* altes/inkompatibles Format — im PDF einfach ausgelassen */ }
        }

        return Document.Create(c => c.Page(p =>
        {
            p.Size(PageSizes.A4); p.MarginTop(30); p.MarginBottom(30); p.MarginHorizontal(45);
            p.DefaultTextStyle(x => x.FontFamily(Brand.Body).FontSize(9.5f).FontColor(Brand.Text));
            p.Header().Element(h => BuildDocHeader(h, co, logo, "ANGEBOT", $"Nr. {quote.QuoteNumber} · V{quote.Version}", quote.Customer, contact, loc, $"Datum: {quote.CreatedAt:dd.MM.yyyy}", quote.ExpiresAt.HasValue ? $"Gültig bis: {quote.ExpiresAt:dd.MM.yyyy}" : null));
            p.Content().PaddingTop(16).Column(col =>
            {
                var n = 0;
                string Num(string title) => $"{++n:00} — {title}";
                if (!string.IsNullOrEmpty(quote.Subject)) col.Item().Text($"Betr.: {quote.Subject}").FontFamily(Brand.Display).Bold().FontSize(13).FontColor(Brand.Navy);
                if (!string.IsNullOrEmpty(quote.IntroText)) col.Item().PaddingTop(8).Text(quote.IntroText).FontSize(9);
                if (oneTime.Any()) { var g = Brand.Group(col, 110); Brand.Section(g, Num("Einmalige Leistungen")); g.Item().PaddingTop(8).Element(e => BuildQuoteTable(e, oneTime, false)); g.Item().AlignRight().PaddingTop(6).Text($"Summe: {quote.SubtotalOneTime:N2} €").Bold().FontColor(Brand.Navy); }
                if (recurring.Any()) { var g = Brand.Group(col, 110); Brand.Section(g, Num("Monatliche Leistungen")); g.Item().PaddingTop(8).Element(e => BuildQuoteTable(e, recurring, true)); g.Item().AlignRight().PaddingTop(6).Text($"Monatlich: {quote.SubtotalMonthly:N2} €/Mon.").Bold().FontColor(Brand.Navy); }
                BuildTotalsBox(col, quote.Subtotal, quote.TaxRate, quote.TaxAmount, quote.GrandTotal, quote.TaxMode);
                if (!string.IsNullOrEmpty(quote.OutroText)) col.Item().PaddingTop(14).Text(quote.OutroText).FontSize(9);
                if (paymentTerms?.Any() == true)
                {
                    var pg = Brand.Group(col);
                    Brand.Section(pg, Num("Zahlungsbedingungen"));
                    foreach (var pt in paymentTerms)
                    {
                        var chosen = pt.Key == quote.ChosenPaymentTermKey;
                        pg.Item().PaddingTop(6).Text(t =>
                        {
                            t.Span(chosen ? "☑ " : "☐ ").FontSize(9);
                            t.Span(pt.Title).Bold().FontSize(9).FontColor(chosen ? Brand.Navy : Brand.Text);
                        });
                        pg.Item().PaddingTop(2).PaddingLeft(14).Text(pt.Content).FontSize(8).FontColor(Brand.Muted);
                    }
                }
                if (planOptions?.Any() == true)
                {
                    var singleOption = planOptions.Count == 1;
                    var og = Brand.Group(col);
                    Brand.Section(og, Num(singleOption ? "Zahlungsweise" : "Zahlungsoptionen"));
                    foreach (var opt in planOptions)
                    {
                        var chosen = opt.Key == quote.ChosenPaymentPlanOptionKey;
                        og.Item().PaddingTop(6).Text(t =>
                        {
                            if (!singleOption) t.Span(chosen ? "☑ " : "☐ ").FontSize(9);
                            t.Span(opt.Title).Bold().FontSize(9).FontColor(chosen ? Brand.Navy : Brand.Text);
                            t.Span($"  —  {opt.TotalAmount:N2} €").FontSize(9);
                        });
                        var sub = string.Join(" + ", new[]
                        {
                            opt.DownPayment.HasValue && opt.DownPayment.Value > 0 ? $"{opt.DownPayment:N2} € Anzahlung" : null,
                            opt.MonthlyAmount.HasValue && opt.MonthlyAmount.Value > 0 ? $"{opt.MonthlyAmount:N2} €/Monat" + (opt.Months.HasValue ? $" × {opt.Months}" : "") : null,
                        }.Where(s => s != null));
                        if (!string.IsNullOrEmpty(sub)) og.Item().PaddingTop(2).PaddingLeft(singleOption ? 0 : 14).Text(sub).FontSize(8).FontColor(Brand.Muted);
                    }
                }
                if (legal?.Any() == true) foreach (var b in legal) { col.Item().PaddingTop(12).Text(b.Title).Bold().FontSize(9).FontColor(Brand.Navy); col.Item().PaddingTop(3).Text(b.Content).FontSize(7.5f).FontColor(Brand.Muted); }
                BuildSignatureArea(col, co, quote.SignatureData, quote.SignedByName, quote.SignedByEmail, quote.SignedAt, quote.Customer.CompanyName);
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
            p.DefaultTextStyle(x => x.FontFamily(Brand.Body).FontSize(9.5f).FontColor(Brand.Text));

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
                            lc.Item().Text(co.CompanyName.ToUpperInvariant()).FontFamily(Brand.Display).FontSize(20).Bold().FontColor(Brand.Navy).LetterSpacing(0.05f);
                    });
                    row.RelativeItem(2).AlignRight().AlignBottom().Text(label.ToUpperInvariant()).FontFamily(Brand.Display).FontSize(20).Bold().FontColor(Brand.Navy);
                });

                // Thin separator line
                header.Item().PaddingTop(8).LineHorizontal(1.2f).LineColor(Brand.Navy);

                // Row 2: Metadata right-aligned (Rechnungsnr, Kundennr, Datum, Lieferdatum)
                header.Item().PaddingTop(6).AlignRight().Column(meta =>
                {
                    meta.Item().AlignRight().Text($"Rechnungsnr.: {inv.InvoiceNumber}").FontSize(9);
                    if (!string.IsNullOrEmpty(inv.Customer.CustomerNumber))
                        meta.Item().AlignRight().Text($"Kundennr.: {inv.Customer.CustomerNumber}").FontSize(9);
                    meta.Item().AlignRight().Text($"Datum: {inv.InvoiceDate:dd.MM.yyyy}").FontSize(9);
                });

                // Row 3: Sender line (small) + Address block left / Company contact right
                header.Item().PaddingTop(16).Column(addr =>
                {
                    // Small sender return address line
                    addr.Item().Text($"{co.CompanyName} · {co.Street} · {co.ZipCode} {co.City}").FontSize(7).FontColor(Brand.Muted).Underline();

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
                            company.Item().AlignRight().Text(co.CompanyName).Bold().FontSize(9).FontColor(Brand.Navy);
                            company.Item().AlignRight().Text(co.Street).FontSize(9).FontColor(Brand.Muted);
                            company.Item().AlignRight().Text($"{co.ZipCode} {co.City}").FontSize(9).FontColor(Brand.Muted);
                            if (!string.IsNullOrEmpty(co.Phone))
                                company.Item().AlignRight().Text($"Tel.: {co.Phone}").FontSize(9).FontColor(Brand.Muted);
                            if (!string.IsNullOrEmpty(co.Email))
                                company.Item().AlignRight().Text(co.Email).FontSize(9).FontColor(Brand.Muted);
                            if (!string.IsNullOrEmpty(co.Website))
                                company.Item().AlignRight().Text(co.Website).FontSize(9).FontColor(Brand.Muted);
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
                            cd.ConstantColumn(34);   // Pos.
                            cd.RelativeColumn(4);     // Bezeichnung
                            cd.RelativeColumn(0.8f);  // Menge
                            cd.RelativeColumn(0.8f);  // Einheit
                            cd.RelativeColumn(1.2f);  // Einzelpreis
                            cd.RelativeColumn(1.2f);  // Gesamt
                        });
                        table.Header(hd =>
                        {
                            hd.Cell().Element(Brand.HeadCell).Text("POS.").Style(Brand.HeadText);
                            hd.Cell().Element(Brand.HeadCell).Text("BEZEICHNUNG").Style(Brand.HeadText);
                            hd.Cell().Element(Brand.HeadCell).AlignRight().Text("MENGE").Style(Brand.HeadText);
                            hd.Cell().Element(Brand.HeadCell).Text("EINHEIT").Style(Brand.HeadText);
                            hd.Cell().Element(Brand.HeadCell).AlignRight().Text("EINZEL €").Style(Brand.HeadText);
                            hd.Cell().Element(Brand.HeadCell).AlignRight().Text("GESAMT €").Style(Brand.HeadText);
                        });

                        for (int i = 0; i < lines.Count; i++)
                        {
                            var l = lines[i];
                            table.Cell().Element(Brand.BodyCell).Text($"{i + 1}").FontSize(9);
                            table.Cell().Element(Brand.BodyCell).Column(cc =>
                            {
                                cc.Item().Text(l.Title).Bold().FontSize(9).FontColor(Brand.Navy);
                                if (!string.IsNullOrEmpty(l.Description))
                                    cc.Item().Text(l.Description).FontSize(8).FontColor(Brand.Muted);
                            });
                            table.Cell().Element(Brand.BodyCell).AlignRight().Text(l.Quantity.ToString("N2")).FontSize(9);
                            table.Cell().Element(Brand.BodyCell).Text(l.Unit ?? "Stk.").FontSize(9);
                            table.Cell().Element(Brand.BodyCell).AlignRight().Text($"{l.UnitPrice:N2} €").FontSize(9);
                            table.Cell().Element(Brand.BodyCell).AlignRight().Text($"{l.NetTotal:N2} €").FontSize(9);
                        }
                    }
                    else
                    {
                        // Standard: Pos | Bezeichnung | Menge | Einheit | Einzelpreis | MwSt | Gesamt
                        table.ColumnsDefinition(cd =>
                        {
                            cd.ConstantColumn(34);   // Pos.
                            cd.RelativeColumn(3.5f);  // Bezeichnung
                            cd.RelativeColumn(0.7f);  // Menge
                            cd.RelativeColumn(0.7f);  // Einheit
                            cd.RelativeColumn(1.1f);  // Einzelpreis
                            cd.RelativeColumn(0.6f);  // MwSt
                            cd.RelativeColumn(1.1f);  // Gesamt
                        });
                        table.Header(hd =>
                        {
                            hd.Cell().Element(Brand.HeadCell).Text("POS.").Style(Brand.HeadText);
                            hd.Cell().Element(Brand.HeadCell).Text("BEZEICHNUNG").Style(Brand.HeadText);
                            hd.Cell().Element(Brand.HeadCell).AlignRight().Text("MENGE").Style(Brand.HeadText);
                            hd.Cell().Element(Brand.HeadCell).Text("EINHEIT").Style(Brand.HeadText);
                            hd.Cell().Element(Brand.HeadCell).AlignRight().Text("EINZELPREIS").Style(Brand.HeadText);
                            hd.Cell().Element(Brand.HeadCell).AlignRight().Text("MWST").Style(Brand.HeadText);
                            hd.Cell().Element(Brand.HeadCell).AlignRight().Text("GESAMT €").Style(Brand.HeadText);
                        });

                        for (int i = 0; i < lines.Count; i++)
                        {
                            var l = lines[i];
                            table.Cell().Element(Brand.BodyCell).Text($"{i + 1}").FontSize(9);
                            table.Cell().Element(Brand.BodyCell).Column(cc =>
                            {
                                cc.Item().Text(l.Title).Bold().FontSize(9).FontColor(Brand.Navy);
                                if (!string.IsNullOrEmpty(l.Description))
                                    cc.Item().Text(l.Description).FontSize(8).FontColor(Brand.Muted);
                            });
                            table.Cell().Element(Brand.BodyCell).AlignRight().Text(l.Quantity.ToString("N2")).FontSize(9);
                            table.Cell().Element(Brand.BodyCell).Text(l.Unit ?? "Stk.").FontSize(9);
                            table.Cell().Element(Brand.BodyCell).AlignRight().Text($"{l.UnitPrice:N2} €").FontSize(9);
                            table.Cell().Element(Brand.BodyCell).AlignRight().Text($"{l.VatPercent}%").FontSize(9);
                            table.Cell().Element(Brand.BodyCell).AlignRight().Text($"{l.NetTotal:N2} €").FontSize(9);
                        }
                    }
                });

                // === TOTALS ===
                if (isSmallBusiness)
                {
                    // SmallBusiness: Gesamtbetrag full-width mit Border (wie Referenz-Rechnung)
                    col.Item().PaddingTop(4).Background(Brand.Fill).Padding(8).Row(tr =>
                    {
                        tr.RelativeItem().Text("Gesamtbetrag*").FontFamily(Brand.Display).Bold().FontSize(11).FontColor(Brand.Navy);
                        tr.ConstantItem(120).AlignRight().Text($"{inv.GrossTotal:N2} €").FontFamily(Brand.Display).Bold().FontSize(11).FontColor(Brand.Navy);
                    });
                }
                else
                {
                    // Standard: Netto / MwSt / Brutto
                    col.Item().PaddingTop(10).AlignRight().Row(r =>
                    {
                        r.RelativeItem();
                        r.ConstantItem(260).Background(Brand.Fill).Padding(10).Column(tc =>
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
                            tc.Item().PaddingTop(6).LineHorizontal(1).LineColor(Brand.Navy);
                            tc.Item().PaddingTop(6).Row(tr =>
                            {
                                tr.RelativeItem().Text("Gesamtbetrag").FontFamily(Brand.Display).Bold().FontSize(12).FontColor(Brand.Navy);
                                tr.RelativeItem().AlignRight().Text($"{inv.GrossTotal:N2} €").FontFamily(Brand.Display).Bold().FontSize(12).FontColor(Brand.Navy);
                            });
                            if (inv.TaxMode == TaxMode.ReverseCharge)
                                tc.Item().PaddingTop(4).Text("Reverse Charge – Steuerschuldnerschaft des Leistungsempfängers.").FontSize(8).FontColor(Brand.Muted);
                        });
                    });
                }

                // === §19 UStG NOTE (SmallBusiness) ===
                if (isSmallBusiness)
                {
                    col.Item().PaddingTop(14).Text(inv.SmallBusinessNote ?? "Umsatzsteuerfreie Leistungen gemäß § 19 UStG.").FontSize(9).FontColor(Brand.Muted);
                }

                // === PAYMENT NOTE ===
                col.Item().PaddingTop(10).Text(inv.PaymentTerms ?? $"Bitte überweisen Sie den Betrag bis zum {inv.DueDate:dd.MM.yyyy} auf das unten angegebene Konto.").FontSize(9);

                // === OUTRO TEXT ===
                col.Item().PaddingTop(8).Text(inv.OutroText ?? "Vielen Dank für die gute Zusammenarbeit.").FontSize(9);

                // === QR CODE + BANK INFO ===
                if (!string.IsNullOrEmpty(co.Iban))
                {
                    col.Item().PaddingTop(16).Border(0.5f).BorderColor(Brand.Line).Padding(10).Row(qrRow =>
                    {
                        // EPC QR Code left
                        qrRow.ConstantItem(120).Column(qrCol =>
                        {
                            qrCol.Item().Text("Überweisen per Code").Bold().FontSize(8).FontColor(Brand.Muted);
                            qrCol.Item().PaddingTop(2).Text("Ganz bequem Code mit der Banking-App scannen.").FontSize(7).FontColor(Brand.Muted);
                            qrCol.Item().PaddingTop(6).Width(90).Height(90).Image(GenerateEpcQrCode(co, inv));
                        });

                        qrRow.ConstantItem(16); // Spacer

                        // Bank details right
                        qrRow.RelativeItem().Column(bank =>
                        {
                            bank.Item().Text("Bankverbindung").Bold().FontSize(9).FontColor(Brand.Navy);
                            bank.Item().PaddingTop(4).Table(bt =>
                            {
                                bt.ColumnsDefinition(cd => { cd.ConstantColumn(110); cd.RelativeColumn(); });
                                if (!string.IsNullOrEmpty(co.BankName))
                                {
                                    bt.Cell().Text("Bank:").FontSize(9).FontColor(Brand.Muted);
                                    bt.Cell().Text(co.BankName).FontSize(9);
                                }
                                bt.Cell().Text("IBAN:").FontSize(9).FontColor(Brand.Muted);
                                bt.Cell().Text(co.Iban).FontSize(9);
                                if (!string.IsNullOrEmpty(co.Bic))
                                {
                                    bt.Cell().Text("BIC:").FontSize(9).FontColor(Brand.Muted);
                                    bt.Cell().Text(co.Bic).FontSize(9);
                                }
                                bt.Cell().Text("Verwendungszweck:").FontSize(9).FontColor(Brand.Muted);
                                bt.Cell().Text(inv.InvoiceNumber).FontSize(9);
                            });
                        });
                    });
                }

            });

            // === FOOTER ===
            p.Footer().Column(fc =>
            {
                fc.Item().LineHorizontal(0.5f).LineColor(Brand.Line);
                fc.Item().PaddingTop(5).Row(r =>
                {
                    // Links: Firmen-Kontaktblock
                    r.RelativeItem().Column(c =>
                    {
                        c.Item().Text(co.CompanyName).FontSize(7).FontColor(Brand.Muted);
                        c.Item().Text(co.Street).FontSize(7).FontColor(Brand.Muted);
                        c.Item().Text($"{co.ZipCode} {co.City}").FontSize(7).FontColor(Brand.Muted);
                        if (!string.IsNullOrEmpty(co.Phone))
                            c.Item().Text($"Tel.: {co.Phone}").FontSize(7).FontColor(Brand.Muted);
                        if (!string.IsNullOrEmpty(co.Email))
                            c.Item().Text(co.Email).FontSize(7).FontColor(Brand.Muted);
                        if (!string.IsNullOrEmpty(co.Website))
                            c.Item().Text(co.Website).FontSize(7).FontColor(Brand.Muted);
                    });
                    // Mitte: Steuernummer + Stadt + Ansprechpartner
                    r.RelativeItem().AlignCenter().Column(c =>
                    {
                        if (!string.IsNullOrEmpty(co.TaxId))
                            c.Item().AlignCenter().Text($"Steuernummer: {co.TaxId}").FontSize(7).FontColor(Brand.Muted);
                        if (!string.IsNullOrEmpty(co.VatId))
                            c.Item().AlignCenter().Text($"USt-IdNr.: {co.VatId}").FontSize(7).FontColor(Brand.Muted);
                        c.Item().AlignCenter().Text(co.City).FontSize(7).FontColor(Brand.Muted);
                        if (!string.IsNullOrEmpty(co.ManagingDirector))
                            c.Item().AlignCenter().Text($"Herr {co.ManagingDirector}").FontSize(7).FontColor(Brand.Muted);
                    });
                    // Rechts: Kontoinhaber + Bank + IBAN
                    r.RelativeItem().AlignRight().Column(c =>
                    {
                        if (!string.IsNullOrEmpty(co.LegalName))
                            c.Item().AlignRight().Text(co.LegalName).FontSize(7).FontColor(Brand.Muted);
                        if (!string.IsNullOrEmpty(co.BankName))
                            c.Item().AlignRight().Text(co.BankName).FontSize(7).FontColor(Brand.Muted);
                        if (!string.IsNullOrEmpty(co.Iban))
                            c.Item().AlignRight().Text($"IBAN: {co.Iban}").FontSize(7).FontColor(Brand.Muted);
                        c.Item().AlignRight().Text(t =>
                        {
                            t.Span("Seite ").FontSize(7).FontColor(Brand.Muted);
                            t.CurrentPageNumber().FontSize(7).FontColor(Brand.Muted);
                            t.Span(" / ").FontSize(7).FontColor(Brand.Muted);
                            t.TotalPages().FontSize(7).FontColor(Brand.Muted);
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
                row.RelativeItem(3).AlignBottom().Column(lc =>
                {
                    if (logo != null)
                        lc.Item().MaxHeight(50).MaxWidth(180).Image(logo, ImageScaling.FitArea);
                    else
                        lc.Item().Text(co.CompanyName).FontFamily(Brand.Display).FontSize(16).Bold().FontColor(Brand.Navy);
                });
                row.RelativeItem(2).AlignRight().AlignBottom().Column(c =>
                {
                    c.Item().AlignRight().Text(docType).FontFamily(Brand.Display).FontSize(docType.Length > 10 ? 15 : 20).Bold().FontColor(Brand.Navy).LetterSpacing(0.06f);
                    c.Item().AlignRight().Text(docNumber.ToUpperInvariant()).FontFamily(Brand.Display).FontSize(7.5f).FontColor(Brand.Muted).LetterSpacing(0.14f);
                });
            });
            col.Item().PaddingTop(8).PaddingBottom(12).LineHorizontal(1.2f).LineColor(Brand.Navy);
            col.Item().Row(row =>
            {
                row.RelativeItem(3).Column(c =>
                {
                    c.Item().Text($"{co.CompanyName} · {co.Street} · {co.ZipCode} {co.City}").FontSize(7).FontColor(Brand.Muted);
                    c.Item().PaddingTop(8).Text(cust.CompanyName).FontFamily(Brand.Display).Bold().FontSize(11).FontColor(Brand.Navy);
                    if (contact != null) c.Item().Text(contact.FullName);
                    if (loc != null) { c.Item().Text(loc.Street); c.Item().Text($"{loc.ZipCode} {loc.City}"); }
                });
                row.RelativeItem(2).AlignRight().PaddingTop(8).Column(c => { foreach (var m in meta.Where(x => x != null)) c.Item().AlignRight().Text(m!).FontSize(9).FontColor(Brand.Muted); });
            });
        });
    }

    private static void BuildQuoteTable(IContainer container, List<QuoteLine> items, bool monthly)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(c => { c.ConstantColumn(30); c.RelativeColumn(4); c.RelativeColumn(1); c.RelativeColumn(1.2f); c.RelativeColumn(0.8f); c.RelativeColumn(1.2f); });
            table.Header(h =>
            {
                h.Cell().Element(Brand.HeadCell).Text("POS").Style(Brand.HeadText);
                h.Cell().Element(Brand.HeadCell).Text("LEISTUNG").Style(Brand.HeadText);
                h.Cell().Element(Brand.HeadCell).AlignRight().Text("MENGE").Style(Brand.HeadText);
                h.Cell().Element(Brand.HeadCell).AlignRight().Text(monthly ? "MONAT" : "PREIS").Style(Brand.HeadText);
                h.Cell().Element(Brand.HeadCell).AlignRight().Text("MWST").Style(Brand.HeadText);
                h.Cell().Element(Brand.HeadCell).AlignRight().Text("GESAMT").Style(Brand.HeadText);
            });
            for (int i = 0; i < items.Count; i++)
            {
                var l = items[i];
                table.Cell().Element(Brand.BodyCell).Text($"{i+1:00}").FontSize(8).FontColor(Brand.Muted);
                table.Cell().Element(Brand.BodyCell).Column(c =>
                {
                    c.Item().Text(l.Title).Bold().FontSize(9).FontColor(Brand.Navy);
                    if (!string.IsNullOrEmpty(l.Description)) c.Item().Text(l.Description).FontSize(8).FontColor(Brand.Muted);
                    if (l.DiscountPercent > 0) c.Item().Text($"Rabatt: {l.DiscountPercent:N0}%").FontSize(8).FontColor("#B54708");
                });
                table.Cell().Element(Brand.BodyCell).AlignRight().Text(l.Quantity.ToString("N0")).FontSize(8);
                table.Cell().Element(Brand.BodyCell).AlignRight().Text($"{l.UnitPrice:N2} €").FontSize(8);
                table.Cell().Element(Brand.BodyCell).AlignRight().Text($"{l.VatPercent}%").FontSize(8);
                table.Cell().Element(Brand.BodyCell).AlignRight().Text($"{l.Total:N2} €").FontSize(8);
            }
        });
    }

    private static void BuildTotalsBox(ColumnDescriptor col, decimal net, decimal taxRate, decimal tax, decimal gross, TaxMode mode)
    {
        col.Item().PaddingTop(16).AlignRight().MinWidth(240).Background(Brand.Fill).Padding(12).Column(tc =>
        {
            tc.Item().Row(r => { r.RelativeItem().Text("Netto:"); r.RelativeItem().AlignRight().Text($"{net:N2} €"); });
            if (mode == TaxMode.SmallBusiness) tc.Item().PaddingTop(4).Text("Gem. § 19 UStG – keine MwSt.").FontSize(8).FontColor(Brand.Muted);
            else tc.Item().Row(r => { r.RelativeItem().Text($"MwSt. ({taxRate:N0}%):"); r.RelativeItem().AlignRight().Text($"{tax:N2} €"); });
            tc.Item().PaddingTop(6).LineHorizontal(1).LineColor(Brand.Navy);
            tc.Item().PaddingTop(6).Row(r => { r.RelativeItem().Text("Gesamt:").FontFamily(Brand.Display).Bold().FontSize(12).FontColor(Brand.Navy); r.RelativeItem().AlignRight().Text($"{gross:N2} €").FontFamily(Brand.Display).Bold().FontSize(12).FontColor(Brand.Navy); });
        });
    }

    private static void BuildSignatureArea(ColumnDescriptor col, CompanySettings co, string? sigData, string? sigName, string? sigEmail, DateTimeOffset? sigDate, string? customerCompanyName)
    {
        col.Item().PaddingTop(30).Row(row =>
        {
            // Left: Auftragnehmer (us) — no signature line needed
            row.RelativeItem().Column(c =>
            {
                c.Item().PaddingTop(4).Text("Auftragnehmer").FontSize(8).FontColor(Brand.Muted);
                c.Item().PaddingTop(6).Text(co.CompanyName).FontSize(9).Bold().FontColor(Brand.Navy);
                if (!string.IsNullOrEmpty(co.ManagingDirector))
                    c.Item().Text(co.ManagingDirector).FontSize(8).FontColor(Brand.Muted);
                if (!string.IsNullOrEmpty(co.Email))
                    c.Item().Text(co.Email).FontSize(8).FontColor(Brand.Muted);
            });

            row.ConstantItem(40);

            // Right: Auftraggeber (customer) — signature or blank line
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

                    c.Item().LineHorizontal(0.5f).LineColor(Brand.Line);
                    c.Item().PaddingTop(4).Text("Auftraggeber").FontSize(8).FontColor(Brand.Muted);

                    var displayName = !string.IsNullOrEmpty(customerCompanyName)
                        ? customerCompanyName
                        : sigName ?? "";

                    c.Item().PaddingTop(2).Text($"✓ {displayName}").FontSize(9).Bold().FontColor(Brand.Success);

                    if (!string.IsNullOrEmpty(sigName) && sigName != displayName)
                        c.Item().Text(sigName).FontSize(8).FontColor(Brand.Navy);

                    if (!string.IsNullOrEmpty(sigEmail))
                        c.Item().Text(sigEmail).FontSize(8).FontColor(Brand.Muted);

                    c.Item().Text($"Unterzeichnet am {sigDate:dd.MM.yyyy} um {sigDate:HH:mm} Uhr").FontSize(8).FontColor(Brand.Muted);
                }
                else
                {
                    c.Item().Height(60);
                    c.Item().LineHorizontal(0.5f).LineColor(Brand.Line);
                    c.Item().PaddingTop(4).Text("Auftraggeber (Unterschrift)").FontSize(8).FontColor(Brand.Muted);
                }
            });
        });
    }

    private static void BuildDocFooter(IContainer container, CompanySettings co)
    {
        container.Column(fc =>
        {
            fc.Item().LineHorizontal(0.5f).LineColor(Brand.Line);
            fc.Item().PaddingTop(3).Row(r =>
            {
                r.RelativeItem().Text($"{co.CompanyName} · {co.Street} · {co.ZipCode} {co.City}").FontSize(7).FontColor(Brand.Muted);
                r.RelativeItem().AlignCenter().Text($"St.Nr.: {co.TaxId} · USt-IdNr.: {co.VatId}").FontSize(7).FontColor(Brand.Muted);
                r.RelativeItem().AlignRight().Text(t =>
                {
                    t.CurrentPageNumber().FontSize(7).FontColor(Brand.Muted);
                    t.Span("/").FontSize(7).FontColor(Brand.Muted);
                    t.TotalPages().FontSize(7).FontColor(Brand.Muted);
                });
            });
            if (!string.IsNullOrEmpty(co.Iban)) fc.Item().AlignCenter().Text($"{co.BankName} · IBAN: {co.Iban} · BIC: {co.Bic}").FontSize(7).FontColor(Brand.Muted);
            if (!string.IsNullOrEmpty(co.RegisterCourt)) fc.Item().AlignCenter().Text($"{co.RegisterCourt} · {co.RegisterNumber} · GF: {co.ManagingDirector}").FontSize(7).FontColor(Brand.Muted);
        });
    }

    public async Task<byte[]> GenerateAgencyContractPdfAsync(AgencyContract contract, CompanySettings co, CancellationToken ct = default)
    {
        var logo = await LoadLogoAsync(co.LogoPath);
        var contact = contract.Customer.Contacts.FirstOrDefault(c => c.IsPrimary) ?? contract.Customer.Contacts.FirstOrDefault();
        var loc = contract.Customer.Locations.FirstOrDefault(l => l.IsPrimary) ?? contract.Customer.Locations.FirstOrDefault();
        var headerCustomer = contract.Customer;
        if (!string.IsNullOrEmpty(contract.CustomerAddressSnapshot))
        {
            var snap = JsonSerializer.Deserialize<CustomerAddressSnapshotDto>(contract.CustomerAddressSnapshot);
            if (snap != null)
            {
                headerCustomer = new Customer { CompanyName = snap.Name };
                loc = new Location { Street = snap.Strasse ?? "", ZipCode = snap.Plz ?? "", City = snap.Ort ?? "", Country = snap.Land ?? "Deutschland" };
                contact = string.IsNullOrEmpty(snap.Ansprechpartner) ? null : new Contact { FirstName = snap.Ansprechpartner, LastName = "" };
            }
        }
        var sections = JsonSerializer.Deserialize<List<ContractSectionDto>>(contract.SectionsJson) ?? new();
        List<(string Title, string Content)>? legal = null;
        if (!string.IsNullOrEmpty(contract.LegalTextBlocksSnapshot))
        {
            var snapshot = JsonSerializer.Deserialize<List<LegalTextSnapshotItem>>(contract.LegalTextBlocksSnapshot);
            if (snapshot?.Any() == true) legal = snapshot.Select(s => (s.Title, string.IsNullOrEmpty(s.AttachmentFileName) ? s.Content : $"Dieses Dokument liegt als Anhang bei ({s.AttachmentFileName}).")).ToList();
        }

        return Document.Create(c => c.Page(p =>
        {
            p.Size(PageSizes.A4); p.MarginTop(30); p.MarginBottom(30); p.MarginHorizontal(45);
            p.DefaultTextStyle(x => x.FontFamily(Brand.Body).FontSize(9.5f).FontColor(Brand.Text));
            p.Header().Element(h => BuildDocHeader(h, co, logo, "AGENTURVERTRAG", $"Nr. {contract.ContractNumber}", headerCustomer, contact, loc, $"Vertragsart: {contract.ContractTypeName}", contract.TotalContractValue.HasValue ? $"Vertragswert: {contract.TotalContractValue:N2} €" : null));
            p.Content().PaddingTop(16).Column(col =>
            {
                foreach (var s in sections)
                {
                    var sec = Brand.Group(col);
                    Brand.Section(sec, s.Title);
                    sec.Item().PaddingTop(6).Text(s.Content).FontSize(9);
                }
                if (legal?.Any() == true) foreach (var b in legal) { col.Item().PaddingTop(12).Text(b.Title).Bold().FontSize(9).FontColor(Brand.Navy); col.Item().PaddingTop(3).Text(b.Content).FontSize(7.5f).FontColor(Brand.Muted); }
                BuildBilateralSignatureArea(col, co, contract);
            });
            p.Footer().Element(f => BuildDocFooter(f, co));
        })).GeneratePdf();
    }

    /// <summary>Unlike BuildSignatureArea (Angebot: only the customer signs), the Agenturvertrag is actively
    /// countersigned by our side too — a one-click confirmation by the logged-in employee, not a drawn image.</summary>
    private static void BuildBilateralSignatureArea(ColumnDescriptor col, CompanySettings co, AgencyContract contract)
    {
        col.Item().PaddingTop(30).Row(row =>
        {
            row.RelativeItem().Column(c =>
            {
                c.Item().PaddingTop(4).Text("Auftragnehmer").FontSize(8).FontColor(Brand.Muted);
                c.Item().PaddingTop(6).Text(co.CompanyName).FontSize(9).Bold().FontColor(Brand.Navy);
                if (!string.IsNullOrEmpty(contract.RepSignedByName))
                {
                    c.Item().PaddingTop(2).Text($"✓ {contract.RepSignedByName}").FontSize(9).Bold().FontColor(Brand.Success);
                    c.Item().Text($"Bestätigt am {contract.RepSignedAt:dd.MM.yyyy} um {contract.RepSignedAt:HH:mm} Uhr").FontSize(8).FontColor(Brand.Muted);
                }
                else
                {
                    c.Item().Height(30);
                    c.Item().LineHorizontal(0.5f).LineColor(Brand.Line);
                    c.Item().PaddingTop(4).Text("Noch nicht bestätigt").FontSize(8).FontColor(Brand.Muted);
                }
            });

            row.ConstantItem(40);

            row.RelativeItem().Column(c =>
            {
                if (!string.IsNullOrEmpty(contract.CustomerSignatureData))
                {
                    try
                    {
                        var base64 = contract.CustomerSignatureData.Contains(",") ? contract.CustomerSignatureData.Split(',')[1] : contract.CustomerSignatureData;
                        var imgBytes = Convert.FromBase64String(base64);
                        c.Item().MaxHeight(60).MaxWidth(200).Image(imgBytes, ImageScaling.FitArea);
                    }
                    catch { }

                    c.Item().LineHorizontal(0.5f).LineColor(Brand.Line);
                    c.Item().PaddingTop(4).Text("Auftraggeber").FontSize(8).FontColor(Brand.Muted);
                    var displayName = !string.IsNullOrEmpty(contract.Customer.CompanyName) ? contract.Customer.CompanyName : contract.CustomerSignedByName ?? "";
                    c.Item().PaddingTop(2).Text($"✓ {displayName}").FontSize(9).Bold().FontColor(Brand.Success);
                    if (!string.IsNullOrEmpty(contract.CustomerSignedByName) && contract.CustomerSignedByName != displayName)
                        c.Item().Text(contract.CustomerSignedByName).FontSize(8).FontColor(Brand.Navy);
                    if (!string.IsNullOrEmpty(contract.CustomerSignedByEmail))
                        c.Item().Text(contract.CustomerSignedByEmail).FontSize(8).FontColor(Brand.Muted);
                    c.Item().Text($"Unterzeichnet am {contract.CustomerSignedAt:dd.MM.yyyy} um {contract.CustomerSignedAt:HH:mm} Uhr").FontSize(8).FontColor(Brand.Muted);
                }
                else
                {
                    c.Item().Height(60);
                    c.Item().LineHorizontal(0.5f).LineColor(Brand.Line);
                    c.Item().PaddingTop(4).Text("Auftraggeber (Unterschrift)").FontSize(8).FontColor(Brand.Muted);
                }
            });
        });
    }

    // === Aufwandsschätzung ("01 — Aufwandsschätzung" Word template) ===
    private static readonly System.Globalization.CultureInfo De = new("de-DE");
    private static string Eur(decimal v, bool cents = false) => v.ToString(cents ? "N2" : "N0", De) + " €";
    private static string EurRange(decimal? from, decimal? to)
    {
        if (from.HasValue && to.HasValue && from.Value != to.Value) return $"{from.Value.ToString("N0", De)} – {Eur(to.Value)}";
        var v = from ?? to;
        return v.HasValue ? Eur(v.Value) : "–";
    }

    public async Task<byte[]> GenerateEstimatePdfAsync(Estimate e, CompanySettings co, CancellationToken ct = default)
    {
        var logo = await LoadLogoAsync(co.LogoPath);
        var c = EstimateJson.Parse(e.ContentJson);
        static bool Has(string? s) => !string.IsNullOrWhiteSpace(s);
        static List<EstimateItemDto> Items(List<EstimateItemDto> l) => l.Where(i => Has(i.Text) || Has(i.Label)).ToList();
        static List<EstimateModuleDto> Rows(List<EstimateModuleDto> l) => l.Where(m => Has(m.Title) || Has(m.Description) || m.PriceFrom.HasValue || m.PriceTo.HasValue).ToList();
        var basis = Items(c.Basis); var decisions = Items(c.Decisions);
        var architecture = Items(c.Architecture); var implementation = Items(c.Implementation); var pilot = Items(c.Pilot);
        var modules = Rows(c.Modules); var extensions = Rows(c.Extensions);
        var assumptions = Items(c.Assumptions); var exclusions = Items(c.Exclusions);
        var risks = Items(c.Risks); var questions = Items(c.OpenQuestions); var nextSteps = Items(c.NextSteps);
        var timeline = c.Timeline.Where(t => Has(t.Phase) || Has(t.Period) || Has(t.Result)).ToList();
        var customerName = Has(e.CustomerDisplayName) ? e.CustomerDisplayName : e.Customer?.CompanyName;
        var classification = Has(e.Classification) ? char.ToUpper(e.Classification[0], De) + e.Classification[1..] : "–";

        // A section without any content is skipped entirely (heading included); the remaining ones are numbered consecutively.
        var hasSituation = Has(e.SituationText) || basis.Count > 0 || decisions.Count > 0;
        var hasProposal = Has(e.ProposalText) || architecture.Count > 0 || implementation.Count > 0 || pilot.Count > 0;
        var hasCost = modules.Count > 0 || extensions.Count > 0 || e.FixedPrice.HasValue || Has(e.OperationText) || Has(e.CostNote) || assumptions.Count > 0 || exclusions.Count > 0;
        var hasTimeline = timeline.Count > 0 || Has(e.TimelineNote);

        return Document.Create(doc => doc.Page(p =>
        {
            p.Size(PageSizes.A4); p.MarginTop(30); p.MarginBottom(30); p.MarginHorizontal(45);
            p.DefaultTextStyle(x => x.FontFamily(Brand.Body).FontSize(9.5f).FontColor(Brand.Text));
            p.Header().Element(h => BuildEstimateHeader(h, co, logo, $"Aufwandsschätzung · {e.EstimateNumber} · V{e.Version}"));
            p.Content().PaddingTop(14).Column(col =>
            {
                var n = 0;
                string Num(string title) => $"{++n:00} — {title}";

                // Cover block
                col.Item().Background(Brand.Navy).PaddingHorizontal(24).PaddingVertical(22).Column(cv =>
                {
                    cv.Item().Text("AUFWANDSSCHÄTZUNG").FontFamily(Brand.Display).FontSize(7.5f).Bold().FontColor("#AEB5D1").LetterSpacing(0.3f);
                    cv.Item().PaddingTop(10).Text(e.Title.ToUpperInvariant()).FontFamily(Brand.Display).FontSize(22).Bold().FontColor("#FFFFFF").LineHeight(1.1f);
                    if (Has(e.Subtitle)) cv.Item().PaddingTop(6).Text(e.Subtitle!).FontSize(11).FontColor(Brand.Line);
                    if (Has(customerName)) cv.Item().PaddingTop(16).Text(t =>
                    {
                        t.Span("Erstellt für ").FontSize(9).FontColor("#AEB5D1");
                        t.Span(customerName!).FontSize(9).Bold().FontColor("#FFFFFF");
                    });
                });

                // Meta table
                col.Item().PaddingTop(10).Table(t =>
                {
                    t.ColumnsDefinition(cd => { cd.ConstantColumn(92); cd.RelativeColumn(); cd.ConstantColumn(92); cd.RelativeColumn(); });
                    void Pair(string label, string? value)
                    {
                        t.Cell().Element(MetaLabelCell).Text(label.ToUpperInvariant()).FontFamily(Brand.Display).FontSize(6.5f).Bold().FontColor(Brand.Muted).LetterSpacing(0.08f);
                        t.Cell().Element(MetaValueCell).Text(Has(value) ? value! : "–").FontSize(9);
                    }
                    Pair("Version", e.Version); Pair("Datum", e.Date.ToString("dd.MM.yyyy", De));
                    Pair("Erstellt von", e.PreparedBy); Pair("Freigegeben von", e.ApprovedBy);
                    Pair("Verantwortlicher", e.Responsible); Pair("Klassifizierung", classification);
                });

                if (hasSituation)
                {
                    var sec = EstimateSection(col, Num("Ausgangslage"));
                    EstimatePara(sec, e.SituationText);
                    if (basis.Count > 0) EstimateBullets(EstimateSub(sec, "Grundlage"), basis);
                    if (decisions.Count > 0) EstimateBullets(EstimateSub(sec, "Festlegungen"), decisions);
                }

                if (hasProposal)
                {
                    var sec = EstimateSection(col, Num("Vorschlag Architektur, Umsetzung und Pilot"));
                    EstimatePara(sec, e.ProposalText);
                    if (architecture.Count > 0) EstimateBullets(EstimateSub(sec, "Architektur"), architecture);
                    if (implementation.Count > 0) EstimateBullets(EstimateSub(sec, "Umsetzung"), implementation);
                    if (pilot.Count > 0) EstimateBullets(EstimateSub(sec, "Pilotbetrieb"), pilot);
                }

                if (hasCost)
                {
                    var sec = EstimateSection(col, Num("Aufwandsschätzung"));
                    var (mFrom, mTo) = EstimateJson.Range(modules);
                    var (xFrom, xTo) = EstimateJson.Range(extensions);
                    if (modules.Count > 0) sec.Item().PaddingTop(10).Element(x => BuildEstimateModuleTable(x, modules, "Summe Module"));
                    if (e.FixedPrice.HasValue)
                        sec.Item().PaddingTop(10).ShowEntire().Background(Brand.Fill).BorderLeft(3).BorderColor(Brand.Navy).PaddingVertical(9).PaddingHorizontal(12).Row(r =>
                        {
                            r.RelativeItem().AlignMiddle().Text((Has(e.FixedPriceLabel) ? e.FixedPriceLabel!.Trim().TrimEnd(':') : "Festpreis") + ":").FontFamily(Brand.Display).Bold().FontSize(10).FontColor(Brand.Navy);
                            r.AutoItem().AlignMiddle().Text(co.DefaultTaxMode == TaxMode.SmallBusiness ? Eur(e.FixedPrice.Value) : $"{Eur(e.FixedPrice.Value)} netto").FontFamily(Brand.Display).Bold().FontSize(12).FontColor(Brand.Navy);
                        });
                    if (extensions.Count > 0)
                    {
                        EstimateSub(sec, "Ausbaustufen (optional)").Item().PaddingTop(6).Element(x => BuildEstimateModuleTable(x, extensions, "Summe Ausbaustufen"));
                    }

                    var overview = new List<(string Label, string Value, bool Strong)>(); // Betrieb is free text → left aligned
                    if (e.FixedPrice.HasValue)
                    {
                        var fp = e.FixedPrice.Value;
                        if (co.DefaultTaxMode == TaxMode.SmallBusiness)
                        {
                            overview.Add(("Festpreis (Endpreis)", Eur(fp, true), true));
                            overview.Add(("Umsatzsteuer", "entfällt gem. § 19 UStG", false));
                        }
                        else
                        {
                            var vat = Math.Round(fp * 0.19m, 2, MidpointRounding.AwayFromZero);
                            overview.Add(("Festpreis netto", Eur(fp, true), false));
                            overview.Add(("zzgl. 19 % USt", Eur(vat, true), false));
                            overview.Add(("Festpreis brutto", Eur(fp + vat, true), true));
                        }
                    }
                    else if (modules.Count > 0) overview.Add(("Aufwand netto (Spanne)", EurRange(mFrom, mTo), true));
                    if (extensions.Count > 0) overview.Add((co.DefaultTaxMode == TaxMode.SmallBusiness ? "Ausbaustufen gesamt (optional)" : "Ausbaustufen gesamt (optional, netto)", EurRange(xFrom, xTo), false));
                    if (Has(e.OperationText)) overview.Add(("Betrieb", e.OperationText!, false));
                    if (overview.Count > 0)
                    {
                        EstimateSub(sec, "Kostenübersicht").Item().PaddingTop(6).ShowEntire().Table(t =>
                        {
                            t.ColumnsDefinition(cd => { cd.RelativeColumn(1.1f); cd.RelativeColumn(1.6f); });
                            foreach (var (label, value, strong) in overview)
                            {
                                t.Cell().Element(Brand.BodyCell).Text(label).FontSize(9).Bold().FontColor(strong ? Brand.Navy : Brand.Text);
                                var vc = t.Cell().Element(Brand.BodyCell);
                                var cell = (label == "Betrieb" ? vc.AlignLeft() : vc.AlignRight()).Text(value).FontSize(9).FontColor(strong ? Brand.Navy : Brand.Text);
                                if (strong) cell.Bold();
                            }
                        });
                    }
                    if (Has(e.CostNote)) sec.Item().PaddingTop(8).Text(e.CostNote!).FontSize(8.5f).FontColor(Brand.Muted).LineHeight(1.35f);
                    if (assumptions.Count > 0) EstimateBullets(EstimateSub(sec, "Annahmen und Abhängigkeiten"), assumptions);
                    if (exclusions.Count > 0) EstimateBullets(EstimateSub(sec, "Nicht im Umfang enthalten"), exclusions);
                }

                if (risks.Count > 0) { var sec = EstimateSection(col, Num("Wesentliche Risiken")); sec.Item().PaddingTop(4); EstimateBullets(sec, risks); }
                if (questions.Count > 0) { var sec = EstimateSection(col, Num("Offene Fragen")); sec.Item().PaddingTop(4); EstimateNumbered(sec, questions); }

                if (hasTimeline)
                {
                    var sec = EstimateSection(col, Num("Zeitplan"));
                    if (timeline.Count > 0)
                        sec.Item().PaddingTop(10).Table(t =>
                        {
                            t.ColumnsDefinition(cd => { cd.RelativeColumn(1.6f); cd.RelativeColumn(1.3f); cd.RelativeColumn(2.6f); });
                            t.Header(h =>
                            {
                                h.Cell().Element(Brand.HeadCell).Text("PHASE").Style(Brand.HeadText);
                                h.Cell().Element(Brand.HeadCell).Text("ZEITRAUM").Style(Brand.HeadText);
                                h.Cell().Element(Brand.HeadCell).Text("ERGEBNIS").Style(Brand.HeadText);
                            });
                            foreach (var row in timeline)
                            {
                                t.Cell().Element(Brand.BodyCell).Text(row.Phase).Bold().FontSize(9).FontColor(Brand.Navy);
                                t.Cell().Element(Brand.BodyCell).Text(row.Period ?? "").FontSize(8.5f);
                                t.Cell().Element(Brand.BodyCell).Text(row.Result ?? "").FontSize(8.5f);
                            }
                        });
                    if (Has(e.TimelineNote)) sec.Item().PaddingTop(8).Text(e.TimelineNote!).FontSize(8.5f).FontColor(Brand.Muted).LineHeight(1.35f);
                }

                // The last section and the closing box move to the next page together (when they fit there), so the
                // "LASS UNS STARTEN" box never ends up alone on an otherwise empty page.
                var tailHeight = Math.Min(380, 120 + (nextSteps.Count > 0 ? 50 + 22 * nextSteps.Count : 0));
                col.Item().EnsureSpace(tailHeight).Column(tail =>
                {
                if (nextSteps.Count > 0) { var sec = EstimateSection(tail, Num("Nächste Schritte")); sec.Item().PaddingTop(4); EstimateNumbered(sec, nextSteps); }

                // Closing
                tail.Item().PaddingTop(24).ShowEntire().Background(Brand.Navy).PaddingHorizontal(24).PaddingVertical(20).Column(cl =>
                {
                    cl.Item().Text("LASS UNS STARTEN").FontFamily(Brand.Display).FontSize(16).Bold().FontColor("#FFFFFF").LetterSpacing(0.06f);
                    cl.Item().PaddingTop(6).Text(t =>
                    {
                        t.DefaultTextStyle(x => x.FontSize(9.5f).FontColor(Brand.Line));
                        if (Has(co.Email)) { t.Span("Bei Fragen erreichen Sie uns jederzeit unter "); t.Span(co.Email!).Bold().FontColor("#FFFFFF"); t.Span("."); }
                        else t.Span("Bei Fragen erreichen Sie uns jederzeit.");
                    });
                });
                });
            });
            p.Footer().Element(f => BuildDocFooter(f, co));
        })).GeneratePdf();
    }

    private static IContainer MetaLabelCell(IContainer c) => c.Background(Brand.Fill).BorderBottom(0.5f).BorderColor("#FFFFFF").PaddingVertical(5).PaddingHorizontal(6).AlignMiddle();
    private static IContainer MetaValueCell(IContainer c) => c.BorderBottom(0.5f).BorderColor(Brand.Line).PaddingVertical(5).PaddingHorizontal(6).AlignMiddle();

    private static void BuildEstimateHeader(IContainer container, CompanySettings co, byte[]? logo, string docLabel)
    {
        container.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().AlignBottom().Column(lc =>
                {
                    if (logo != null) lc.Item().MaxHeight(34).MaxWidth(150).Image(logo).FitArea();
                    else lc.Item().Text(co.CompanyName).FontFamily(Brand.Display).FontSize(13).Bold().FontColor(Brand.Navy);
                });
                row.RelativeItem().AlignRight().AlignBottom().Text(docLabel.ToUpperInvariant()).FontFamily(Brand.Display).FontSize(7).FontColor(Brand.Muted).LetterSpacing(0.12f);
            });
            col.Item().PaddingTop(6).LineHorizontal(1.2f).LineColor(Brand.Navy);
        });
    }

    private static void EstimatePara(ColumnDescriptor col, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text)) col.Item().PaddingTop(8).Text(text).FontSize(9.5f).LineHeight(1.35f);
    }

    /// <summary>Brand.Section in its own group: add the section's content to the returned column so the heading
    /// never ends up alone at the bottom of a page.</summary>
    private static ColumnDescriptor EstimateSection(ColumnDescriptor col, string text)
    {
        var g = Brand.Group(col, 110);
        Brand.Section(g, text);
        return g;
    }

    private static ColumnDescriptor EstimateSub(ColumnDescriptor col, string title)
    {
        var g = Brand.Group(col, 60);
        g.Item().PaddingTop(12).Text(title).FontFamily(Brand.Display).Bold().FontSize(9.5f).FontColor(Brand.Navy);
        return g;
    }

    private static void EstimateItemText(TextDescriptor t, EstimateItemDto i)
    {
        t.DefaultTextStyle(x => x.LineHeight(1.3f));
        var hasText = !string.IsNullOrWhiteSpace(i.Text);
        if (!string.IsNullOrWhiteSpace(i.Label)) t.Span(i.Label.Trim().TrimEnd(':') + (hasText ? ": " : "")).Bold().FontColor(Brand.Navy);
        if (hasText) t.Span(i.Text.Trim());
    }

    /// <summary>Bullet list; an item's optional label is rendered as a bold lead-in ("Label: text").</summary>
    private static void EstimateBullets(ColumnDescriptor col, List<EstimateItemDto> items)
    {
        foreach (var i in items)
            col.Item().PaddingTop(4).Row(r =>
            {
                r.ConstantItem(14).Text("•").LineHeight(1.3f).FontColor(Brand.Navy).Bold();
                r.RelativeItem().Text(t => EstimateItemText(t, i));
            });
    }

    /// <summary>Numbered list 01, 02, … (Offene Fragen, Nächste Schritte).</summary>
    private static void EstimateNumbered(ColumnDescriptor col, List<EstimateItemDto> items)
    {
        for (var k = 0; k < items.Count; k++)
        {
            var i = items[k];
            var no = (k + 1).ToString("00");
            col.Item().PaddingTop(5).Row(r =>
            {
                r.ConstantItem(24).Text(no).LineHeight(1.3f).FontFamily(Brand.Display).Bold().FontSize(9.5f).FontColor(Brand.Navy);
                r.RelativeItem().Text(t => EstimateItemText(t, i));
            });
        }
    }

    /// <summary>"Modul | Leistungsumfang | Pauschale" with a von–bis range per row and a summed range row.</summary>
    private static void BuildEstimateModuleTable(IContainer container, List<EstimateModuleDto> rows, string sumLabel)
    {
        var (from, to) = EstimateJson.Range(rows);
        container.Table(t =>
        {
            t.ColumnsDefinition(cd => { cd.RelativeColumn(2.1f); cd.RelativeColumn(3.6f); cd.ConstantColumn(104); });
            t.Header(h =>
            {
                h.Cell().Element(Brand.HeadCell).Text("MODUL").Style(Brand.HeadText);
                h.Cell().Element(Brand.HeadCell).Text("LEISTUNGSUMFANG").Style(Brand.HeadText);
                h.Cell().Element(Brand.HeadCell).AlignRight().Text("PAUSCHALE").Style(Brand.HeadText);
            });
            foreach (var m in rows)
            {
                t.Cell().Element(Brand.BodyCell).Text(m.Title).Bold().FontSize(9).FontColor(Brand.Navy);
                t.Cell().Element(Brand.BodyCell).Text(m.Description ?? "").FontSize(8.5f).LineHeight(1.3f);
                t.Cell().Element(Brand.BodyCell).AlignRight().Text(EurRange(m.PriceFrom, m.PriceTo)).FontSize(9);
            }
            t.Cell().ColumnSpan(2).Background(Brand.Fill).PaddingVertical(7).PaddingHorizontal(4).Text(sumLabel).FontFamily(Brand.Display).Bold().FontSize(9).FontColor(Brand.Navy);
            t.Cell().Background(Brand.Fill).PaddingVertical(7).PaddingHorizontal(4).AlignRight().Text(EurRange(from, to)).FontFamily(Brand.Display).Bold().FontSize(9).FontColor(Brand.Navy);
        });
    }
}
