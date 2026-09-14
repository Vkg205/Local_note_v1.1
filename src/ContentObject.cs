namespace LocalNote.Domain.Entities;

public enum ContentObjectType
{
    Text,
    Image,
    Attachment,
    Table,
    Shape
}

public sealed class ContentObject
{
    public required string Id { get; init; }
    public required string PageId { get; init; }
    public ContentObjectType Type { get; init; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public int ZIndex { get; set; }
    public string Payload { get; set; } = string.Empty;
    public string StyleJson { get; set; } = "{}";
    public bool IsTodo { get; set; }
    public bool TodoCompleted { get; set; }
    public bool IsImportant { get; set; }
    public string SearchText { get; set; } = string.Empty;
    public int LocalVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}
