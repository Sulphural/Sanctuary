using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

using Sanctuary.Game.Entities;
using Sanctuary.Packet;

namespace Sanctuary.Game.Interactions;

// One-shot animations for held props (the yo-yo's tricks, the whip's cracks).
//
// ★★ THE ANIMATION MUST BE PUT BACK TO IDLE AFTERWARDS. A set animation is HELD until something replaces
// it - the client does not fall back on its own - so a prop clip that is never reset leaves the player
// stuck in it: no later emote, dance or prop move will play, and a transform's model swap fights the held
// clip. That was one bug reported as two ("it won't do any other animation" AND "it bugs out when trying
// to do another transformation after the whip"). Every other animation user in the tree already does this
// reset - SnowballTool after its throw gesture, GatheringManager after the dig, BoomboxAbility.StopDancing
// - and the props were the odd ones out on a wrong assumption that upper-body one-shots self-terminate.
//
// ★ THE RESET IS TICKETED. It lands one clip later, and the yo-yo can be pressed again inside that window
// (2s cooldown vs a 2.06s clip), so an untickected reset from press #1 would cut press #2 short a tenth of
// a second in. Only the most recent play for a player is allowed to reset them.
public static class PropAnimation
{
    // StopDancing's idle id - the proven way to put a player back to their default animation.
    private const int IdleAnimationId = 1;

    // Both props' ambient clips run 2.063s - read from the .adr event timelines, which is the only length
    // available (the .gr2 is Granny with Oodle-compressed sections). 2100 clears it by a hair.
    public const int ClipMs = 2100;

    private static readonly ConcurrentDictionary<ulong, int> _tickets = new();

    // ★★ ALWAYS RESET WITH ID 1 - IT IS A "CLEAR TO DEFAULT" SENTINEL, NOT A CLIP TO PLAY.
    //
    // A previous version passed the prop's OWN branch stand here (loc_yoyo_stand 43100025 /
    // loc_whip_stand 43100028) on the reasoning that a wielded player is in that branch, so returning them
    // to the bare-handed clip must be wrong. That reasoning was backwards and it bugged the yo-yo badly:
    // those are LOCOMOTION slots, which the CLIENT drives itself off the wield type (stand/walk/run/jump
    // all come from the branch automatically). Forcing one pins the character in that pose and fights the
    // client's own locomotion state machine. Id 1 doesn't force a pose - it hands control back, which is
    // why StopDancing, GatheringManager, SnowballTool, QuestDialogue and CombatNpc all use exactly it.
    public static void PlayOneShot(Player player, int animationId)
    {
        var ticket = _tickets.AddOrUpdate(player.Guid, 1, (_, previous) => previous + 1);

        // PlayType 2 = "play now", the one-shot action-clip mechanism (the same one the gather channel's
        // dig animation uses). Everyone who can see the player gets it - a trick nobody else sees is
        // pointless.
        player.SendTunneledToVisible(new PlayerUpdatePacketSetAnimation
        {
            Guid = player.Guid,
            AnimationId = animationId,
            PlayType = 2
        }, sendToSelf: true);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ClipMs);

                // Superseded by a later prop animation - that one owns the reset now.
                if (!_tickets.TryGetValue(player.Guid, out var current) || current != ticket)
                    return;

