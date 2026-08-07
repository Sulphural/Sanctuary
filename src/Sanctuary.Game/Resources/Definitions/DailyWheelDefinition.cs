using System.Collections.Generic;

namespace Sanctuary.Game.Resources.Definitions;

// "Spin For The Win!" daily prize wheel — the data behind the client's native game_wheel.gfx widget.
//
// GROUND TRUTH (2026-08-06, reversed straight out of the client asset FRAssets/.../game_wheel.gfx —
// AS2 classes Wheel.WheelGameServer / Wheel.WheelData / Wheel.WheelSpinner / Wheel.WheelMC):
//
//   * Retail drove this from three server data tables, MicroGameWheel.txt (id^type^activity^start^end^
//     nameStringId^msgStringId), MicroGameWheelCategory.txt (id^index^rewardset) and
//     MicroGameWheelSlots.txt (wheel^category^index). Those tables are lost, so this file replaces them:
//     one entry per wheel, its slices in wheel order, and what each slice pays out.
//   * Type is the mcSpinner FRAME (1-7) = which wheel artwork/slice layout is shown. Frame 1 and 5 lay
//     out 10 slices (mcCategory1..mcCategory10), frame 3 lays out 8. More slots than the frame has
//     clips = slices that spin but never render, so keep Slots.Count == the frame's slice count.
//   * Category is the mcCategory FRAME = the picture painted on that slice (gotoAndStop). Extracted from
//     the widget's art and identified 2026-08-07 (the frames are shapes filled with external DDS images;
//     22 of the 25 frames carry their own art):
//        1 ring + gem          2 clothing            3 pet + potions      4 EXTRA SPINS medallion
//                                                                          (same art as
//                                                                           icon_wheel_reward_extra_spins)
//        5 TCG card pack       6 money bag           7 rod / kart / ball  8 potion + scrolls
//        9 exchange ticket    10 tools              11 halloween pails   12 candy canes
//       13 house item         15-21 a coin marked 2 / 5 / 10 / 20 / 30 / 40 / 50
//       22 robgoblin          23 valentine heart    (14 draws nothing of its own)
//     The wheel is rigged server-side, so a slice whose picture doesn't match its prize is immediately
//     obvious to the player - keep the two in step.
//
// ALTERNATE TABLE (themed rather than art-ordered) if the wheel is ever re-cut - same 10 slices, art
// grouped by prize type instead of running 1..10 in the widget's own order:
//     6 money bag   250 coins            (weight 18)
//     8 potion      Flabbergast Sphere x3      (12)
//     6 money bag   500 coins                  (14)
//    12 candy canes Candy Cane x3 (76621)      (10)
//     6 money bag   1000 coins                  (8)
//     2 clothing    Striped T-Shirt (248)      (10)
//     1 ring + gem  Smarty-Pants Ring (897)     (8)
//    10 tools       Pickaxe (1170)             (10)
//    22 robgoblin   Robgoblin T-Shirt (340)     (8)
//     5 card pack   Battle Item Mystery Pack (10482), jackpot (2)
public class DailyWheelDefinition
{
    public string Comment { get; set; } = "";

    // Wheel id. Sent as OnWheelDataMsg's first arg and echoed back by the client on every spin/change
    // request, so it only has to be unique across the wheels we serve.
    public int Id { get; set; }

    // mcSpinner frame (1-7): the wheel's artwork and how many slices it has.
    public int Type { get; set; } = 1;

    // Wheel title (shown on the left/right page buttons when more than one wheel exists).
    public int NameStringId { get; set; }

    // The line under the wheel ("Come back every day to spin the wheel and win a great prize...").
    // Rendered by WheelMC.SetWheel as GetStringById(msgStringID) into tfStatus.
    public int MsgStringId { get; set; }

    // How many spins a player gets per calendar day (UTC), gated by DbCharacter.LastDailyWheelSpinUtc.
    public int SpinsPerDay { get; set; } = 1;

    // Line under the prize in the "Congratulations! You won" window, for slices that don't set their own.
    // MUST be non-zero: the widget only overwrites that text field when the id is > 0, so sending 0 leaves
    // the placeholder the movie was authored with ("test") on screen. 408045 is retail's own
    // "Come back tomorrow for another spin!".
    public int RewardMsgStringId { get; set; } = 408045;

    // Slices, in wheel order. The landed slice is picked server-side (weighted) and the client is simply
    // told which index to stop on, so the odds live entirely here.
    public List<DailyWheelSlot> Slots { get; set; } = [];

    public class DailyWheelSlot
    {
        public string Comment { get; set; } = "";

        // mcCategory frame (1-25) - the picture on this slice.
        public int Category { get; set; } = 1;

        // Prize: an item (ItemId + Quantity), coins, or extra spins - whichever is set. Icon/name/tooltip
        // for the "Congratulations! You won" window are resolved from the item definition unless
        // overridden.
        public int ItemId { get; set; }
        public int Quantity { get; set; } = 1;
        public int Coins { get; set; }

        // Alternatives to ItemId: one is picked at random each time the slice is won, so a slice can pay
        // any of several items of the kind its picture shows (six shirts for the clothing slice, and so
        // on). The reward window names whichever was rolled, since icon/name come from the item itself.
        public List<int> ItemIds { get; set; } = [];

        // The same idea for coins, except each amount has to carry its own name string: the reward window
        // never draws a quantity, so the amount only reaches the player through the name.
        public List<CoinPrize> CoinAmounts { get; set; } = [];

        public class CoinPrize
        {
            public int Coins { get; set; }

            // A string that spells this exact amount out. What the client stocks:
            //   5 -> 435919 · 250 -> 435924 · 1000 -> 433744 · 5000 -> 4773 · 7500 -> 6875
            //   10000 -> 5146 · 20000 -> 4941 · 30000 -> 6801 · 40000 -> 6802 · 50000 -> 6803
            //   100000 -> 6680 · 200000 -> 435932 · 250000 -> 6682 · 500000 -> 6681
            // Amounts outside that list can't be named, so pick from it rather than inventing one - a
            // mismatch means the window confidently states the wrong number.
            public int NameStringId { get; set; }
        }

        // Extra spins of the wheel itself, the prize the widget's own "extra spins" medallion (category 4,
        // the same art as icon_wheel_reward_extra_spins) depicts. Granted like /wheel give, so they persist.
        public int Spins { get; set; }

        // Relative odds of landing here (0 = never; the wheel still shows the slice).
        public int Weight { get; set; } = 1;

        // Optional overrides for the reward window. IconId 22663 is special-cased by the client into the
        // coin icon + a "Shop Now!" button, so coin slices default to the plain coin icon (4809) instead.
        //
        // NameStringId is worth setting on COIN slices: the reward window shows a name but never draws the
        // quantity (it stores the value and no field displays it), so "Coins" alone hides the amount. The
        // client has amount-specific strings - 435924 "250 coins", 433744 "1000 Coins", 441210 "5000 Coins",
        // 5245 "1,000 Coin", 6874 "5,000 Coin", 4941 "20,000 Coin" - so match the string to the payout.
        public int IconId { get; set; }
        public int NameStringId { get; set; }
        public int TooltipId { get; set; }
        public int TintId { get; set; }

        // Optional body line in the reward window (GetStringById), e.g. "Come back tomorrow!".
        public int RewardMsgStringId { get; set; }
    }
}
