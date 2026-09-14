namespace LocalNote.Domain.Entities;

public sealed class VaultInfo
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required string RootPath { get; init; }
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
