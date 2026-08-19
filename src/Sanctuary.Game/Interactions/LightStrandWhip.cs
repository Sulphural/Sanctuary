using System.Numerics;
using System.Threading.Tasks;

using Sanctuary.Game.Entities;
using Sanctuary.Game.Zones;
using Sanctuary.Packet;

namespace Sanctuary.Game.Interactions;

// The Light Strand Whip - a Christmas light strand converted into a whip, and a 12 Days of Presents
// reward in retail (its strings sit inside that block: 441923-441944 are the 12 Days quest lines).
//
// ★ ITS TWO ABILITIES ARE NOT GUESSWORK - THE ITEM DESCRIPTION SPELLS THEM OUT. Text 441922 reads:
//   "This is a Christmas light strand of red, blue, green and yellow bulbs, that's been converted into a
//    whip. Ability 1 will turn other players into a Christmas light bulb critter. Ability 2 will do the
//    same, but to the wielder."
// So slot 0 transforms the TARGET and slot 1 transforms the CASTER, both into lightbulb_critter
// (Models.txt id 4123). That is also why this is its own class rather than another entry beside the
// yo-yo's: the yo-yo's moves are pure animation, these are real transforms with a victim.
//
// The whip's CRACK IS BUILT INTO THE MODEL, so unlike the yo-yo nothing here plays an effect or a sound:
// whip_ar_ag_weapon_*.adr binds whip_crack/whip_crack_emitter and the WIELD_Whip_Whoosh / WIELD_Whip_Crack
// sound emitters directly to loc_whip_fidget_01/02, and the client fires them with the clip.
public static class LightStrandWhip
{
    // Resources/ClientItemDefinitions.json - Class 159 ("Light Strand Whip", wield 28 = the whip animation
    // branch). ★ Class 159 is granted to the ADVENTURER in Profiles.json by us: retail shipped the class
    // and the art but no profile in this data set lists it, so nothing could hold one until we added it.
    public const int WhipItemDefinitionId = 79520;

    // Text ids: the item's own name doubles as the ability name, because the client has no separate
    // strings for the two moves (the description is where retail documented them).
    private const int WhipNameId = 441921;         // "Light Strand Whip"

    // Icons are IMAGE ids (ImageSetMappings type 5 = Small).
    //   Ability 1 (crack it at someone) wears the whip itself - set 8146 whip_ar_ag_weapon_xmaslights.
    //   Ability 2 (become one) wears the CRITTER YOU TURN INTO - set 8145
    //   item_vanitypet_lightbulb_critter_lightbulb-critter, i.e. the same art as the vanity pet. The whip
    //   icon was wrong on that slot; the button should show what you are about to become.
    private const int WhipIconId = 40730;
    private const int LightbulbCritterIconId = 40727;

    // AnimationTypes.xml "whip" branch (wield 28) - the two fidgets, which carry the crack FX + sounds.
    private const int TargetAnimationId = 43310028;   // loc_whip_fidget_01
    private const int SelfAnimationId = 43320028;     // loc_whip_fidget_02

    // Private, stable ability-definition ids - same band as the arena (990001-3) and the yo-yo (990010-11).
    private const int TargetAbilityId = 990020;
    private const int SelfAbilityId = 990021;

    public const int TargetSlotIndex = 0;   // "Ability 1" - the victim
    public const int SelfSlotIndex = 1;     // "Ability 2" - the wielder

    // Models.txt 4123 lightbulb_critter.adr - the "Christmas light bulb critter" the description names.
    private const int LightbulbCritterModelId = 4123;

    // ★ ABILITY 2 WINDS UP BEFORE IT TRANSFORMS: a snow tornado swirls around the wielder first, THEN they
    // pop into the bulb. 53 = PFX_snowflakes_white_sphere_loop, a sphere of snowflakes centred on the actor
    // (with its own sound), which is the client's only snow effect shaped to wrap a player like that.
    //
    // It LOOPS, so it can't be a fire-and-forget PlayCompositeEffect - that would leave snow spinning round
    // the player forever. It rides an effect TAG instead (op35/41 attach, op35/42 remove), the same way the
    // snowball guard's bubble does, and the tag comes off at the moment the transform lands. The tag id sits
    // in the static 91xxx band (SnowballGuard 91021, freeze 91022, scare spotlight 91040) - deliberately NOT
    // near 5000, where _castFxTagCounter increments and would eventually collide.
    private const int SnowTornadoFxId = 53;
    private const int SnowTornadoTagId = 91050;

