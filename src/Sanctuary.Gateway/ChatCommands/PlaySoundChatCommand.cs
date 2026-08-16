using System.Numerics;

using Sanctuary.Game.ChatCommands;
using Sanctuary.Game.Entities;
using Sanctuary.Packet;

namespace Sanctuary.Gateway.ChatCommands;

// Fires CommandPacketPlaySoundIdOnTarget (op26/39) at the invoker ONLY.
//
// ★ This exists because the first attempt at that packet CRASHED every client entering Snowhill: it was
// wired into the per-viewer npc path, so a wrong body layout took down everyone who walked in rather than
// the one person testing it. The layout has since been derived properly from the client's own factory and
// readers (see the packet), but "derived" is not "confirmed", and the cost of being wrong here is a client
// crash - so it gets proven on one consenting client before any world npc is allowed to send it.
//
//   /playsound <soundId>            - no target (type 0; the client provably skips the factory - safest)
//   /playsound <soundId> <guid>     - anchored on an actor at its position (type 1)
//   /playsound <soundId> me         - anchored on the invoker
//
// Sound ids come from the client's ActorSoundEmitterDefinitions.xml, NOT from the composite effect table.
// Worth knowing: 17716 = MX_Bruce_ItsYourWorld_loop (loops forever), 17715 = the same track once through,
// 17450 = MX_Robgolbin_RockBand.
public class PlaySoundChatCommand : GatewayChatCommand
{
    public PlaySoundChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "playsound";
    public override string Usage => "<soundId> [me|<guid>]";
    public override string Description => "Plays a sound-emitter id on yourself (op26/39 test).";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Admin;

    public override bool Handle(Player invoker, string[] args)
    {
        if (args.Length < 1 || !int.TryParse(args[0], out var soundId))
            return false;

        var packet = new CommandPacketPlaySoundIdOnTarget { SoundId = soundId };

        if (args.Length >= 2)
        {
            ulong guid = 0;
            var position = invoker.Position;

            if (args[1].Equals("me", System.StringComparison.OrdinalIgnoreCase))
            {
                guid = invoker.Guid;
            }
            else if (ulong.TryParse(args[1], out var parsed))
            {
                guid = parsed;

                // Anchor on the actor's real position when we can find it, so the emitter attenuates
                // around the thing it is supposed to be coming from.
                if (invoker.Zone.TryGetNpc(parsed, out var npc))
                    position = npc.Position;
            }

            if (guid != 0)
            {
                packet.TargetType = CommandPacketPlaySoundIdOnTarget.TargetPositionAndActor;
                packet.TargetGuid = guid;
                packet.TargetPosition = new Vector4(position.X, position.Y, position.Z, 1f);
            }
        }

        // Deliberately ONLY to the invoker - see the note above.
        invoker.SendTunneled(packet);
        return true;
    }
}
