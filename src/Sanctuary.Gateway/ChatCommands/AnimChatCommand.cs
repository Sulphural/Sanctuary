using System;
using System.Linq;
using System.Numerics;

using Sanctuary.Game.ChatCommands;
using Sanctuary.Game.Entities;
using Sanctuary.Packet;

namespace Sanctuary.Gateway.ChatCommands;

// Plays an arbitrary animation id on the nearest NPC (or yourself), so animation ids can be tried live
// instead of guessed and rebuilt. Ids are the client's Resources/AnimationGroups.xml group ids.
//
// Worth knowing when hunting for one: every emote slot in AnimationTypes.xml is type="4", but they split
// into priority="0" (emo_laugh 3316 - the id in the live 2014 capture that op35/8 was verified against -
// plus cheer/wave/point/shrug...) and priority="1" (the whole emo_talk_* family, 3101-3112). If a
// priority-1 id plays nothing here while a priority-0 id works, that split is the reason.
public class AnimChatCommand : GatewayChatCommand
{
    public AnimChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "anim";
    public override string Usage => "<animationId> [me] [playType]";
    public override string Description => "Plays an animation id on the nearest NPC (or yourself).";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Admin;

    public override bool Handle(Player invoker, string[] args)
    {
        var conn = GetConnection(invoker);
        if (conn is null)
            return true;

        if (args.Length < 1 || !int.TryParse(args[0], out var animationId))
        {
            CommandSupport.SendSystem(conn, $"Usage: /{KeyWord} {Usage}");
            return true;
        }

        bool onSelf = args.Any(a => a.Equals("me", StringComparison.OrdinalIgnoreCase));

        // PlayType 2 = "play now" (every live sample); 1 = set the entity's base/idle animation instead.
        byte playType = 2;
        foreach (var arg in args.Skip(1))
            if (byte.TryParse(arg, out var parsed))
                playType = parsed;

        string name = string.Empty;
        var target = onSelf ? invoker.Guid : NearestNpcGuid(invoker, out name);
        if (target == 0)
        {
            CommandSupport.SendSystem(conn, "No NPC nearby.");
            return true;
        }

        invoker.SendTunneled(new PlayerUpdatePacketSetAnimation
        {
            Guid = target,
            AnimationId = animationId,
            PlayType = playType
        });

        CommandSupport.SendSystem(conn,
            $"[anim] id={animationId} playType={playType} -> {(onSelf ? "you" : $"{name} ({target})")}");
        return true;
    }

    private static ulong NearestNpcGuid(Player player, out string name)
    {
        var from = new Vector3(player.Position.X, player.Position.Y, player.Position.Z);

        var nearest = player.VisibleNpcs.Values
            .Select(npc => new
            {
                Npc = npc,
                Distance = Vector3.Distance(from, new Vector3(npc.Position.X, npc.Position.Y, npc.Position.Z))
            })
            .OrderBy(x => x.Distance)
            .FirstOrDefault();

        name = nearest?.Npc.Name ?? nearest?.Npc.NameId.ToString() ?? string.Empty;
        return nearest?.Npc.Guid ?? 0;
    }
}
