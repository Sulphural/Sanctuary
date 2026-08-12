using System;

namespace Sanctuary.Database.Entities;

// A character's spin state for one wheel. Each wheel runs its own daily spin, streak and bonus spins, so
// spinning the Daily Wheel doesn't use up the Coin Wheel's spin.
public class DbCharacterDailyWheel
{
    public int WheelId { get; set; }

    public ulong CharacterId { get; set; }
    public DbCharacter Character { get; set; } = null!;

    // When this wheel's free spin was last used. The next one is due once the UTC calendar day differs.
    public DateTimeOffset? LastSpinUtc { get; set; }

    // Spins on top of the daily one, from streak milestones, prizes or "/wheel give".
    public int BonusSpins { get; set; }

    // Consecutive days this wheel has been spun.
    public int Streak { get; set; }
}
