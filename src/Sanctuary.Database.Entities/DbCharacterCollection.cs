using System;

namespace Sanctuary.Database.Entities;

// One completed collection (Collections.json id) per character. A collection pays its reward exactly
// once, so completion has to outlive the session: without this row a relog would re-award the XP/coins
// every time the panel was rebuilt from the still-owned collection items.
public class DbCharacterCollection
{
    public int CollectionId { get; set; }

    public ulong CharacterId { get; set; }
    public DbCharacter Character { get; set; } = null!;

    public DateTimeOffset CompletedUtc { get; set; } = DateTimeOffset.UtcNow;
}
