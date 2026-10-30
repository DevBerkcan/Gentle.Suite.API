using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace GentleSuite.Infrastructure.Data;

/// <summary>
/// GentleSuite's own legal documents, written independently (no third-party generator text):
/// the B2B AGB of Gentle Group (auto-attached to every quote) and the AV-Vertrag (Art. 28 DSGVO) as a
/// signable contract template. Additive only: the AGB are inserted only while no AGB exist at all, the
/// AV template only if its key is missing — anything the user created or edited is never touched.
/// </summary>
public static class LegalDocumentSeed
{
    public const string AgbKey = "agb-gentlegroup";
    public const string AvTemplateKey = "av-vertrag";

    public static async Task EnsureAsync(AppDbContext db)
    {
        if (!await db.LegalTextBlocks.IgnoreQueryFilters().AnyAsync(b => b.Key == AgbKey || b.Type == LegalDocumentType.Agb))
        {
            db.LegalTextBlocks.Add(new LegalTextBlock
            {
                Key = AgbKey, Title = "Allgemeine Geschäftsbedingungen (AGB)", Content = Agb.Trim(),
                Type = LegalDocumentType.Agb, AutoAttachToQuotes = true, IsActive = true, SortOrder = 0,
            });
            await db.SaveChangesAsync();
        }

        if (!await db.ContractTemplates.IgnoreQueryFilters().AnyAsync(t => t.Key == AvTemplateKey))
        {
            db.ContractTemplates.Add(new ContractTemplate
            {
                Key = AvTemplateKey, Name = "AV-Vertrag (Auftragsverarbeitung nach Art. 28 DSGVO)", SortOrder = 10,
                SectionsJson = JsonSerializer.Serialize(AvSections.Select(s => new { Title = s.Title, Content = s.Content.Trim() })),
                DefaultBlockKeysJson = "[]",
            });
            await db.SaveChangesAsync();
        }
    }

    // ------------------------------------------------------------------ AGB

