using GentleSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GentleSuite.Infrastructure.Data;

/// <summary>
/// Replaces the original starter clause blocks (whose wording followed a third-party generator) with GentleSuite's own
/// formulations, so contracts no longer depend on Legal Cockpit. A block or template is only rewritten while its text
/// is still exactly the original starter text (compared by hash of the whitespace-normalised content) — anything the
/// user edited stays untouched, and already concluded contracts keep their own frozen sections anyway.
/// </summary>
public static class ClauseRewriteSeed
{
    record Rewrite(string OldHash, string Title, string Content);

    static readonly Dictionary<string, Rewrite> Blocks = new()
    {
        ["geltungsbereich"] = new("8deb713a773f27ff", "Gegenstand des Vertrags", """
            Dieser Vertrag regelt die Zusammenarbeit zwischen Anbieter und Kunde für die nachfolgend unter „Spezifizierte Leistungen“ beschriebenen Leistungen. Ergänzend gelten das zugrunde liegende Angebot und die Allgemeinen Geschäftsbedingungen des Anbieters. Bei Widersprüchen geht dieser Vertrag den AGB vor.
            """),
        ["vertragsschluss"] = new("fedceda5a30cb2bd", "Zustandekommen", """
            Der Vertrag wird wirksam, sobald beide Parteien ihn unterzeichnet haben; die elektronische Unterschrift über das Kundenportal genügt. Unterlagen, Entwürfe und Konzepte, die vor Vertragsschluss übergeben wurden, darf der Kunde nur nutzen, wenn der Vertrag zustande kommt.
            """),
        ["mitwirkung"] = new("9c3fe280b0598e89", "Mitwirkung des Kunden", """
            Der Kunde liefert alle Informationen, Inhalte (z. B. Texte, Bilder, Logos), Zugänge und Freigaben, die für die Leistung benötigt werden, vollständig und rechtzeitig. Für die Rechtmäßigkeit dieser Inhalte ist der Kunde verantwortlich; der Anbieter prüft sie nicht auf Rechte Dritter.

            Kommt der Kunde seiner Mitwirkung nicht oder verspätet nach, verschieben sich vereinbarte Termine entsprechend. Dadurch entstehenden Mehraufwand kann der Anbieter gesondert berechnen.
            """),
        ["vertraulichkeit"] = new("01e2ef6ad1ab64ad", "Vertraulichkeit", """
            Die Parteien behandeln alle nicht öffentlichen Informationen, die sie im Rahmen der Zusammenarbeit voneinander erhalten, vertraulich und verwenden sie nur zur Durchführung dieses Vertrags. Der Anbieter verpflichtet seine Mitarbeiter und Subunternehmer entsprechend. Die Pflicht gilt auch nach Ende des Vertrags fort.
            """),
        ["sonstiges"] = new("78166b92b182fe49", "Schlussbestimmungen", """
            Änderungen und Ergänzungen dieses Vertrags bedürfen der Textform. Es gilt das Recht der Bundesrepublik Deutschland unter Ausschluss des UN-Kaufrechts. Ist der Kunde Kaufmann, juristische Person des öffentlichen Rechts oder öffentlich-rechtliches Sondervermögen, ist Gerichtsstand der Sitz des Anbieters.

            Ist eine Bestimmung dieses Vertrags unwirksam, bleiben die übrigen Bestimmungen wirksam.
            """),
        ["abnahme"] = new("edaf2c25685d7493", "Abnahme", """
            Nach Fertigstellung stellt der Anbieter die Leistung zur Abnahme bereit. Der Kunde prüft sie innerhalb von 14 Tagen und erklärt die Abnahme oder benennt wesentliche Mängel in Textform. Unwesentliche Mängel berechtigen nicht zur Verweigerung der Abnahme.

            Äußert sich der Kunde innerhalb der Frist nicht, gilt die Leistung als abgenommen; der Anbieter weist bei der Aufforderung zur Abnahme darauf hin. Die produktive Nutzung der Leistung steht der Abnahme gleich.
            """),
        ["maengel"] = new("d73a737aaba2146c", "Mängel", """
            Bei Mängeln bessert der Anbieter nach oder stellt die Leistung neu her; die Wahl liegt beim Anbieter. Gelingt dies nicht, gelten die gesetzlichen Rechte. Mängel sind unverzüglich in Textform zu melden.

            Ansprüche wegen Mängeln verjähren ein Jahr nach der Abnahme. Das gilt nicht bei Vorsatz, grober Fahrlässigkeit, Arglist sowie bei Schäden an Leben, Körper oder Gesundheit.
            """),
        ["rechteeinraumung"] = new("2d4f7fdcab735e13", "Nutzungsrechte", """
            Sobald die Vergütung vollständig bezahlt ist, erhält der Kunde an den für ihn erstellten Arbeitsergebnissen ein einfaches, zeitlich und räumlich unbeschränktes Nutzungsrecht für den vereinbarten Zweck. Bis dahin ist die Nutzung nur widerruflich erlaubt. Weitergehende Rechte, etwa ein ausschließliches Nutzungsrecht, werden gesondert vereinbart.
            """),
        ["ansprechpartner"] = new("da188281fedf3e15", "Ansprechpartner", """
            Jede Partei benennt eine Ansprechperson, die für alle Fragen zu diesem Vertrag zuständig ist und Entscheidungen treffen oder kurzfristig herbeiführen kann.
            """),
        ["av-vertrag"] = new("d504109dc5b7f160", "Datenschutz und Auftragsverarbeitung", """
            Verarbeitet der Anbieter bei der Leistungserbringung personenbezogene Daten im Auftrag des Kunden, schließen die Parteien vor Beginn dieser Leistungen einen Vertrag über die Auftragsverarbeitung nach Art. 28 DSGVO. Der Anbieter stellt hierfür ein Muster bereit.
            """),
        ["eigenwerbung"] = new("a04364f922c0399b", "Referenznennung", """
            Der Anbieter darf das Projekt in angemessener Form als Referenz nennen und zeigen, zum Beispiel mit Name, Logo und Screenshots auf seiner Website. Der Kunde kann dem jederzeit in Textform widersprechen.
            """),
        ["webseiten-erstellung"] = new("973680a28bd3a69d", "Spezifizierte Leistungen: Erstellung von Webseiten", """
            Der Anbieter erstellt für den Kunden eine Website nach den im Angebot beschriebenen Vorgaben. Es handelt sich um eine Werkleistung (§§ 631 ff. BGB).

            Die Website wird für Smartphones, Tablets und Desktop sowie für die zum Abnahmezeitpunkt aktuellen und jeweils vorherigen Hauptversionen von Chrome, Firefox, Safari und Edge optimiert. Nach Fertigstellung fordert der Anbieter den Kunden zur Abnahme auf.
            """),
        ["webseiten-impressum"] = new("6d76be33d5357c7c", "Spezifizierte Leistungen: Impressum und Datenschutzerklärung", """
            Der Anbieter erstellt Impressum und Datenschutzerklärung für die Website mithilfe eines Generators auf Grundlage der Angaben des Kunden und bindet sie ein. Geschuldet ist allein die Erstellung mit dem Generator; eine Rechtsberatung erbringt der Anbieter nicht und darf er nicht erbringen.

            Der Kunde prüft die Texte vor Veröffentlichung, ist für ihre Richtigkeit verantwortlich und teilt Änderungen seiner Angaben unverzüglich mit. Spätere Aktualisierungen sind gesondert zu beauftragen, sofern sie nicht Teil eines Wartungsvertrags sind.
            """),
        ["design-logo"] = new("08ed6656d3ea0e5d", "Spezifizierte Leistungen: Logodesign", """
            Der Anbieter entwirft ein Logo nach dem Briefing des Kunden. Enthalten sind, sofern das Angebot nichts anderes vorsieht, zwei Korrekturschleifen; weitere Änderungen werden gesondert berechnet. Der gestalterische Spielraum des Anbieters ist kein Mangel.

            Der Anbieter prüft nicht, ob das Logo als Marke eintragungsfähig ist oder Rechte Dritter verletzt; diese Prüfung obliegt dem Kunden. Nicht ausgewählte Entwürfe darf der Kunde nicht verwenden.
            """),
        ["marketing-seo"] = new("53aaa32aeac16163", "Spezifizierte Leistungen: Suchmaschinenoptimierung (SEO)", """
            Der Anbieter führt die im Angebot vereinbarten Maßnahmen zur Verbesserung der Auffindbarkeit in Suchmaschinen durch. Es handelt sich um eine Dienstleistung (§§ 611 ff. BGB). Bestimmte Platzierungen, Besucherzahlen oder Anfragen sind nicht geschuldet, da sie von den Suchmaschinenbetreibern und vom Wettbewerb abhängen.
            """),
        ["marketing-social"] = new("9e5ad11566b1fbd0", "Spezifizierte Leistungen: Social-Media-Marketing", """
            Der Anbieter betreut die vereinbarten Social-Media-Kanäle im Umfang des Angebots, zum Beispiel durch Planung und Erstellung von Beiträgen oder die Steuerung von Kampagnen. Es handelt sich um eine Dienstleistung (§§ 611 ff. BGB); Reichweiten, Followerzahlen oder Interaktionen sind nicht geschuldet. Werbebudgets der Plattformen trägt der Kunde.
            """),
    };

