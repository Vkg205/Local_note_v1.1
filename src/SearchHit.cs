namespace LocalNote.Domain.Entities;

public sealed class SearchHit
{
    public required string PageId { get; init; }
    public string? ObjectId { get; init; }
    public required string PageTitle { get; init; }
    public required string Source { get; init; }
    public required string Snippet { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public override string ToString() => $"{PageTitle} · {Snippet}";
}
