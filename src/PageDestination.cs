namespace LocalNote.Domain.Entities;

public sealed class PageDestination
{
    public required string NotebookId { get; init; }
    public required string NotebookName { get; init; }
    public required string SectionId { get; init; }
    public required string SectionName { get; init; }
    public string DisplayName => $"{NotebookName}  /  {SectionName}";
    public override string ToString() => DisplayName;
}