                player.SendTunneledToVisible(new PlayerUpdatePacketSetAnimation
                {
                    Guid = player.Guid,
                    AnimationId = IdleAnimationId,
                    PlayType = 1
                }, sendToSelf: true);
            }
            catch
            {
                // The player may have gone; a missed idle reset is not worth failing anything over.
            }
        });
    }

    // ★★ THE MISSING HALF OF THE COOLDOWN VISUAL: TELL THE CLIENT A CAST HAPPENED.
    //
    // The toolbar slot already carries its own recharge (Slot.Unknown8, in seconds) and the client is
    // documented to draw the sweep ITSELF "on cast" - but nothing was ever telling it a cast occurred, so
    // it had a duration and no event to start it from. The other two packets can't cover that gap:
    // MeleeRefresh carries no slot (one global cooldown-end, so it greys but can't aim), and LaunchAndLand's
    // sweep only renders against a REAL ENEMY target - aimed at yourself or another player it silently
    // no-ops, which is every case a prop has.
    //
    // StartCasting is the cast event, and its ActionTime is what LOCKS the action-bar slot for the
    // duration (the combat kits use it exactly so you can't fire again mid-swing).
    //
    // ★★ TRIED AND REJECTED - DEFAULT OFF, DO NOT TURN IT BACK ON WITHOUT A NEW IDEA.
    //
    // The snowball throw had already tried StartCasting for this exact purpose and reverted it, because the
    // client plays an animation off it that cannot be suppressed. The reasoning here was that a prop is the
    // one case where that is harmless - we want a clip anyway, so pass the prop's own fidget id. That was
    // WRONG: tested live 2026-08-18 and it "glitched the character really bad". Passing the prop's own
    // animation id does not tame it; StartCasting's animation resolves through a different, per-model
    // table (the same reason the gather channel's dig id played nothing through this field), so the id we
    // send is not the clip it plays, and whatever it does play fights the real one.
    //
    // The code stays, off, as the record of a dead end - the sweep on a prop slot is still unsolved.
    public static bool SendCastLock { get; set; }

    public static void SendCast(Player player, int animationId, int cooldownMs)
    {
        if (!SendCastLock)
            return;

        player.SendTunneledToVisible(new AbilityPacketStartCasting
        {
            Unknown = player.Guid,             // caster
            Unknown2 = player.Guid,            // no separate target for a prop
            Animation = animationId,           // the prop's own clip, so an unsuppressable play is the right one
            ActionTime = cooldownMs / 1000f,   // what locks the slot for the cooldown
            HasActionProgress = false,         // a lock, not a cast bar
        }, sendToSelf: true);
    }

    // ★ WHICH FIDGET BELONGS TO WHICH ABILITY IS A GUESS, AND IT IS FLIPPABLE HERE.
    //
    // Nothing in the client names the two fidget clips - the `.adr` binds `loc_*_fidget_01/02` to
    // `*_amb_01/02.gr2` and stops there. The ability NAMES came from adjacent string ids ("Throw Down"
    // 440246 then "Around the World" 440247), so slot 0 was paired with fidget_01 purely because it was
    // listed first. That is a coin flip, and live reports say the pairing looks wrong.
    //
    // `!yoyofx swap` flips both props' slot->clip mapping so the right one can be confirmed by eye rather
    // than argued about; whichever wins gets baked in as the default.
    public static bool SwapSlotAnimations { get; set; }

    // The clip for a pressed slot, honouring the swap switch. `first` is the fidget_01 id.
    public static int ClipForSlot(bool isSecondSlot, int first, int second) =>
        (isSecondSlot ^ SwapSlotAnimations) ? second : first;

    // ★★ THE DECIDING DIAGNOSTIC for "the abilities randomly play by themselves".
    //
    // There are exactly two explanations and they need opposite fixes:
    //   (a) the CLIENT is sending presses nobody made - its ability auto-repeat - and the server is
    //       faithfully playing them. Then the fix is server-side: stop re-arming the repeat.
    //   (b) NO packet arrives, and the animation is being replayed client-side (or by some other packet
    //       we send). Then nothing about the press path can fix it.
    // Turning this on echoes EVERY prop press the server receives, including ones the guards reject. A
    // random play WITH a line is (a); a random play with NO line is (b). `!yoyofx trace on`.
    public static bool Trace { get; set; }

    public static void TracePress(Player player, string prop, int slot)
    {
        if (Trace)
            player.SendSystemMessage($"[prop] {prop} press: slot {slot} @ {DateTime.UtcNow:HH:mm:ss.fff}");
    }

    // ★★ THE FIX FOR "holding the prop makes every other animation glitch out".
    //
    // The cause is client data we can't edit: EVERY wieldable branch's locomotion slots carry
    // `interruptOneShots="1"` - including their STAND - and emotes/dances are one-shots. The bare-handed
    // `loc_stand` (id 1) has no such flag, which is why animations behave normally unarmed. The whip is the
    // worst offender because its stand is also priority="2", beating the base 0 outright.
    //
    // What the server CAN change is which branch the client is in at all: that is chosen by the WIELD TYPE
    // sent on the equip change. Reporting 0 puts the player in the ordinary unarmed locomotion set, so
    // one-shots stop being interrupted - while the prop stays visibly in hand, because the attachment
    // (model/texture/slot) is a separate thing and the model's own attach point is R_WEAPON regardless.
    //
    // ★ THE TRADE: you lose the prop's bespoke walk/run/jump styling (the "strolling along yo-yoing" look).
    // The two prop MOVES are unaffected - those are sent explicitly by animation id rather than picked from
    // the branch. `!yoyofx wield on` restores the prop branch if the styling matters more than emotes.
    public static bool SuppressPropWieldType { get; set; } = true;

    // The wield type to report for an equipped item. Only the props are overridden; everything else keeps
    // whatever its item class says.
    public static int EffectiveWieldType(int itemDefinitionId, int classWieldType)
    {
        if (!SuppressPropWieldType)
            return classWieldType;

        return itemDefinitionId is YoYoTricks.YoYoItemDefinitionId or LightStrandWhip.WhipItemDefinitionId
            ? 0
            : classWieldType;
    }

    // ---- Cooldowns -------------------------------------------------------------------------------
    //
    // ★ TWO HALVES, AND THE PROPS ORIGINALLY HAD NEITHER PROPERLY. The button's grey/sweep is CLIENT-side
    // presentation, and it is NOT a gate: without a server-side check the ability can still be spammed by
    // pressing through it. So every press goes through IsOnCooldown/StartCooldown here, and SendCooldown
    // draws it.
    //
    // ★ THE VISUAL NEEDS BOTH PACKETS. MeleeRefresh alone (which is all the props sent) carries the
    // cooldown-END but renders no radial; AbilityPacketLaunchAndLand is what actually starts the SWEEP on
    // the slots. This mirrors SnowballTool.SendCooldown, the proven path for a non-combat prop ability.
    // Note LaunchAndLand's target resolution wants a guid to aim at and falls back to the caster's own.
    private static readonly ConcurrentDictionary<(ulong Player, int Ability), DateTime> _cooldowns = new();

    public static bool IsOnCooldown(Player player, int abilityId) =>
        _cooldowns.TryGetValue((player.Guid, abilityId), out var readyAt) && DateTime.UtcNow < readyAt;

    public static void StartCooldown(Player player, int abilityId, int cooldownMs) =>
        _cooldowns[(player.Guid, abilityId)] = DateTime.UtcNow.AddMilliseconds(cooldownMs);

    // ★★ THE TOOLBAR RE-SEND IS PART OF THE COOLDOWN, NOT A NICETY. op36/5 is the authoritative statement
    // of what the slots ARE - including each slot's own recharge (the Slot.Unknown8 float the client
    // animates its sweep from). Without it the client keeps whatever ability state it last latched onto,
    // and a burst of presses on slot 0 left it replaying slot 0's animation when slot 1 was pressed.
    //
    // ★ ORDER MATTERS: re-arm the slots FIRST, then start the cooldown. The other way round, the fresh
    // definition wipes the cooldown that was just started and the button never greys at all.
    public static void SendCooldown(Player player, AbilityPacketSetDefinition? bar, int cooldownMs, ulong targetGuid = 0)
    {
        if (bar is not null && RefreshBarOnUse)
            player.SendTunneled(bar);

        player.SendTunneled(new AbilityPacketMeleeRefresh { CooldownMs = cooldownMs });

        if (!SendSweep)
            return;

        // Falls back to the caster's own guid when there's nobody else to aim at - the same thing
        // SnowballTool.SendCooldown does for the guard, which likewise targets no one. An earlier version
        // SKIPPED the send entirely in that case, on the theory that self-aimed resolution was replaying
        // the ability; that theory was wrong (the replay outlived it), and skipping meant ability 2 and
        // every yo-yo trick silently got no sweep at all.
        if (targetGuid == 0)
            targetGuid = player.Guid;

        player.SendTunneled(new AbilityPacketLaunchAndLand
        {
            Guid = player.Guid,
            Guid2 = targetGuid,
            Guid3 = targetGuid,
            Position = player.Position,
        });
    }

    // The cooldown RADIAL (AbilityPacketLaunchAndLand). ON - the radial is wanted.
    //
    // It was briefly suspected of causing a prop's animation/effect to replay by itself (it makes the
    // client resolve and PRESENT the ability, not just draw a sweep) and turned off to test - the replay
    // still happened, so it is NOT the cause and it stays on.
    public static bool SendSweep { get; set; } = true;

    // The per-use toolbar re-send - ON, and needed: it is what stops the client replaying the PREVIOUS
    // slot's animation when the other one is pressed (op36/5 is the authoritative slot state, and each
    // slot's recharge rides in it).
    //
    // ★ IT IS ALSO THE REMAINING SUSPECT for a prop's animation/effect replaying by itself, because a
    // re-sent bar is exactly what KEEPS THE CLIENT'S ABILITY AUTO-REPEAT ALIVE (the documented reason the
    // ranged auto-fire needs a toolbar restore after a profile re-send - see the combat XP-bar notes). Both
    // behaviours are wanted, so they can't simply be traded off; `!yoyofx refresh off` exists to CONFIRM
    // the cause, after which the fix is to re-send only when the pressed slot differs from the last one,
    // rather than on every press.
    public static bool RefreshBarOnUse { get; set; } = true;

    // Called when a player leaves so the ticket/cooldown tables don't grow forever.
    public static void Forget(ulong playerGuid)
    {
        _tickets.TryRemove(playerGuid, out _);

        foreach (var key in _cooldowns.Keys)
        {
            if (key.Player == playerGuid)
                _cooldowns.TryRemove(key, out _);
        }
    }
}
