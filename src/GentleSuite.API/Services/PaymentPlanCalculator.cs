using GentleSuite.Application.DTOs;

namespace GentleSuite.Infrastructure.Services;

/// <summary>Shared Preisangebot math (Einmalzahlung/Hybrid/Monatlich 12/24), used by both the
/// admin/customer-facing quote resolution (QuoteService) and the Angebots-PDF (PdfService) so the
/// numbers a customer sees on screen and in the PDF are always computed identically.</summary>
public static class PaymentPlanCalculator
{
    public static List<PaymentPlanOptionResolvedDto> Resolve(PaymentPlanConfigDto cfg, decimal projectPrice)
    {
        var hybridDown = Math.Round(cfg.Hybrid.TotalAmount * cfg.Hybrid.DownPaymentPercent / 100m, 2);
        var hybridFinanced = cfg.Hybrid.TotalAmount - hybridDown;
        var hybridMonthly = cfg.Hybrid.DurationMonths > 0 ? Math.Round(hybridFinanced / cfg.Hybrid.DurationMonths, 2) : 0m;
        var m12Monthly = Math.Round(cfg.Monthly12.TotalAmount / 12, 2);
        var m24Monthly = Math.Round(cfg.Monthly24.TotalAmount / 24, 2);

        return new List<PaymentPlanOptionResolvedDto>
        {
            new("onetime", "Einmalzahlung", "100% des Projektpreises, keine Rate", null, null, projectPrice, null),
            new("hybrid", "Hybrid-Modell", $"Anzahlung {cfg.Hybrid.DownPaymentPercent:0.#}% · Rest in {cfg.Hybrid.DurationMonths} Raten",
                hybridDown, hybridMonthly, cfg.Hybrid.TotalAmount, cfg.Hybrid.DurationMonths),
            new("monthly12", "Monatlich 12 Monate", "0 € Anzahlung · in 12 Monatsraten",
                0m, m12Monthly, cfg.Monthly12.TotalAmount, 12),
            new("monthly24", "Monatlich 24 Monate", "0 € Anzahlung · in 24 Monatsraten",
                0m, m24Monthly, cfg.Monthly24.TotalAmount, 24),
        };
    }
}
