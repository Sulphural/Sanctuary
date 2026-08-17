using Sanctuary.Core.IO;
using Sanctuary.Packet.Common;

namespace Sanctuary.Packet;

// op50 (RewardBase family) sub 1 - the standalone REWARD GRANT banner. The live server sent it right
// after the loot wheel stopped (04-01 idx 38142 + 38146). Two live shapes:
//   * CONTENTS grant (38142): one ITEM entry (3x Flabbergast Sphere 3015) with CarriesItemGuids set, the
//     entry's ItemGuid being the player's new inventory row id.
//   * PRIZE banner (38146): zero entries, IconId/NameId set to the won prize (Mystery Pack 973/6666),
//     Trailing = 957 - the "you won X" display for the wheel result itself.
public class RewardBundlePacket : RewardBasePacket, ISerializablePacket
{
    public new const byte OpCode = 1;

    public RewardBundleBase RewardBundle { get; } = new();

    public RewardBundlePacket() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        RewardBundle.Serialize(writer);

        return writer.Buffer;
    }
}