    // ★ LONG ENOUGH FOR THE WHIP CLIP TO FINISH FIRST - transforming mid-crack cut the animation off.
    // The clip's true length can't be read from the assets (the .gr2 is Granny with Oodle-compressed
    // sections), but the .adr DOES carry the fidget's own event timeline, and its last trigger sits at
    // 2.063s - the same value in both whip models. 2100ms clears that by a hair, so the crack plays out
    // and the snow is still swirling when the bulb appears.
    //
    // ★ A TOUCH LONGER THAN THE CLIP so the idle reset (PropAnimation, at ClipMs) lands FIRST and the model
    // swaps out of a cleanly-reset animation rather than out of a held one - the ordering that was making
    // transforms after a whip misbehave.
    //
    // Settable because it is still an inference, not a measurement: `!yoyofx windup <ms>` retunes it live.
    public static int WindupMs { get; set; } = PropAnimation.ClipMs + 100;

    // How long the victim stays a bulb. Retail's duration is unknown; this matches the tone of the other
    // transform consumables rather than being copied from anything.
    private const int TransformDurationMs = 30_000;

    // ★ ABILITY 2 COSTS MORE THAN ABILITY 1. Cracking the whip at someone is the everyday move, so it gets
    // the basic cooldown; turning YOURSELF into a bulb takes you out of the action for the whole transform,
    // so it waits noticeably longer before it can be used again. Both are enforced server-side (see
    // PropAnimation) - the greyed button is presentation, not a gate.
    private const int TargetCooldownMs = 5_000;
    private const int SelfCooldownMs = 20_000;

    private static int CooldownFor(int slot) => slot == SelfSlotIndex ? SelfCooldownMs : TargetCooldownMs;

    // ★ HOW CLOSE THE VICTIM HAS TO BE - and this was FAR too generous at first. 25 units is very nearly
    // the ARCHER'S BOW RANGE (30f); the whole codebase's melee reach is 7f, and a collection node is
    // clickable from 12. A whip is a couple of arm-lengths, so it sits just past melee: you have to be
    // next to someone to crack it at them, which is what it looked like in retail.
    //
    // Settable so the feel can be dialled without a rebuild: `!yoyofx reach <units>`.
    public static float ReachUnits { get; set; } = 4f;

    private static float ReachSquared => ReachUnits * ReachUnits;

    public static bool IsEquipped(Player player) =>
        player.GetEquippedWeaponDefinitionId() == WhipItemDefinitionId;

    public static AbilityPacketSetDefinition? BuildToolbar(Player player)
    {
        if (!IsEquipped(player))
            return null;

        SendAbilityDefinitions(player);

        var def = AbilityPacketSetDefinition.CreateEmpty(player.ActiveProfileId);
        def.Slots.Add(MakeSlot(TargetAbilityId));
        def.Slots.Add(MakeSlot(SelfAbilityId));
        return def;
    }

