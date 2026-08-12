using System;
using System.Linq;
using System.Numerics;

using Sanctuary.Game.ChatCommands;
using Sanctuary.Game.Entities;

namespace Sanctuary.Gateway.ChatCommands;

// Lists the NPCs the server believes this player can currently see, with the fields that decide whether
// a thing is clickable. Answers "is that prop actually one of my entities, or just world scenery?" -
// which is otherwise unknowable from in-game, and is exactly the question a quest collectible that
// refuses clicks raises.
public class NearbyChatCommand : GatewayChatCommand
{
    public NearbyChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "nearby";
    public override string Usage => "[radius]";
    public override string Description => "Lists visible NPCs and why they are (or aren't) clickable.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Admin;

    public override bool Handle(Player invoker, string[] args)
    {
        var conn = GetConnection(invoker);
        if (conn is null)
            return true;

        float radius = args.Length >= 1 && float.TryParse(args[0], out var parsed) ? parsed : 25f;

        var from = new Vector3(invoker.Position.X, invoker.Position.Y, invoker.Position.Z);

        var found = invoker.VisibleNpcs.Values
            .Select(npc => new
            {
                Npc = npc,
                Distance = Vector3.Distance(from, new Vector3(npc.Position.X, npc.Position.Y, npc.Position.Z))
            })
            .Where(x => x.Distance <= radius)
            .OrderBy(x => x.Distance)
            .Take(15)
            .ToList();

        CommandSupport.SendSystem(conn,
            $"[nearby] {found.Count} visible NPC(s) within {radius:0}m of ({from.X:0.0}, {from.Y:0.0}, {from.Z:0.0}):");

        foreach (var x in found)
        {
            var npc = x.Npc;

            // The exact inputs to the clickability decision: CursorId + HasCursor (from the badge or a
            // plain InteractAction) client-side, InteractRange server-side.
            string how = npc.InteractAction is not null ? "action"
                : npc.InteractionProviders.Count > 0 ? $"providers({npc.InteractionProviders.Count})"
                : "NONE";

            CommandSupport.SendSystem(conn,
                $"  {x.Distance:0.0}m guid={npc.Guid} model={npc.ModelId} nameId={npc.NameId} " +
                $"'{npc.Name ?? ""}' cursor={npc.CursorId} badge={invoker.GetNotificationImageId(npc)} " +
                $"interact={how} range={npc.InteractRange:0.#}");
        }

        if (found.Count == 0)
            CommandSupport.SendSystem(conn, "  (nothing - if you can SEE a prop here, it is world scenery, not a server entity)");

        return true;
    }
}
