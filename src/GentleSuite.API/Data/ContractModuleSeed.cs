using GentleSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace GentleSuite.Infrastructure.Data;

/// <summary>
/// Second wave of Leistungsmodule (Software, APIs, Apps, Hosting, Domain, SEA, Content, Beratung, UX/UI, Print,
/// Software-Wartung) and the matching Vertragsvorlagen, modelled on the module structure of the Legal-Cockpit
/// Agenturvertrag. Purely additive: each block/template is inserted only if its Key does not exist yet, so texts
/// the user already edited in "Vertragsvorlagen" are never overwritten and nothing is deleted.
/// </summary>
public static class ContractModuleSeed
{
    record BlockDef(string Key, string Category, int SortOrder, bool Creative, string Title, string Content);

    static readonly BlockDef[] Blocks =
    {
        // ---------- Kern (Ergänzung): wizard contracts had no liability clause at all ----------
        new("haftung", "kern", 4, false, "Haftung und Freistellung", Haftung),

        // ---------- Webseiten (Ergänzungen) ----------
        new("webseiten-hosting", "webseiten", 3, false, "Spezifizierte Leistungen: Webhosting", """
            Der Anbieter stellt dem Kunden Speicherplatz auf einem Webserver für den Betrieb der Webseite zur Verfügung. Der Anbieter bedient sich hierfür eines externen Hosting-Dienstleisters (Reseller-Modell); er bleibt alleiniger Vertragspartner des Kunden.

            Der Anbieter schuldet eine Erreichbarkeit im Rahmen der Verfügbarkeit, die der eingesetzte Hosting-Dienstleister zusichert. Ausfallzeiten durch Wartungsarbeiten, höhere Gewalt oder Störungen außerhalb des Einflussbereichs des Anbieters begründen keine Ansprüche des Kunden.

            Der Kunde ist für die von ihm eingestellten Inhalte selbst verantwortlich. Der Anbieter ist berechtigt, Inhalte vorübergehend zu sperren, wenn ein begründeter Verdacht auf Rechtsverstöße besteht oder der Betrieb des Servers gefährdet wird.

            Soweit beim Hosting personenbezogene Daten im Auftrag verarbeitet werden, schließen die Parteien einen Vertrag über Auftragsverarbeitung nach Art. 28 DSGVO.
            """),
        new("webseiten-domain", "webseiten", 4, false, "Spezifizierte Leistungen: Domainregistrierung", """
            Der Anbieter beantragt die vom Kunden gewünschte Domain im Namen und auf Rechnung des Kunden bei der zuständigen Vergabestelle bzw. über einen Registrar. Inhaber der Domain wird der Kunde.

            Der Anbieter hat auf die Vergabe keinen Einfluss und übernimmt keine Gewähr dafür, dass die gewünschte Domain zugeteilt wird oder frei von Rechten Dritter ist. Die Prüfung, ob die Domain Marken-, Namens- oder sonstige Rechte Dritter verletzt, obliegt dem Kunden.

            Bei Beendigung des Vertrags unterstützt der Anbieter den Kunden beim Umzug der Domain zu einem anderen Anbieter (Herausgabe des Auth-Codes).
            """),

        // ---------- Software & Apps ----------
        new("software-entwicklung", "software", 1, true, "Spezifizierte Leistungen: Entwicklung von Individualsoftware", """
            Gegenstand dieser Leistung ist die Entwicklung einer individuellen Software (z. B. Webanwendung, Portal, Backend-System) nach den Vorgaben des Kunden. Der Vertrag ist ein Werkvertrag im Sinne der §§ 631 ff. BGB.

            Grundlage der Entwicklung ist die im Angebot bzw. in einem Lasten-/Pflichtenheft vereinbarte Leistungsbeschreibung. Nicht ausdrücklich beschriebene Funktionen sind nicht geschuldet. Änderungen und Erweiterungen des Leistungsumfangs (Change Requests) werden gesondert vereinbart und vergütet.

            Der Anbieter entwickelt nach dem Stand der Technik zum Zeitpunkt des Vertragsschlusses. Die Software wird für die im Angebot genannten Systemumgebungen (z. B. Browser, Betriebssysteme, Server) entwickelt; für andere Umgebungen wird keine Lauffähigkeit geschuldet.

            Die Einbindung von Open-Source-Komponenten und Diensten Dritter ist zulässig. Deren Lizenzbedingungen gehen für die jeweilige Komponente vor; der Anbieter weist den Kunden auf wesentliche Lizenzpflichten hin.

            Der Anbieter stellt die Software in Teilschritten oder nach Fertigstellung zur Abnahme bereit und fordert den Kunden hierzu auf.
            """),
        new("software-api", "software", 2, true, "Spezifizierte Leistungen: API-Entwicklung und Schnittstellen", """
            Gegenstand dieser Leistung ist die Entwicklung von Programmierschnittstellen (APIs) bzw. die Anbindung von Systemen des Kunden oder Dritter (z. B. Zahlungsanbieter, CRM, ERP, Buchhaltung). Der Vertrag ist ein Werkvertrag im Sinne der §§ 631 ff. BGB.

            Der Anbieter schuldet die Funktion der Schnittstelle gemäß der vereinbarten Spezifikation (Endpunkte, Datenformate, Authentifizierung). Die Verfügbarkeit, Funktionsfähigkeit und unveränderte Fortführung der Systeme und Schnittstellen Dritter liegen außerhalb des Einflussbereichs des Anbieters und sind nicht geschuldet. Anpassungen, die durch Änderungen Dritter (z. B. neue API-Versionen, geänderte Nutzungsbedingungen) erforderlich werden, sind gesondert zu beauftragen.

            Der Kunde stellt die erforderlichen Zugänge, Testumgebungen und API-Schlüssel rechtzeitig bereit und ist für den Abschluss und die Kosten der Verträge mit den Drittanbietern selbst verantwortlich. Zugangsdaten werden vom Anbieter vertraulich behandelt und nur für die Durchführung des Auftrags verwendet.

            Der Anbieter liefert eine technische Dokumentation der Schnittstelle in angemessenem Umfang.
            """),
        new("software-app", "software", 3, true, "Spezifizierte Leistungen: Entwicklung von Apps", """
            Gegenstand dieser Leistung ist die Entwicklung einer mobilen Anwendung (App) für die im Angebot genannten Plattformen (z. B. iOS, Android) bzw. einer Progressive Web App. Der Vertrag ist ein Werkvertrag im Sinne der §§ 631 ff. BGB.

            Die App wird für die zum Zeitpunkt der Abnahme aktuellen und die jeweils vorherige Hauptversion der Betriebssysteme optimiert. Eine Lauffähigkeit auf älteren oder künftigen Versionen ist nicht geschuldet.

            Die Veröffentlichung in den App-Stores erfolgt über Entwicklerkonten des Kunden; der Kunde trägt die Gebühren der Store-Betreiber. Auf die Prüfung und Freigabe durch die Store-Betreiber hat der Anbieter keinen Einfluss; eine Ablehnung begründet keinen Mangel, sofern sie nicht auf einem vom Anbieter zu vertretenden Verstoß gegen die Richtlinien beruht.
            """),
        new("software-quellcode", "software", 4, false, "Quellcode und Dokumentation", """
            Nach vollständiger Bezahlung übergibt der Anbieter dem Kunden den Quellcode der individuell für ihn entwickelten Bestandteile in einem gängigen Format bzw. über ein Code-Repository. Vom Anbieter vorbestehende, wiederverwendbare Komponenten, Bibliotheken und Werkzeuge (Framework-Bestandteile) bleiben im Eigentum des Anbieters; hieran erhält der Kunde ein einfaches, zeitlich unbegrenztes Nutzungsrecht im Rahmen der Software.

            Eine Dokumentation wird in dem Umfang geschuldet, der im Angebot vereinbart ist; im Übrigen genügt eine technische Kurzdokumentation (Aufbau, Installation, Konfiguration).
            """),

        // ---------- Wartung (Software) ----------
        new("wartung-software", "wartung", 3, false, "Leistungsumfang (Wartung & Support für Software und Schnittstellen)", """
            Der Anbieter erbringt für die im Angebot genannte Software bzw. Schnittstelle Wartungs- und Supportleistungen als Dienstleistung im Sinne der §§ 611 ff. BGB.

            Inkludiert: Sicherheits- und Abhängigkeitsupdates (Frameworks, Bibliotheken), Monitoring der Erreichbarkeit, Analyse und Behebung von Fehlern der vom Anbieter erstellten Software, Anpassungen an kleinere Änderungen von Schnittstellen Dritter, Backups gemäß Angebot, Support per E-Mail bzw. Ticket.

            Nicht inkludiert: Neue Funktionen und Erweiterungen, größere Umbauten infolge neuer API-Versionen Dritter, Datenmigrationen, Schulungen sowie Fehler, die durch Eingriffe des Kunden oder Dritter verursacht wurden. Diese Leistungen werden auf Wunsch gesondert angeboten.

            Die Reaktionszeiten richten sich nach dem vereinbarten Service Level Agreement. Ein bestimmter Erfolg (z. B. eine bestimmte Verfügbarkeit) ist nur geschuldet, soweit er ausdrücklich zugesichert wurde.
            """),

        // ---------- Marketing (Ergänzungen) ----------
        new("marketing-sea", "marketing", 3, false, "Spezifizierte Leistungen: SEA-Kampagnen", """
            Der Anbieter plant, erstellt und betreut bezahlte Werbekampagnen in Suchmaschinen (z. B. Google Ads) für den Kunden. Es handelt sich um eine Dienstleistung im Sinne der §§ 611 ff. BGB; ein bestimmter Erfolg (z. B. Klicks, Anfragen, Umsätze) wird nicht geschuldet.

            Die Werbekosten (Klickpreise) sind nicht in der Vergütung des Anbieters enthalten und werden vom Kunden direkt an den Werbeplattform-Betreiber gezahlt, sofern nichts anderes vereinbart ist. Das Werbekonto wird auf den Kunden angelegt oder an ihn übergeben.

            Der Kunde ist für die Zulässigkeit der beworbenen Angebote und der von ihm vorgegebenen Werbeaussagen selbst verantwortlich.
            """),
        new("marketing-content", "marketing", 4, false, "Spezifizierte Leistungen: Content-Marketing", """
            Der Anbieter erstellt für den Kunden Inhalte (z. B. Texte, Blogbeiträge, Grafiken, Videos) in dem im Angebot vereinbarten Umfang. Die Inhalte werden dem Kunden vor Veröffentlichung zur Freigabe vorgelegt; mit der Freigabe übernimmt der Kunde die Verantwortung für die inhaltliche Richtigkeit.

            Dem Kunden steht je Inhalt eine Korrekturschleife zu. Weitere Änderungswünsche werden gesondert vergütet. Ein bestimmter Erfolg (z. B. Reichweite oder Rankings) wird nicht geschuldet.
            """),

        // ---------- Design (Ergänzung) ----------
        new("design-print", "design", 2, true, "Spezifizierte Leistungen: Print-Design", """
            Der Anbieter gestaltet für den Kunden Druckerzeugnisse (z. B. Visitenkarten, Flyer, Broschüren) auf Grundlage der Vorgaben des Kunden. Dem Kunden stehen zwei Korrekturschleifen zu; weitere Änderungen werden gesondert vergütet.

            Vor dem Druck erteilt der Kunde eine schriftliche Druckfreigabe. Nach der Freigabe haftet der Anbieter nicht für Fehler, die in der freigegebenen Fassung enthalten waren (z. B. Text- oder Zahlenfehler). Farbabweichungen zwischen Bildschirmdarstellung und Druck sind technisch bedingt und stellen keinen Mangel dar. Die Beauftragung der Druckerei ist nur geschuldet, wenn sie ausdrücklich vereinbart wurde.
            """),

        // ---------- Beratung ----------
        new("beratung-consulting", "beratung", 1, false, "Spezifizierte Leistungen: Beratung und Consulting", """
            Der Anbieter berät den Kunden in den im Angebot genannten Themen (z. B. Digitalisierung, Software-Architektur, Online-Marketing). Es handelt sich um eine Dienstleistung im Sinne der §§ 611 ff. BGB; ein bestimmter wirtschaftlicher Erfolg wird nicht geschuldet. Die Entscheidung über die Umsetzung der Empfehlungen trifft der Kunde in eigener Verantwortung.

            Rechts- und Steuerberatung ist nicht Gegenstand der Beratung.
            """),
        new("beratung-uxui", "beratung", 2, false, "Spezifizierte Leistungen: UX-/UI-Beratung", """
            Der Anbieter analysiert die Nutzerführung und Gestaltung der im Angebot genannten Anwendung bzw. Webseite und gibt Empfehlungen zur Verbesserung (z. B. in Form eines Berichts, von Wireframes oder Prototypen). Es handelt sich um eine Dienstleistung im Sinne der §§ 611 ff. BGB; eine bestimmte Steigerung von Kennzahlen (z. B. Conversion-Rate) wird nicht geschuldet. Die Umsetzung der Empfehlungen ist nur geschuldet, wenn sie gesondert beauftragt wird.
            """),
    };