    /// <summary>Starter templates and how to rebuild them from the (rewritten) blocks.</summary>
    static readonly (string Key, string OldHash, string[] Blocks, bool Ongoing)[] Templates =
    {
        ("wartungsvertrag", "f6c799049f6ce98a", new[] { "wartung-leistung", "wartung-sla" }, true),
        ("seo-vertrag", "712e27e92a68c61b", new[] { "marketing-seo" }, true),
        ("webdesign-vertrag", "16bc946009a2a089", new[] { "webseiten-erstellung", "webseiten-impressum", "design-logo" }, false),
        // Module templates of the previous release were assembled from the old core blocks — rebuild them as well.
        ("softwareentwicklungsvertrag", "98627fbfd33c84f3", new[] { "software-entwicklung", "software-quellcode" }, false),
        ("api-integrationsvertrag", "acd834f27caa568c", new[] { "software-api", "software-quellcode" }, false),
        ("app-entwicklungsvertrag", "471a2f35da90fefe", new[] { "software-app", "software-quellcode" }, false),
        ("wartungsvertrag-software", "4bf3de198c0f0aed", new[] { "wartung-software", "wartung-sla" }, true),
        ("hosting-vertrag", "51b5fca4cadf8c4e", new[] { "webseiten-hosting", "webseiten-domain" }, true),
        ("beratungsvertrag", "b4d6328f99f42a57", new[] { "beratung-consulting" }, false),
    };

