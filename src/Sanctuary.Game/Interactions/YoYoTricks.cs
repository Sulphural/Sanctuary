using Sanctuary.Game.Entities;
using Sanctuary.Packet;

namespace Sanctuary.Game.Interactions;

// The Yo-Yo's two tricks. Holding the yo-yo (item 79519) puts them on the "1" and "2" keys, whatever job
// the player is wearing - the moves belong to the PROP, not to a job, so they work for a Ninja who somehow
// has one equipped exactly as they do for the Adventurer whose profile actually allows the item class.
//
// ★ EVERYTHING HERE IS REAL CLIENT DATA, none of it invented:
//   * The two names sit immediately after the yo-yo's own name/description in the T4 string space -
//     440243 "Yo-Yo", 440244 "Impress your friends with sweet yo-yo tricks.", 440246 "Throw Down",
//     440247 "Around the World" - which is how retail groups an item with its abilities.
//   * The two animations are the two fidget clips the model itself binds: misc_ar_ag_weapon_yoyo.adr maps
//     loc_yoyo_fidget_01 -> yoyo_amb_01.gr2 and loc_yoyo_fidget_02 -> yoyo_amb_02.gr2.
//   * Their ids come from the "yoyo" AnimationTreeBranch in AnimationTypes.xml. That whole branch (stand,
//     walk, run, jumps, falls, swim, both fidgets) is selected by the item CLASS's WIELD_TYPE - class 157
//     is wield 25, and every slot in the branch is id 43<nnnn>25. The client drives all of it itself; the
//     server only has to fire the two one-shots. Both are type="5" upper-body clips, so they layer over
//     whatever the player is doing rather than interrupting it.
public static class YoYoTricks
{
    // Resources/ClientItemDefinitions.json - Class 157 (the client's own "Yo-Yo" item class), Slot 7.
    public const int YoYoItemDefinitionId = 79519;

    // Name ids (see the block comment - these are the real strings, not placeholders).
    private const int ThrowDownNameId = 440246;
    private const int AroundTheWorldNameId = 440247;

    // Icons are IMAGE ids (ImageSetMappings type 5 = Small), NOT set ids - the toolbar wants the image.
    // Around the World has dedicated art (set 8009 abil_yoyo_around-the-world -> 3912). Throw Down has no
    // ability art anywhere in the client, so it borrows the yo-yo's own item icon (set 7947 -> 39712);
    // the abil_adventurer_* set has no throw of any kind to use instead.
    private const int ThrowDownIconId = 39712;
    private const int AroundTheWorldIconId = 3912;

    // AnimationTypes.xml, "yoyo" branch: loc_yoyo_fidget_01 / _02.
    private const int ThrowDownAnimationId = 43310025;
    private const int AroundTheWorldAnimationId = 43320025;

    // Arbitrary but stable ability-definition ids, in the same private band the arena slots use (990001-3)
    // and well clear of any real ability id. The client asks for a definition the moment it reads a slot
    // and won't re-check, so SendAbilityDefinitions has to run BEFORE the toolbar.
    private const int ThrowDownAbilityId = 990010;
    private const int AroundTheWorldAbilityId = 990011;

    // Toolbar slot indexes - the "1" and "2" keys.
    public const int ThrowDownSlotIndex = 0;
    public const int AroundTheWorldSlotIndex = 1;

    // Matched to the CLIP so a re-press can't arrive mid-trick. Enforced server-side too (PropAnimation),
    // since the greyed button is only presentation - it can be pressed through.
    private const int TrickCooldownMs = PropAnimation.ClipMs;
    private const float TrickRechargeSeconds = TrickCooldownMs / 1000f;

    // ★ THE YO-YO'S EFFECT IS NOT WIRED HERE - IT IS DATA, AND IT IS PERMANENT WHILE EQUIPPED.
    // It rides the item definition's CompositeEffectId (Resources/ClientItemDefinitions.json, item 79519 =
    // 15010 WFX_magic-sparkles_blue_skel_loop - a placeholder pick until the retail look is identified on
    // sight with !yoyofx), which the client attaches to the model itself: every path that
    // builds an attachment - equip, login, job swap, and what OTHER players see via GetAttachments - reads
    // that one field, so the effect follows the prop everywhere for free and needs no server traffic at all.
    //
    // An earlier attempt did this from here, per-trick, and was wrong twice over: it used
    // PlayerUpdatePacketPlayCompositeEffect, which attaches to the ACTOR and so renders under the player's
    // feet rather than on the prop, and it was a momentary flourish rather than the constant effect the
    // yo-yo actually carries. op35/31 SlotCompositeEffectOverride WOULD bind FX to item slot 7 (it is how
    // the weapon-empowering ninja specials put a glow on the sword) and is the tool to reach for if a trick
    // ever needs its OWN effect on top of this one - but the standing effect belongs on the definition.

