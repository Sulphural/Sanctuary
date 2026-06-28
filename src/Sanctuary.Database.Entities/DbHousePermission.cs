using System;

namespace Sanctuary.Database.Entities;

public sealed class DbHousePermission
{
    public ulong HouseId { get; set; }
    public DbHouse House { get; set; } = null!;

    public ulong CharacterId { get; set; }
    public DbCharacter Character { get; set; } = null!;

    public int PermissionLevel { get; set; }

    public DateTimeOffset Created { get; set; }
}
