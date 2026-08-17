using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// op207/7 - the "here is what was inside" popup that appears when a 12 Days present is opened: a framed
// panel with the reward's icon, its name as the gold title bar, a line of description and an OK button.
//
// ★★ FULLY REVERSED 2026-08-17, and it is a thin packet with a lot of client behind it:
//   dispatcher 0x00bdea00 -> sub 7 -> handler 0x00bde610
//   the handler constructs the packet (ctor 0x00bdcae0, which pushes 7), deserializes with 0x00bddf70...
//   ...actually 0x00bdde70 = base header + FOUR int32s into +0x0c/+0x10/+0x14/+0x18,
//   packs those four into an argument array (written at +0, +0xa0, +0x140, +0x1e0 - one slot each),
//   and calls the Lua bridge 0x009797a0 with the literal name at 0x01826c6c:
//
//       UnifiedMessageWindow:ShowItemPanel(iconId, tintId, titleId, bodyId)
//
//   The parameter NAMES are not guessed - `UnifiedMessageWindow.lua` in ScriptsBase.bin declares
//   `ShowItemPanel` with exactly `iconId / tintId / titleId / bodyId` locals before invoking the swf's
//   `showItemPanel_lua`. So the four wire ints map straight onto them, in order.
//
// ★ The window is shared, not 12-Days-specific: the same class also serves ShowPetUpsell,
// ShowMountUpsell, ShowRewardPanel and ShowXpCoinBankReward. Only ShowItemPanel is reachable from op207.
public class ProgressiveQuestNotifyRewardItemPacket : ISerializablePacket
{
    public const short OpCode = 207;
    private const int SubOpCode = 7;

    public int IconId;    // Images.txt id
    public int TintId;    // icon tint; 0 = untinted
    public int TitleId;   // gold title bar, e.g. 441207 "Snow Days Gift Box - Fireworks"
    public int BodyId;    // the line under the icon, e.g. 441222 "A fantastic assortment..."

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        writer.Write(OpCode);
        writer.Write(SubOpCode);

        writer.Write(IconId);
        writer.Write(TintId);
        writer.Write(TitleId);
        writer.Write(BodyId);

        return writer.Buffer;
    }
}
