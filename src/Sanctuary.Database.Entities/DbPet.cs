using System;

namespace Sanctuary.Database.Entities;

public class DbPet
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
    public int Tint { get; set; }
    public int Definition { get; set; }

    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;

    public ulong CharacterId { get; set; }
    public DbCharacter Character { get; set; } = null!;
}
