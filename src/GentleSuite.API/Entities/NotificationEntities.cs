namespace GentleSuite.Domain.Entities;

/// <summary>In-App-Benachrichtigung je Login (Glocke oben rechts). Kind: quote-accepted, payment-received, chargeback, ticket-created.</summary>
public class AppNotification : BaseEntity
{
    public string UserId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Body { get; set; }
    public string? Href { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}
