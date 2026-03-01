using GentleSuite.Domain.Enums;

namespace GentleSuite.Domain.Entities;

public class Project : BaseEntity
{
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public Guid? QuoteId { get; set; }
    public Quote? Quote { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.Planning;
    public DateTimeOffset? StartDate { get; set; }
    public DateTimeOffset? DueDate { get; set; }
    public string? ManagerId { get; set; }
    public List<ProjectTeamMember> TeamMembers { get; set; } = new();
    public List<Milestone> Milestones { get; set; } = new();
    public List<ProjectComment> Comments { get; set; } = new();
    public List<ProjectBoardTask> BoardTasks { get; set; } = new();
    public List<OnboardingWorkflow> OnboardingWorkflows { get; set; } = new();
}

public class ProjectTeamMember
{
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public Guid TeamMemberId { get; set; }
    public TeamMember TeamMember { get; set; } = null!;
}

public class Milestone : BaseEntity
{
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset? DueDate { get; set; }
    public bool IsCompleted { get; set; }
}

public class ProjectComment : BaseEntity
{
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public string Content { get; set; } = string.Empty;
    public string? AuthorId { get; set; }
    public string? AuthorName { get; set; }
}

public class ProjectBoardTask : BaseEntity
{
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ProjectBoardTaskStatus Status { get; set; } = ProjectBoardTaskStatus.Todo;
    public int SortOrder { get; set; }
    public string? AssigneeName { get; set; }
    public DateTimeOffset? DueDate { get; set; }
}

public class TimeEntry : BaseEntity
{
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }
    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset Date { get; set; }
    public decimal Hours { get; set; }
    public bool IsBillable { get; set; } = true;
    public bool IsInvoiced { get; set; }
    public Guid? InvoiceId { get; set; }
    public decimal? HourlyRate { get; set; }
}
