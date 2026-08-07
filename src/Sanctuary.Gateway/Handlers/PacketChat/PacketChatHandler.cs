using System;
using System.Linq;
using System.Numerics;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.Helpers;
using Sanctuary.Core.IO;
using Sanctuary.Database;
using Sanctuary.Database.Entities;
using Sanctuary.Game;
using Sanctuary.Game.Combat;
using Sanctuary.Game.Entities;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.Attributes;
using Sanctuary.Packet.Common.Chat;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class PacketChatHandler
{
    private static ILogger _logger = null!;
    private static ILogger _chatLogger = null!;
    private static IZoneManager _zoneManager = null!;
    private static IResourceManager _resourceManager = null!;
    private static IChatCommandManager _chatCommandManager = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(PacketChatHandler));
        _chatLogger = loggerFactory.CreateLogger("Chat");

        _zoneManager = serviceProvider.GetRequiredService<IZoneManager>();
        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
        _chatCommandManager = serviceProvider.GetRequiredService<IChatCommandManager>();
        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();

        // Shared plumbing for the ported chat commands (same database path the router uses).
        Sanctuary.Gateway.ChatCommands.CommandSupport.Initialize(
            serviceProvider,
            loggerFactory.CreateLogger("Commands"),
            _zoneManager,
            _resourceManager,
            $"Data Source={System.IO.Path.Combine(AppContext.BaseDirectory, "sanctuary.db")}");

    }

    private static void SendMuteNotice(GatewayConnection connection)
    {
        DateTimeOffset? mutedUntil = connection.Player.MutedUntil;

        var packet = new PacketChat
        {
            Channel = ChatChannel.System,
            FromName = connection.Player.Name,
            ToName = connection.Player.Name,
            Message = $"You are muted until {mutedUntil:u} and cannot send chat messages."
        };

        connection.Player.SendTunneled(packet);
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!PacketChat.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(PacketChat));
            return false;
        }

        _logger.LogTrace("Received {name} packet. ( {packet} )", nameof(PacketChat), packet);
        _logger.LogInformation("Chat message from {Player}: {Message}", connection.Player.Name, packet.Message);

        // Chat commands go through the ChatCommandManager (IChatCommand implementations, role gated,
        // self-documenting). "/" is accepted as an alias for the manager's "!" prefix because half the
        // commands here were written with it.
        if (packet.Message is { Length: > 1 } message && (message[0] == '!' || message[0] == '/'))
        {
            var command = message[0] == '/' ? _chatCommandManager.Prefix + message[1..] : message;

            if (_chatCommandManager.TryHandle(connection.Player, command))
            {
                _logger.LogInformation("Command was handled by ChatCommandManager");
                return true;
            }
        }

        // COMBAT WIP: live trial tool. Type "!cast [Unknown Unknown2 CompositeEffectId Animation
        // AbilityId ActionTime HasActionProgress]" in chat to fire a server->client StartCasting and
        // watch how the client reacts -> verifies the packet's field order/meaning. (See docs/STATUS.md.)
        if (packet.Message is { } chatMsg && chatMsg.StartsWith("!cast"))
        {
            HandleCastTest(connection, chatMsg);
            return true;
        }

        // COMBAT WIP: "!anim <id> [actionTime]" plays animation <id> on the caster via a StartCasting
        // (the confirmed packet) with a near-zero cast bar. Lets us brute-force the sword-swing /
        // attack animation id: type !anim 1, !anim 2, ... and watch the character. (See docs/STATUS.md.)
        if (packet.Message is { } animMsg && animMsg.StartsWith("!anim"))
        {
            HandleAnimTest(connection, animMsg);
            return true;
        }

        // COMBAT WIP: "!fight [0/1]" puts the client into "fighting" state (EncounterPacketIsFighting,
        // op41/sub133 -> SetIsFighting). This opens the gate that lets floating damage numbers / MISS!
        // text render. Default 1.
        if (packet.Message is { } fightMsg && fightMsg.StartsWith("!fight"))
        {
            var parts2 = fightMsg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var on = !(parts2.Length > 1 && parts2[1] == "0");
            connection.SendTunneled(new EncounterOverworldCombatPacket { Unknown3 = on });
            connection.SendTunneled(new EncounterPacketIsFighting { InWorldCombat = on });
            _logger.LogInformation("!fight -> InWorldCombat + IsFighting = {on}", on);
            return true;
        }

        // COMBAT WIP: fire the REAL combat packet CombatPacketAttackProcessed (op32/sub7) at the nearest
        // hostile NPC. This one packet should produce the damage number + health bar + hit effect + recoil.
        // "!atk [dmg] [maxHp] [effectId] [b1] [b2] [i4] [i5]"  (defaults: 250 5000 7 0 0 0 0)
        // Guid1=you (attacker), Guid2/Guid3=the NPC (target).
        if (packet.Message is { } atkMsg && atkMsg.StartsWith("!atk"))
        {
            HandleAtkTest(connection, atkMsg);
            return true;
        }

        // COMBAT WIP: brute-force the damage NUMBER live. "!dmg <u2> <u3> <u4> [u1] [u5]" fires a
        // HitPointModification at the nearest hostile NPC (Guid=you, Guid2=npc). The number comes from
        // PlayerHitpointDeltaEvent: amount>=0 -> "+N" (heal), <0 -> "N"/"N!!" (damage), 0 -> nothing.
        // So find which int is the amount and try NEGATIVE values, e.g. "!dmg -50 0 0", "!dmg 0 -50 0".
        if (packet.Message is { } dmgMsg && dmgMsg.StartsWith("!dmg"))
        {
            HandleDmgTest(connection, dmgMsg);
            return true;
        }

        // COMBAT WIP: brute-force the NPC health bar live.
        //   "!hp <cur> <max> [u3]"  -> UpdateHitpoints (op35/sub5) at the nearest hostile NPC.
        //   "!hpme <cur> <max> [u3]" -> same packet but targeting YOURSELF (diagnostic: does op35/sub5
        //                               move any bar at all?).
        // Try value orders, e.g. "!hp 100 5000 0" then "!hp 5000 100 0", and watch the bar.
        if (packet.Message is { } hpMsg && hpMsg.StartsWith("!hp"))
        {
            HandleHpTest(connection, hpMsg);
            return true;
        }

        // COMBAT WIP: "!slot [barId]" fills slots 0-3 of an action bar via UpdateActionBarSlot, using the
        // values from a real captured ability slot (icon 543, name 422910, Unknown5=1, Unknown6=2). Lets us
        // find which bar/slot drives the left ability UI. Default barId=2 (try 0/1 too). (See docs/STATUS.md.)
        if (packet.Message is { } chatMsg2 && chatMsg2.StartsWith("!slot"))
        {
            HandleSlotTest(connection, chatMsg2);
            return true;
        }

        // COMBAT WIP: "!give" grants the ACTIVE JOB's kit weapons (ninja Shadow Blades 75110-75119, or
        // the 30 archer bows 75000-75029 when played as an Archer) to the character's inventory
        // (DB + in-memory) so you can equip them and watch the ability toolbar change per weapon.
        if (packet.Message is { } giveMsg && giveMsg.StartsWith("!give"))
        {
            HandleGiveJobWeapons(connection);
            return true;
        }

        // COMBAT WIP: "!ticon [melee] [special]" probes ability-slot ICON ids live. No args clears the override
        // (back to the weapon icon). One arg sets both slots; two sets melee + special. Re-sends the toolbar so
        // the new icons show immediately. Use to discover which icon ids actually render in THIS client build.
        if (packet.Message is { } iconMsg && iconMsg.StartsWith("!ticon"))
        {
            HandleIconProbe(connection, iconMsg);
            return true;
        }

        // RECOVERY: "!home" (or "!spawn") re-zones the client back to the starting zone (FabledRealms) at the
        // spawn near the training dummy. Use to escape a broken instance test (e.g. falling through Frostfang).
        if (packet.Message is { } homeMsg && (homeMsg.StartsWith("!home") || homeMsg.StartsWith("!spawn")))
        {
            HandleHome(connection);
            return true;
        }

        // LOOT TEST: "!pack [count]" opens 1..25 Battle Item Mystery Packs on the spot — same code path
        // as the loot-wheel payout (random sphere table, real inventory grant, contents banner). Each
        // roll logs "Mystery Pack -> 3x sphere def NNNN" — spam it to sample the distribution without
        // replaying the encounter.
        if (packet.Message is { } packMsg && packMsg.StartsWith("!pack"))
        {
            var parts = packMsg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var count = parts.Length > 1 && int.TryParse(parts[1], out var n) ? Math.Clamp(n, 1, 25) : 1;
            for (var i = 0; i < count; i++)
                BaseMiniGamePacketHandler.OpenMysteryPack(connection);
            return true;
        }

        // INSTANCE WIP (Frostfang Fury): "!offer" sends the EncounterDetailsResponsePacket (op41/sub114) — the
        // adventure OFFER POPUP (title/difficulty/description + GO!). Wire format RE'd from the client's
        // Unserialize fns. Tests whether the panel renders before we wire it to the wolf interaction.
        if (packet.Message is { } offerMsg && offerMsg.StartsWith("!offer"))
        {
            // Popup strings resolve via the client's SERVER-FED string table (POI-populated), not local en_us_data.
            // Use ids the client already knows: 5698 "Frostfang Caverns" + 382845 (POI desc) DO resolve; the
            // en_us_data "Frostfang Growler!" id (3078903256) is unknown to the client -> blank. (See interact handler.)
            connection.SendTunneled(new EncounterDetailsResponsePacket
            {
                // REAL ids from minigame branch ClientActivityDefinitions.json activity Id 174 "Frostfang Growler!".
                NameId = 93276,                       // "Frostfang Growler!"
                DescriptionId = 104171,               // Growler description
                Difficulty = 1,                       // 1 of 5 pips
                IconId = 1345,                        // wolf emblem ImageSetId
            });
            _logger.LogInformation("!offer -> EncounterDetailsResponsePacket (Frostfang) sent.");
            return true;
        }

        // INSTANCE WIP (Frostfang Fury): "!ready" sends EncounterZoneIsReadyPacket (op41/sub107) — the handshake
        // that the client (sub_9B0CC0) turns into "HandlerMiniGameStart:setReady", flipping the offer popup's
        // loading SPINNER into the green GO! button (and rendering the title/desc/prizes). Open the offer first,
        // then "!ready". If GO! appears, the handshake works and we move it into the click/entrance flow.
        if (packet.Message is { } readyMsg && readyMsg.StartsWith("!ready"))
        {
            connection.SendTunneled(new EncounterZoneIsReadyPacket());
            _logger.LogInformation("!ready -> EncounterZoneIsReadyPacket (sub107) sent.");
            return true;
        }


        // INSTANCE (Frostfang Fury): "!arena" logs your current position (coordinate scouting);
        // "!arena set" pins the arena SPAWN to exactly where you're standing (do it while standing in the
        // arena world to fine-tune where GO! drops players this server run).
        if (packet.Message is { } arenaMsg && arenaMsg.StartsWith("!arena"))
        {
            var pos = connection.Player.Position;

            if (arenaMsg.Contains("set"))
            {
                Sanctuary.Game.Zones.FrostfangArenaZone.SpawnOverride = pos;
                _logger.LogInformation("!arena set -> arena spawn pinned at ({x}, {y}, {z}).", pos.X, pos.Y, pos.Z);
            }
            else
            {
                _logger.LogInformation("!arena -> player at ({x}, {y}, {z}) in zone {zone}.",
                    pos.X, pos.Y, pos.Z, connection.Player.Zone.Name);
            }
            return true;
        }

        // INSTANCE WIP (Frostfang Fury, Phase 0): "!frostfang [worldName]" sends PacketClientBeginZoning (op31)
        // to re-zone the client INTO the Frostfang Caverns instance world mid-session. Smoke test to resolve
        // the world-name string the client expects + validate op31. (See drafts/frostfang-instance-build.md.)
        if (packet.Message is { } ffMsg && ffMsg.StartsWith("!frostfang"))
        {
            HandleFrostfangTest(connection, ffMsg);
            return true;
        }

        if (packet.Message == null)
        {
            _logger.LogWarning("Received {name} packet with null message. ( {packet} )", nameof(PacketChat), packet);
            return false;
        }
        
        if (connection.Player.IsMuted())
        {
            SendMuteNotice(connection);
            return true;
        }

        packet.FromGuid = connection.Player.Guid;
        packet.FromName = connection.Player.Name;

        switch (packet.Channel)
        {
            case ChatChannel.Tell:
                {
                    if (_zoneManager.TryGetPlayer(packet.ToName.FullName, out var toPlayer))
                    {
                        _chatLogger.LogInformation("Tell|From: \"{FromName}\" ({FromGuid}), To: \"{ToName}\" ({ToGuid}), Msg: \"{Message}\"",
                            packet.FromName,
                            packet.FromGuid,
                            packet.ToName,
                            toPlayer.Guid,
                            packet.Message
                        );

                        if (!toPlayer.Ignores.Any(x => x.Guid == connection.Player.Guid))
                            toPlayer.SendTunneled(packet);

                        var tellEchoPacket = new TellEchoPacket();

                        tellEchoPacket.Name = packet.ToName;
                        tellEchoPacket.Message = packet.Message;

                        connection.Player.SendTunneled(tellEchoPacket);
                    }
                }
                break;

            case ChatChannel.WorldShout:
                {
                    _chatLogger.LogInformation("WorldShout|From: \"{FromName}\" ({FromGuid}), Msg: \"{Message}\"",
                        packet.FromName,
                        packet.FromGuid,
                        packet.Message
                    );

                    foreach (var zonePlayer in connection.Player.Zone.Players)
                    {
                        if (zonePlayer.Ignores.Any(x => x.Guid == connection.Player.Guid))
                            continue;

                        zonePlayer.SendTunneled(packet);
                    }
                }
                break;

            case ChatChannel.WorldTrade:
            case ChatChannel.WorldLfg:
            case ChatChannel.WorldArea:
            case ChatChannel.WorldMembersOnly:
                {
                    _chatLogger.LogInformation("{Channel}|Area: {AreaNameId}, From: \"{FromName}\" ({FromGuid}), Msg: \"{Message}\"",
                        packet.Channel,
                        packet.AreaNameId,
                        packet.FromName,
                        packet.FromGuid,
                        packet.Message
                    );

                    connection.Player.SendTunneled(packet);

                    foreach (var visiblePlayer in connection.Player.VisiblePlayers)
                    {
                        if (visiblePlayer.Value.ChatChannelStatus.TryGetValue(packet.Channel, out var channelStatus) && !channelStatus)
                            continue;

                        if (visiblePlayer.Value.Ignores.Any(x => x.Guid == connection.Player.Guid))
                            continue;

                        visiblePlayer.Value.SendTunneled(packet);
                    }
                }
                break;

            default:
                {
                    _chatLogger.LogInformation("{Channel}|From: \"{FromName}\" ({FromGuid}), Msg: \"{Message}\"",
                        packet.Channel,
                        packet.FromName,
                        packet.FromGuid,
                        packet.Message
                    );

                    connection.Player.SendTunneled(packet);

                    foreach (var visiblePlayer in connection.Player.VisiblePlayers)
                    {
                        if (visiblePlayer.Value.Ignores.Any(x => x.Guid == connection.Player.Guid))
                            continue;

                        visiblePlayer.Value.SendTunneled(packet);
                    }
                }
                break;
        }

        return true;
    }

    // COMBAT WIP — see note above. Builds a StartCasting from optional chat args and sends it.
    private static void HandleCastTest(GatewayConnection connection, string msg)
    {
        var parts = msg.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var p = new AbilityPacketStartCasting
        {
            Unknown = connection.Player.Guid,   // best guess: caster guid
            Unknown2 = connection.Player.Guid,
            CompositeEffectId = 0,
            Animation = -1,
            AbilityId = 1,
            ActionTime = 5f,                    // 5s cast bar so it's obvious if ActionTime is right
            HasActionProgress = true,
        };

        if (parts.Length > 1 && ulong.TryParse(parts[1], out var u)) p.Unknown = u;
        if (parts.Length > 2 && ulong.TryParse(parts[2], out var u2)) p.Unknown2 = u2;
        if (parts.Length > 3 && int.TryParse(parts[3], out var ce)) p.CompositeEffectId = ce;
        if (parts.Length > 4 && int.TryParse(parts[4], out var an)) p.Animation = an;
        if (parts.Length > 5 && int.TryParse(parts[5], out var ab)) p.AbilityId = ab;
        if (parts.Length > 6 && float.TryParse(parts[6], out var at)) p.ActionTime = at;
        if (parts.Length > 7) p.HasActionProgress = parts[7] == "1" || parts[7].Equals("true", StringComparison.OrdinalIgnoreCase);

        _logger.LogInformation(
            "!cast -> StartCasting Unknown={u} Unknown2={u2} CompositeEffectId={ce} Animation={an} AbilityId={ab} ActionTime={at} HasActionProgress={h}",
            p.Unknown, p.Unknown2, p.CompositeEffectId, p.Animation, p.AbilityId, p.ActionTime, p.HasActionProgress);

        connection.SendTunneled(p);
    }

    // COMBAT WIP — see note above. TOGGLE: "!anim <id>" sets an animation override so every ability key-press
    // plays that animation (spam your keys, no chat flood, replays in sequence). "!anim" / "!anim 0" clears it.
    private static void HandleAnimTest(GatewayConnection connection, string msg)
    {
        var parts = msg.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Clear the override (no id, "0", or "off").
        if (parts.Length <= 1 || parts[1] == "0" || parts[1].Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            AbilityPacketClientRequestStartAbilityHandler.DebugAnimationOverride = null;
            _logger.LogInformation("!anim -> cleared animation override (abilities use their own anim again).");
            return;
        }

        if (!int.TryParse(parts[1], out var animId))
            return;

        // Set the override: pressing ANY ability key now plays this animation. Press repeatedly to see it.
        AbilityPacketClientRequestStartAbilityHandler.DebugAnimationOverride = animId;
        _logger.LogInformation("!anim -> animation override = {anim}. Press an ability key (1/2) to play it.", animId);

        // Also fire it once immediately for instant feedback.
        connection.SendTunneled(new AbilityPacketStartCasting
        {
            Unknown = connection.Player.Guid,
            Unknown2 = connection.Player.Guid,
            CompositeEffectId = 0,
            Animation = animId,
            AbilityId = 1,
            ActionTime = 0.1f,
            HasActionProgress = false,
        });
    }

    // COMBAT WIP — see note above. Fires the real CombatPacketAttackProcessed at the nearest hostile NPC.
    private static void HandleAtkTest(GatewayConnection connection, string msg)
    {
        var parts = msg.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var dmg = parts.Length > 1 && int.TryParse(parts[1], out var a) ? a : 250;
        var maxHp = parts.Length > 2 && int.TryParse(parts[2], out var b) ? b : 5000;
        var fx = parts.Length > 3 && int.TryParse(parts[3], out var c) ? c : 7;
        var b1 = parts.Length > 4 && parts[4] == "1";
        var b2 = parts.Length > 5 && parts[5] == "1";
        var i4 = parts.Length > 6 && int.TryParse(parts[6], out var d) ? d : 0;
        // i5 defaults to maxHp: the client uses it as the starting HP when it doesn't yet know the NPC's
        // current HP (handler: if currentHP==0 && i5>0 -> hp = i5 - damage), so the bar starts from full.
        var i5 = parts.Length > 7 && int.TryParse(parts[7], out var e) ? e : maxHp;

        var npc = connection.Player.Zone.Npcs.FirstOrDefault(n => n.IsHostile && n.IsDamageable);

        if (npc is null)
        {
            _logger.LogInformation("!atk -> no hostile NPC found.");
            return;
        }

        var p = new CombatPacketAttackProcessed
        {
            AttackerGuid = connection.Player.Guid,
            TargetGuid = npc.Guid,
            Damage = dmg,
            MaxHealth = maxHp,
            CompositeEffectId = fx,
            Bool1 = b1,
            Bool2 = b2,
            Int4 = i4,
            CurrentHealth = i5,
        };

        _logger.LogInformation("!atk -> AttackProcessed npc={guid} dmg={dmg} maxHp={max} fx={fx} b1={b1} b2={b2} i4={i4} i5={i5}",
            npc.Guid, dmg, maxHp, fx, b1, b2, i4, i5);

        connection.SendTunneled(p);
    }

    // COMBAT WIP — see note above. Fires HitPointModification (op35/sub35) to brute-force the damage number.
    private static void HandleDmgTest(GatewayConnection connection, string msg)
    {
        var parts = msg.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var u2 = parts.Length > 1 && int.TryParse(parts[1], out var a) ? a : -50;
        var u3 = parts.Length > 2 && int.TryParse(parts[2], out var b) ? b : 0;
        var u4 = parts.Length > 3 && int.TryParse(parts[3], out var c) ? c : 0;
        var u1 = parts.Length > 4 && parts[4] == "1";
        var u5 = parts.Length > 5 && parts[5] == "1";

        var npc = connection.Player.Zone.Npcs.FirstOrDefault(n => n.IsHostile && n.IsDamageable);

        if (npc is null)
        {
            _logger.LogInformation("!dmg -> no hostile NPC found.");
            return;
        }

        var p = new PlayerUpdatePacketHitPointModification
        {
            Guid = connection.Player.Guid, // source
            Guid2 = npc.Guid,              // victim
            Unknown = u1,
            Unknown2 = u2,
            Unknown3 = u3,
            Unknown4 = u4,
            Unknown5 = u5,
        };

        _logger.LogInformation("!dmg -> HitPointModification npc={guid} u1={u1} u2={u2} u3={u3} u4={u4} u5={u5}",
            npc.Guid, u1, u2, u3, u4, u5);

        connection.SendTunneled(p);
    }

    // COMBAT WIP — see note above. Fires UpdateHitpoints (op35/sub5) to brute-force the health bar.
    private static void HandleHpTest(GatewayConnection connection, string msg)
    {
        var parts = msg.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var self = parts[0].Equals("!hpme", StringComparison.OrdinalIgnoreCase);

        var cur = parts.Length > 1 && int.TryParse(parts[1], out var c) ? c : 100;
        var max = parts.Length > 2 && int.TryParse(parts[2], out var m) ? m : 5000;
        var u3 = parts.Length > 3 && int.TryParse(parts[3], out var u) ? u : 0;

        ulong targetGuid;

        if (self)
        {
            targetGuid = connection.Player.Guid;
        }
        else
        {
            var npc = connection.Player.Zone.Npcs.FirstOrDefault(n => n.IsHostile && n.IsDamageable);

            if (npc is null)
            {
                _logger.LogInformation("!hp -> no hostile NPC found.");
                return;
            }

            targetGuid = npc.Guid;
        }

        var p = new PlayerUpdatePacketUpdateHitpoints
        {
            Guid = targetGuid,
            Hitpoints = cur,
            MaxHitpoints = max,
            Unknown = u3,
        };

        _logger.LogInformation("!hp -> UpdateHitpoints Guid={guid} cur={cur} max={max} u3={u3} (self={self})",
            targetGuid, cur, max, u3, self);

        connection.SendTunneled(p);
    }

    // COMBAT WIP — see note above. Probes ability-slot icon ids live so we can find which ids render correctly.
    private static void HandleIconProbe(GatewayConnection connection, string msg)
    {
        var parts = msg.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length <= 1)
        {
            NinjaWeaponAbilities.DebugMeleeIcon = null;
            NinjaWeaponAbilities.DebugSpecialIcon = null;
            _logger.LogInformation("!ticon -> cleared icon override (back to weapon icon).");
        }
        else
        {
            var melee = int.TryParse(parts[1], out var m) ? m : 0;
            var special = parts.Length > 2 && int.TryParse(parts[2], out var s) ? s : melee;

            NinjaWeaponAbilities.DebugMeleeIcon = melee;
            NinjaWeaponAbilities.DebugSpecialIcon = special;
            _logger.LogInformation("!ticon -> melee icon {melee}, special icon {special}.", melee, special);
        }

        connection.SendTunneled(NinjaWeaponAbilities.BuildToolbar(connection.Player, _resourceManager));
    }

    // COMBAT WIP — see note above. Grants the active job's kit weapons so they can be equipped to test
    // the item-driven ability toolbar. Adds each missing weapon to the DB + in-memory inventory and
    // pushes an ItemAdd so it appears immediately. Equip one -> the toolbar refreshes to its abilities.
    private static void HandleGiveJobWeapons(GatewayConnection connection)
    {
        var characterId = GuidHelper.GetPlayerId(connection.Player.Guid);

        using var dbContext = _dbContextFactory.CreateDbContext();

        var dbCharacter = dbContext.Characters
            .Include(x => x.Items)
            .SingleOrDefault(x => x.Id == characterId);

        if (dbCharacter is null)
        {
            _logger.LogWarning("!give -> character {id} not found.", characterId);
            return;
        }

        var weaponDefIds = connection.Player.ActiveProfileId == ArcherWeaponAbilities.ArcherProfileId
            ? ArcherWeaponAbilities.AllWeaponDefIds
            : NinjaWeaponAbilities.AllWeaponDefIds;

        var nextId = dbCharacter.Items.Count > 0 ? dbCharacter.Items.Max(i => i.Id) : 0;
        var granted = 0;

        foreach (var defId in weaponDefIds)
        {
            if (dbCharacter.Items.Any(i => i.Definition == defId))
                continue; // already owned

            if (!_resourceManager.ClientItemDefinitions.ContainsKey(defId))
                continue;

            var dbItem = new DbItem
            {
                Id = ++nextId,
                Definition = defId,
                Tint = 0,
                Count = 1,
            };

            dbCharacter.Items.Add(dbItem);

            var clientItem = new ClientItem
            {
                Id = dbItem.Id,
                Tint = 0,
                Count = 1,
                Definition = defId,
            };

            connection.Player.Items.Add(clientItem);

            using var writer = new PacketWriter();
            clientItem.Serialize(writer);

            connection.SendTunneled(new ClientUpdatePacketItemAdd { Payload = writer.Buffer });

            granted++;
        }

        if (granted > 0)
            dbContext.SaveChanges();

        _logger.LogInformation("!give -> granted {n} kit weapon(s) to character {id} (profile {profile}).",
            granted, characterId, connection.Player.ActiveProfileId);
    }

    // INSTANCE WIP — see note above. Re-zones the client into Frostfang Caverns via PacketClientBeginZoning
    // (op31). worldName arg lets us try candidates live: sh_frostfang_cavern (default) / frostfang_cavern /
    // FrostfangCavern. Spawn from PointOfInterests.json id 59 "Frostfang Caverns". WaitForZoneReadyPacket=false
    // for the smoke test (immediate load) — if the client hangs waiting, we add the zone-ready handshake next.
    // RECOVERY — see note above. Re-zones the client to the starting zone spawn (where the dummy is) and
    // syncs the player's server-side position to match, so a broken instance state (falling) is escapable
    // without a full relaunch.
    private static void HandleHome(GatewayConnection connection)
    {
        var zone = _zoneManager.StartingZone;

        if (connection.Player.Zone != zone)
        {
            // Leaving the arena mid-encounter: release the client's minigame/combat state FIRST
            // (op39/sub19 + op62 defaults), or it stays InCombat forever (stuck job changes).
            if (connection.Player.Zone is Sanctuary.Game.Zones.FrostfangArenaZone arena)
                arena.EndEncounterForPlayer(connection.Player);

            // In another zone (e.g. the Frostfang arena): do the PROPER server-side transfer so
            // tiles/visibility/zone registration all move with the player (a raw BeginZoning here
            // would desync client world vs server zone — the "invisible NPCs" class of bug).
            connection.Player.TeleportToZone(zone, zone.SpawnPosition, zone.SpawnRotation, sky: null, geometryId: 0);

            _logger.LogInformation("!home -> TeleportToZone back to {name} ({id}).", zone.Name, zone.Id);
            return;
        }

        // Already in the starting zone: just reposition + re-zone the client to the spawn.
        connection.Player.UpdatePosition(zone.SpawnPosition, zone.SpawnRotation);

        connection.SendTunneled(new PacketClientBeginZoning
        {
            Name = zone.Name,
            Sky = null,
            Position = zone.SpawnPosition,
            Rotation = zone.SpawnRotation,
            Id = zone.Id,
            WaitForZoneReadyPacket = false,
        });

        _logger.LogInformation("!home -> ClientBeginZoning back to {name} ({id}) at spawn.", zone.Name, zone.Id);
    }

    private static void HandleFrostfangTest(GatewayConnection connection, string msg)
    {
        // "!frostfang [x] [y] [z] [worldName]" — probe the in-cavern spawn live. The POI overworld coords were
        // wrong (entrance in FabledRealms, not inside the cavern world). From Phase-0 logs: world name
        // sh_frostfang_cavern is confirmed (client requested its tiles); TileSize ~64; failed edge tiles
        // (Tile_000_008) at Z~550 => the ~12-tile (~768²) map's valid area is the lower tiles, center ~ (350,350).
        // Default spawn = map center, dropped from above so we land on the cave floor. Sky=null (the overworld
        // sends no Sky; "frostfang_cavern" logged "Bad sky definition" — let the world define its own).
        var parts = msg.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var x = parts.Length > 1 && float.TryParse(parts[1], out var px) ? px : 350f;
        var y = parts.Length > 2 && float.TryParse(parts[2], out var py) ? py : 100f;
        var z = parts.Length > 3 && float.TryParse(parts[3], out var pz) ? pz : 350f;
        var worldName = parts.Length > 4 ? parts[4] : "sh_frostfang_cavern";

        var beginZoning = new PacketClientBeginZoning
        {
            Name = worldName,
            Sky = null,
            Position = new Vector4(x, y, z, 1f),
            Rotation = Quaternion.Identity,
            Id = 59,
            WaitForZoneReadyPacket = false,
        };

        _logger.LogInformation("!frostfang -> ClientBeginZoning Name={name} pos=({x},{y},{z})", worldName, x, y, z);

        connection.SendTunneled(beginZoning);
    }

    // COMBAT WIP — see note above. Fills slots 0-3 of an action bar with a captured ability slot template.
    private static void HandleSlotTest(GatewayConnection connection, string msg)
    {
        var parts = msg.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        var barId = parts.Length > 1 && int.TryParse(parts[1], out var bi) ? bi : 2;

        for (var slot = 0; slot < 4; slot++)
        {
            var p = new ClientUpdatePacketUpdateActionBarSlot();
            p.Data.Id = barId;
            p.Data.Slot = slot;
            p.Slot.IsEmpty = false;
            p.Slot.IconId = 543;        // from capture
            p.Slot.IconTintId = 0;
            p.Slot.NameId = 422910;     // from capture
            p.Slot.Unknown5 = 1;        // from capture
            p.Slot.Unknown6 = 2;        // from capture
            p.Slot.Unknown7 = 0;
            p.Slot.ManaCost = 0;
            p.Slot.Enabled = true;

            connection.SendTunneled(p);
        }

        _logger.LogInformation("!slot -> filled bar {bar} slots 0-3 (UpdateActionBarSlot).", barId);
    }
}