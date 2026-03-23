using GentleSuite.Domain.Enums;

namespace GentleSuite.Domain.Entities;

public class OnboardingWorkflowTemplate : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsDefault { get; set; }
    public List<OnboardingStepTemplate> Steps { get; set; } = new();
}

public class OnboardingStepTemplate : BaseEntity
{
    public Guid WorkflowTemplateId { get; set; }
    public OnboardingWorkflowTemplate WorkflowTemplate { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public int DefaultDurationDays { get; set; } = 3;
    public List<TaskItemTemplate> TaskTemplates { get; set; } = new();
}

public class TaskItemTemplate : BaseEntity
{
    public Guid StepTemplateId { get; set; }
    public OnboardingStepTemplate StepTemplate { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public string? DefaultAssigneeRole { get; set; }
}

public class OnboardingWorkflow : BaseEntity
{
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }
    public Guid TemplateId { get; set; }
    public OnboardingWorkflowTemplate Template { get; set; } = null!;
    public OnboardingStatus Status { get; set; } = OnboardingStatus.NotStarted;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<OnboardingStep> Steps { get; set; } = new();
}

public class OnboardingStep : BaseEntity
{
    public Guid WorkflowId { get; set; }
    public OnboardingWorkflow Workflow { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public OnboardingStepStatus Status { get; set; } = OnboardingStepStatus.Pending;
    public DateTimeOffset? DueDate { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? AssigneeId { get; set; }
    public List<TaskItem> Tasks { get; set; } = new();
}

public class TaskItem : BaseEntity
{
    public Guid StepId { get; set; }
    public OnboardingStep Step { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public TaskItemStatus Status { get; set; } = TaskItemStatus.Open;
    public DateTimeOffset? DueDate { get; set; }
    public string? AssigneeId { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
