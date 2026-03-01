using GentleSuite.Domain.Entities;

namespace GentleSuite.Domain.Entities;

public class TeamMember : BaseEntity
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string FullName => $"{FirstName} {LastName}";
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? JobTitle { get; set; }
    public bool IsActive { get; set; } = true;
    public string? AppUserId { get; set; } // optional link to AppUser (system login)
    public List<ProductTeamMember> ProductAssignments { get; set; } = new();
}

public class Product : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Unit { get; set; } = "h";
    public decimal DefaultPrice { get; set; }
    public bool IsActive { get; set; } = true;
    public List<ProductTeamMember> TeamMembers { get; set; } = new();
    public List<PriceListItem> PriceListItems { get; set; } = new();
}

public class ProductTeamMember
{
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public Guid TeamMemberId { get; set; }
    public TeamMember TeamMember { get; set; } = null!;
}

public class PriceList : BaseEntity
{
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public List<PriceListItem> Items { get; set; } = new();
}

public class PriceListItem : BaseEntity
{
    public Guid PriceListId { get; set; }
    public PriceList PriceList { get; set; } = null!;
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public Guid? TeamMemberId { get; set; }
    public TeamMember? TeamMember { get; set; }
    public decimal CustomPrice { get; set; }
    public string? Note { get; set; }
    public int SortOrder { get; set; }
}
