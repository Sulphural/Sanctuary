using Sanctuary.Game.ChatCommands;
using Sanctuary.Game.Entities;
using Sanctuary.Packet;

namespace Sanctuary.Gateway.ChatCommands;

// Bisects the two overworld combat-state flags, written for ONE bug: entering combat turns on a health bar
// over EVERY npc in the world (live 2026-08-13) - quest givers, props, and the invisible carrier of every
// arrow and snowball in flight. That is the encounter/arena combat HUD, where everything in the world really
// is a combatant; it does not belong in the overworld.
//
// The server sends two flags on entering combat:
//   op41/132 EncounterOverworldCombatPacket -> BaseClient::SetInWorldCombat
//   op41/133 EncounterPacketIsFighting      -> BaseClient::SetIsFighting
//
// ANSWER (bisected live 2026-08-13): they cannot be separated. 132 is a master switch for the whole combat
// HUD - dropping it does remove the health bars, and removes the floating damage numbers with them. So the
// bars are simply what the client does in combat, 132 stays on, and the projectile carrier is excluded on
// the carrier itself instead (ProjectileNpc.ShowTo -> CharacterStatus.IsNonAttackable).
//
// Kept so this can be re-checked without a rebuild, and because /worldcombat 132 off is a quick way to prove
// whether any future "bar showed up somewhere" report is this HUD or something else.
public class WorldCombatChatCommand : GatewayChatCommand
{
    public WorldCombatChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "worldcombat";
    public override string Usage => "[132|133 on|off]";
    public override string Description => "Toggles the overworld combat-state flags (health-bar bisect).";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Admin;

    public override bool Handle(Player invoker, string[] args)
    {
        if (args.Length >= 2)
        {
            var on = args[1].Equals("on", System.StringComparison.OrdinalIgnoreCase);

            switch (args[0])
            {
                case "132":
                    Player.SendInWorldCombatFlag = on;
                    // Clear it on the client immediately, otherwise a state it is ALREADY in survives the
                    // switch and the next test reads as "turning it off changed nothing".
                    if (!on)
                        invoker.SendTunneled(new EncounterOverworldCombatPacket { Unknown3 = false });
                    Reply(invoker, $"[worldcombat] op41/132 SetInWorldCombat = {on}.");
                    return true;

                case "133":
                    Player.SendIsFightingFlag = on;
                    if (!on)
                        invoker.SendTunneled(new EncounterPacketIsFighting { InWorldCombat = false });
                    Reply(invoker, $"[worldcombat] op41/133 SetIsFighting = {on}.");
                    return true;
            }
        }

        Reply(invoker, $"[worldcombat] 132 SetInWorldCombat={Player.SendInWorldCombatFlag}, " +
                       $"133 SetIsFighting={Player.SendIsFightingFlag}. " +
                       "Fight something and check for (a) bars on every npc, (b) floating damage numbers.");
        return true;
    }
}