    record TemplateDef(string Key, string Name, int SortOrder, string[] DefaultBlocks, bool Ongoing);

    static readonly TemplateDef[] Templates =
    {
        new("softwareentwicklungsvertrag", "Softwareentwicklungsvertrag", 4, new[] { "software-entwicklung", "software-quellcode" }, false),
        new("api-integrationsvertrag", "API- und Integrationsvertrag", 5, new[] { "software-api", "software-quellcode" }, false),
        new("app-entwicklungsvertrag", "App-Entwicklungsvertrag", 6, new[] { "software-app", "software-quellcode" }, false),
        new("wartungsvertrag-software", "Wartungsvertrag Software & APIs", 7, new[] { "wartung-software", "wartung-sla" }, true),
        new("hosting-vertrag", "Hosting- und Domainvertrag", 8, new[] { "webseiten-hosting", "webseiten-domain" }, true),
        new("beratungsvertrag", "Beratungsvertrag", 9, new[] { "beratung-consulting" }, false),
    };

    const string VerguetungText = """
        Die Vergütung ergibt sich aus dem zugrunde liegenden Angebot. Nebenkosten (z. B. Reisekosten, Lizenzen und Gebühren Dritter) werden gesondert in Rechnung gestellt, sofern nichts anderes vereinbart ist.

        Rechnungen sind innerhalb von 14 Tagen nach Erhalt ohne Abzug zahlbar.
        """;
    const string LaufzeitProjekt = """
        Der Vertrag beginnt mit der Unterzeichnung und endet nach Erbringung der vereinbarten Leistungen, sofern nicht anders vereinbart.
        """;
    const string LaufzeitDauer = """
        Der Vertrag beginnt mit der Unterzeichnung und läuft auf unbestimmte Zeit, sofern im Angebot keine Mindestlaufzeit vereinbart ist. Er kann von beiden Parteien mit einer Frist von einem Monat zum Monatsende gekündigt werden; nach Ablauf einer Mindestlaufzeit verlängert er sich jeweils um einen Monat. Das Recht zur außerordentlichen Kündigung bleibt unberührt. Kündigungen bedürfen der Textform.
        """;
    const string Haftung = """
        Der Anbieter haftet unbeschränkt bei Vorsatz und grober Fahrlässigkeit, bei der Verletzung von Leben, Körper oder Gesundheit, nach dem Produkthaftungsgesetz sowie im Umfang einer übernommenen Garantie. Bei leicht fahrlässiger Verletzung einer wesentlichen Vertragspflicht (Kardinalpflicht) ist die Haftung auf den bei Vertragsschluss vorhersehbaren, vertragstypischen Schaden begrenzt. Im Übrigen ist die Haftung für leichte Fahrlässigkeit ausgeschlossen. Dies gilt auch für Erfüllungsgehilfen und gesetzliche Vertreter des Anbieters.

        Für den Verlust von Daten haftet der Anbieter nur in dem Umfang, der bei ordnungsgemäßer, regelmäßiger Datensicherung durch den Kunden entstanden wäre, sofern die Datensicherung nicht ausdrücklich Leistung des Anbieters ist.

        Der Kunde stellt den Anbieter von Ansprüchen Dritter frei, die aufgrund von Verstößen des Kunden gegen diesen Vertrag oder geltendes Recht geltend gemacht werden.
        """;