    public static bool IsEquipped(Player player) =>
        player.GetEquippedWeaponDefinitionId() == YoYoItemDefinitionId;

    // The yo-yo's bar, or null when it isn't the equipped weapon (so the caller falls through to the job's
    // own toolbar). Seeds the ability definitions as a side effect, the same way the arena bar does.
    public static AbilityPacketSetDefinition? BuildToolbar(Player player)
    {
        if (!IsEquipped(player))
            return null;

        SendAbilityDefinitions(player);

        var def = AbilityPacketSetDefinition.CreateEmpty(player.ActiveProfileId);

        // Positional serialization - slot 0 then slot 1, in key order.
        def.Slots.Add(MakeSlot(ThrowDownAbilityId, ThrowDownIconId, ThrowDownNameId));
        def.Slots.Add(MakeSlot(AroundTheWorldAbilityId, AroundTheWorldIconId, AroundTheWorldNameId));

        return def;
    }

    // Plays the trick on the pressed slot. Returns false for a slot the yo-yo doesn't own.
    public static bool TryPerform(Player player, int slot, IResourceManager resources)
    {
        // Before every guard, so a press the server REJECTS still shows up - see PropAnimation.TracePress.
        PropAnimation.TracePress(player, "yo-yo", slot);

        if (!IsEquipped(player))
            return false;

        // ★ NOT WHILE TRANSFORMED OR MOUNTED. A player wearing a temporary appearance (a bulb critter from
        // the whip, a dog from a treat) is a different MODEL, and the yo-yo clips live in the yoyo branch of
        // the PLAYER model - firing them at a critter asks the client for a clip that skeleton doesn't have.
        // Mounted is the same story: the rider is seated on the mount actor, so a hand clip has nowhere
        // sensible to play. The transform foods guard on appearance the same way.
        if (player.TemporaryAppearance != 0 || player.Mount is not null)
            return false;

        if (slot is not (ThrowDownSlotIndex or AroundTheWorldSlotIndex))
            return false;

        // Which fidget belongs to which trick is unconfirmed - see PropAnimation.SwapSlotAnimations.
        var animationId = PropAnimation.ClipForSlot(
            slot == AroundTheWorldSlotIndex, ThrowDownAnimationId, AroundTheWorldAnimationId);

        var abilityId = slot == AroundTheWorldSlotIndex ? AroundTheWorldAbilityId : ThrowDownAbilityId;

        // Enforced server-side, not just drawn: the greyed button is presentation and can be pressed through.
        if (PropAnimation.IsOnCooldown(player, abilityId))
            return false;

        PropAnimation.StartCooldown(player, abilityId, TrickCooldownMs);

        // Inert unless the cast-lock experiment is switched back on - it glitched the character, see
        // PropAnimation.SendCastLock.
        PropAnimation.SendCast(player, animationId, TrickCooldownMs);

        // Plays the clip, then hands animation control back a clip later - see PropAnimation for why that
        // reset is mandatory AND why it must be the plain default-clear id.
        PropAnimation.PlayOneShot(player, animationId);

        // No effect is sent here - the yo-yo's blue wind rides its item definition and is already on the
        // model (see the note above).

        // ★ THE BUTTON HAS TO BE GIVEN BACK, greyed and sweeping for the real duration - see
        // PropAnimation.SendCooldown (MeleeRefresh alone drew no radial). Still no StartCasting: the
        // snowball throw proved the client plays an unsuppressable animation off it, which would fight the
        // trick clip.
        // The bar goes with it - see PropAnimation.SendCooldown. Re-stating the slots is what stops the
        // client replaying the previously-pressed trick when the other one is used.
        PropAnimation.SendCooldown(player, Combat.JobWeaponAbilities.BuildToolbar(player, resources), TrickCooldownMs);

        return true;
    }

    private static void SendAbilityDefinitions(Player player)
    {
        Send(ThrowDownAbilityId, ThrowDownNameId, ThrowDownIconId);
        Send(AroundTheWorldAbilityId, AroundTheWorldNameId, AroundTheWorldIconId);

        void Send(int abilityId, int nameId, int iconId) =>
            player.SendTunneled(new AbilityPacketAbilityDefinition
            {
                AbilityId = abilityId,
                NameId = nameId,
                IconId = iconId,
            });
    }

    private static AbilityPacketSetDefinition.Slot MakeSlot(int abilityDefId, int iconId, int nameId) => new()
    {
        Type = 3,
        Unknown2 = abilityDefId,
        ManaCost = 0,          // cosmetic - never gated on the energy bar
        IconId = iconId,
        NameId = nameId,
        Unknown8 = TrickRechargeSeconds,
        AbilityDefinitionId = abilityDefId,
    };
}
