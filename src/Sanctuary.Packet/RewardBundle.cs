using System.Collections.Generic;
using System.Linq;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public sealed class RewardEntry
{
    public int Type = 1;
    public bool Hidden;
    public int IconId;
    public int TintId;
    public int NameId;
    public int Quantity = 1;
    public int ItemDefId;
    public int Param2;

    public int? TailItemGuid;

    public string DisplayName = "";
}

public static class RewardBundle
{
    public static void Write(PacketWriter writer, IReadOnlyList<RewardEntry> entries,
        int coins = 0, int xp = 0, int iconOverride = -1, int nameOverride = -1, int unknown15 = 0)
    {
        var tails = entries.Any(e => e.TailItemGuid.HasValue);

        writer.Write(tails);
        writer.Write(coins);
        writer.Write(xp);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(1.0f);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0); writer.Write(0);
        writer.Write(0); writer.Write(0);
        writer.Write(iconOverride);
        writer.Write(nameOverride);
        writer.Write(entries.Count);
        foreach (var e in entries)
        {
            writer.Write(e.Type);
            writer.Write(e.Hidden);
            writer.Write(e.IconId);
            writer.Write(e.TintId);
            writer.Write(e.NameId);
            writer.Write(e.Quantity);
            writer.Write(e.ItemDefId);
            writer.Write(e.Param2);
            writer.Write((string?)null);
            writer.Write(0);
            writer.Write(false);
            if (tails)
                writer.Write(e.TailItemGuid ?? 0);
        }
        writer.Write(unknown15);
    }
}
