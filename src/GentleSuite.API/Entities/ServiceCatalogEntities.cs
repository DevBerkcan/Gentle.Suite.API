namespace GentleSuite.Domain.Entities;

public class ServiceCategory : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public List<ServiceCatalogItem> Items { get; set; } = new();
}

public class ServiceCatalogItem : BaseEntity
{
    public Guid CategoryId { get; set; }
    public ServiceCategory Category { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ShortCode { get; set; }
    public decimal? DefaultPrice { get; set; }
    public Enums.QuoteLineType DefaultLineType { get; set; } = Enums.QuoteLineType.OneTime;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}
