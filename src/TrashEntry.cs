namespace LocalNote.Domain.Entities;

public sealed class TrashEntry
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public override string ToString() => $"[{Kind}] {Name}";
}
