using System;
using System.Collections.Generic;

namespace Sanctuary.Game.Resources.Definitions;

// One entry per wheel in DailyWheel.json, standing in for retail's lost MicroGameWheel tables.
//
// Type is the widget's mcSpinner frame: it picks the artwork AND how many slices are laid out, so a wheel
// with more slots than its frame has slices will land on wedges the player can't see.
//     1 = everyday, 10 slices        2 = unused, draws nothing
//     3 = Halloween, 8  (pointer and slices only - the disc, centre and base art is gone from this build)
//     4 = Snow Days, 8  (same)       5 = blue base + gold tree, 10
//     6 = Festival of Hearts, 8      7 = red base + orange tree, 8
//
// Category is the slice picture (mcCategory frame):
//     1 ring    2 clothing   3 pet     4 extra spins   5 TCG      6 money bag   7 kart/rod
//     8 potion  9 ticket    10 tools  11 pails        12 candy   13 house
//    15-21 coins           22 robgoblin  23 heart     (14 is blank)
// The wheel is rigged server-side, so keep each slice's picture and its prize in step.
public class DailyWheelDefinition
{
    public string Comment { get; set; } = "";

    // Only has to be unique across the wheels we serve - the client just echoes it back.
    public int Id { get; set; }

    // mcSpinner frame (1-7): the artwork, and how many slices are laid out.
    public int Type { get; set; } = 1;

    // Wheel title (shown on the left/right page buttons when more than one wheel exists).
    public int NameStringId { get; set; }

    // The line under the wheel.
    public int MsgStringId { get; set; }

    // Spins per calendar day (UTC). Each wheel tracks its own - see DbCharacterDailyWheel.
    public int SpinsPerDay { get; set; } = 1;

    // "MM-DD" bounds, inclusive, repeating every year. Empty means always available, and a window may
    // wrap the new year (12-14 -> 01-06).
    public string SeasonStart { get; set; } = "";
    public string SeasonEnd { get; set; } = "";

    // Whether this wheel should be offered on the given day.
    public bool IsInSeason(DateTime utcNow)
    {
        if (string.IsNullOrEmpty(SeasonStart) || string.IsNullOrEmpty(SeasonEnd))
            return true;

        if (!TryParseMonthDay(SeasonStart, out var start) || !TryParseMonthDay(SeasonEnd, out var end))
            return true;

        var today = (utcNow.Month, utcNow.Day);

        // A wrapping window is the inverse test.
        return Compare(start, end) <= 0
            ? Compare(start, today) <= 0 && Compare(today, end) <= 0
            : Compare(start, today) <= 0 || Compare(today, end) <= 0;
    }

    private static int Compare((int Month, int Day) a, (int Month, int Day) b) =>
        a.Month != b.Month ? a.Month.CompareTo(b.Month) : a.Day.CompareTo(b.Day);

    private static bool TryParseMonthDay(string value, out (int Month, int Day) result)
    {
        result = default;

        var parts = value.Split('-');

        if (parts.Length != 2 || !int.TryParse(parts[0], out var month) || !int.TryParse(parts[1], out var day))
            return false;

        result = (month, day);
        return true;
    }

    // Line under the prize in the reward window. Must be non-zero - the widget only replaces that field
    // when the id is > 0, so a 0 leaves its authored placeholder ("test") on screen.
    public int RewardMsgStringId { get; set; } = 408045;

    // Slices in wheel order. We pick the winner by weight and tell the client where to stop.
    public List<DailyWheelSlot> Slots { get; set; } = [];

    public class DailyWheelSlot
    {
        public string Comment { get; set; } = "";

        // mcCategory frame (1-25) - the picture on this slice.
        public int Category { get; set; } = 1;

        // Prize: an item, coins or extra spins - whichever is set. The reward window takes its icon and
        // name from the item definition unless overridden below.
        public int ItemId { get; set; }
        public int Quantity { get; set; } = 1;
        public int Coins { get; set; }

        // A pool for ItemId - one is rolled per win, so a slice can pay any item its picture fits.
        public List<int> ItemIds { get; set; } = [];

        // Same for coins, but each amount needs its own name string (see CoinPrize).
        public List<CoinPrize> CoinAmounts { get; set; } = [];

        public class CoinPrize
        {
            public int Coins { get; set; }

            // A string naming this exact amount. The client stocks 250 (435924), 1000 (433744),
            // 5000 (4773), 7500 (6875), 10000 (5146), 20000 (4941), 50000 (6803), 500000 (6681) and a
            // few more. Stick to amounts that have one, or the window states the wrong number.
            public int NameStringId { get; set; }
        }

        // Extra spins, the category 4 medallion. Retail gave 1, 2, 3 or 5 - put those in SpinAmounts.
        public int Spins { get; set; }
        public List<int> SpinAmounts { get; set; } = [];

        // The grab bag: this many further slices are rolled and paid alongside this one.
        public int GrabBagPrizes { get; set; }

        // Relative odds of landing here (0 = never; the wheel still shows the slice).
        public int Weight { get; set; } = 1;

        // Reward window overrides. Worth setting NameStringId on coin slices: the window draws a name but
        // never a quantity, so "Coins" on its own hides the amount.
        public int IconId { get; set; }
        public int NameStringId { get; set; }
        public int TooltipId { get; set; }
        public int TintId { get; set; }

        // Optional body line in the reward window (GetStringById), e.g. "Come back tomorrow!".
        public int RewardMsgStringId { get; set; }
    }
}
