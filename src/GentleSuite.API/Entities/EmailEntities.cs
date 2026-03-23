using GentleSuite.Domain.Enums;

namespace GentleSuite.Domain.Entities;

public class EmailTemplate : BaseEntity
{
    public string Key { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? VariablesSchema { get; set; }
    public bool IsActive { get; set; } = true;
}

public class EmailLog : BaseEntity
{
    public string To { get; set; } = string.Empty;
    public string? Cc { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public EmailStatus Status { get; set; } = EmailStatus.Queued;
    public string? Error { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string? TemplateKey { get; set; }
    public Guid? CustomerId { get; set; }
    public string? AttachmentPaths { get; set; }
}

public class FileUpload : BaseEntity
{
    public string FileName { get; set; } = string.Empty;
    public string StoragePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }
}
