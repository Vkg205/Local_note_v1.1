namespace LocalNote.Domain.Entities;

public sealed class Section
{
    public required string Id { get; init; }
    public required string NotebookId { get; init; }
    public string? ParentGroupId { get; init; }
    public required string Name { get; set; }
    public string Color { get; set; } = "#5B9BD5";
    public int SortOrder { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }

    public override string ToString() => Name;
}
