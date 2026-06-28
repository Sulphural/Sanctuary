using System;
using System.Collections.Generic;

namespace Sanctuary.Database.Entities;

public sealed class DbHouse
{
    public ulong Id { get; set; }

    public ulong OwnerId { get; set; }
    public DbCharacter Owner { get; set; } = null!;

    public int HouseDefinitionId { get; set; }

    public int NameId { get; set; }
    public string? CustomName { get; set; }

    public bool IsLocked { get; set; }
    public bool IsMembersOnly { get; set; }
    public bool IsFloraAllowed { get; set; }
    public bool PetAutospawn { get; set; }

    public int MaxFixtureCount { get; set; }
    public int MaxLandmarkCount { get; set; }

    public int IconId { get; set; }

    public string? Description { get; set; }
    public string? KeywordList { get; set; }

    public float Rating { get; set; }
    public int Votes { get; set; }

    public DateTimeOffset Created { get; set; }
    public DateTimeOffset LastVisited { get; set; }

    public ICollection<DbHouseFixture> Fixtures { get; set; } = new HashSet<DbHouseFixture>();
    public ICollection<DbHousePermission> Permissions { get; set; } = new HashSet<DbHousePermission>();
}