    // slot 0 = crack at the selected player (turns THEM into a bulb), slot 1 = turn yourself into one.
    public static bool TryPerform(Player player, int slot, ulong targetGuid, IResourceManager resources)
    {
        // Before every guard, so a press the server REJECTS still shows up - see PropAnimation.TracePress.
        PropAnimation.TracePress(player, "whip", slot);

        if (!IsEquipped(player))
            return false;

        if (slot is not (TargetSlotIndex or SelfSlotIndex))
            return false;

        // Which fidget belongs to which ability is unconfirmed - see PropAnimation.SwapSlotAnimations.
        var animationId = PropAnimation.ClipForSlot(
            slot == SelfSlotIndex, TargetAnimationId, SelfAnimationId);

        // ★ NOT WHILE TRANSFORMED OR MOUNTED - a bulb critter has no whip animation branch, and a rider is
        // seated on the mount actor. Also stops the obvious loop of whipping yourself, becoming a bulb, and
        // whipping again from inside the bulb.
        if (player.TemporaryAppearance != 0 || player.Mount is not null)
            return false;

        var abilityId = slot == SelfSlotIndex ? SelfAbilityId : TargetAbilityId;

        if (PropAnimation.IsOnCooldown(player, abilityId))
            return false;

        PropAnimation.StartCooldown(player, abilityId, CooldownFor(slot));

        // The crack always plays, even when nobody is hit - the whoosh/crack ride this clip. The idle
        // reset that follows is what lets the player animate again afterwards (see PropAnimation).
        // Inert unless the cast-lock experiment is switched back on - it glitched the character, see
        // PropAnimation.SendCastLock.
        PropAnimation.SendCast(player, animationId, CooldownFor(slot));

        PropAnimation.PlayOneShot(player, animationId);

        // Either way the transform waits for the crack to finish - popping someone mid-swing threw the
        // animation away. Ability 2 additionally wraps the wielder in the snow tornado while it winds up.
        if (slot == SelfSlotIndex)
            TransformSelfAfterWindup(player);
        else if (ResolveVictim(player, targetGuid) is { } victim)
            TransformAfterWindup(victim);

        // Grey + sweep the button for the real duration, and hand it back when it expires. Needs BOTH
        // packets - see PropAnimation.SendCooldown; MeleeRefresh alone drew no radial. The victim's guid
        // is what LaunchAndLand aims its sweep at, falling back to the caster for the self-transform.
        PropAnimation.SendCooldown(player, Combat.JobWeaponAbilities.BuildToolbar(player, resources), CooldownFor(slot),
            slot == SelfSlotIndex ? 0 : targetGuid);

        return true;
    }

    // Who the crack lands on.
    //
    // ★ IT CANNOT RELY ON THE PACKET'S TARGET GUID. That field is the COMBAT target, and Free Realms has no
    // way to select another PLAYER as one - there is nothing to click, so the client sends 0 and ability 1
    // hit nobody, which is exactly how it was first written and exactly why it did nothing. The guid is
    // still honoured when one does arrive (it costs nothing and covers a targeted case), but the real
    // targeting is spatial: you crack the whip at whoever is in front of you.
    //
    // Preference order: an explicit selection, then the nearest valid player within the forward cone, then
    // the nearest valid player in range at all (so it still lands when the facing is a little off).
    private static Player? ResolveVictim(Player player, ulong targetGuid)
    {
        if (targetGuid != 0 && targetGuid != player.Guid &&
            player.Zone is { } zone && zone.TryGetPlayer(targetGuid, out var selected) &&
            selected is not null && CanBeWhipped(player, selected))
        {
            return selected;
        }

        // Facing, as the other position-aware abilities compute it (see CakeAbility's throw direction).
        var forward = Vector3.Transform(new Vector3(0, 0, 1), player.Rotation);

        Player? bestInFront = null, bestAnywhere = null;
        float bestInFrontD2 = ReachSquared, bestAnywhereD2 = ReachSquared;

        foreach (var candidate in player.VisiblePlayers.Values)
        {
            if (!CanBeWhipped(player, candidate))
                continue;

            var dx = candidate.Position.X - player.Position.X;
            var dz = candidate.Position.Z - player.Position.Z;
            var d2 = dx * dx + dz * dz;

            if (d2 > ReachSquared)
                continue;

            if (d2 < bestAnywhereD2)
            {
                bestAnywhereD2 = d2;
                bestAnywhere = candidate;
            }

            // Dot > 0 is the 180-degree arc ahead of the caster - deliberately wide, since a whip crack is
            // a party trick and being fussy about aim would just make it feel broken.
            if (dx * forward.X + dz * forward.Z > 0 && d2 < bestInFrontD2)
            {
                bestInFrontD2 = d2;
                bestInFront = candidate;
            }
        }

        return bestInFront ?? bestAnywhere;
    }

    private static void SendTornadoRemoval(Player player) =>
        player.SendTunneledToVisible(new PlayerUpdatePacketRemoveEffectTagCompositeEffect
        {
            Guid = player.Guid,
            TagId = SnowTornadoTagId,
        }, sendToSelf: true);

