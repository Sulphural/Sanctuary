using System.Collections.Generic;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// op207/1 - the payload behind the "12 Days of Presents" browser (MysteryRewardBrowser).
//
// ★★ FULLY REVERSED 2026-08-17. Every field below is named from the client's own data-source metadata and
// placed from its own deserializer - nothing here is inferred. The trail:
//
//   RTTI `.?AVProgressiveQuestClientDataPacket@@` -> td 0x01b659b0 -> COL 0x01950d84 -> vtable 0x01826c04
//   -> ctor 0x00bdc970 (pushes 1 to base ctor 0x00bdc8a0, which writes opcode 0xcf=207 at +4).
//   Header reader 0x00bdddd0 = `[int16 opcode][int32 subOpcode]`.
//   Record reader 0x00bde1f0; element readers 0x00bdd9c0 (slot) and 0x00bddba0 (prize).
//   Column NAMES come from the three GetColumnName jump tables - 0x00ce7ea0 (definition), 0x00ce7f70
//   (slots), 0x00ce8020 (prizes) - and column->OFFSET from the matching GetData tables 0x00ce8460,
//   0x00ce85f0, 0x00ce8860. Object strides confirm the record sizes (slots 36 bytes via
//   `lea eax,[ebx+ebx*8]` + `[ecx+eax*4]` in 0x00ce80f0).
//
// ★★★ THE BUG THAT COST THREE LIVE TESTS: an automated pass over the readers keyed on the cursor-advance
// idiom MISSED THE FINAL FIELD OF ALL THREE RECORDS, because the last read's early-exit path interleaves
// its `pop` instructions before the advance and breaks the pattern. The records are one field longer than
// they first appear:
//     definition  - a THIRD trailing int32 at +0x28 (ButtonStringIdBuy), `mov [edi+0x28]` @0x00bde3a0
//     slot        - a 12th int32 at +0x20 (Name),                        `mov [ecx+0x20]` @0x00bddb74
//     prize       - a trailing bool at +0x18 (CanClaimPrizeItem),        `mov [ecx+0x18]` @0x00bddc84
// Being short made the reader run off the end, which sets the error flag and skips the data-source
// refresh - yet the panel still DREW, because the client assigns the quest object BEFORE deserialising it
// and the Lua `Show()` reads the data sources directly rather than waiting for the refresh. So a
// half-parsed record renders as a populated-but-wrong panel instead of failing visibly. When auto-deriving
// a layout, always reconcile the field count against the object stride.
//
// WIRE ORDER (what Serialize writes), with the object offset each field lands on:
//   definition : int32 x8 -> +0x00 QuestId, +0x04 Name, +0x08 SecondsRemaining, +0x0c ActiveSlotId,
//                            +0x10 IconId, +0x14/18/1c ObjectiveStringId1..3
//                int32 count + Slots[]        (object +0x30)
//                int32 count + PrizeSlots[]   (object +0x40)
//                bool  -> +0x2c UseSmallWindow
//                int32 -> +0x20 ButtonStringIdOpen
//                int32 -> +0x24 ButtonStringIdOpened
//                int32 -> +0x28 ButtonStringIdBuy
//   ★ the trailing bool+3 ints come LATER on the wire than the two lists but EARLIER in the object.
public class ProgressiveQuestClientDataPacket : ISerializablePacket
{
    public const short OpCode = 207;
    private const int SubOpCode = 1;

