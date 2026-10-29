using GentleSuite.Domain.Enums;

namespace GentleSuite.Domain.Entities;

/// <summary>Aufwandsschätzung / Ersteinschätzung ("01 — Aufwandsschätzung" template). Not GoBD-relevant:
/// a non-binding estimate with fixed-price ranges (Pauschalen) only, no hourly rates. List-like content
/// (Grundlage, Module, Risiken, Zeitplan, …) lives in <see cref="ContentJson"/> to avoid many child tables.</summary>
public class Estimate : BaseEntity
{
    /// <summary>e.g. SC-2026-001. Kept identical across versions created via "Duplizieren / neue Version".</summary>
    public string EstimateNumber { get; set; } = string.Empty;
    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    /// <summary>Overrides the customer's company name in "Erstellt für …" (or names a prospect without a customer record).</summary>
    public string? CustomerDisplayName { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public string Version { get; set; } = "1.0";
    public DateTimeOffset Date { get; set; } = DateTimeOffset.UtcNow;
    public string? PreparedBy { get; set; }
    public string? ApprovedBy { get; set; }
    public string? Responsible { get; set; }
    /// <summary>intern / vertraulich / öffentlich</summary>
    public string Classification { get; set; } = "vertraulich";
    public EstimateStatus Status { get; set; } = EstimateStatus.Draft;

    /// <summary>Optional Zielpreis/Festpreis (netto).</summary>
    public decimal? FixedPrice { get; set; }
    /// <summary>Label of the fixed-price line, e.g. "Zielpreis/Festpreis".</summary>
    public string? FixedPriceLabel { get; set; }

    /// <summary>01 — Ausgangslage, free text.</summary>
    public string? SituationText { get; set; }
    /// <summary>02 — Vorschlag Architektur, Umsetzung und Pilot, free text.</summary>
    public string? ProposalText { get; set; }
    /// <summary>03 — Hinweis unter der Kostenübersicht.</summary>
    public string? CostNote { get; set; }
    /// <summary>03 — "Betrieb" line in the cost overview (free text, e.g. "ab 149 € / Monat, separates Angebot").</summary>
    public string? OperationText { get; set; }
    /// <summary>06 — Hinweis unter dem Zeitplan.</summary>
    public string? TimelineNote { get; set; }

    /// <summary>JSON of EstimateContentDto: Basis, Decisions, Architecture, Implementation, Pilot, Modules, Extensions,
    /// Assumptions, Exclusions, Risks, OpenQuestions, Timeline, NextSteps.</summary>
    public string ContentJson { get; set; } = "{}";
}