    public static async Task EnsureAsync(AppDbContext db)
    {
        var existingBlockKeys = (await db.ContractClauseBlocks.IgnoreQueryFilters().Select(b => b.Key).ToListAsync()).ToHashSet();
        foreach (var b in Blocks.Where(b => !existingBlockKeys.Contains(b.Key)))
            db.ContractClauseBlocks.Add(new ContractClauseBlock { Key = b.Key, Category = b.Category, SortOrder = b.SortOrder, IsCreativeWork = b.Creative, Title = b.Title, Content = Trim(b.Content) });
        await db.SaveChangesAsync();

        var existingTemplateKeys = (await db.ContractTemplates.IgnoreQueryFilters().Select(t => t.Key).ToListAsync()).ToHashSet();
        var missing = Templates.Where(t => !existingTemplateKeys.Contains(t.Key)).ToList();
        if (missing.Count == 0) return;

        var blocks = await db.ContractClauseBlocks.Where(b => b.IsActive).ToListAsync();
        foreach (var t in missing)
        {
            db.ContractTemplates.Add(new ContractTemplate
            {
                Key = t.Key, Name = t.Name, SortOrder = t.SortOrder,
                SectionsJson = BuildSectionsJson(blocks, t.DefaultBlocks, t.Ongoing),
                DefaultBlockKeysJson = JsonSerializer.Serialize(t.DefaultBlocks),
            });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Standard section order of a contract template, assembled from the current clause blocks:
    /// Geltung, Vertragsschluss, Leistungsmodule, Mitwirkung, (Abnahme), Vergütung, Laufzeit, (Mängel), Haftung,
    /// (Rechte), AV, Vertraulichkeit, Sonstiges. Bracketed parts only for Werkleistungen (IsCreativeWork).</summary>
    public static string BuildSectionsJson(List<ContractClauseBlock> blocks, string[] defaultBlocks, bool ongoing)
    {
        Section? Block(string key) => blocks.FirstOrDefault(b => b.Key == key) is { } b ? new Section(b.Title, b.Content) : null;
        var creative = defaultBlocks.Any(k => blocks.Any(b => b.Key == k && b.IsCreativeWork));
        var sections = new List<Section?> { Block("geltungsbereich"), Block("vertragsschluss") };
        sections.AddRange(defaultBlocks.Select(Block));
        sections.Add(Block("mitwirkung"));
        if (creative) sections.Add(Block("abnahme"));
        sections.Add(new Section("Vergütung", Trim(VerguetungText)));
        sections.Add(new Section(ongoing ? "Vertragslaufzeit und Kündigung" : "Vertragslaufzeit", Trim(ongoing ? LaufzeitDauer : LaufzeitProjekt)));
        if (creative) sections.Add(Block("maengel"));
        sections.Add(Block("haftung") ?? new Section("Haftung und Freistellung", Trim(Haftung)));
        if (creative) sections.Add(Block("rechteeinraumung"));
        sections.Add(Block("av-vertrag"));
        sections.Add(Block("vertraulichkeit"));
        sections.Add(Block("sonstiges"));
        return JsonSerializer.Serialize(sections.Where(s => s != null));
    }

    record Section(string Title, string Content);

    static string Trim(string s) => s.Trim();
}