    const string Agb = """
        Gentle Group · Inhaber Berk-Can Atesoglu · Girardetstraße 17 · 42109 Wuppertal (nachfolgend „Anbieter“)
        Stand: 30.10.2026

        TEIL A – ALLGEMEINE BESTIMMUNGEN

        § 1 Geltungsbereich
        (1) Diese Allgemeinen Geschäftsbedingungen gelten für alle Verträge zwischen dem Anbieter und seinen Kunden über Leistungen aus den Bereichen Webseiten, Software, Schnittstellen (APIs), Apps, Hosting, Wartung, Online-Marketing, Design und Beratung.
        (2) Der Anbieter schließt Verträge ausschließlich mit Unternehmern im Sinne des § 14 BGB, juristischen Personen des öffentlichen Rechts und öffentlich-rechtlichen Sondervermögen. Mit der Annahme eines Angebots bestätigt der Kunde, in Ausübung seiner gewerblichen oder selbständigen beruflichen Tätigkeit zu handeln.
        (3) Abweichende oder ergänzende Geschäftsbedingungen des Kunden werden nicht Vertragsbestandteil, auch wenn der Anbieter ihnen nicht ausdrücklich widerspricht.
        (4) Teil A gilt für alle Leistungen. Teil B enthält ergänzende Regelungen für einzelne Leistungsarten; sie gelten nur, soweit der Kunde die jeweilige Leistung beauftragt hat.

        § 2 Vertragsschluss und Rangfolge
        (1) Angebote des Anbieters sind bis zum Ablauf der darin genannten Gültigkeitsdauer verbindlich. Der Vertrag kommt zustande, wenn der Kunde das Angebot innerhalb dieser Frist annimmt, insbesondere durch digitale Unterschrift über den Angebotslink oder durch Erklärung in Textform.
        (2) Bei Widersprüchen gelten in dieser Reihenfolge: individuelle Vereinbarungen im Angebot, ein gesondert abgeschlossener Agenturvertrag, vereinbarte Leistungsbeschreibungen (z. B. Lasten-/Pflichtenheft), Teil B und zuletzt Teil A dieser AGB.
        (3) Entwürfe, Konzepte und Muster, die der Anbieter vor Vertragsschluss vorlegt, bleiben sein geistiges Eigentum. Kommt kein Vertrag zustande, darf der Kunde sie nicht nutzen und hat überlassene Kopien zu löschen.

        § 3 Leistungsumfang, Änderungen, Termine
        (1) Der Umfang der Leistungen ergibt sich abschließend aus dem Angebot und den dort in Bezug genommenen Unterlagen. Nicht ausdrücklich genannte Leistungen sind nicht geschuldet.
        (2) Wünscht der Kunde nach Vertragsschluss Änderungen oder Erweiterungen, prüft der Anbieter deren Auswirkungen auf Aufwand und Termine und unterbreitet hierzu ein Nachtragsangebot. Bis zur Einigung wird auf Grundlage des ursprünglichen Auftrags weitergearbeitet.
        (3) Termine sind nur verbindlich, wenn sie ausdrücklich als verbindlich vereinbart wurden. Verzögert sich die Leistung, weil der Kunde Mitwirkungspflichten nicht rechtzeitig erfüllt, verschieben sich Termine angemessen; dadurch entstehender Mehraufwand kann gesondert berechnet werden.
        (4) Der Anbieter darf zur Leistungserbringung qualifizierte Subunternehmer einsetzen. Er bleibt alleiniger Vertragspartner des Kunden.

        § 4 Mitwirkung des Kunden
        (1) Der Kunde benennt einen Ansprechpartner, der Entscheidungen treffen oder kurzfristig herbeiführen kann.
        (2) Der Kunde stellt alle für die Leistung erforderlichen Informationen, Inhalte (Texte, Bilder, Logos usw.), Zugänge und Freigaben vollständig, rechtzeitig und in geeigneter Form zur Verfügung. Liefert der Kunde Inhalte nicht rechtzeitig, darf der Anbieter Platzhalter oder lizenzierte Stockinhalte verwenden; hierfür anfallende Kosten trägt der Kunde.
        (3) Der Kunde sichert seine Daten in eigener Verantwortung regelmäßig, sofern die Datensicherung nicht ausdrücklich Leistung des Anbieters ist.

        § 5 Abnahme
        (1) Werkleistungen (z. B. Webseiten, Software, Schnittstellen, Apps, Designs) stellt der Anbieter nach Fertigstellung zur Abnahme bereit. In sich abgeschlossene Teilleistungen können gesondert abgenommen werden.
        (2) Der Kunde prüft die Leistung innerhalb von 14 Tagen nach Bereitstellung und erklärt die Abnahme oder teilt wesentliche Mängel in Textform mit. Wegen unwesentlicher Mängel darf die Abnahme nicht verweigert werden.
        (3) Die Leistung gilt als abgenommen, wenn der Kunde innerhalb der Frist weder die Abnahme erklärt noch wesentliche Mängel benennt; der Anbieter weist bei der Aufforderung zur Abnahme auf diese Folge hin. Gleiches gilt, wenn der Kunde die Leistung produktiv nutzt, etwa eine Webseite oder Software veröffentlicht.

        § 6 Vergütung und Zahlung
        (1) Es gelten die im Angebot genannten Preise. Soweit Umsatzsteuer anfällt, wird sie zusätzlich in gesetzlicher Höhe berechnet.
        (2) Der Anbieter darf für bereits erbrachte Teilleistungen angemessene Abschlagszahlungen verlangen (§ 632a BGB). Wiederkehrende Vergütungen sind zu Beginn des jeweiligen Abrechnungszeitraums fällig.
        (3) Rechnungen sind innerhalb von 14 Tagen nach Zugang ohne Abzug zahlbar. Hat der Kunde ein SEPA-Lastschriftmandat erteilt, kündigt der Anbieter Betrag und Fälligkeit mindestens fünf Kalendertage vor dem Einzug an. Kosten einer vom Kunden verschuldeten Rücklastschrift trägt der Kunde.
        (4) Bei Zahlungsverzug gelten die gesetzlichen Regelungen (§ 288 Abs. 2 und 5 BGB). Der Anbieter darf weitere Leistungen bis zum Ausgleich fälliger Forderungen zurückhalten, nachdem er dies mit angemessener Frist angekündigt hat.
        (5) Der Kunde kann nur mit unbestrittenen oder rechtskräftig festgestellten Forderungen aufrechnen.
        (6) Bei Dauerschuldverhältnissen kann der Anbieter die Preise mit einer Ankündigungsfrist von sechs Wochen zum Beginn eines Abrechnungszeitraums angemessen anpassen, wenn sich seine Kosten (z. B. Lizenz-, Hosting- oder Personalkosten) erhöht haben. Der Kunde kann den Vertrag in diesem Fall zum Zeitpunkt der Preisänderung kündigen.

        § 7 Laufzeit und Kündigung von Dauerschuldverhältnissen
        (1) Verträge über fortlaufende Leistungen (z. B. Wartung, Hosting, Marketing-Betreuung) laufen auf unbestimmte Zeit, sofern das Angebot keine Mindestlaufzeit vorsieht.
        (2) Sie können mit einer Frist von einem Monat zum Monatsende gekündigt werden, frühestens zum Ende einer vereinbarten Mindestlaufzeit. Das Recht zur außerordentlichen Kündigung aus wichtigem Grund bleibt unberührt.
        (3) Kündigungen bedürfen der Textform.

        § 8 Nutzungsrechte
        (1) Mit vollständiger Zahlung der Vergütung erhält der Kunde an den für ihn individuell erstellten Arbeitsergebnissen ein einfaches, zeitlich und räumlich unbeschränktes Nutzungsrecht für den vertraglich vorausgesetzten Zweck. Weitergehende Rechte (z. B. ausschließliche Rechte, Recht zur Weiterveräußerung) bedürfen einer gesonderten Vereinbarung.
        (2) Bis zur vollständigen Zahlung ist die Nutzung nur widerruflich gestattet.
        (3) Vorbestehende Komponenten, Werkzeuge und Bibliotheken des Anbieters sowie Open-Source- und Drittkomponenten werden nur im Rahmen ihrer jeweiligen Lizenzen überlassen. Ein Anspruch auf Herausgabe des Quellcodes besteht nur, wenn er vereinbart ist.
        (4) Der Anbieter darf das Projekt in angemessener Form als Referenz nennen und darstellen (z. B. Name, Logo, Screenshots), sofern der Kunde dem nicht in Textform widerspricht.

        § 9 Inhalte des Kunden
        (1) Der Kunde ist für die von ihm gelieferten oder freigegebenen Inhalte sowie für die Rechtmäßigkeit seines Angebots und seines Geschäftsmodells verantwortlich. Er versichert, über die erforderlichen Rechte an gelieferten Inhalten zu verfügen.
        (2) Der Anbieter erbringt keine Rechtsberatung und prüft Inhalte, Werbeaussagen, Marken und Domains nicht auf ihre rechtliche Zulässigkeit.
        (3) Der Kunde stellt den Anbieter von Ansprüchen Dritter frei, die auf vom Kunden gelieferten Inhalten oder seinen Weisungen beruhen, einschließlich angemessener Kosten der Rechtsverteidigung.

        § 10 Mängelrechte
        (1) Bei Werkleistungen leistet der Anbieter bei Mängeln nach seiner Wahl Nacherfüllung durch Nachbesserung oder Neuherstellung. Schlägt die Nacherfüllung fehl, stehen dem Kunden die gesetzlichen Rechte zu.
        (2) Mängel sind unverzüglich nach ihrer Entdeckung in Textform und so genau wie möglich zu beschreiben.
        (3) Mängelansprüche verjähren in einem Jahr ab Abnahme. Dies gilt nicht bei Vorsatz, grober Fahrlässigkeit, Arglist, der Verletzung von Leben, Körper oder Gesundheit sowie bei Ansprüchen nach dem Produkthaftungsgesetz.
        (4) Keine Mängel sind Abweichungen, die auf Änderungen durch den Kunden oder Dritte, auf nicht freigegebene Systemumgebungen oder auf Änderungen von Diensten Dritter nach der Abnahme zurückgehen.

        § 11 Haftung
        (1) Der Anbieter haftet unbeschränkt bei Vorsatz und grober Fahrlässigkeit, bei der Verletzung von Leben, Körper oder Gesundheit, nach dem Produkthaftungsgesetz und im Umfang übernommener Garantien.
        (2) Bei leicht fahrlässiger Verletzung wesentlicher Vertragspflichten, deren Erfüllung die ordnungsgemäße Durchführung des Vertrags erst ermöglicht und auf deren Einhaltung der Kunde regelmäßig vertrauen darf, ist die Haftung auf den vertragstypischen, bei Vertragsschluss vorhersehbaren Schaden begrenzt. Im Übrigen ist die Haftung für leichte Fahrlässigkeit ausgeschlossen.
        (3) Für Datenverlust haftet der Anbieter nur in Höhe des Aufwands, der bei ordnungsgemäßer Datensicherung durch den Kunden zur Wiederherstellung erforderlich gewesen wäre.
        (4) Die vorstehenden Regelungen gelten auch zugunsten der Mitarbeiter, Erfüllungsgehilfen und Subunternehmer des Anbieters.

        § 12 Datenschutz
        Soweit der Anbieter im Auftrag des Kunden personenbezogene Daten verarbeitet (z. B. bei Hosting, Wartung oder Support mit Datenzugriff), schließen die Parteien vor Beginn dieser Leistungen einen Vertrag über die Auftragsverarbeitung nach Art. 28 DSGVO. Der Anbieter stellt hierfür ein Muster zur Verfügung.

        § 13 Vertraulichkeit
        Beide Parteien behandeln alle nicht öffentlich bekannten Informationen der anderen Partei, die ihnen im Rahmen der Zusammenarbeit bekannt werden, vertraulich und nutzen sie nur zur Vertragsdurchführung. Diese Pflicht besteht auch nach Vertragsende fort. Der Anbieter verpflichtet eingesetzte Mitarbeiter und Subunternehmer entsprechend.

        § 14 Änderungen dieser AGB
        Der Anbieter kann diese AGB für bestehende Dauerschuldverhältnisse aus sachlichem Grund (z. B. Gesetzes- oder Rechtsprechungsänderungen, neue Leistungen) ändern. Er teilt die Änderungen mindestens sechs Wochen vor ihrem Inkrafttreten in Textform mit. Widerspricht der Kunde nicht innerhalb dieser Frist, gelten die Änderungen als angenommen; auf diese Folge weist der Anbieter in der Mitteilung hin. Widerspricht der Kunde, können beide Parteien den Vertrag zum Inkrafttreten der Änderungen kündigen. Wesentliche Leistungs- und Preisbestandteile werden auf diesem Weg nicht geändert.

        § 15 Schlussbestimmungen
        (1) Änderungen und Ergänzungen des Vertrags bedürfen der Textform.
        (2) Es gilt das Recht der Bundesrepublik Deutschland unter Ausschluss des UN-Kaufrechts.
        (3) Ist der Kunde Kaufmann, juristische Person des öffentlichen Rechts oder öffentlich-rechtliches Sondervermögen, ist ausschließlicher Gerichtsstand der Sitz des Anbieters. Der Anbieter darf auch am Sitz des Kunden klagen.
        (4) Sollte eine Bestimmung unwirksam sein, bleibt die Wirksamkeit der übrigen Bestimmungen unberührt.

        TEIL B – BESONDERE BESTIMMUNGEN FÜR EINZELNE LEISTUNGEN

        § 16 Webseiten
        (1) Die Erstellung von Webseiten ist eine Werkleistung. Webseiten werden für die beim Abnahmezeitpunkt aktuellen und die jeweils vorherige Hauptversion gängiger Browser (Chrome, Firefox, Safari, Edge) sowie für mobile Endgeräte optimiert.
        (2) Erstellt der Anbieter Impressum, Datenschutzerklärung oder Cookie-Hinweise, geschieht dies mithilfe von Generatoren oder Vorlagen auf Grundlage der Angaben des Kunden. Die rechtliche Prüfung und Aktualisierung dieser Texte obliegt dem Kunden.
        (3) Maßnahmen zur Barrierefreiheit nach dem Barrierefreiheitsstärkungsgesetz sowie Suchmaschinenplatzierungen sind nur geschuldet, wenn sie ausdrücklich vereinbart sind.

        § 17 Software und Schnittstellen (APIs)
        (1) Die Entwicklung von Software und Schnittstellen ist eine Werkleistung auf Grundlage der vereinbarten Leistungsbeschreibung. Der Anbieter entwickelt nach dem Stand der Technik bei Vertragsschluss für die vereinbarten Systemumgebungen.
        (2) Für Verfügbarkeit, Funktion und Änderungen von Systemen und Schnittstellen Dritter ist der Anbieter nicht verantwortlich. Anpassungen an geänderte Drittsysteme sind gesondert zu beauftragen, sofern sie nicht Teil eines Wartungsvertrags sind.
        (3) Der Kunde stellt Testzugänge, Testdaten und API-Schlüssel bereit und trägt die Kosten der Dienste Dritter.

        § 18 Apps
        (1) § 17 gilt entsprechend. Apps werden für die beim Abnahmezeitpunkt aktuellen und die jeweils vorherige Hauptversion der vereinbarten Betriebssysteme optimiert.
        (2) Die Veröffentlichung in App-Stores erfolgt über Entwicklerkonten des Kunden. Auf Prüfung und Freigabe durch die Store-Betreiber hat der Anbieter keinen Einfluss.

        § 19 Hosting und Domains
        (1) Hosting-Leistungen erbringt der Anbieter über spezialisierte Rechenzentrumsbetreiber. Geschuldet ist die Verfügbarkeit, die diese Betreiber gewährleisten; geplante Wartungsfenster und Störungen außerhalb des Einflussbereichs des Anbieters bleiben außer Betracht.
        (2) Der Anbieter darf Inhalte vorübergehend sperren, wenn konkrete Anhaltspunkte für Rechtsverstöße bestehen oder die Sicherheit der Systeme gefährdet ist. Er informiert den Kunden unverzüglich.
        (3) Domains werden im Namen des Kunden registriert; der Kunde wird Domaininhaber. Der Anbieter schuldet keine Zuteilung der gewünschten Domain und prüft nicht, ob sie Rechte Dritter verletzt. Bei Vertragsende unterstützt er den Umzug zu einem anderen Anbieter.

        § 20 Wartung und Support
        (1) Wartung und Support sind Dienstleistungen. Umfang, Reaktionszeiten und Kontingente ergeben sich aus dem Angebot bzw. dem vereinbarten Service Level.
        (2) Nicht umfasst sind, sofern nicht anders vereinbart, neue Funktionen, Redesigns, Datenmigrationen, Schulungen sowie die Behebung von Störungen, die der Kunde oder Dritte verursacht haben. Pauschale Leistungen gelten im Rahmen einer üblichen, fairen Nutzung.

        § 21 Online-Marketing (SEO, SEA, Social Media, Content)
        (1) Marketingleistungen sind Dienstleistungen. Ein bestimmter Erfolg, etwa Rankings, Reichweite, Klicks oder Umsätze, ist nicht geschuldet.
        (2) Werbebudgets und Gebühren der Plattformen sind nicht in der Vergütung enthalten und werden vom Kunden getragen. Werbekonten werden auf den Kunden angelegt oder an ihn übergeben.
        (3) Inhalte werden vor Veröffentlichung zur Freigabe vorgelegt. Mit der Freigabe übernimmt der Kunde die Verantwortung für die Inhalte.

        § 22 Design
        (1) Für Gestaltungsleistungen sind, sofern nicht anders vereinbart, zwei Korrekturschleifen enthalten. Weitere Änderungen werden gesondert berechnet. Der künstlerische Gestaltungsspielraum des Anbieters ist kein Mangel.
        (2) Druckdaten werden erst nach Druckfreigabe durch den Kunden verwendet. Für Fehler in freigegebenen Fassungen haftet der Anbieter nicht. Farbabweichungen zwischen Bildschirm und Druck sind technisch bedingt.
        (3) Der Kunde prüft selbst, ob für ihn eine Abgabe an die Künstlersozialkasse anfällt.

        § 23 Beratung
        Beratungsleistungen sind Dienstleistungen. Die Entscheidung über die Umsetzung von Empfehlungen trifft der Kunde. Rechts- und Steuerberatung erbringt der Anbieter nicht.
        """;

