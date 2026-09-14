namespace LocalNote.Domain.Entities;

public sealed class Page
{
    public required string Id { get; init; }
    public required string SectionId { get; init; }
    public string? ParentPageId { get; set; }
    public required string Title { get; set; }
    public int IndentLevel { get; set; }
    public int SortOrder { get; set; }
    public bool IsPinned { get; set; }
    public bool IsDeleted { get; set; }
    public int LocalVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }

    public override string ToString() => Title;
}