    // In range, and not someone the transform would break (see Transform).
    private static bool CanBeWhipped(Player caster, Player candidate)
    {
        if (candidate.Guid == caster.Guid || candidate.TemporaryAppearance != 0 || candidate.Mount is not null)
            return false;

        var dx = candidate.Position.X - caster.Position.X;
        var dz = candidate.Position.Z - caster.Position.Z;

        return dx * dx + dz * dz <= ReachSquared;
    }

    // Already-transformed players are left alone, matching the transform foods' own guard - re-applying
    // would stack timers and could strand someone as a bulb.
    private static void Transform(Player target)
    {
        // Not over an existing transform, and not onto a MOUNTED player - the client would be left with a
        // bulb critter sitting on a horse, and the rider still owes the mount a dismount.
        if (target.TemporaryAppearance != 0 || target.Mount is not null)
            return;

        target.ApplyTemporaryAppearance(LightbulbCritterModelId, TransformDurationMs);
    }

    // Ability 2's wind-up: the snow tornado spins around the wielder, then they become the bulb. The
    // transform is deliberately deferred to the END of it - the tornado is what sells the change, so
    // popping the model on the first frame would waste it.
    private static void TransformSelfAfterWindup(Player player)
    {
        if (player.TemporaryAppearance != 0)
            return;

        // ★ CLEAR ANY STALE TORNADO FIRST. The effect is a LOOP on a fixed tag id, and its removal is
        // delayed - if an earlier one's removal was ever missed (the player zoned, or a viewer only saw the
        // attach), the snow would still be spinning and attaching again would sit a second loop on top of
        // it. Removing a tag that isn't there is a no-op, so this is free insurance.
        SendTornadoRemoval(player);

        player.SendTunneledToVisible(new PlayerUpdatePacketAddEffectTagCompositeEffect
        {
            Guid = player.Guid,
            TagId = SnowTornadoTagId,
            CompositeEffectId = SnowTornadoFxId,
            SourceGuid = player.Guid,
        }, sendToSelf: true);

        // The tag comes off on the tick loop's delayed-packet queue...
        player.SendTunneledToVisibleDelayed(new PlayerUpdatePacketRemoveEffectTagCompositeEffect
        {
            Guid = player.Guid,
            TagId = SnowTornadoTagId,
        }, WindupMs, sendToSelf: true);

        // ...with a second, later removal as a backstop. The queue is FIFO by INSERTION, not by due time,
        // so a longer-delayed packet queued ahead of this one can hold it up; a lingering snow loop is far
        // more noticeable than a redundant remove, which does nothing.
        player.SendTunneledToVisibleDelayed(new PlayerUpdatePacketRemoveEffectTagCompositeEffect
        {
            Guid = player.Guid,
            TagId = SnowTornadoTagId,
        }, WindupMs + 2000, sendToSelf: true);

        // ...and the transform lands with it.
        TransformAfterWindup(player);
    }

    // Waits out the whip clip, then transforms. Same deferred-work shape the snowball guard's bubble
    // teardown uses, guarded so a fault here can't take down the ability press that scheduled it.
    private static void TransformAfterWindup(Player target)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(WindupMs);
                Transform(target);
            }
            catch
            {
                // The player may have gone; nothing here is worth failing the press over.
            }
        });
    }

    private static void SendAbilityDefinitions(Player player)
    {
        Send(TargetAbilityId);
        Send(SelfAbilityId);

        void Send(int abilityId) =>
            player.SendTunneled(new AbilityPacketAbilityDefinition
            {
                AbilityId = abilityId,
                NameId = WhipNameId,
                DescriptionId = WhipDescriptionId,
                IconId = WhipIconId,
            });
    }

    private const int WhipDescriptionId = 441922;

    private static AbilityPacketSetDefinition.Slot MakeSlot(int abilityDefId) => new()
    {
        Type = 3,
        Unknown2 = abilityDefId,
        ManaCost = 0,
        IconId = abilityDefId == SelfAbilityId ? LightbulbCritterIconId : WhipIconId,
        NameId = WhipNameId,
        Unknown8 = (abilityDefId == SelfAbilityId ? SelfCooldownMs : TargetCooldownMs) / 1000f,
        AbilityDefinitionId = abilityDefId,
    };
}