    // 33 wire bytes. Columns (GetColumnName 0x00ce7f70): SlotId, IconId, HasSlotItem, HasKeyItem,
    // CanDoQuest, CanPurchaseKeyItem, KeyBundleId, KeyBundlePrice, CanClaimSlotItem, Tooltip, Name,
    // ProgressiveQuestId.
    public sealed class Slot
    {
        public int QuestId;              // +0x00
        public int SlotId;               // +0x04 - the day index
        public int IconId;               // +0x08 - tile art (0 = the plain box retail shows when locked)
        public bool HasSlotItem;         // +0x0c - the present has been earned
        public bool HasKeyItem;          // +0x0d - ...and its bow
        public bool CanDoQuest;          // +0x0e - today's challenge is startable
        public bool CanPurchaseKeyItem;  // +0x0f - a bow can be bought for it
        public int KeyBundleId;          // +0x10 - StoreBundles row for the bow ("12 Days Bow - Day N")
        public int KeyBundlePrice;       // +0x14
        public bool CanClaimSlotItem;    // +0x18 - the present can be opened
        public int TooltipId;            // +0x1c
        public int NameId;               // +0x20 - ★ THE TILE LABEL ("Day 1 Present" = 441950..441961)
    }

    // 25 wire bytes. Columns (GetColumnName 0x00ce8020): PrizeSlotId, IconId, ProgressPercent,
    // CanClaimPrizeItem, Tooltip, Name, ProgressiveQuestId.
    public sealed class PrizeSlot
    {
        public int QuestId;              // +0x00
        public int PrizeSlotId;          // +0x04
        public int NameId;               // +0x08
        public int IconId;               // +0x0c
        public int TooltipId;            // +0x10
        public int ProgressPercent;      // +0x14 - drives the bar under each Big Present
        public bool CanClaimPrizeItem;   // +0x18
    }

    // Definition. Columns (GetColumnName 0x00ce7ea0): ProgressiveQuestId, Name, SecondsRemaining,
    // ActiveSlotId, IconId, ObjectiveStringId1..3, UseSmallWindow, ButtonStringIdOpen,
    // ButtonStringIdOpened, ButtonStringIdBuy.
    public int QuestId;
    public int NameId;
    public int SecondsRemaining;
    public int ActiveSlotId;
    public int IconId;
    public int ObjectiveStringId1;
    public int ObjectiveStringId2;
    public int ObjectiveStringId3;

    public List<Slot> Slots = [];
    public List<PrizeSlot> PrizeSlots = [];

    public bool UseSmallWindow;
    public int ButtonStringIdOpen;
    public int ButtonStringIdOpened;
    public int ButtonStringIdBuy;

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        writer.Write(OpCode);
        writer.Write(SubOpCode);

        writer.Write(QuestId);
        writer.Write(NameId);
        writer.Write(SecondsRemaining);
        writer.Write(ActiveSlotId);
        writer.Write(IconId);
        writer.Write(ObjectiveStringId1);
        writer.Write(ObjectiveStringId2);
        writer.Write(ObjectiveStringId3);

        writer.Write(Slots.Count);
        foreach (var slot in Slots)
        {
            writer.Write(slot.QuestId);
            writer.Write(slot.SlotId);
            writer.Write(slot.IconId);
            writer.Write(slot.HasSlotItem);
            writer.Write(slot.HasKeyItem);
            writer.Write(slot.CanDoQuest);
            writer.Write(slot.CanPurchaseKeyItem);
            writer.Write(slot.KeyBundleId);
            writer.Write(slot.KeyBundlePrice);
            writer.Write(slot.CanClaimSlotItem);
            writer.Write(slot.TooltipId);
            writer.Write(slot.NameId);
        }

        writer.Write(PrizeSlots.Count);
        foreach (var prize in PrizeSlots)
        {
            writer.Write(prize.QuestId);
            writer.Write(prize.PrizeSlotId);
            writer.Write(prize.NameId);
            writer.Write(prize.IconId);
            writer.Write(prize.TooltipId);
            writer.Write(prize.ProgressPercent);
            writer.Write(prize.CanClaimPrizeItem);
        }

        writer.Write(UseSmallWindow);
        writer.Write(ButtonStringIdOpen);
        writer.Write(ButtonStringIdOpened);
        writer.Write(ButtonStringIdBuy);

        return writer.Buffer;
    }
}
