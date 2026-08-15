using System;

using Sanctuary.Game.ChatCommands;
using Sanctuary.Game.Entities;

namespace Sanctuary.Gateway.ChatCommands;

// Live probe for the projectile carrier's AddNpc identity fields, written to settle ONE specific bug: the
// carrier grows a health bar once the player is in overworld combat (op41 sub132/133), so a snowball thrown
// on a fresh login is clean and every projectile after the first bow shot has a bar floating along with it.
//
// The flags that should prevent it (ShowHealthBar false, MaxHealth 0, HideNamePlate true) are ALREADY set
// and are enough out of combat, so the client is keying on something else in that state and there is no
// capture or disassembly here that says what. Rather than guess-and-rebuild, flip the candidates in-game:
//
//   1. /projectile            - show the current values
//   2. shoot a bow (to enter combat), then throw/shoot and watch for the bar
//   3. change one field, repeat, and note which one makes it stop
//
// Whichever wins becomes the default in ProjectileNpc and this command can go.
public class ProjectileChatCommand : GatewayChatCommand
{
    public ProjectileChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "projectile";
    public override string Usage => "[profile <n>] [disposition <0|1|2>] [nameid <id>] [status <n>]";
    public override string Description => "Tunes the projectile carrier's AddNpc fields (health-bar probe).";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Admin;

    public override bool Handle(Player invoker, string[] args)
    {
        var verb = args.Length >= 1 ? args[0].ToLowerInvariant() : string.Empty;

        switch (verb)
        {
            // ★ Must stay non-zero: 0 short-circuits the client's nameplate resolver and restores the ctor's
            // default ally plate, i.e. a bar in EVERY state instead of only in combat. Other non-zero values
            // are fair game - 8 is just what the live heart capture used.
            case "profile" when args.Length >= 2 && int.TryParse(args[1], out var profile):
                ProjectileNpc.CarrierActiveProfile = profile;
                Reply(invoker, profile == 0
                    ? "[projectile] ActiveProfile 0 will bring the bar back in ALL states - use a non-zero value."
                    : $"[projectile] Carrier ActiveProfile = {profile}.");
                return true;

            case "disposition" when args.Length >= 2 && int.TryParse(args[1], out var disposition):
                ProjectileNpc.CarrierDisposition = Math.Clamp(disposition, 0, 2);
                Reply(invoker, $"[projectile] Carrier Disposition = {ProjectileNpc.CarrierDisposition} (0 hostile, 1 neutral, 2 ally).");
                return true;

            // The carrier ships NameId 0. The one prop proven bar-less under a combat-ish state (the Frostfang
            // heart) carries a REAL name id instead, so an unresolvable name is a live suspect: a plate that
            // fails to resolve may fall back to a default plate, and the default plate has a bar.
            case "nameid" when args.Length >= 2 && int.TryParse(args[1], out var nameId):
                ProjectileNpc.CarrierNameId = nameId;
                Reply(invoker, $"[projectile] Carrier NameId = {nameId} (0 = nameless; 5102381 is the heart's).");
                return true;

            // The CharacterStatus bitfield sent as op35/20, i.e. the nameplate-suppression lever. The client's
            // plate gate (FUN_009d08f0) hides every plate element when the status matches mask 0x1542CE; 9 =
            // IsNonAttackable|IsSilenced is the default, and the other inert bits in that mask are 0x4000
            // IsGoingHome and 0x100000 IsPoppedUp. 0 = the old bar-showing behavior.
            case "status" when args.Length >= 2 && int.TryParse(args[1], out var status):
                ProjectileNpc.CarrierStatus = (Sanctuary.Packet.CharacterStatus)status;
                Reply(invoker, $"[projectile] Carrier CharacterStatus = {status} ({ProjectileNpc.CarrierStatus}).");
                return true;
        }

        Reply(invoker, $"[projectile] ActiveProfile={ProjectileNpc.CarrierActiveProfile} " +
                       $"Disposition={ProjectileNpc.CarrierDisposition} NameId={ProjectileNpc.CarrierNameId} " +
                       $"Status={(int)ProjectileNpc.CarrierStatus}. " +
                       "Enter combat with a bow first - the bar only appears in world-combat state.");
        return true;
    }
}
