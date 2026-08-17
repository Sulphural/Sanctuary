using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// BaseProgressiveQuest (op 207) - the "12 Days of Presents" panel, and the system behind it.
//
// ★ WHAT THIS FAMILY DRIVES, decoded 2026-08-16 from the client's own Lua (ScriptsBase.bin): the window
// is `MysteryRewardBrowser` (`Main.wndMysteryRewardBrowser` / swfMysteryRewardBrowser), and it renders
// from three data sources that the server fills:
//
//   ProgressiveQuest.Definition   SecondsRemaining, ActiveSlotIndex, ObjectiveStringId1/2/3,
//                                 ButtonStringIdOpen, ButtonStringIdOpened, ButtonStringIdBuy
//   ProgressiveQuest.Slots        HasSlotItem, HasKeyItem, CanDoQuest, CanPurchaseKeyItem,
//                                 KeyBundleId, KeyBundlePrice, CanRedeemSlotItem, Tooltip
//   ProgressiveQuest.PrizeSlots   ProgressPercent, CanRedeemPrizeItem
//
// That model lines up 1:1 with the retail screenshot: twelve "Day N Present" Slots, three big-present
// PrizeSlots with their progress bars, the right-hand pane's three objective ticks and its action button,
// and the countdown in the corner. Lua-side entry points are SetProgressiveQuest / PopulateItems /
// PopulateUberItems / BuyBow / SetSelectedIndex / OnOrderResponse.
//
// Sub-opcodes (a full INT, not a short or a byte - see the op207 branch of PacketReaderExtensions):
//   0 ShowWindow          S2C - this packet, opens the browser
//   1 ClientData          S2C - fills the three data sources above
//   2 RequestStartQuest   C2S - the green "Start Quest" button
//   3 RequestClaimSlot    C2S - claiming a finished day's present
//   4 RequestClaimPrize   C2S - claiming a Big Present
//   7 NotifyRewardItem    S2C
//
// ★★ THIS PACKET HAS A ONE-BYTE BODY, AND SENDING IT WITHOUT ONE OPENS NOTHING AT ALL. That was a real
// bug here, and the way it was got wrong is worth keeping: the CONSTRUCTOR (0x00bdca30) initialises no
// fields, so "header only" looked safe. It isn't - the DESERIALIZER 0x00bddf50 reads a trailing bool into
// +0x0c, and when the body is missing it takes its short-read path, which sets the READER'S ERROR FLAG
// ([reader+0x10] = 1). The receive handler 0x00bde3d0 then does `cmp byte [esp+0x1c], 0 / jne bail` and
// gives up before the window is ever named. A ctor's field-init list is evidence about the OBJECT, not
// about the WIRE - only the deserializer is evidence about the wire.
//
// The bool chooses which browser opens, and the client picks between two literal strings for it:
//     false -> "large" (0x01826c5c)   ->  Main.wndMysteryRewardBrowser
//     true  -> "small" (0x01826c64)   ->  Main.wndMysteryRewardBrowserSmall
// (Read straight off 0x00bde474: esi is loaded with "small" and only kept when the byte is non-zero.)
// The 12 Days of Presents grid is the LARGE one, so Small stays false.
// ★★ AND THE SUB-OPCODE IS 5, NOT 0. This is the second reason it opened nothing, and no amount of body
// fixing would have helped: the op207 dispatcher (0x00bdea00) computes `subOpcode - 1`, rejects anything
// ABOVE 6 unsigned, and jumps through a 7-entry table at 0x00bdeaa0 - so only 1..7 are dispatched at all,
// and 0 wraps to 0xffffffff and is thrown away before any handler runs. The table resolves as:
//     1 -> 0x00bde8a0  ClientData (fills the three data sources)
//     2,3,4 -> ignored (the C2S request subs; the client never handles its own sends)
//     5 -> 0x00bde3d0  ** invokes Lua `MysteryRewardBrowser:Show(<"large"|"small">)` **
//     6 -> 0x00bde510
//     7 -> 0x00bde610  NotifyRewardItem
//
// This codebase's own op207 name table (PacketReaderExtensions) lists 0 as "ShowWindowPacket" and has no
// entry for 5 or 6 at all - 0 is presumably the C2S request direction. The class keeps its name because
// that is what the client's RTTI calls the type; only the wire sub-opcode is corrected here.
public class ProgressiveQuestShowWindowPacket : ISerializablePacket
{
    public const short OpCode = 207;

    // The S2C "open the browser" sub-opcode. An int, unlike most families here.
    private const int SubOpCode = 5;

    // false = the full-size browser (the 12 Days grid), true = the compact variant.
    public bool Small;

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        writer.Write(OpCode);
        writer.Write(SubOpCode);

        writer.Write(Small);

        return writer.Buffer;
    }
}