    /// <summary>First 16 hex chars of SHA-256 over the whitespace-normalised text.</summary>
    public static string Hash(string text)
    {
        var norm = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(norm))).ToLowerInvariant()[..16];
    }

    record TemplateSection(string Title, string Content);

    public static async Task EnsureAsync(AppDbContext db)
    {
        var blocks = await db.ContractClauseBlocks.Where(b => Blocks.Keys.Contains(b.Key)).ToListAsync();
        foreach (var b in blocks)
        {
            var r = Blocks[b.Key];
            if (Hash(b.Content) != r.OldHash) continue; // edited by the user (or already rewritten) → keep
            b.Title = r.Title;
            b.Content = r.Content.Trim();
            b.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();

        var keys = Templates.Select(t => t.Key).ToList();
        var templates = await db.ContractTemplates.Where(t => keys.Contains(t.Key)).ToListAsync();
        if (templates.Count == 0) return;
        var activeBlocks = await db.ContractClauseBlocks.Where(b => b.IsActive).ToListAsync();
        foreach (var t in templates)
        {
            var def = Templates.First(x => x.Key == t.Key);
            var sections = System.Text.Json.JsonSerializer.Deserialize<List<TemplateSection>>(t.SectionsJson) ?? new();
            if (Hash(string.Join(" || ", sections.Select(s => s.Title + " | " + s.Content))) != def.OldHash) continue;
            t.SectionsJson = ContractModuleSeed.BuildSectionsJson(activeBlocks, def.Blocks, def.Ongoing);
            t.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();
    }
}
