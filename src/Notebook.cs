namespace LocalNote.Domain.Entities;

public sealed class Notebook
{
    public required string Id { get; init; }
    public required string VaultId { get; init; }
    public required string Name { get; set; }
    public string Color { get; set; } = "#2B579A";
    public int SortOrder { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }

    public override string ToString() => Name;
}