    // ------------------------------------------------------------------ AV-Vertrag

    record AvSection(string Title, string Content);

    static readonly AvSection[] AvSections =
    {
        new("Parteien", """
            Verantwortlicher im Sinne der DSGVO ist der Kunde (nachfolgend „Auftraggeber“). Auftragsverarbeiter ist Gentle Group, Inhaber Berk-Can Atesoglu, Girardetstraße 17, 42109 Wuppertal (nachfolgend „Auftragnehmer“).

            Dieser Vertrag konkretisiert die datenschutzrechtlichen Pflichten der Parteien aus dem zugrunde liegenden Hauptvertrag (Angebot bzw. Agenturvertrag) gemäß Art. 28 Abs. 3 DSGVO.
            """),
        new("§ 1 Gegenstand, Art und Zweck der Verarbeitung", """
            Der Auftragnehmer verarbeitet personenbezogene Daten ausschließlich im Auftrag und nach Weisung des Auftraggebers, soweit dies zur Erbringung der vereinbarten Leistungen erforderlich ist, insbesondere:
            – Hosting und Betrieb von Webseiten, Webanwendungen und Schnittstellen,
            – Wartung, Updates, Fehlerbehebung und Support mit Zugriff auf Systeme des Auftraggebers,
            – Entwicklung und Test mit Daten des Auftraggebers.

            Die Laufzeit dieses Vertrags entspricht der Laufzeit des Hauptvertrags.
            """),
        new("§ 2 Arten der Daten und Kreise der Betroffenen", """
            Arten der Daten (je nach Leistung): Kontakt- und Stammdaten (Name, Anschrift, E-Mail, Telefon), Inhalte von Formular- und Kommunikationsanfragen, Kunden- und Vertragsdaten, Nutzungs- und Protokolldaten (z. B. IP-Adressen, Zugriffszeiten), Zugangsdaten.

            Betroffene: Kunden, Interessenten, Website- und App-Nutzer, Beschäftigte und Ansprechpartner des Auftraggebers.

            Besondere Kategorien personenbezogener Daten (Art. 9 DSGVO) werden nur verarbeitet, wenn dies gesondert vereinbart ist.

            Ergänzungen für dieses Projekt: [bitte vor Versand ergänzen oder streichen]
            """),
        new("§ 3 Weisungen", """
            Der Auftragnehmer verarbeitet die Daten nur auf dokumentierte Weisung des Auftraggebers, auch hinsichtlich der Übermittlung in Drittländer, es sei denn, er ist gesetzlich zur Verarbeitung verpflichtet; in diesem Fall teilt er dem Auftraggeber diese Verpflichtung vorab mit, sofern das Gesetz dies nicht verbietet.

            Weisungen erfolgen in Textform. Ist der Auftragnehmer der Ansicht, dass eine Weisung gegen Datenschutzvorschriften verstößt, weist er den Auftraggeber unverzüglich darauf hin und darf die Ausführung bis zur Bestätigung oder Änderung aussetzen.
            """),
        new("§ 4 Pflichten des Auftragnehmers", """
            Der Auftragnehmer
            – verpflichtet alle Personen, die Zugang zu den Daten haben, zur Vertraulichkeit,
            – trifft die technischen und organisatorischen Maßnahmen nach Art. 32 DSGVO gemäß Anlage 1 und passt sie dem Stand der Technik an, ohne das Schutzniveau zu unterschreiten,
            – unterstützt den Auftraggeber im Rahmen seiner Möglichkeiten bei der Erfüllung der Rechte der Betroffenen (Art. 12–22 DSGVO) sowie bei den Pflichten nach Art. 32–36 DSGVO,
            – meldet dem Auftraggeber Verletzungen des Schutzes personenbezogener Daten unverzüglich, möglichst innerhalb von 24 Stunden nach Kenntnis, mit den ihm vorliegenden Informationen,
            – stellt dem Auftraggeber alle erforderlichen Informationen zum Nachweis der Einhaltung dieser Pflichten zur Verfügung.
            """),
        new("§ 5 Unterauftragsverhältnisse", """
            Der Auftraggeber genehmigt den Einsatz der in Anlage 2 genannten Unterauftragsverarbeiter. Der Auftragnehmer informiert den Auftraggeber vorab in Textform über die beabsichtigte Hinzuziehung oder Ersetzung weiterer Unterauftragsverarbeiter. Der Auftraggeber kann innerhalb von 14 Tagen aus wichtigem datenschutzrechtlichem Grund widersprechen; erfolgt kein Widerspruch, gilt die Änderung als genehmigt.

            Der Auftragnehmer legt Unterauftragsverarbeitern vertraglich dieselben Datenschutzpflichten auf. Erfolgt eine Verarbeitung außerhalb der EU/des EWR, stellt der Auftragnehmer sicher, dass die Voraussetzungen der Art. 44 ff. DSGVO erfüllt sind (z. B. Angemessenheitsbeschluss oder Standardvertragsklauseln).
            """),
        new("§ 6 Kontrollrechte des Auftraggebers", """
            Der Auftraggeber kann sich nach rechtzeitiger Anmeldung, in der Regel mit einer Frist von 14 Tagen, während der üblichen Geschäftszeiten von der Einhaltung dieses Vertrags überzeugen. Der Nachweis kann auch durch aktuelle Zertifikate, Berichte oder Selbstauskünfte erbracht werden. Kontrollen dürfen den Geschäftsbetrieb nicht unverhältnismäßig stören. Der Auftragnehmer kann für Kontrollen, die über eine Kontrolle pro Jahr hinausgehen, eine angemessene Vergütung verlangen.
            """),
        new("§ 7 Löschung und Rückgabe", """
            Nach Ende der Leistungserbringung löscht der Auftragnehmer nach Wahl des Auftraggebers alle personenbezogenen Daten oder gibt sie zurück, sofern keine gesetzliche Pflicht zur Speicherung besteht. Datensicherungen werden im Rahmen der regulären Rotation, spätestens nach 90 Tagen, gelöscht. Die Löschung wird auf Verlangen in Textform bestätigt.
            """),
        new("§ 8 Haftung und Schlussbestimmungen", """
            Für die Haftung gilt Art. 82 DSGVO. Im Übrigen gelten die Haftungsregelungen des Hauptvertrags.

            Bei Widersprüchen gehen die Regelungen dieses Vertrags denen des Hauptvertrags vor, soweit es um den Schutz personenbezogener Daten geht. Änderungen bedürfen der Textform. Sollte eine Bestimmung unwirksam sein, bleibt der Vertrag im Übrigen wirksam.
            """),
        new("Anlage 1 – Technische und organisatorische Maßnahmen (Art. 32 DSGVO)", """
            Vertraulichkeit
            – Zugang zu Systemen nur über persönliche Konten mit starken Passwörtern und, wo verfügbar, Zwei-Faktor-Authentifizierung
            – Rollen- und Berechtigungskonzept nach dem Minimalprinzip; Zugänge werden bei Projektende entzogen
            – Verschlüsselte Übertragung (TLS/HTTPS, SFTP/FTPS), verschlüsselte Speicherung von Endgeräten
            – Zugangsdaten des Auftraggebers werden ausschließlich in einem Passwortmanager gespeichert
            – Getrennte Verarbeitung von Daten verschiedener Auftraggeber (Mandantentrennung)

            Integrität
            – Protokollierung administrativer Zugriffe, soweit die eingesetzten Systeme dies unterstützen
            – Versionsverwaltung des Quellcodes; Änderungen an Produktivsystemen nur nach Test

            Verfügbarkeit und Belastbarkeit
            – Regelmäßige Datensicherungen beim Hosting-Dienstleister gemäß dessen Leistungsbeschreibung
            – Zeitnahes Einspielen von Sicherheitsupdates, Überwachung der Erreichbarkeit
            – Rechenzentren der Unterauftragsverarbeiter mit physischer Zutrittskontrolle, Brandschutz und Notstromversorgung

            Überprüfung
            – Regelmäßige Überprüfung der Maßnahmen und Anpassung an den Stand der Technik
            – Verpflichtung aller Beschäftigten und Subunternehmer auf Vertraulichkeit und Datenschutz
            """),
        new("Anlage 2 – Genehmigte Unterauftragsverarbeiter", """
            [bitte prüfen: nicht genutzte Dienste streichen, fehlende ergänzen, dann diese Zeile löschen]

            – MonsterASP.NET (Hosting von Anwendungen und Datenbanken), Rechenzentrum in der EU
            – Vercel Inc. (Hosting von Web-Frontends), Standardvertragsklauseln bzw. EU-US Data Privacy Framework
            – Mollie B.V., Amsterdam (Zahlungsabwicklung, sofern Zahlungen über die Anwendung abgewickelt werden)
            – E-Mail-Hosting des Auftragnehmers (Kommunikation und Support)
            – Weitere: [bitte ergänzen]
            """),
    };
}
