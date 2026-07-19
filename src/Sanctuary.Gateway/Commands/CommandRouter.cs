using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sanctuary.Core.IO;
using Sanctuary.Game;
using Sanctuary.Game.Entities;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Chat;
using System.Linq;
using Sanctuary.Packet.Common;
using Sanctuary.Gateway.Handlers;

namespace Sanctuary.Gateway.Commands;

public static class CommandRouter
{
    private static ILogger _logger = null!;
    private static IZoneManager _zoneManager = null!;
    private static IResourceManager _resourceManager = null!;
    private static string _dbConnectionString = "Data Source=sanctuary.db";
    private static readonly HashSet<ulong> _flyingPlayers = [];

    public static void Initialize(IServiceProvider sp)
    {
        var lf = sp.GetRequiredService<ILoggerFactory>();
        _logger = lf.CreateLogger("Commands");
        _zoneManager = sp.GetRequiredService<IZoneManager>();
        _resourceManager = sp.GetRequiredService<IResourceManager>();

        // Try to get the database path from configuration
        try
        {
            var dbPath = System.IO.Path.Combine(AppContext.BaseDirectory, "sanctuary.db");
            _dbConnectionString = $"Data Source={dbPath}";
            _logger.LogInformation("CommandRouter using database: {DbPath}", dbPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not determine database path, using default");
        }
    }

    // Entry point for all slash commands.
    // Returns true if the message was handled as a command.
    public static bool TryHandle(GatewayConnection conn, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        // Accept commands with or without slash
        bool isCommand = message[0] == '/';
        if (!isCommand && !message.StartsWith("!"))
            return false; // Must start with / or !

        _logger.LogInformation("Command received: {Message} from {Player}", message, conn.Player.Name);

        var parts = message.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        // Remove the prefix (/ or !)
        var verb = parts[0].Substring(1).ToLowerInvariant();

        // A throwing handler must never wedge the chat pipeline — catch, log, and tell the caller, but always
        // report the command as handled so it can't fall through to the legacy "!cast"/"!anim" string checks.
        try
        {
            return Dispatch(conn, verb, message, parts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command '{Verb}' threw.", verb);
            SendSystem(conn, $"Command '{verb}' failed: {ex.Message}");
            return true;
        }
    }

    private static bool Dispatch(GatewayConnection conn, string verb, string message, string[] parts)
    {
        switch (verb)
        {
            case "help":
                return HandleHelp(conn);
            case "dungeon":
                return HandleDungeon(conn, parts);
            case "npc":
                return HandleNpc(conn, parts);
            case "admin":
                return HandleAdmin(conn, parts);
            case "enforcer":
                return HandleEnforcer(conn, parts);
            case "where":
                return HandleWhere(conn, parts);
            case "pos":
            case "coords":
            case "loc":
                return HandlePos(conn, parts);
            case "tp":
                return HandleTp(conn, parts);
            case "bring":
                return HandleBring(conn, parts);
            case "goto":
                return HandleGoto(conn, parts);
            case "kick":
                return HandleKick(conn, parts);
            case "warn":
                return HandleWarn(conn, parts);
            case "gift":
                return HandleGift(conn, parts);
            case "announce":
                return HandleAnnounce(conn, parts);
            case "listplayers":
                return HandlePlayers(conn, parts);
            case "gohouse":
                return HandleGoHouse(conn, parts);
            case "listhouses":
                return HandleListHouses(conn, parts);
            case "createhouse":
                return HandleCreateHouse(conn, parts);
            case "spawnhouse":
                return HandleSpawnHouse(conn, parts);
            case "testeffect":
                return HandleTestEffect(conn, parts);
            case "playeffect":
                return HandlePlayEffect(conn, parts);
            case "petspawn":
                return HandlePetSpawn(conn, parts);
            case "petdespawn":
                return HandlePetDespawn(conn, parts);
            case "petlist":
                return HandlePetList(conn, parts);
            case "respawn":
                return HandleRespawn(conn);
            case "die":
                return HandleDie(conn);
            case "dodge":
                return HandleDodge(conn, parts);
            case "spawnenemy":
                return HandleSpawnEnemy(conn, parts);
            case "hp":
                return HandleHp(conn, parts);
            case "xp":
                return HandleXp(conn, parts);
            case "testtransform":
                return HandleTestTransform(conn, parts);
            case "fly":
                return HandleFly(conn);
            case "testicons":
                return HandleTestIcons(conn);
            case "testsubtext":
                return HandleTestSubText(conn, parts);
            case "spawntest":
                return HandleSpawnTest(conn, parts);
            case "giveitem":
                return HandleGiveItem(conn, parts);
            case "lua":
                // ADMIN ONLY: asks the client to execute arbitrary Lua. Proven NOT to work on this build
                // (the client ignores both op36/17 and op47/7), but it must not be player-reachable if the
                // encoding is ever fixed.
                return RequireAdmin(conn) && HandleLua(conn, message);

            // PARTY (interim accept path until the native accept packet's byte format is captured).
            case "paccept":
                BaseGroupPacketHandler.AcceptInvite(conn.Player);
                return true;
            case "pleave":
                BaseGroupPacketHandler.LeaveParty(conn.Player);
                return true;

            // PARTY UI RE (2026-07-11): "!ptest" sends the runner a candidate S2C GroupInvite so we
            // can Frida-watch their OWN client parse it + (hopefully) raise the invite popup, then
            // refine the wire format. Self-targeted so a single client can iterate.
            case "ptest":
                conn.Player.SendTunneled(new GroupPacketGroupInvite
                {
                    InviterGuid = conn.Player.Guid,
                    InviterName = new Sanctuary.Packet.Common.NameData { FirstName = "Test", LastName = "Inviter" },
                });
                SendSystem(conn, "!ptest -> sent a candidate S2C GroupInvite to you (watch Frida).");
                return true;

            // PARTY UI RE: "!proster" sends the runner a candidate S2C GroupUpdate (sub-8 roster) with
            // themselves + a fake member, so Frida can capture the sub-8 handler + how the client parses
            // the member list, and (hopefully) show the group/combat-group window.
            case "proster":
                conn.Player.SendTunneled(new GroupPacketGroupUpdate
                {
                    LeaderGuid = conn.Player.Guid,
                    Members =
                    {
                        new GroupPacketGroupUpdate.Member { Guid = conn.Player.Guid, Name = conn.Player.Name, ProfileId = conn.Player.ActiveProfileId, ProfileRank = 1 },
                        new GroupPacketGroupUpdate.Member { Guid = conn.Player.Guid + 1, Name = new Sanctuary.Packet.Common.NameData { FirstName = "Party", LastName = "Member" }, ProfileId = conn.Player.ActiveProfileId, ProfileRank = 1 },
                    },
                });
                SendSystem(conn, "!proster -> sent a candidate S2C GroupUpdate (watch Frida).");
                return true;

            // ENCOUNTER GAQ RE (2026-07-17): "!ginvite" sends the runner an op41/102 EncounterInvitation so we
            // can Frida-watch their OWN client's op41 dispatcher (FUN_00aa36c0) parse it + (hopefully) raise the
            // accept/reject popup / "Waiting…" banner, then sweep the fields. Self-targeted so one client can
            // iterate. Usage: !ginvite [encId] [instId] [A] [B]  (Guid defaults to self; all default 0/1/0/0).
            case "ginvite":
            {
                int Arg(int i, int def) => parts.Length > i && int.TryParse(parts[i], out var v) ? v : def;
                var pkt = new EncounterInvitationPacket
                {
                    Unknown = Arg(1, 0),   // EncounterId (header)
                    Unknown2 = Arg(2, 1),  // InstanceId (header)
                    Guid = conn.Player.Guid, // inviter = self (names the popup)
                    A = Arg(3, 0),
                    B = Arg(4, 0),
                };
                // Optional 6th arg: a raw guid (numeric) to override the guid field, OR a TARGET PLAYER NAME to
                // send the popup to (so a leader can sweep A on a member's REAL cross-player popup). Default self.
                var target = conn.Player;
                if (parts.Length > 5)
                {
                    if (ulong.TryParse(parts[5], out var g))
                        pkt.Guid = g;
                    else if (!_zoneManager.TryGetPlayer(parts[5], out target))
                    {
                        SendSystem(conn, $"!ginvite: player '{parts[5]}' not found.");
                        return true;
                    }
                }
                target.SendTunneled(pkt);
                SendSystem(conn, $"!ginvite -> op41/102 enc={pkt.Unknown} A={pkt.A} B={pkt.B} guid={pkt.Guid} -> {target.Name?.FullName}. Watch their screen.");
                return true;
            }

            // DEV PROBE: PlayCompositeEffect (op35/16) as a DIFFERENT projectile mechanism than op36/4.
            // op36/4's fly path is gated on caster ProxiedCharacter+0x508 (client combat state we cannot set).
            // op35/16 carries TWO guids (Guid + Unknown2); its sibling AddEffectTagCompositeEffect uses a
            // SourceGuid, so a projectile composite effect played source->target may TRAVEL. This tests it.
            //   !fx [effId] [mode]   effId default 5479 (PRJ_fireball). mode 0: Guid=caster,Unknown2=target
            //                        (effect emanates FROM caster). mode 1: Guid=target,Unknown2=caster.
            case "fx":
            {
                if (!RequireAdmin(conn))
                    return true;

                int FxI(int i, int def) => parts.Length > i && int.TryParse(parts[i], out var v) ? v : def;
                var effId = FxI(1, 5479);
                var mode = FxI(2, 0);

                var self = conn.Player.Guid;
                var p = conn.Player.Position;
                ulong target = 0;
                var tpos = p;
                if (conn.Player.Zone is { } z)
                {
                    var best = float.MaxValue;
                    foreach (var n in z.Npcs)
                    {
                        if (!n.Visible || !n.IsHostile) continue;
                        var dx = n.Position.X - p.X; var dz = n.Position.Z - p.Z;
                        var d = dx * dx + dz * dz;
                        if (d < best) { best = d; target = n.Guid; tpos = n.Position; }
                    }
                }

                // mode 0: emanate FROM caster toward target; mode 1: swap the two guids.
                ulong g, u2; System.Numerics.Vector4 pos;
                if (mode == 1) { g = target; u2 = self; pos = tpos; }
                else { g = self; u2 = target; pos = new System.Numerics.Vector4(p.X, p.Y + 1f, p.Z, 1f); }

                conn.Player.SendTunneledToVisible(new PlayerUpdatePacketPlayCompositeEffect
                {
                    Guid = g,
                    Unknown2 = u2,
                    CompositeEffectId = effId,
                    Position = pos,
                    Clear = false,
                }, sendToSelf: true);
                SendSystem(conn, $"!fx -> op35/16 PlayCompositeEffect eff={effId} mode={mode} guid={g} unk2={u2} target={target}");
                return true;
            }

            // NATIVE op35/62 LaunchProjectile probe. Wire = [35][62] + 008e8910 trajectory struct + int32.
            //   !lp <N>        send N zero body bytes (SIZE SWEEP - find the exact accepted length)
            //   !lp traj [g]   trajectory: start=caster@24, end=target@40, velocity@68; g=source guid mode
            // Watch the frida op35/62 trace (does 92f460 case 0x3e run + 984960 resolve + a projectile spawn).
            case "lp":
            {
                if (!RequireAdmin(conn))
                    return true;

                int LI(int i, int def) => parts.Length > i && int.TryParse(parts[i], out var v) ? v : def;

                var self = conn.Player.Guid;
                var p = conn.Player.Position;
                ulong target = 0; var tpos = p;
                if (conn.Player.Zone is { } lz)
                {
                    var best = float.MaxValue;
                    foreach (var n in lz.Npcs)
                    {
                        if (!n.Visible || !n.IsHostile) continue;
                        var dx0 = n.Position.X - p.X; var dz0 = n.Position.Z - p.Z;
                        var d0 = dx0 * dx0 + dz0 * dz0;
                        if (d0 < best) { best = d0; target = n.Guid; tpos = n.Position; }
                    }
                }

                // "!lp src [guidMode]" - construct the SOURCE TargetCharacterGuid (factory type-id 1) at the
                // Target wire offset (60, after the START/END vectors + the empty variable blob @56) and sweep
                // the total size to find the parse (deser ret=1) + guid resolve. type-id written as int 1
                // (crash-safe: reads as 1 whether the type-id is 1 or 4 bytes). guid at 64 (after int type-id).
                if (parts.Length > 1 && parts[1] == "src")
                {
                    // Target wire (RE'd from 0101c850 + TargetCharacterGuid reader 0101c7c0):
                    //   [int type-id][Vector4 16B][guid 8B]  for type-id 1 (TargetCharacterGuid)
                    // SOURCE Target @ wire 60 (after START@24, END@40, blob@56). DEST Target after it.
                    var effId = LI(2, 16110);   // ProjectileParameters effect/model id (probe which field)
                    var effField = LI(3, -1);   // -1 = set all header int slots (0,4,16,20)+trailing; >=0 = one slot
                    var vx2 = tpos.X - p.X; var vz2 = tpos.Z - p.Z;
                    var l2 = (float)System.Math.Sqrt(vx2 * vx2 + vz2 * vz2);
                    if (l2 > 0.01f) { vx2 = vx2 / l2 * 45f; vz2 = vz2 / l2 * 45f; }
                    for (var total = 185; total <= 220; total++)
                    {
                        var b = new byte[total];
                        void PV2(int o, float x, float y, float z, float w)
                        {
                            if (o + 16 > b.Length) return;
                            System.BitConverter.GetBytes(x).CopyTo(b, o); System.BitConverter.GetBytes(y).CopyTo(b, o + 4);
                            System.BitConverter.GetBytes(z).CopyTo(b, o + 8); System.BitConverter.GetBytes(w).CopyTo(b, o + 12);
                        }
                        // ProjectileParameters header (wire 0..23) = PP+0x10/+0x14/+0x18/+0x1c/+0x28/+0x2c.
                        // Set the effect/model id at every INT header slot (0,4,16,20) to find which renders;
                        // effField>=0 overrides to a single slot for bisecting. Also the trailing int32.
                        if (effField == -2)
                        {
                            System.BitConverter.GetBytes(effId).CopyTo(b, total - 4);   // trailing int32 only
                        }
                        else if (effField < 0)
                        {
                            System.BitConverter.GetBytes(effId).CopyTo(b, 0);
                            System.BitConverter.GetBytes(effId).CopyTo(b, 4);
                            System.BitConverter.GetBytes(effId).CopyTo(b, 16);
                            System.BitConverter.GetBytes(effId).CopyTo(b, 20);
                            if (total >= 8) System.BitConverter.GetBytes(effId).CopyTo(b, total - 4);
                        }
                        else System.BitConverter.GetBytes(effId).CopyTo(b, effField);
                        PV2(24, p.X, p.Y + 1.2f, p.Z, 1f);              // START
                        PV2(40, tpos.X, tpos.Y + 1.2f, tpos.Z, 1f);     // END
                        // wire 56-59 blob count = 0
                        // SOURCE Target (caster) @ wire 60: [int 1][Vector4 16][guid 8] = 28B (wire 60..87)
                        System.BitConverter.GetBytes(1).CopyTo(b, 60);
                        PV2(64, p.X, p.Y + 1.2f, p.Z, 1f);
                        if (80 + 8 <= b.Length) System.BitConverter.GetBytes(self).CopyTo(b, 80);
                        // DEST Target (enemy) @ wire 88: [int 1][Vector4 16][guid 8] = 28B (wire 88..115)
                        System.BitConverter.GetBytes(1).CopyTo(b, 88);
                        PV2(92, tpos.X, tpos.Y + 1.2f, tpos.Z, 1f);
                        if (108 + 8 <= b.Length) System.BitConverter.GetBytes(target).CopyTo(b, 108);
                        PV2(116, vx2, 0f, vz2, 0f);                     // VELOCITY after both targets (wire 116)
                        conn.Player.SendTunneledToVisible(new PlayerUpdateLaunchProjectilePacket { Body = b }, sendToSelf: true);
                    }
                    SendSystem(conn, $"!lp src -> swept 160..195; TargetCharacterGuid(type1)+vec+guid={self} @ wire60/80; check trace");
                    return true;
                }

                // "!lp sweep" - fire one packet per body size 120..180 so the trace pins the exact length
                // (deser ret=1 = PARSED OK at that pktLen). One test instead of many.
                if (parts.Length > 1 && parts[1] == "sweep")
                {
                    for (var n = 120; n <= 184; n++)
                        conn.Player.SendTunneledToVisible(new PlayerUpdateLaunchProjectilePacket { Body = new byte[n] }, sendToSelf: true);
                    SendSystem(conn, "!lp sweep -> sent op35/62 bodies 120..184; check the trace for deser ret=1");
                    return true;
                }

                byte[] body;
                if (parts.Length > 1 && parts[1] == "traj")
                {
                    // Body = 149 bytes (pinned live: pktLen 153 - 4 header). = 008e8910 nested struct (145B,
                    // wire offset 0) + trailing int32 @145. Trajectory Vector4s at nested 24/40/68 (start/end/
                    // velocity). Source guid = caster, placed at a probe offset (default nested wire 0 = the
                    // first int-pair, the likeliest source-entity field): "!lp traj <guidOffset>".
                    var guidOff = LI(2, 0);
                    body = new byte[149];
                    void PutVec(int off, float x, float y, float z, float w)
                    {
                        System.BitConverter.GetBytes(x).CopyTo(body, off);
                        System.BitConverter.GetBytes(y).CopyTo(body, off + 4);
                        System.BitConverter.GetBytes(z).CopyTo(body, off + 8);
                        System.BitConverter.GetBytes(w).CopyTo(body, off + 12);
                    }
                    // The client reads the guid as (firstDword << 32) | secondDword, i.e. dword-swapped vs
                    // a normal little-endian ulong (live trace: writing self plain gave the client self<<32).
                    // So write the dword-swapped value; the client swaps it back to the real caster guid.
                    var swapMode = LI(3, 1);
                    var gval = swapMode == 1 ? ((self & 0xFFFFFFFFUL) << 32) | (self >> 32) : self;
                    if (guidOff >= 0 && guidOff + 8 <= body.Length)
                        System.BitConverter.GetBytes(gval).CopyTo(body, guidOff); // source guid = caster
                    PutVec(24, p.X, p.Y + 1.2f, p.Z, 1f);          // START = caster
                    PutVec(40, tpos.X, tpos.Y + 1.2f, tpos.Z, 1f); // END = target
                    var vx = tpos.X - p.X; var vz = tpos.Z - p.Z;
                    var len = (float)System.Math.Sqrt(vx * vx + vz * vz);
                    if (len > 0.01f) { vx = vx / len * 45f; vz = vz / len * 45f; }
                    PutVec(68, vx, 0f, vz, 0f);                    // VELOCITY
                    SendSystem(conn, $"!lp traj guidOff={guidOff} guid={self} start=({p.X:0.#},{p.Z:0.#}) end=({tpos.X:0.#},{tpos.Z:0.#})");
                }
                else
                {
                    body = new byte[LI(1, 149)];   // size sweep default = the pinned 149
                }

                conn.Player.SendTunneledToVisible(new PlayerUpdateLaunchProjectilePacket { Body = body },
                    sendToSelf: true);
                SendSystem(conn, $"!lp -> op35/62 bodyLen={body.Length} target={target}");
                return true;
            }

            // SERVER-AUTHORITATIVE TRAVELLING PROJECTILE. The client's own fly path (op36/4) is gated on
            // combat state we cannot set, so we fly a real actor instead: an invisible carrier NPC that
            // moves caster->enemy with a PRJ_ effect attached (see ProjectileNpc). This works with NO client
            // combat state.  !proj [effId] [modelId] [speed] [impactEffId] [scale]
            //   effId   default 16110 (PRJ_archer_freezing-shot_trail)
            //   modelId carrier model (default 0 = hopefully invisible; the effect is the visual)
            //   speed   units/sec (default 45)
            case "proj":
            {
                if (!RequireAdmin(conn))
                    return true;

                int PI(int i, int def) => parts.Length > i && int.TryParse(parts[i], out var v) ? v : def;
                float PF(int i, float def) => parts.Length > i && float.TryParse(parts[i], out var v) ? v : def;

                var effId = PI(1, 16110);       // trail effect, attached to the flying model (0 = none)
                var modelId = PI(2, 1056);      // carrier model (1056 = invisible_cube_with_skeleton: invisible + bone)
                var speed = PF(3, 45f);
                var impactEffId = PI(4, 0);
                var scale = PF(5, 1f);
                var lingerMs = PI(6, 1500);     // keep carrier alive after arrival so the trail fades out

                if (conn.Player.Zone is not { } pz)
                {
                    SendSystem(conn, "!proj: no zone.");
                    return true;
                }

                var start = conn.Player.Position;
                start = new System.Numerics.Vector4(start.X, start.Y + 1.2f, start.Z, 1f); // chest height
                var tgt = new System.Numerics.Vector4(start.X, start.Y, start.Z + 20f, 1f); // 20u ahead fallback
                var best = float.MaxValue;
                foreach (var n in pz.Npcs)
                {
                    if (!n.Visible || !n.IsHostile) continue;
                    var dx0 = n.Position.X - start.X; var dz0 = n.Position.Z - start.Z;
                    var d0 = dx0 * dx0 + dz0 * dz0;
                    if (d0 < best) { best = d0; tgt = new System.Numerics.Vector4(n.Position.X, n.Position.Y + 1.2f, n.Position.Z, 1f); }
                }

                if (!pz.TryCreateProjectileNpc(out var proj))
                {
                    SendSystem(conn, "!proj: spawn failed.");
                    return true;
                }

                proj.ModelId = modelId;
                proj.Scale = scale;
                proj.SetTrail(effId);           // PRJ trail effect attached to the flying carrier model
                proj.Launch(start, tgt, speed, impactEffId, lingerMs);
                proj.ShowTo(conn.Player);      // register visibility + AddNpc + ExpectedSpeed
                proj.AttachTrail();             // op35/41 attach (follows the model); removed on landing

                SendSystem(conn, $"!proj -> eff={effId} model={modelId} speed={speed} from=({start.X:0.#},{start.Y:0.#},{start.Z:0.#}) to=({tgt.X:0.#},{tgt.Y:0.#},{tgt.Z:0.#})");
                return true;
            }

            // DEV PROBE: fire a newly-reversed ability packet at yourself so we can confirm the client
            // ACCEPTS the layout before wiring it into combat. Layouts came from the client's own inner
            // readers (op36 dispatcher FUN_00a35cc0) and the method was validated against StartCasting,
            // but "parses" and "does what we think" are different claims - this checks the first.
            //   !abil 18 [int]                 CastInterrupt        (guid, int)
            //   !abil 14 [int] [int] [float]   DetonateProjectile   (guid, int, int, float)
            case "abil":
            {
                // ADMIN ONLY. These fire raw, partially-understood ability packets at players: !abil 6 is
                // proven to FORCE a client cast, and !abil 4 reaches the action-bar system (it triggers a
                // toolbar cooldown refresh), so a non-admin could wedge someone's toolbar or puppet their
                // character. Dev probe, not a player command.
                if (!RequireAdmin(conn))
                    return true;

                if (parts.Length < 2 || !int.TryParse(parts[1], out var sub))
                {
                    SendSystem(conn, "Usage: !abil 18 [int] | !abil 14 [int] [int] [float]");
                    return true;
                }

                int ArgI(int i, int def) => parts.Length > i && int.TryParse(parts[i], out var v) ? v : def;
                float ArgF(int i, float def) => parts.Length > i && float.TryParse(parts[i], out var v) ? v : def;

                var self = conn.Player.Guid;
                switch (sub)
                {
                    case 18:
                        conn.Player.SendTunneledToVisible(new AbilityPacketCastInterrupt
                        {
                            Guid = self,
                            Unknown = ArgI(2, 0),
                        }, sendToSelf: true);
                        SendSystem(conn, $"!abil -> op36/18 CastInterrupt guid={self} u={ArgI(2, 0)}");
                        return true;

                    case 14:
                        conn.Player.SendTunneledToVisible(new AbilityPacketDetonateProjectile
                        {
                            Guid = self,
                            CompositeEffectId = ArgI(2, 0),
                            Unknown2 = ArgI(3, 0),
                            Unknown3 = ArgF(4, 0f),
                        }, sendToSelf: true);
                        SendSystem(conn, $"!abil -> op36/14 DetonateProjectile guid={self} " +
                                         $"u={ArgI(2, 0)} u2={ArgI(3, 0)} u3={ArgF(4, 0f)}");
                        return true;

                    case 9:
                        conn.Player.SendTunneledToVisible(new AbilityPacketStopAura
                        {
                            Guid = self,
                        }, sendToSelf: true);
                        SendSystem(conn, $"!abil -> op36/9 StopAura guid={self}");
                        return true;

                    case 15:
                        conn.Player.SendTunneledToVisible(new AbilityPacketPulseLocationTargeting
                        {
                            Enabled = ArgI(2, 1) != 0,
                            Unknown = ArgF(3, 5f),
                        }, sendToSelf: true);
                        SendSystem(conn, $"!abil -> op36/15 PulseLocationTargeting " +
                                         $"enabled={ArgI(2, 1) != 0} u={ArgF(3, 5f)}");
                        return true;

                    case 6:
                        conn.Player.SendTunneledToVisible(new AbilityPacketClientMoveAndCast
                        {
                            Position = conn.Player.Position,
                            Guid = self,
                        }, sendToSelf: true);
                        SendSystem(conn, $"!abil -> op36/6 ClientMoveAndCast pos={conn.Player.Position} guid={self}");
                        return true;

                    // op36/11 MeleeRefresh - the ability system's OWN cooldown packet (already drives the
                    // BASIC attack radial sweep). !abil 11 [ms] sends a visible-length cooldown so we can
                    // SEE which ability button it sweeps (basic only, or the special too). Default 10000ms.
                    case 11:
                    {
                        var ms = ArgI(2, 10000);
                        conn.Player.SendTunneled(new AbilityPacketMeleeRefresh { CooldownMs = ms });
                        SendSystem(conn, $"!abil -> op36/11 MeleeRefresh {ms}ms - watch which button sweeps");
                        return true;
                    }

                    // sub 4 LaunchAndLand. CRASH-SAFE BY DEFAULT: a NON-EMPTY string field crashes the
                    // client (proven twice), so the string is EMPTY unless explicitly requested. With an
                    // empty string this packet is safe and refreshes the ability toolbar cooldown - so we
                    // use it to hunt which numeric field controls the cooldown DURATION.
                    //   !abil 4                 empty string, all zeros (safe; does the cooldown refresh)
                    //   !abil 4 <field> <val>   set ONE int field (1..11) to val, string stays empty (safe)
                    //   !abil 4 name <asset>    DANGEROUS: sends the string (the crasher). Explicit only.
                    case 4:
                    {
                        var launch = new AbilityPacketLaunchAndLand
                        {
                            Guid = self,
                            Position = conn.Player.Position,
                            Guid2 = self,
                            Guid3 = self,
                        };

                        // NOTE: the old "!abil 4 name <asset>" path is GONE - the +0x18 field is a LIST, not
                        // a string; a non-empty value made the client parse garbage items and crash. The
                        // packet now always sends an empty list, so LaunchAndLand is crash-safe.

                        // PROJECTILE HUNT (now fully safe): set ONE int
                        // field to a real archer projectile-effect id and watch for a travelling projectile.
                        // Guid = caster; Position = a point ~20u in front of the player = the LAND target;
                        // Guid2 = nearest enemy (the projectile's target), else self. !abil 4 <field> <effectId>
                        // e.g. !abil 4 1 16110  (16110 = PRJ_archer_freezing-shot_trail).
                        var field = ArgI(2, 0);
                        var val = ArgI(3, 0);
                        var fval = ArgF(3, 0f);

                        // Land point ~20u ahead of the player (rough forward; good enough to see a projectile).
                        var p = conn.Player.Position;
                        launch.Position = new System.Numerics.Vector4(p.X, p.Y + 1f, p.Z + 20f, 1f);

                        // Target the nearest visible enemy if there is one.
                        if (conn.Player.Zone is { } z)
                        {
                            var best = float.MaxValue;
                            foreach (var n in z.Npcs)
                            {
                                if (!n.Visible || !n.IsHostile) continue;
                                var dx = n.Position.X - p.X; var dz = n.Position.Z - p.Z;
                                var d = dx * dx + dz * dz;
                                if (d < best) { best = d; launch.Guid2 = n.Guid; launch.Guid3 = n.Guid; launch.Position = n.Position; }
                            }
                        }

                        // Flag1 is the CONFIRMED projectile trigger (fires the projectile source 958220). Set
                        // it so the spawn block runs, then sweep an EFFECT/MODEL id across the fields to find
                        // what supplies the projectile visual.
                        launch.Flag1 = true;

                        // "!abil 4 shoot [effId]" - THE MOVING PROJECTILE. Reverse-engineered end to end:
                        //   * Unknown1 (=local_288) is the anim-vs-projectile SWITCH. !=0 routes op36/4 into
                        //     the real projectile launcher FUN_00b84190 (verified live: b84190 fires only
                        //     when Unknown1!=0). ==0 stays in the animation branch (958220/959bf0).
                        //   * Inside b84190 the VISIBLE moving projectile is spawned by FUN_00969590, but only
                        //     inside `if (iVar9 != 0)` where iVar9 = FUN_007c4710(param_6), and param_6 is fed
                        //     from local_278 = Unknown4. So Unknown4 must be a projectile EFFECT id that
                        //     007c4710 resolves; if it is 0, iVar9=0 and 969590 never runs (no projectile).
                        // Therefore the moving projectile needs BOTH Unknown1!=0 AND a valid Unknown4 - which
                        // the single-field probe could never set together. Default effId 16110 =
                        // PRJ_archer_freezing-shot_trail (a projectile trail effect).
                        if (parts.Length > 2 && parts[2] == "shoot")
                        {
                            launch.Unknown1 = 1;                 // local_288 != 0 -> projectile launcher branch
                            launch.Unknown4 = ArgI(3, 16110);    // param_6/local_278 -> 969590 spawn (projectile fx)
                            launch.Flag1 = true;
                            conn.Player.SendTunneledToVisible(launch, sendToSelf: true);
                            SendSystem(conn, $"!abil -> op36/4 SHOOT Unknown1=1 Unknown4={launch.Unknown4} " +
                                             $"target={launch.Guid2} pos={launch.Position}");
                            return true;
                        }

                        // "!abil 4 traj" - CRASH-SAFE trajectory. The nested struct 8e8910 was mapped
                        // statically: after six header ints/floats (wire 0..23) come TWO guaranteed 16-byte
                        // Vector4 slots - Vector1 @ wire offset 24 (in_ECX+0x50) and Vector2 @ wire offset 40
                        // (in_ECX+0x60) - BEFORE the variable blob (56) and the two polymorphic sub-object
                        // deserializers (60+). The old "proj" crash put a velocity Vector4 at offset 60, which
                        // is the polymorphic region, not Vector3. This path writes ONLY the two safe Vector4
                        // slots (start=caster @24, end=target @40) and leaves 56+ zeroed, so it cannot corrupt
                        // the polymorphic fields. Start+end alone should define the projectile path.
                        if (parts.Length > 2 && parts[2] == "traj")
                        {
                            // Live trace (nestedmap.js) confirmed the wire offsets: VEC1@24 (start),
                            // VEC2@40 (end), VEC3@68 (velocity). The wall (blob@56 + two polymorphic
                            // deserializers@60/64) consumes only 12 bytes WHEN LEFT ZERO (empty counts),
                            // so writing VEC3 at 68 while keeping 56/60/64 = 0 is crash-safe.
                            var startX = p.X; var startY = p.Y + 1f; var startZ = p.Z;
                            var endP = launch.Position;   // = target pos if an enemy was found, else 20u ahead
                            var nested = new byte[84];     // up to VEC3 (68..83); wall bytes 56..67 stay 0
                            void PutVec(int off, float x, float y, float zz, float w)
                            {
                                System.BitConverter.GetBytes(x).CopyTo(nested, off);
                                System.BitConverter.GetBytes(y).CopyTo(nested, off + 4);
                                System.BitConverter.GetBytes(zz).CopyTo(nested, off + 8);
                                System.BitConverter.GetBytes(w).CopyTo(nested, off + 12);
                            }
                            // Velocity = direction caster->target, scaled.
                            //   "!abil 4 traj [speed] [effectId]"
                            // Flag1's spawn (958220) is a PlayerAnimationEvent (an ANIM, not a projectile) -
                            // that is why Flag1 only made the enemy "attack". The travelling projectile needs a
                            // MODEL/EFFECT id + a real trajectory. The nested header int at wire 0 (dest +0x10)
                            // is a flat, non-NaN int = the prime model/effect-id suspect; earlier "no travel"
                            // tries on it had NO trajectory. Now it does. Pass effectId to seed it.
                            var spd = ArgF(3, 30f);
                            var effId = ArgI(4, 0);
                            var vx = endP.X - startX; var vy = endP.Y - startY; var vz = endP.Z - startZ;
                            var len = (float)System.Math.Sqrt(vx * vx + vy * vy + vz * vz);
                            if (len > 0.0001f) { vx = vx / len * spd; vy = vy / len * spd; vz = vz / len * spd; }
                            PutVec(24, startX, startY, startZ, 1f);              // VEC1 = start (caster)
                            PutVec(40, endP.X, endP.Y, endP.Z, 1f);             // VEC2 = end   (target)
                            PutVec(68, vx, vy, vz, 0f);                          // VEC3 = velocity (dir * speed)
                            if (effId != 0)
                                System.BitConverter.GetBytes(effId).CopyTo(nested, 0);   // nested +0x10 model/effect
                            launch.Nested = nested;
                            conn.Player.SendTunneledToVisible(launch, sendToSelf: true);
                            SendSystem(conn, $"!abil -> op36/4 TRAJ start=({startX:0.#},{startY:0.#},{startZ:0.#}) " +
                                             $"end=({endP.X:0.#},{endP.Y:0.#},{endP.Z:0.#}) vel=({vx:0.#},{vy:0.#},{vz:0.#}) " +
                                             $"eff={effId} target={launch.Guid2}");
                            return true;
                        }

                        switch (field)
                        {
                            case 1: launch.Unknown1 = val; break;
                            case 2: launch.Unknown2 = val; break;
                            case 3: launch.Unknown3 = val; break;
                            case 4: launch.Unknown4 = val; break;
                            case 5: launch.Unknown5 = val; break;
                            case 6: launch.Unknown6 = val; break;
                            case 7: launch.Unknown7 = val; break;
                            case 8: launch.Unknown8 = val; break;
                            case 9: launch.Unknown9 = fval; break;   // the float (+0x60)
                            case 10: launch.Unknown10 = val; break;
                            case 11: launch.Unknown11 = val; break;
                            // The bool FLAGS - the processor gates the projectile on a flag (local_274).
                            case 12: launch.Flag1 = val != 0; break;   // +0x3c
                            case 13: launch.Flag2 = val != 0; break;   // +0x3d
                            case 14: launch.Flag3 = val != 0; break;   // +0x80
                        }

                        // NESTED-STRUCT ints: field 100+N writes 'val' as an int at nested int-index N (the
                        // 8e8910 struct that holds the projectile MODEL/trajectory). Nested int 0/1 (+0x10/
                        // +0x14) are non-NaN-checked -> the prime suspects for the projectile MODEL id.
                        if (field >= 100)
                        {
                            var idx = field - 100;
                            var nested = new byte[(idx + 1) * 4];
                            System.BitConverter.GetBytes(val).CopyTo(nested, idx * 4);
                            launch.Nested = nested;
                        }

                        conn.Player.SendTunneledToVisible(launch, sendToSelf: true);
                        SendSystem(conn, $"!abil -> op36/4 PROJECTILE probe (empty str, safe) field{field}=" +
                                         (field == 9 ? $"{fval}f" : $"{val}") + $" target={launch.Guid2} pos={launch.Position}");
                        return true;
                    }

                    // RAW SIZE SWEEP: !abil 0 <sub> <bodyBytes>
                    // Sends [op36][sub] + N zero bytes. The deserializer accepts only at the EXACT body
                    // length, so sweeping N and watching the hook's return value pins the packet size.
                    case 0:
                    {
                        if (parts.Length < 4 ||
                            !int.TryParse(parts[2], out var rawSub) ||
                            !int.TryParse(parts[3], out var len))
                        {
                            SendSystem(conn, "Usage: !abil 0 raw <sub> <bodyBytes>");
                            return true;
                        }

                        conn.Player.SendTunneledToVisible(new AbilityPacketRawProbe((short)rawSub)
                        {
                            Body = new byte[len],
                        }, sendToSelf: true);
                        SendSystem(conn, $"!abil raw -> op36/{rawSub} body={len} bytes");
                        return true;
                    }

                    default:
                        SendSystem(conn, $"!abil: sub {sub} not implemented (have 6, 9, 14, 15, 18, 0=raw).");
                        return true;
                }
            }

            // Find the ability-cooldown field: re-send the SPECIAL's AbilityDefinition (op36/13) with ONE
            // candidate float set, then fire a special (op36/4 triggers the cooldown, reading the def's
            // duration). !abildef <1..8> <seconds>. Slots map to the def's still-zero floats:
            //   1=+0x44 2=+0x48 3=+0x6c 4=+0x78 5=+0x7c 6=+0x8c 7=+0x90 8=+0xa8
            case "abildef":
            {
                if (!RequireAdmin(conn))
                    return true;
                if (parts.Length < 3 || !int.TryParse(parts[1], out var which) ||
                    !float.TryParse(parts[2], out var secs))
                {
                    SendSystem(conn, "Usage: !abildef <1..8> <seconds>  (then fire a special to test)");
                    return true;
                }

                var kit = Sanctuary.Game.Combat.JobKits.Active(conn.Player);
                if (kit is null || kit.SlotAbilityDefIds.Count < 2)
                {
                    SendSystem(conn, "!abildef: no active combat kit / special def.");
                    return true;
                }

                var specialDefId = kit.SlotAbilityDefIds[1];
                var def = Sanctuary.Game.Combat.JobWeaponAbilities.ResolveAbilityDefinition(conn.Player, specialDefId);
                var packet = new AbilityPacketAbilityDefinition
                {
                    AbilityId = specialDefId,
                    NameId = def?.NameId ?? 0,
                    DescriptionId = def?.DescId ?? 0,
                    IconId = def?.IconId ?? 0,
                };
                switch (which)
                {
                    case 1: packet.Probe44 = secs; break;
                    case 2: packet.Probe48 = secs; break;
                    case 3: packet.Probe6c = secs; break;
                    case 4: packet.Probe78 = secs; break;
                    case 5: packet.Probe7c = secs; break;
                    case 6: packet.Probe8c = secs; break;
                    case 7: packet.Probe90 = secs; break;
                    case 8: packet.ProbeA8 = secs; break;
                }
                conn.Player.SendTunneled(packet);
                SendSystem(conn, $"!abildef -> special def {specialDefId} field{which}={secs}. " +
                                 $"Now fire a special and watch the sweep length.");
                return true;
            }

            // Find where a LONG, server-TICKED radial cooldown renders on the combat ability slots. This is
            // the item-cooldown mechanism (ClientUpdatePacketUpdateActionBarSlot, ticked every second) that
            // DOES show long radials (boombox 120s). Sweep <bar>/<slot> to find the combat special.
            // !cd <bar> <slot> <seconds>   e.g. !cd 1 1 10  (bar 1, slot 1 = special, 10s)
            case "cd":
            {
                if (!RequireAdmin(conn))
                    return true;
                if (parts.Length < 4 || !int.TryParse(parts[1], out var bar) ||
                    !int.TryParse(parts[2], out var slot) || !int.TryParse(parts[3], out var secs))
                {
                    SendSystem(conn, "Usage: !cd <bar> <slot> <seconds>  e.g. !cd 1 1 10");
                    return true;
                }

                var ab = Sanctuary.Game.Combat.JobWeaponAbilities.ResolveAbility(conn.Player, slot);
                conn.Player.StartActionBarCooldown(bar, slot, ab.IconImageId, 0, 1, secs * 1000);
                SendSystem(conn, $"!cd -> bar {bar} slot {slot} for {secs}s (ticked radial). " +
                                 $"Watch if the special icon shows a {secs}s sweep.");
                return true;
            }

            default:
                SendSystem(conn, $"Unknown command '{verb}'. Try /help.");
                return true;
        }
    }


    // ================== BASIC HELP ==================

    // Enter a data-driven combat dungeon (DungeonCatalog) directly by activity id — the test entry until
    // each dungeon gets its world entry NPC. Pulls the party in (co-op), same as the GO! button.
    private static bool HandleDungeon(GatewayConnection conn, string[] parts)
    {
        var catalog = Sanctuary.Game.Dungeons.DungeonCatalog.ByActivity;
        if (parts.Length < 2 || !int.TryParse(parts[1], out var id) || !catalog.ContainsKey(id))
        {
            SendSystem(conn, "Usage: !dungeon <id>. Available:");
            foreach (var d in catalog.Values)
                SendSystem(conn, $"  {d.ActivityId} - {d.Comment}");
            return true;
        }
        EncounterParticipantRequestEntranceHandler.EnterEncounterArena(conn, id);
        SendSystem(conn, $"Entering dungeon {id} ({catalog[id].Comment})...");
        return true;
    }

    // Dev mapping helper: print (and log) the caller's current coordinates in copy-paste-ready forms — a raw
    // Vector4 and the DungeonDefinition center fields — for noting player/enemy spawn spots per dungeon. Stand on
    // the spot and run "!pos". "!pos npcs" also lists the nearest NPCs + their coords (handy for enemy placement).
    // Everything is also written to the gateway Info log, so a whole mapping run can be collected from the file.
    private static bool HandlePos(GatewayConnection conn, string[] parts)
    {
        var p = conn.Player;
        var pos = p.Position;
        var rot = p.Rotation;
        var heading = MathF.Atan2(rot.X, rot.Z);
        var deg = heading * 180f / MathF.PI;
        var world = p.Zone?.Name ?? "(no zone)";

        SendSystem(conn, $"[POS] {p.Name?.FullName} @ {world}");
        SendSystem(conn, $"  X={pos.X:0.00}  Y={pos.Y:0.00}  Z={pos.Z:0.00}  heading={deg:0}°");
        SendSystem(conn, $"  new Vector4({pos.X:0.00}f, {pos.Y:0.00}f, {pos.Z:0.00}f, 1f)");
        SendSystem(conn, $"  CenterX = {pos.X:0.00}f, CenterZ = {pos.Z:0.00}f, GroundY = {pos.Y:0.00}f");

        // NB: don't reuse {x}/{y}/{z} in this template — NLog binds named placeholders POSITIONALLY, so a
        // reused name needs another arg (an 11-placeholder template with 8 args threw FormatException on every
        // !pos). The copy-paste Vector4 form is already shown to the caller via SendSystem above.
        _logger.LogInformation("[POS] {name} @ {world} | X={x:0.00} Y={y:0.00} Z={z:0.00} W={w:0.00} | heading={deg:0}deg ({h:0.000}rad)",
            p.Name?.FullName, world, pos.X, pos.Y, pos.Z, pos.W, deg, heading);

        if (parts.Length >= 2 && parts[1].StartsWith("npc", StringComparison.OrdinalIgnoreCase))
        {
            var zone = p.Zone;
            if (zone is not null)
            {
                float Dist(Npc n) { var dx = n.Position.X - pos.X; var dz = n.Position.Z - pos.Z; return MathF.Sqrt(dx * dx + dz * dz); }
                var near = zone.Npcs.Where(n => n.Visible).OrderBy(Dist).Take(12).ToList();
                SendSystem(conn, $"  -- {near.Count} nearest NPCs --");
                foreach (var n in near)
                {
                    SendSystem(conn, $"  model={n.ModelId} name={n.NameId} @ ({n.Position.X:0.00}, {n.Position.Y:0.00}, {n.Position.Z:0.00}) d={Dist(n):0.0}{(n.IsHostile ? " [hostile]" : "")}");
                    _logger.LogInformation("[POS-NPC] {world} model={model} nameId={nameId} hostile={h} @ new Vector4({x:0.00}f,{y:0.00}f,{z:0.00}f,1f) dist={d:0.0}",
                        world, n.ModelId, n.NameId, n.IsHostile, n.Position.X, n.Position.Y, n.Position.Z, Dist(n));
                }
            }
        }
        return true;
    }

    private static bool HandleHelp(GatewayConnection conn)
    {
        string helpText =
            "Available commands:\n" +
            "/help - This list\n" +
            "/pos (or /coords, /loc) - Show your coordinates; '/pos npc' also lists nearby NPCs\n" +
            "/listplayers - List online players\n" +
            "/hp [full] - HP/Mana status (full = heal to max)\n" +
            "/respawn - Revive after being knocked out\n" +
            "/die - Knock yourself out (test)\n" +
            "/dodge [on|off] - Toggle always-dodge (test)\n" +
            "/xp [amount] - Grant your active job XP\n" +
            "/fly - Toggle fly mode\n" +
            "/createhouse [HouseDefId] - Create a new house\n" +
            "/listhouses - List your houses\n" +
            "/gohouse [HouseId] - Enter a house\n" +
            "/petspawn [PetId] - Spawn a pet\n" +
            "/petdespawn - Despawn your active pet\n" +
            "/petlist - List your pets\n" +
            "\nParty:\n" +
            "/paccept - Accept a party invite\n" +
            "/pleave - Leave your party\n" +
            "/ptest - (RE) send yourself a candidate group invite\n" +
            "/proster - (RE) send yourself a candidate group roster\n" +
            "\nDev/test:\n" +
            "/dungeon [id] - Enter a combat dungeon by activity id (no arg lists them)\n" +
            "/spawntest - Spawn a combat test dummy\n" +
            "/testicons - Icon probe\n" +
            "/testsubtext - Subtext probe\n" +
            "/lua [code] - Run a client Lua snippet";

        if (IsEnforcer(conn))
        {
            helpText += "\n\nEnforcer commands:\n" +
                "/tp [PlayerName] - Teleport to player\n" +
                "/bring [PlayerName] - Bring player to you\n" +
                "/where [PlayerName] - Show player location\n" +
                "/kick [PlayerName] [reason] - Kick a player\n" +
                "/warn [PlayerName] [message] - Warn a player\n" +
                "/gift [PlayerName] [ItemId] [quantity] - Gift items\n" +
                "/enforcer list - List active enforcers";
        }

        if (IsAdmin(conn))
        {
            helpText += "\n\nAdmin commands:\n" +
                "/npc spawn [NameId] [ModelId] [TextureAlias]\n" +
                "/goto x y z - Teleport to coordinates\n" +
                "/announce [Message] - Server-wide announcement\n" +
                "/admin list - List admins\n" +
                "/giveitem [ItemId] [quantity] - Give yourself an item\n" +
                "/spawnenemy [ModelId] [Level] [Name] - Spawn a combat NPC\n" +
                "/spawnhouse [ModelId] - Test house models\n" +
                "/testeffect [effectId] [modelId] [animId] [standAnimId]\n" +
                "/playeffect [effectId] - Play a composite effect on you\n" +
                "/testtransform [transformId] - Test a transformation";
        }

        SendSystem(conn, helpText);
        return true;
    }


    // ================== ADMIN CHECK ==================

    private static bool RequireAdmin(GatewayConnection conn)
    {
        if (!IsAdmin(conn))
        {
            SendSystem(conn, "You do not have permission to use this command.");
            return false;
        }
        return true;
    }

    private static bool IsAdmin(GatewayConnection conn)
    {
        // Use the database character ID, not the runtime GUID
        long characterId = (long)conn.Player.CharacterId;

        try
        {
            _logger.LogInformation("Checking admin status for character ID: {CharId}, DB: {DbConn}", characterId, _dbConnectionString);

            using var db = new SqliteConnection(_dbConnectionString);
            db.Open();

            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
            SELECT u.IsAdmin
            FROM Users u
            JOIN Characters c ON c.UserId = u.Id
            WHERE c.Id = $charId
            LIMIT 1;
        ";

            cmd.Parameters.AddWithValue("$charId", characterId);

            var result = cmd.ExecuteScalar();

            _logger.LogInformation("Admin check result for char {CharId}: {Result}", characterId, result);

            if (result == null || result is DBNull)
            {
                _logger.LogWarning("No admin result found for character {CharId}", characterId);
                return false;
            }

            bool isAdmin = Convert.ToInt32(result) == 1;
            _logger.LogInformation("Character {CharId} admin status: {IsAdmin}", characterId, isAdmin);
            return isAdmin;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking admin status for character {CharId}", characterId);
            return false;
        }
    }

    private static long? GetUserGuid(GatewayConnection conn)
    {
        long characterId = (long)conn.Player.CharacterId;

        try
        {
            using var db = new SqliteConnection(_dbConnectionString);
            db.Open();

            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
            SELECT c.UserId
            FROM Characters c
            WHERE c.Id = $charId
            LIMIT 1;
        ";
            cmd.Parameters.AddWithValue("$charId", characterId);

            var result = cmd.ExecuteScalar();
            if (result == null || result is DBNull)
                return null;

            return Convert.ToInt64(result);
        }
        catch
        {
            return null;
        }
    }

    private static bool RequireOwnerForAdminManagement(GatewayConnection conn)
    {
        var userGuid = GetUserGuid(conn);

        if (userGuid != 1)
        {
            SendSystem(conn, "Only the server owner can manage admins.");
            return false;
        }

        return true;
    }


    // ================== /NPC COMMANDS ==================

    // /npc spawn <NameId> <ModelId> [TextureAlias]
    private static bool HandleNpc(GatewayConnection conn, string[] parts)
    {
        if (!RequireAdmin(conn))
            return true;

        if (parts.Length < 2)
        {
            SendSystem(conn, "Usage: /npc spawn <NameId> <ModelId> [TextureAlias]");
            return true;
        }

        var sub = parts[1].ToLowerInvariant();
        return sub switch
        {
            "spawn" => HandleNpcSpawn(conn, parts),
            _ => UnknownSubCommand(conn, "npc", sub)
        };
    }

    private static bool HandleNpcSpawn(GatewayConnection conn, string[] parts)
    {
        if (parts.Length < 4)
        {
            SendSystem(conn, "Usage: /npc spawn <NameId> <ModelId> [TextureAlias]");
            return true;
        }

        if (!int.TryParse(parts[2], out var nameId) ||
            !int.TryParse(parts[3], out var modelId))
        {
            SendSystem(conn, "Usage: /npc spawn <NameId> <ModelId> [TextureAlias]");
            return true;
        }

        string? texture = parts.Length >= 5 ? parts[4] : null;

        var zone = conn.Player.Zone;
        if (zone == null)
        {
            SendSystem(conn, "You are not in a zone.");
            return true;
        }

        if (!zone.TryCreateNpc(out var npc) || npc is null)
        {
            SendSystem(conn, "Failed to create NPC.");
            return true;
        }

        npc.NameId = nameId;
        npc.ModelId = modelId;
        npc.TextureAlias = texture;
        npc.Scale = 1f;
        npc.Visible = true;

        npc.UpdatePosition(conn.Player.Position, conn.Player.Rotation);

        var tile = zone.GetTileFromPosition(conn.Player.Position);
        tile.Entities.TryAdd(npc.Guid, npc);

        SendSystem(conn, $"NPC spawned (Guid={npc.Guid}, NameId={nameId}, ModelId={modelId}).");
        return true;
    }

    // ================== /ADMIN COMMANDS ==================

    // /admin add <Username>
    // /admin remove <Username>
    // /admin list
    private static bool HandleAdmin(GatewayConnection conn, string[] parts)
    {
        if (!RequireAdmin(conn))
            return true;

        if (parts.Length < 2)
        {
            SendSystem(conn, "Usage: /admin <add|remove|list> [...]");
            return true;
        }

        var sub = parts[1].ToLowerInvariant();

        return sub switch
        {
            "add" => HandleAdminAdd(conn, parts),
            "remove" => HandleAdminRemove(conn, parts),
            "list" => HandleAdminList(conn),
            _ => UnknownSubCommand(conn, "admin", sub)
        };
    }

    private static bool HandleAdminAdd(GatewayConnection conn, string[] parts)
    {
        if (!RequireOwnerForAdminManagement(conn))
            return true;

        if (parts.Length < 3)
        {
            SendSystem(conn, "Usage: /admin add <Username>");
            return true;
        }

        // Support multi-word usernames just in case
        string pattern = string.Join(' ', parts, 2, parts.Length - 2);

        if (!TryResolveUsernamePattern(pattern, out var resolvedUsername, out var error))
        {
            SendSystem(conn, error);
            return true;
        }

        int rows = ExecuteNonQuery(
            "UPDATE Users SET IsAdmin = 1 WHERE Username = $u;",
            ("$u", resolvedUsername));

        if (rows > 0)
            SendSystem(conn, $"User '{resolvedUsername}' is now an admin.");
        else
            SendSystem(conn, $"User '{resolvedUsername}' not found.");

        return true;
    }


    private static bool HandleAdminRemove(GatewayConnection conn, string[] parts)
    {
        if (!RequireOwnerForAdminManagement(conn))
            return true;

        if (parts.Length < 3)
        {
            SendSystem(conn, "Usage: /admin remove <Username>");
            return true;
        }

        string pattern = string.Join(' ', parts, 2, parts.Length - 2);

        if (!TryResolveUsernamePattern(pattern, out var resolvedUsername, out var error))
        {
            SendSystem(conn, error);
            return true;
        }

        int rows = ExecuteNonQuery(
            "UPDATE Users SET IsAdmin = 0 WHERE Username = $u;",
            ("$u", resolvedUsername));

        if (rows > 0)
            SendSystem(conn, $"User '{resolvedUsername}' is no longer an admin.");
        else
            SendSystem(conn, $"User '{resolvedUsername}' not found.");

        return true;
    }


    private static bool HandleAdminList(GatewayConnection conn)
    {
        try
        {
            using var db = new SqliteConnection(_dbConnectionString);
            db.Open();

            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT Username FROM Users WHERE IsAdmin = 1 ORDER BY Username;";

            using var reader = cmd.ExecuteReader();

            var list = new List<string>();
            while (reader.Read())
            {
                list.Add(reader.GetString(0));
            }

            if (list.Count == 0)
            {
                SendSystem(conn, "No admins configured.");
            }
            else
            {
                SendSystem(conn, "Admins: " + string.Join(", ", list));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list admins.");
            SendSystem(conn, "Error listing admins.");
        }

        return true;
    }

    // ================== ENFORCER COMMANDS ==================

    private static bool IsEnforcer(GatewayConnection conn)
    {
        // Only users with IsAdmin = 1 in the database can use Referee commands
        return IsAdmin(conn);
    }

    private static bool IsPlayerAdmin(Player player)
    {
        long characterId = (long)player.CharacterId;

        try
        {
            using var db = new SqliteConnection(_dbConnectionString);
            db.Open();

            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
                SELECT u.IsAdmin
                FROM Users u
                JOIN Characters c ON c.UserId = u.Id
                WHERE c.Id = $charId
                LIMIT 1;
            ";

            cmd.Parameters.AddWithValue("$charId", characterId);

            var result = cmd.ExecuteScalar();

            if (result == null || result is DBNull)
                return false;

            return Convert.ToInt32(result) == 1;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check admin status for player {Player}", player.Name.FullName);
            return false;
        }
    }

    private static bool RequireEnforcer(GatewayConnection conn)
    {
        if (!IsEnforcer(conn))
        {
            SendSystem(conn, "You must be a Referee (admin) to use this command.");
            return false;
        }
        return true;
    }

    private static bool HandleEnforcer(GatewayConnection conn, string[] parts)
    {
        if (!RequireOwnerForAdminManagement(conn))
            return true;

        if (parts.Length < 2)
        {
            SendSystem(conn, "Usage: /enforcer <add|remove|list> [Username]");
            return true;
        }

        var sub = parts[1].ToLowerInvariant();

        return sub switch
        {
            "list" => HandleEnforcerList(conn),
            _ => UnknownSubCommand(conn, "enforcer", sub)
        };
    }

    private static bool HandleEnforcerList(GatewayConnection conn)
    {
        try
        {
            using var db = new SqliteConnection(_dbConnectionString);
            db.Open();

            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
                SELECT u.Username
                FROM Users u
                WHERE u.IsAdmin = 1
                ORDER BY u.Username;
            ";

            using var reader = cmd.ExecuteReader();

            var list = new List<string>();
            while (reader.Read())
            {
                list.Add(reader.GetString(0));
            }

            if (list.Count == 0)
            {
                SendSystem(conn, "No Referees (admins) configured.");
            }
            else
            {
                SendSystem(conn, "Referees: " + string.Join(", ", list));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list referees.");
            SendSystem(conn, "Error listing referees.");
        }

        return true;
    }

    private static bool HandleKick(GatewayConnection conn, string[] parts)
    {
        if (!RequireEnforcer(conn))
            return true;

        if (parts.Length < 2)
        {
            SendSystem(conn, "Usage: /kick <PlayerName> [reason]");
            return true;
        }

        string pattern = parts[1];
        string reason = parts.Length > 2 ? string.Join(' ', parts, 2, parts.Length - 2) : "Kicked by an Enforcer";

        if (!TryResolvePlayerNamePattern(pattern, out var resolvedName, out var error))
        {
            SendSystem(conn, error);
            return true;
        }

        if (!_zoneManager.TryGetPlayer(resolvedName, out var target))
        {
            SendSystem(conn, $"Player '{resolvedName}' not found.");
            return true;
        }

        // Don't allow kicking other admins
        if (IsPlayerAdmin(target))
        {
            SendSystem(conn, "You cannot kick other admins/Referees.");
            return true;
        }

        _logger.LogWarning("Player {Player} kicked by Referee {Referee}. Reason: {Reason}",
            target.Name.FullName, conn.Player.Name.FullName, reason);

        SendMessageToPlayer(target, $"You have been kicked from the server. Reason: {reason}");

        // Give them a moment to see the message, then disconnect
        System.Threading.Tasks.Task.Delay(2000).ContinueWith(_ =>
        {
            target.Disconnect();
        });

        SendSystem(conn, $"Kicked {target.Name.FullName}. Reason: {reason}");
        return true;
    }

    private static bool HandleWarn(GatewayConnection conn, string[] parts)
    {
        if (!RequireEnforcer(conn))
            return true;

        if (parts.Length < 3)
        {
            SendSystem(conn, "Usage: /warn <PlayerName> <message>");
            return true;
        }

        string pattern = parts[1];
        string message = string.Join(' ', parts, 2, parts.Length - 2);

        if (!TryResolvePlayerNamePattern(pattern, out var resolvedName, out var error))
        {
            SendSystem(conn, error);
            return true;
        }

        if (!_zoneManager.TryGetPlayer(resolvedName, out var target))
        {
            SendSystem(conn, $"Player '{resolvedName}' not found.");
            return true;
        }

        _logger.LogInformation("Player {Player} warned by Referee {Referee}. Message: {Message}",
            target.Name.FullName, conn.Player.Name.FullName, message);

        SendMessageToPlayer(target, $"[REFEREE WARNING] {message}");
        SendSystem(conn, $"Warning sent to {target.Name.FullName}");
        return true;
    }

    private static bool HandleGift(GatewayConnection conn, string[] parts)
    {
        if (!RequireEnforcer(conn))
            return true;

        if (parts.Length < 3)
        {
            SendSystem(conn, "Usage: /gift <PlayerName> <ItemId> [quantity]");
            return true;
        }

        string pattern = parts[1];

        if (!int.TryParse(parts[2], out var itemId))
        {
            SendSystem(conn, "ItemId must be a number.");
            return true;
        }

        int quantity = 1;
        if (parts.Length >= 4 && !int.TryParse(parts[3], out quantity))
        {
            SendSystem(conn, "Quantity must be a number.");
            return true;
        }

        if (!TryResolvePlayerNamePattern(pattern, out var resolvedName, out var error))
        {
            SendSystem(conn, error);
            return true;
        }

        if (!_zoneManager.TryGetPlayer(resolvedName, out var target))
        {
            SendSystem(conn, $"Player '{resolvedName}' not found.");
            return true;
        }

        // Check if item exists
        if (!_resourceManager.ClientItemDefinitions.TryGetValue(itemId, out var itemDef))
        {
            SendSystem(conn, $"Item {itemId} not found in item definitions.");
            return true;
        }

        _logger.LogInformation("Referee {Referee} gifted {Quantity}x Item {ItemId} to {Player}",
            conn.Player.Name.FullName, quantity, itemId, target.Name.FullName);

        // TODO: Actually add the item to player's inventory
        // This requires inventory system implementation

        SendMessageToPlayer(target, $"[GIFT] A Referee has gifted you {quantity}x {itemDef.NameId}!");
        SendSystem(conn, $"Gifted {quantity}x Item {itemId} to {target.Name.FullName}");
        SendSystem(conn, "Note: Inventory system not yet implemented - item not actually added.");

        return true;
    }

    private static bool HandleWhere(GatewayConnection conn, string[] parts)
    {
        if (!RequireEnforcer(conn))
            return true;

        var zone = conn.Player.Zone;
        if (zone == null)
        {
            SendSystem(conn, "You are not in a zone.");
            return true;
        }

        var target = conn.Player;

        // /where <pattern>  → look up another player
        if (parts.Length >= 2)
        {
            string pattern = string.Join(' ', parts, 1, parts.Length - 1);

            if (!TryResolvePlayerNamePattern(pattern, out var resolvedName, out var error))
            {
                SendSystem(conn, error);
                return true;
            }

            if (!_zoneManager.TryGetPlayer(resolvedName, out var found))
            {
                SendSystem(conn, $"Player '{resolvedName}' not found (after resolving pattern).");
                return true;
            }

            target = found;
            zone = target.Zone ?? zone; // if target is in another zone, prefer that
        }

        var pos = target.Position;
        SendSystem(conn, $"{target.Name.FullName} is at ({pos.X:0.0}, {pos.Y:0.0}, {pos.Z:0.0}) in zone {zone.Id}.");
        return true;
    }



    private static bool HandleTp(GatewayConnection conn, string[] parts)
    {
        if (!RequireEnforcer(conn))
            return true;

        if (parts.Length < 2)
        {
            SendSystem(conn, "Usage: /tp <PlayerName>");
            return true;
        }

        // Multi-word pattern: everything after /tp
        string pattern = string.Join(' ', parts, 1, parts.Length - 1);

        if (!TryResolvePlayerNamePattern(pattern, out var resolvedName, out var error))
        {
            SendSystem(conn, error);
            return true;
        }

        // Now use the resolved *exact* name with ZoneManager
        if (!_zoneManager.TryGetPlayer(resolvedName, out var target))
        {
            // This really shouldn't happen now, but just in case:
            SendSystem(conn, $"Player '{resolvedName}' not found (after resolving pattern).");
            return true;
        }

        var targetZone = target.Zone;
        if (targetZone == null)
        {
            SendSystem(conn, $"Player '{resolvedName}' is not in a valid zone.");
            return true;
        }

        conn.Player.TeleportToZone(targetZone, target.Position, target.Rotation);

        SendSystem(conn, $"Teleported to {target.Name.FullName}.");
        return true;
    }



    private static bool HandleBring(GatewayConnection conn, string[] parts)
    {
        if (!RequireEnforcer(conn))
            return true;

        if (parts.Length < 2)
        {
            SendSystem(conn, "Usage: /bring <PlayerName>");
            return true;
        }

        string pattern = string.Join(' ', parts, 1, parts.Length - 1);

        if (!TryResolvePlayerNamePattern(pattern, out var resolvedName, out var error))
        {
            SendSystem(conn, error);
            return true;
        }

        if (!_zoneManager.TryGetPlayer(resolvedName, out var target))
        {
            SendSystem(conn, $"Player '{resolvedName}' not found (after resolving pattern).");
            return true;
        }

        var zone = conn.Player.Zone;
        if (zone == null)
        {
            SendSystem(conn, "You are not in a zone.");
            return true;
        }

        target.TeleportToZone(zone, conn.Player.Position, conn.Player.Rotation);

        SendSystem(conn, $"Brought {target.Name.FullName} to your position.");
        return true;
    }



    private static bool HandleGoto(GatewayConnection conn, string[] parts)
    {
        if (!RequireAdmin(conn))
            return true;

        if (parts.Length < 4)
        {
            SendSystem(conn, "Usage: /goto <x> <y> <z>");
            return true;
        }

        if (!float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) ||
            !float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y) ||
            !float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z))
        {
            SendSystem(conn, "Usage: /goto <x> <y> <z>");
            return true;
        }

        var zone = conn.Player.Zone;
        if (zone == null)
        {
            SendSystem(conn, "You are not in a zone.");
            return true;
        }

        var newPos = new System.Numerics.Vector4(x, y, z, 1);
        var rot = conn.Player.Rotation;

        // Use the same logic as zoning/teleporting between zones,
        // but allow same-zone teleports now that we patched TeleportToZone.
        conn.Player.TeleportToZone(zone, newPos, rot);

        SendSystem(conn, $"Teleported to ({x:0.0}, {y:0.0}, {z:0.0}) in zone {zone.Id}.");
        return true;
    }

    private static bool HandleAnnounce(GatewayConnection conn, string[] parts)
    {
        if (!RequireAdmin(conn))
            return true;

        if (parts.Length < 2)
        {
            SendSystem(conn, "Usage: /announce <message>");
            return true;
        }

        string msg = string.Join(" ", parts, 1, parts.Length - 1);

        var chatPacket = new PacketChat
        {
            Channel = ChatChannel.System,
            FromGuid = 0,                    // system / anonymous
            FromName = new NameData(),       // empty name
            Message = "[ANNOUNCEMENT] " + msg
        };

        int sentCount = 0;

        // Send to starting zone players
        foreach (var player in _zoneManager.StartingZone.Players)
        {
            player.SendTunneled(chatPacket);
            sentCount++;
        }

        SendSystem(conn, $"Announcement sent to {sentCount} player(s).");
        return true;
    }



    private static bool HandlePlayers(GatewayConnection conn, string[] parts)
    {
        if (!RequireAdmin(conn))
            return true;

        var list = new List<string>();

        // Get all players from starting zone
        foreach (var p in _zoneManager.StartingZone.Players)
        {
            // Show GUID + Name so you can distinguish players
            list.Add($"{p.Guid} — {p.Name.FullName}");
        }

        if (list.Count == 0)
        {
            SendSystem(conn, "No players online.");
            return true;
        }

        // Build a nice readable list
        string msg = "Online players:\n" + string.Join("\n", list);

        SendSystem(conn, msg);
        return true;
    }

    // ================== HOUSING COMMANDS ==================

    private static bool HandleCreateHouse(GatewayConnection conn, string[] parts)
    {
        // Default house definition ID (you can change this based on your house definitions)
        int houseDefId = 1;

        if (parts.Length >= 2 && int.TryParse(parts[1], out var customDefId))
        {
            houseDefId = customDefId;
        }

        // Validate the house definition exists
        if (!_resourceManager.Houses.TryGetValue(houseDefId, out var houseDef))
        {
            var availableIds = string.Join(", ", _resourceManager.Houses.Keys.OrderBy(k => k).Take(10));
            SendSystem(conn, $"House definition {houseDefId} not found.");
            SendSystem(conn, $"Available house types: {availableIds}...");
            return true;
        }

        long characterId = (long)conn.Player.CharacterId;

        try
        {
            using var db = new SqliteConnection(_dbConnectionString);
            db.Open();

            // Create a new house for the player
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Houses (OwnerId, HouseDefinitionId, NameId, IsLocked, IsMembersOnly, IsFloraAllowed,
                                   PetAutospawn, MaxFixtureCount, MaxLandmarkCount, IconId, Rating, Votes,
                                   Created, LastVisited)
                VALUES ($ownerId, $houseDefId, 0, 0, 0, 1, 0, 100, 10, 0, 0.0, 0, datetime('now'), datetime('now'));

                SELECT last_insert_rowid();
            ";
            cmd.Parameters.AddWithValue("$ownerId", characterId);
            cmd.Parameters.AddWithValue("$houseDefId", houseDefId);

            var newHouseId = cmd.ExecuteScalar();

            SendSystem(conn, $"Created house #{newHouseId} (Type: {houseDef.NameId}). Use /gohouse {newHouseId} to enter!");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create house for character {CharId}", characterId);
            SendSystem(conn, "Error creating house.");
            return true;
        }
    }

    private static bool HandleListHouses(GatewayConnection conn, string[] parts)
    {
        long characterId = (long)conn.Player.CharacterId;

        try
        {
            using var db = new SqliteConnection(_dbConnectionString);
            db.Open();

            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
                SELECT Id, HouseDefinitionId, CustomName, Created
                FROM Houses
                WHERE OwnerId = $charId
                ORDER BY Created DESC;
            ";
            cmd.Parameters.AddWithValue("$charId", characterId);

            using var reader = cmd.ExecuteReader();

            var houses = new List<string>();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var defId = reader.GetInt32(1);
                var customName = reader.IsDBNull(2) ? null : reader.GetString(2);
                var created = reader.GetString(3);

                var name = customName ?? $"House #{id}";
                houses.Add($"#{id}: {name} (Def: {defId}, Created: {created})");
            }

            if (houses.Count == 0)
            {
                SendSystem(conn, "You don't have any houses. Use /createhouse to get one!");
            }
            else
            {
                SendSystem(conn, "Your houses:\n" + string.Join("\n", houses));
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list houses for character {CharId}", characterId);
            SendSystem(conn, "Error listing houses.");
            return true;
        }
    }

    private static bool HandleGoHouse(GatewayConnection conn, string[] parts)
    {
        if (parts.Length < 2)
        {
            SendSystem(conn, "Usage: /gohouse <HouseId>");
            return true;
        }

        if (!long.TryParse(parts[1], out var houseId))
        {
            SendSystem(conn, "House ID must be a number.");
            return true;
        }

        long characterId = (long)conn.Player.CharacterId;

        try
        {
            using var db = new SqliteConnection(_dbConnectionString);
            db.Open();

            // Verify the house exists and get its info
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
                SELECT h.OwnerId, h.HouseDefinitionId
                FROM Houses h
                WHERE h.Id = $houseId
                LIMIT 1;
            ";
            cmd.Parameters.AddWithValue("$houseId", houseId);

            using var reader = cmd.ExecuteReader();

            if (!reader.Read())
            {
                SendSystem(conn, $"House #{houseId} not found.");
                return true;
            }

            var ownerId = reader.GetInt64(0);
            var houseDefId = reader.GetInt32(1);

            // For now, only allow owners to enter (you can add permissions later)
            if (ownerId != characterId)
            {
                SendSystem(conn, $"You don't have permission to enter house #{houseId}.");
                return true;
            }

            // Get the house definition from the resource manager
            if (!_resourceManager.Houses.TryGetValue(houseDefId, out var houseDef))
            {
                SendSystem(conn, $"House definition {houseDefId} not found. Using default.");
                // Fall back to default housing zone
                var defaultPacket = new PacketClientBeginZoning
                {
                    Name = "hsg_emptylot_seaside_beach_01",
                    Type = 2,
                    Position = new System.Numerics.Vector4(440.632f, -0.071f, 432.801f, 1.0f),
                    Rotation = new System.Numerics.Quaternion(-0.9999741f, 0.0f, -0.0072035603f, 0.0f),
                    Sky = "sky_seaside24.xml",
                    Unknown = 1,
                    Id = (int)houseId,
                    GeometryId = 214,
                    OverrideUpdateRadius = true
                };
                conn.SendTunneled(defaultPacket);
                SendSystem(conn, $"Entering house #{houseId}...");
                return true;
            }

            // Get the zone definition for this house
            string zoneName = "hsg_emptylot_seaside_beach_01"; // Default fallback
            string sky = "sky_seaside24.xml"; // Default sky
            int geometryId = 214; // Default geometry
            var spawnPosition = houseDef.SpawnPosition;
            var spawnRotation = new System.Numerics.Quaternion(
                houseDef.SpawnRotation.X,
                houseDef.SpawnRotation.Y,
                houseDef.SpawnRotation.Z,
                houseDef.SpawnRotation.W
            );

            if (_resourceManager.Zones.TryGetValue(houseDef.ZoneId, out var zoneDef))
            {
                zoneName = zoneDef.Name;
                // Use zone definition spawn position if available (more reliable)
                if (zoneDef is Sanctuary.Game.Resources.Definitions.Zones.StartingZoneDefinition startingZone)
                {
                    spawnPosition = new System.Numerics.Vector4(
                        startingZone.SpawnPosition.X,
                        startingZone.SpawnPosition.Y + 2f, // Add 2 units height to prevent falling
                        startingZone.SpawnPosition.Z,
                        0
                    );

                    spawnRotation = new System.Numerics.Quaternion(
                        startingZone.SpawnRotation.X,
                        startingZone.SpawnRotation.Y,
                        0,
                        0
                    );

                    _logger.LogInformation("Using zone spawn position: ({X}, {Y}, {Z})",
                        spawnPosition.X, spawnPosition.Y, spawnPosition.Z);
                }

                _logger.LogInformation("Using zone {ZoneName} (ID: {ZoneId}) for house def {HouseDefId}",
                    zoneName, houseDef.ZoneId, houseDefId);
            }
            else
            {
                // Add safety height to Houses.json position
                spawnPosition = new System.Numerics.Vector4(
                    houseDef.SpawnPosition.X,
                    houseDef.SpawnPosition.Y + 2f,
                    houseDef.SpawnPosition.Z,
                    houseDef.SpawnPosition.W
                );

                _logger.LogWarning("Zone {ZoneId} not found for house def {HouseDefId}, using default zone",
                    houseDef.ZoneId, houseDefId);
            }

            // Zone the player to the house
            var packetClientBeginZoning = new PacketClientBeginZoning
            {
                Name = zoneName,
                Type = 2,
                Position = spawnPosition,
                Rotation = spawnRotation,
                Sky = sky,
                Unknown = 1,
                Id = (int)houseId, // Use house ID as zone ID
                GeometryId = geometryId,
                OverrideUpdateRadius = true
            };

            conn.SendTunneled(packetClientBeginZoning);

            SendSystem(conn, $"Entering house #{houseId} (Type: {houseDef.NameId})...");
            _logger.LogInformation("Player {Player} entering house {HouseId} (Def: {DefId}, Zone: {ZoneName})",
                conn.Player.Name.FullName, houseId, houseDefId, zoneName);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enter house {HouseId} for character {CharId}", houseId, characterId);
            SendSystem(conn, "Error entering house.");
            return true;
        }
    }


    private static bool HandleSpawnHouse(GatewayConnection conn, string[] parts)
    {
        if (!RequireAdmin(conn))
            return true;

        if (parts.Length < 2)
        {
            SendSystem(conn, "Usage: /spawnhouse <ModelId>");
            SendSystem(conn, "Try different model IDs to find house models (e.g., 5000-6000)");
            return true;
        }

        if (!int.TryParse(parts[1], out var modelId))
        {
            SendSystem(conn, "Model ID must be a number.");
            return true;
        }

        var zone = conn.Player.Zone;
        if (zone == null)
        {
            SendSystem(conn, "You are not in a zone.");
            return true;
        }

        if (!zone.TryCreateNpc(out var houseNpc) || houseNpc is null)
        {
            SendSystem(conn, "Failed to create house NPC.");
            return true;
        }

        houseNpc.NameId = 0;
        houseNpc.ModelId = modelId;
        houseNpc.Name = $"House Model {modelId}";
        houseNpc.Scale = 1f;
        houseNpc.Visible = true;
        houseNpc.HideNamePlate = false; // Show nameplate so you can see the model ID

        // Spawn at player's position
        houseNpc.UpdatePosition(conn.Player.Position, conn.Player.Rotation);

        var tile = zone.GetTileFromPosition(conn.Player.Position);
        tile.Entities.TryAdd(houseNpc.Guid, houseNpc);

        conn.Player.OnAddVisibleNpcs([houseNpc]);

        SendSystem(conn, $"Spawned house model {modelId} at your position (GUID: {houseNpc.Guid})");
        return true;
    }

    private static void SpawnHouseStructure(GatewayConnection conn, int houseDefId)
    {
        try
        {
            var zone = conn.Player.Zone;
            if (zone == null)
            {
                _logger.LogWarning("Cannot spawn house structure - player not in zone");
                return;
            }

            if (!zone.TryCreateNpc(out var houseNpc) || houseNpc is null)
            {
                _logger.LogError("Failed to create house NPC");
                return;
            }

            // Get the house definition to find its NameId
            if (!_resourceManager.Houses.TryGetValue(houseDefId, out var houseDef))
            {
                _logger.LogError("House definition {HouseDefId} not found", houseDefId);
                return;
            }

            // Find the store bundle with matching NameId to get the GameItemId
            int gameItemId = 0;
            foreach (var store in _resourceManager.Stores.Values)
            {
                foreach (var bundle in store.Bundles.Values)
                {
                    if (bundle.NameId == houseDef.NameId && bundle.Entries.Count > 0)
                    {
                        gameItemId = bundle.Entries[0].GameItemId;
                        _logger.LogInformation("Found GameItemId {GameItemId} for house NameId {NameId} (Def {HouseDefId})",
                            gameItemId, houseDef.NameId, houseDefId);
                        break;
                    }
                }
                if (gameItemId > 0) break;
            }

            // House definition ID to model ID mapping
            var houseModelMapping = new Dictionary<int, int>
            {
                { 1, 5001 },  // Small Seaside Beach House
                { 2, 5002 },  // Medium Seaside Beach House
                { 3, 5003 },  // Large Seaside Cliffs House
                { 4, 5004 },  // Large Seaside Cliffs House (variant)
                { 5, 5005 },  // Small Seaside Beach House (variant)
                { 6, 5006 },  // Large Seaside House
                { 7, 5007 },  // Large Wilds House
                { 8, 5008 },  // Small Seaside Beach House
                { 9, 5009 },  // Medium Seaside Beach House
                { 10, 5010 }, // Large Seaside Beach House
                // Add more mappings as you discover the correct model IDs
            };

            int houseModelId = 0;

            // Try to use the mapping first
            if (houseModelMapping.TryGetValue(houseDefId, out var mappedModelId))
            {
                houseModelId = mappedModelId;
                _logger.LogInformation("Using mapped model {ModelId} for house def {HouseDefId}",
                    houseModelId, houseDefId);
            }
            // Try to get the model ID from the item definition
            else if (gameItemId > 0 && _resourceManager.ClientItemDefinitions.TryGetValue(gameItemId, out var itemDef))
            {
                houseModelId = itemDef.Param1; // Param1 contains the ModelId
                _logger.LogInformation("Found house model {ModelId} for house def {HouseDefId} from item {GameItemId}",
                    houseModelId, houseDefId, gameItemId);
            }

            // Fallback to placeholder model if item not found
            if (houseModelId == 0)
            {
                houseModelId = 5000 + houseDefId; // Simple fallback
                _logger.LogWarning("Could not find model for house def {HouseDefId} (NameId: {NameId}), using placeholder {ModelId}",
                    houseDefId, houseDef.NameId, houseModelId);
            }

            houseNpc.NameId = 0;
            houseNpc.ModelId = houseModelId;
            houseNpc.Name = "House";
            houseNpc.Scale = 1f;
            houseNpc.Visible = true;
            houseNpc.HideNamePlate = true;

            // Position the house using the spawn position from the house definition
            var housePosition = houseDef.SpawnPosition;
            var houseRotation = new System.Numerics.Quaternion(
                houseDef.SpawnRotation.X,
                houseDef.SpawnRotation.Y,
                houseDef.SpawnRotation.Z,
                houseDef.SpawnRotation.W
            );

            houseNpc.UpdatePosition(housePosition, houseRotation);

            var tile = zone.GetTileFromPosition(housePosition);
            tile.Entities.TryAdd(houseNpc.Guid, houseNpc);

            // Send to player
            conn.Player.OnAddVisibleNpcs([houseNpc]);

            _logger.LogInformation("Spawned house structure with model {ModelId} at position {Pos}", houseModelId, housePosition);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to spawn house structure");
        }
    }

    // ================== TEST EFFECT COMMAND ==================

    // /testeffect <effectId> [modelId] [animId] [standAnimId] - Spawns a boombox with the given effect, model and animation
    private static bool HandleTestEffect(GatewayConnection conn, string[] parts)
    {
        if (!RequireAdmin(conn))
            return true;

        if (parts.Length < 2)
        {
            SendSystem(conn, "Usage: /testeffect <effectId> [modelId] [animId] [standAnimId]");
            SendSystem(conn, "Example: /testeffect 16448 2201 1 2901");
            SendSystem(conn, "Boombox effects: 16448-16453 (red/blue/green/orange/purple/yellow)");
            SendSystem(conn, "Models: 1062=basic, 2201=tiki, 3893=robo, 4095=ballet");
            SendSystem(conn, "StandAnimId: 2901-2910 (env_loop_01-10)");
            return true;
        }

        if (!int.TryParse(parts[1], out var effectId))
        {
            SendSystem(conn, "Effect ID must be a number.");
            return true;
        }

        int modelId = 2201; // Default to Tiki boombox
        if (parts.Length >= 3 && int.TryParse(parts[2], out var model))
        {
            modelId = model;
        }

        int animId = 1;
        if (parts.Length >= 4 && int.TryParse(parts[3], out var anim))
        {
            animId = anim;
        }

        int standAnimId = 0;
        if (parts.Length >= 5 && int.TryParse(parts[4], out var standAnim))
        {
            standAnimId = standAnim;
        }

        var zone = conn.Player.Zone;
        if (zone == null)
        {
            SendSystem(conn, "You are not in a zone.");
            return true;
        }

        if (!zone.TryCreateNpc(out var npc) || npc is null)
        {
            SendSystem(conn, "Failed to create test NPC.");
            return true;
        }

        npc.NameId = 0;
        npc.ModelId = modelId;
        npc.Name = $"E{effectId} M{modelId}";
        npc.Scale = 1f;
        npc.Visible = true;
        npc.HideNamePlate = false;
        npc.CompositeEffectId = effectId;
        npc.Animation = animId;
        npc.StandAnimId = standAnimId;

        npc.UpdatePosition(conn.Player.Position, conn.Player.Rotation);

        var tile = zone.GetTileFromPosition(conn.Player.Position);
        tile.Entities.TryAdd(npc.Guid, npc);

        // Send to player
        conn.Player.OnAddVisibleNpcs([npc]);

        // Also send a PlayCompositeEffect packet to trigger the effect immediately
        var effectPacket = new PlayerUpdatePacketPlayCompositeEffect
        {
            Guid = npc.Guid,
            CompositeEffectId = effectId,
            Position = npc.Position,
            EffectDelay = 0
        };
        conn.Player.SendTunneled(effectPacket);

        SendSystem(conn, $"Spawned: effect={effectId}, model={modelId}, anim={animId}, standAnim={standAnimId}");
        return true;
    }

    // /playeffect <effectId> - Plays a composite effect directly on your character
    private static bool HandlePlayEffect(GatewayConnection conn, string[] parts)
    {
        if (!RequireAdmin(conn))
            return true;

        if (parts.Length < 2 || !int.TryParse(parts[1], out var effectId))
        {
            SendSystem(conn, "Usage: /playeffect <effectId>");
            SendSystem(conn, "Plays the composite effect on your character. Use to find the right ID for food effects.");
            return true;
        }

        var packet = new PlayerUpdatePacketPlayCompositeEffect
        {
            Guid = conn.Player.Guid,
            CompositeEffectId = effectId,
            Position = conn.Player.Position,
        };

        conn.Player.SendTunneledToVisible(packet, true);
        SendSystem(conn, $"Playing effect {effectId} on your character.");
        return true;
    }

    // ================== /GIVEITEM ==================

    private static bool HandleGiveItem(GatewayConnection conn, string[] parts)
    {
        if (!RequireAdmin(conn))
            return true;

        if (parts.Length < 2 || !int.TryParse(parts[1], out var itemId))
        {
            SendSystem(conn, "Usage: /giveitem <itemId> [count]");
            return true;
        }

        int count = 1;
        if (parts.Length >= 3 && (!int.TryParse(parts[2], out count) || count < 1))
        {
            SendSystem(conn, "Count must be a positive number.");
            return true;
        }

        if (!_resourceManager.ClientItemDefinitions.TryGetValue(itemId, out var def))
        {
            SendSystem(conn, $"Item {itemId} not found.");
            return true;
        }

        using var defWriter = new PacketWriter();
        defWriter.Write(new[] { def });
        conn.SendTunneled(new PlayerUpdatePacketItemDefinitions { Payload = defWriter.Buffer });

        // Stack onto existing item if the player already has one with matching tint
        var existing = conn.Player.Items.FirstOrDefault(x => x.Definition == def.Id && x.Tint == 0);
        if (existing is not null)
        {
            existing.Count += count;
            conn.SendTunneled(new ClientUpdatePacketItemUpdate { ItemGuid = existing.Id, Count = existing.Count });

            // Persist updated count
            try
            {
                using var db = new Microsoft.Data.Sqlite.SqliteConnection(_dbConnectionString);
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "UPDATE Items SET Count = $count WHERE Id = $id;";
                cmd.Parameters.AddWithValue("$count", existing.Count);
                cmd.Parameters.AddWithValue("$id", existing.Id);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update item count for item {Id}", existing.Id);
            }

            SendSystem(conn, $"Added {count}x item {itemId} (now have {existing.Count}).");
            return true;
        }

        var newItem = new ClientItem { Definition = def.Id, Count = count, Tint = 0 };

        if (!conn.SaveItemToDatabase(newItem))
        {
            SendSystem(conn, "Failed to save item to database.");
            return true;
        }

        conn.Player.Items.Add(newItem);

        using var itemWriter = new PacketWriter();
        newItem.Serialize(itemWriter);
        conn.SendTunneled(new ClientUpdatePacketItemAdd { Payload = itemWriter.Buffer });

        SendSystem(conn, $"Gave {count}x item {itemId} (NameId={def.NameId}).");
        return true;
    }

    // ================== /LUA (debug: run client-side script) ==================

    // /lua <script>  - sends an ExecuteScriptPacket so the client runs the given Lua.
    // Debug/testing tool for reverse-engineering the client script API.
    private static bool HandleLua(GatewayConnection conn, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return true;

        int sp = message.IndexOf(' ');
        if (sp < 0 || sp + 1 >= message.Length)
        {
            SendSystem(conn, "Usage: /lua <script>");
            return true;
        }

        string script = message.Substring(sp + 1).Trim();

        // There are TWO candidate "run this Lua" packets and it's not settled which one this client build
        // actually honours, so fire both:
        //   * ExecuteScriptPacket        (BaseUi op47/sub7)  string + List<int>
        //   * AbilityPacketExecuteClientLua (op36/sub17)     string + 3 floats  (the layout EDITz specified)
        // If a script has a visible effect, whichever landed is the working one.
        conn.SendTunneled(new ExecuteScriptPacket { Script = script });
        conn.SendTunneled(new AbilityPacketExecuteClientLua { Script = script });

        SendSystem(conn, $"[lua] sent (both packets): {script}");
        _logger.LogInformation("/lua from {Player}: {Script}", conn.Player.Name.FullName, script);
        return true;
    }

    // ================== HELPERS ==================

    private static void SendMessageToPlayer(Player player, string message)
    {
        var packet = new PacketChat
        {
            Channel = ChatChannel.System,
            FromGuid = 0,
            FromName = new NameData(),
            Message = message
        };
        player.SendTunneled(packet);
    }

    private static bool TryResolveUsernamePattern(string pattern, out string resolvedUsername, out string error)
    {
        resolvedUsername = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "Username cannot be empty.";
            return false;
        }

        pattern = pattern.Trim();

        try
        {
            using var db = new SqliteConnection(_dbConnectionString);
            db.Open();

            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT Username FROM Users;";
            using var reader = cmd.ExecuteReader();

            var allUsernames = new List<string>();
            while (reader.Read())
            {
                allUsernames.Add(reader.GetString(0));
            }

            if (allUsernames.Count == 0)
            {
                error = "No users exist in the database.";
                return false;
            }

            // PASS 1: exact match (case-insensitive)
            var exact = allUsernames
                .Where(u => string.Equals(u, pattern, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (exact.Count == 1)
            {
                resolvedUsername = exact[0];
                return true;
            }
            if (exact.Count > 1)
            {
                error = $"Pattern '{pattern}' matches multiple usernames exactly: {string.Join(", ", exact)}. Please be more specific.";
                return false;
            }

            // PASS 2: prefix match (case-insensitive)
            var prefix = allUsernames
                .Where(u => u.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (prefix.Count == 1)
            {
                resolvedUsername = prefix[0];
                return true;
            }
            if (prefix.Count > 1)
            {
                error = $"Pattern '{pattern}' is ambiguous (prefix of: {string.Join(", ", prefix)}). Please type more of the username.";
                return false;
            }

            // PASS 3: contains match (case-insensitive)
            var contains = allUsernames
                .Where(u => u.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            if (contains.Count == 1)
            {
                resolvedUsername = contains[0];
                return true;
            }
            if (contains.Count > 1)
            {
                error = $"Pattern '{pattern}' matches multiple usernames: {string.Join(", ", contains)}. Please be more specific.";
                return false;
            }

            error = $"No user found matching '{pattern}'.";
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve username pattern.");
            error = "Error while resolving username pattern.";
            return false;
        }
    }


    private static bool TryResolvePlayerNamePattern(string pattern, out string resolvedName, out string error)
    {
        resolvedName = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "Player name cannot be empty.";
            return false;
        }

        pattern = pattern.Trim();

        // Get all player full names from starting zone
        var allNames = _zoneManager.StartingZone.Players
            .Select(p => p.Name.FullName)
            .Distinct()
            .ToList();

        if (allNames.Count == 0)
        {
            error = "No players online.";
            return false;
        }

        // PASS 1: exact match (case-insensitive)
        var exact = allNames
            .Where(n => string.Equals(n, pattern, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (exact.Count == 1)
        {
            resolvedName = exact[0];
            return true;
        }
        if (exact.Count > 1)
        {
            error = $"Pattern '{pattern}' matches multiple players exactly: {string.Join(", ", exact)}. Please be more specific.";
            return false;
        }

        // PASS 2: prefix match (case-insensitive)
        var prefix = allNames
            .Where(n => n.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (prefix.Count == 1)
        {
            resolvedName = prefix[0];
            return true;
        }
        if (prefix.Count > 1)
        {
            error = $"Pattern '{pattern}' is ambiguous (prefix of: {string.Join(", ", prefix)}). Please type more of the name.";
            return false;
        }

        // PASS 3: contains match (case-insensitive)
        var contains = allNames
            .Where(n => n.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();

        if (contains.Count == 1)
        {
            resolvedName = contains[0];
            return true;
        }
        if (contains.Count > 1)
        {
            error = $"Pattern '{pattern}' matches multiple players: {string.Join(", ", contains)}. Please be more specific.";
            return false;
        }

        error = $"No player found matching '{pattern}'.";
        return false;
    }


    private static int ExecuteNonQuery(string sql, params (string name, object value)[] parameters)
    {
        try
        {
            using var db = new SqliteConnection(_dbConnectionString);
            db.Open();

            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;

            foreach (var (name, value) in parameters)
                cmd.Parameters.AddWithValue(name, value);

            return cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExecuteNonQuery failed.");
            return 0;
        }
    }

    private static bool UnknownSubCommand(GatewayConnection conn, string root, string sub)
    {
        SendSystem(conn, $"Unknown /{root} subcommand '{sub}'. Try /help.");
        return true;
    }

    private static void SendSystem(GatewayConnection conn, string text)
    {
        var packet = new PacketChat
        {
            Channel = ChatChannel.System,
            FromGuid = conn.Player.Guid,
            FromName = conn.Player.Name, // NameData, not string
            Message = text
        };

        conn.Player.SendTunneled(packet);
    }

    // ================== PET COMMANDS ==================

    private static bool HandlePetSpawn(GatewayConnection conn, string[] parts)
    {
        if (parts.Length < 2)
        {
            // List available pets
            if (conn.Player.Pets.Count == 0)
            {
                SendSystem(conn, "You don't own any pets. Usage: /petspawn [DbPetId]");
                return true;
            }

            SendSystem(conn, "Your pets: " + string.Join(", ", conn.Player.Pets.Select(p => $"DbId:{p.Id}")));
            return true;
        }

        if (!uint.TryParse(parts[1], out var dbPetId))
        {
            SendSystem(conn, "Invalid pet ID.");
            return true;
        }

        // Find the pet in the player's collection by database ID (not Definition ID)
        var petInfo = conn.Player.Pets.FirstOrDefault(x => x.Id == (int)dbPetId);
        if (petInfo is null)
        {
            SendSystem(conn, $"You don't own a pet with database ID {dbPetId}. Your pets: " + string.Join(", ", conn.Player.Pets.Select(p => $"DbId:{p.Id}")));
            return true;
        }

        // Check if a pet is already active
        if (conn.Player.Pet is not null)
        {
            SendSystem(conn, "You already have a pet active. Use /petdespawn first.");
            return true;
        }

        // Get pet definition using the Definition ID from the pet info
        if (!_resourceManager.Pets.TryGetValue(petInfo.Definition, out var petDefinition))
        {
            SendSystem(conn, $"Pet definition not found (Definition ID: {petInfo.Definition}).");
            return true;
        }

        // Create the pet in the zone
        if (!conn.Player.Zone.TryCreatePet(conn.Player, petDefinition, out var pet))
        {
            SendSystem(conn, "Failed to spawn pet in zone.");
            return true;
        }

        pet.Visible = true;

        pet.Name = string.Empty; // Pet name not sent in PacketPetInfo (uses NameId for localization)
        pet.NameId = petDefinition.NameId;
        pet.ModelId = petDefinition.ModelId;

        pet.TextureAlias = petDefinition.TextureAlias;
        pet.TintAlias = petDefinition.TintAlias;
        pet.TintId = petInfo.TintId;

        pet.Scale = petDefinition.Scale;
        pet.Disposition = 1;

        pet.HideNamePlate = false;

        pet.ImageSetId = petDefinition.ImageSetId;

        // Set MovementType=2 (Physics) - server controls position
        pet.MovementType = 2;

        // Set walking animation
        pet.Animation = 1;

        conn.Player.Pet = pet;

        pet.UpdatePosition(conn.Player.Position, conn.Player.Rotation);

        // First send PetSpawnResponsePacket to spawn the pet
        var petSpawnResponsePacket = new PetSpawnResponsePacket();
        petSpawnResponsePacket.OwnerGuid = conn.Player.Guid;
        petSpawnResponsePacket.PetGuid = pet.Guid;
        petSpawnResponsePacket.CompositeEffectId = 0;
        conn.Player.SendTunneledToVisible(petSpawnResponsePacket, true);

        // Then send PetActivePacket to activate following behavior
        var petActivePacket = new PetActivePacket();
        petActivePacket.OwnerGuid = conn.Player.Guid;
        petActivePacket.PetGuid = pet.Guid;
        petActivePacket.CompositeEffectId = 46; // PFX_Teleport_Flash
        conn.Player.SendTunneledToVisible(petActivePacket, true);

        SendSystem(conn, $"Pet spawned!");
        return true;
    }

    private static bool HandlePetDespawn(GatewayConnection conn, string[] parts)
    {
        if (conn.Player.Pet is null)
        {
            SendSystem(conn, "You don't have an active pet.");
            return true;
        }

        // Send despawn response to all visible players
        var petDismountResponsePacket = new PetDismountResponsePacket
        {
            OwnerGuid = conn.Player.Guid,
            CompositeEffectId = 0
        };

        conn.Player.SendTunneledToVisible(petDismountResponsePacket, true);

        conn.Player.Pet.Dispose();
        conn.Player.Pet = null;

        SendSystem(conn, "Pet despawned!");
        return true;
    }

    private static bool HandlePetList(GatewayConnection conn, string[] parts)
    {
        if (conn.Player.Pets.Count == 0)
        {
            SendSystem(conn, "You don't own any pets.");
            return true;
        }

        var petList = string.Join("\n", conn.Player.Pets.Select((p, i) =>
            $"Pet {i + 1}: DB ID={p.Id}, NameId={p.NameId}, ImageSetId={p.ImageSetId}, TintId={p.TintId}"));

        SendSystem(conn, "Your pets:\n" + petList);
        return true;
    }

    // ================== RESPAWN (revive after death) ==================

    private static bool HandleRespawn(GatewayConnection conn)
    {
        if (!conn.Player.IsDead)
        {
            SendSystem(conn, "You are not dead!");
            return true;
        }

        // Context-aware: overworld revives in place, dungeons revive at the dungeon spawn (see the zone
        // overrides of OnPlayerRespawn).
        conn.Player.Zone.OnPlayerRespawn(conn.Player);
        SendSystem(conn, "You have been revived!");
        return true;
    }

    // TEST: force a knockout so the death flow can be tested regardless of combat balance (world enemies
    // are currently weak, so you rarely actually reach 0 HP).
    // DEV PROBE for the floating "Dodge" hit-type text. Sends the dedicated combat sub-opcodes directly at the
    // player (decoupled from the 5% dodge roll) so we can see which one the client actually renders as text:
    //   !dodge        -> op32/6 AttackTargetDodged  (attacker = target = you)
    //   !dodge self   -> op32/6 with a DISTINCT attacker guid (guid+1) in case the client needs attacker != target
    //   !dodge miss   -> op32/5 AttackAttackerMissed (same 2-guid shape)
    private static bool HandleDodge(GatewayConnection conn, string[] parts)
    {
        var self = conn.Player.Guid;
        var arg = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";

        if (arg == "on" || arg == "off")
        {
            conn.Player.ForceDodgeDebug = arg == "on";
            SendSystem(conn, $"Force-dodge {(conn.Player.ForceDodgeDebug ? "ON — every enemy hit will dodge. Go fight a mob and watch for 'Dodge' text." : "OFF")}.");
            return true;
        }

        if (arg == "miss")
        {
            conn.Player.SendTunneledToVisible(new CombatPacketAttackAttackerMissed { AttackerGuid = self, TargetGuid = self }, sendToSelf: true);
            SendSystem(conn, "Sent op32/5 (Missed). Do you see 'Miss' text?");
            return true;
        }

        var attacker = arg == "self" ? self + 1 : self;
        conn.Player.SendTunneledToVisible(new CombatPacketAttackTargetDodged { AttackerGuid = attacker, TargetGuid = self }, sendToSelf: true);
        SendSystem(conn, $"Sent op32/6 (Dodged) attacker={attacker} target={self}. Do you see 'Dodge' text?");
        return true;
    }

    private static bool HandleDie(GatewayConnection conn)
    {
        if (conn.Player.IsDead)
        {
            SendSystem(conn, "You are already knocked out. Use /respawn.");
            return true;
        }

        conn.Player.Knockout();
        SendSystem(conn, "You collapsed. (Knockout triggered — /respawn to get back up.)");
        return true;
    }

    // ================== HP (check/set hitpoints) ==================

    private static bool HandleHp(GatewayConnection conn, string[] parts)
    {
        if (parts.Length < 2)
        {
            var maxHp = conn.Player.Stats[CharacterStatId.MaxHealth].Int;
            SendSystem(conn, $"HP: {conn.Player.CurrentHitpoints}/{maxHp} | Mana: {conn.Player.CurrentMana}/{conn.Player.Stats[CharacterStatId.MaxMana].Int} | In Combat: {conn.Player.InCombat}");
            return true;
        }

        // /hp set <value> — for testing
        if (parts[1].ToLower() == "set" && parts.Length >= 3 && int.TryParse(parts[2], out var newHp))
        {
            var maxHp = conn.Player.Stats[CharacterStatId.MaxHealth].Int;
            conn.Player.CurrentHitpoints = Math.Clamp(newHp, 0, maxHp);

            conn.Player.SendTunneled(new ClientUpdatePacketHitpoints
            {
                CurrentHitpoints = conn.Player.CurrentHitpoints,
                MaxHitpoints = maxHp
            });

            SendSystem(conn, $"HP set to {conn.Player.CurrentHitpoints}/{maxHp}");
            return true;
        }

        // /hp full — heal to full
        if (parts[1].ToLower() == "full")
        {
            var maxHp = conn.Player.Stats[CharacterStatId.MaxHealth].Int;
            var maxMana = conn.Player.Stats[CharacterStatId.MaxMana].Int;
            conn.Player.CurrentHitpoints = maxHp;
            conn.Player.CurrentMana = maxMana;

            conn.Player.SendTunneled(new ClientUpdatePacketHitpoints
            {
                CurrentHitpoints = maxHp,
                MaxHitpoints = maxHp
            });

            conn.Player.SendTunneled(new ClientUpdatePacketMana
            {
                CurrentMana = maxMana,
                MaxMana = maxMana,
                ShowOverHead = false
            });

            SendSystem(conn, $"Healed to full! HP: {maxHp}/{maxHp}, Mana: {maxMana}/{maxMana}");
            return true;
        }

        SendSystem(conn, "Usage: /hp | /hp set <value> | /hp full");
        return true;
    }

    // ================== XP (check / grant job XP) ==================

    private static bool HandleXp(GatewayConnection conn, string[] parts)
    {
        var profile = conn.Player.ActiveProfile;

        if (parts.Length < 2)
        {
            SendSystem(conn, $"Job {profile.NameId}: level {profile.Rank}/{Sanctuary.Game.Leveling.JobLeveling.MaxLevel}, " +
                $"{profile.LevelXpRaw}/{Sanctuary.Game.Leveling.JobLeveling.XpForLevel(profile.Rank)} XP ({profile.RankPercent}%). Usage: /xp <amount>");
            return true;
        }

        if (!int.TryParse(parts[1], out var amount) || amount <= 0)
        {
            SendSystem(conn, "Usage: /xp <amount>");
            return true;
        }

        int before = profile.Rank;
        conn.Player.AwardXp(amount);

        if (profile.Rank > before)
            SendSystem(conn, $"Gained {amount} XP - leveled up to {profile.Rank}! (HP {conn.Player.CurrentHitpoints}/{conn.Player.Stats[CharacterStatId.MaxHealth].Int})");
        else
            SendSystem(conn, $"Gained {amount} XP. Level {profile.Rank}, {profile.LevelXpRaw}/{Sanctuary.Game.Leveling.JobLeveling.XpForLevel(profile.Rank)} ({profile.RankPercent}%)");

        return true;
    }

    // ================== SPAWN ENEMY (combat NPC) ==================

    private static bool HandleSpawnEnemy(GatewayConnection conn, string[] parts)
    {
        if (!RequireAdmin(conn))
            return true;

        // /spawnenemy <ModelId> [Level] [Name]
        if (parts.Length < 2 || !int.TryParse(parts[1], out var modelId))
        {
            SendSystem(conn, "Usage: /spawnenemy <ModelId> [Level] [Name]");
            return true;
        }

        var level = parts.Length >= 3 && int.TryParse(parts[2], out var lvl) ? lvl : 1;
        var name = parts.Length >= 4 ? string.Join(" ", parts[3..]) : "Enemy";

        var zone = conn.Player.Zone;

        if (!zone.TryCreateCombatNpc(out var combatNpc))
        {
            SendSystem(conn, "Failed to create combat NPC.");
            return true;
        }

        combatNpc.ModelId = modelId;
        combatNpc.Name = name;
        combatNpc.Scale = 1.0f;
        combatNpc.Disposition = 0; // Hostile
        combatNpc.IsInteractable = true;
        combatNpc.InteractRange = 100;
        combatNpc.Speed = 6.0f;

        // Set combat stats based on level
        combatNpc.InitializeFromLevel(level);

        // Position slightly in front of the player
        var forward = new System.Numerics.Vector3(
            2.0f * (conn.Player.Rotation.X * conn.Player.Rotation.Z + conn.Player.Rotation.W * conn.Player.Rotation.Y),
            0f,
            1.0f - 2.0f * (conn.Player.Rotation.X * conn.Player.Rotation.X + conn.Player.Rotation.Y * conn.Player.Rotation.Y)
        );

        var spawnPos = new System.Numerics.Vector4(
            conn.Player.Position.X + forward.X * 8f,
            conn.Player.Position.Y,
            conn.Player.Position.Z + forward.Z * 8f,
            1f
        );

        combatNpc.SpawnPosition = spawnPos;
        combatNpc.SpawnRotation = conn.Player.Rotation;
        combatNpc.UpdatePosition(spawnPos, conn.Player.Rotation);
        combatNpc.LastSentPosition = spawnPos;
        combatNpc.Visible = true;
        combatNpc.UpdateZoneTile();

        // Explicitly send the AddNpc packet to the spawning player
        // so they see it immediately (tile system also handles visibility
        // for other nearby players)
        var addPacket = combatNpc.GetAddNpcPacket();
        conn.Player.SendTunneled(addPacket);
        conn.Player.VisibleNpcs.TryAdd(combatNpc.Guid, combatNpc);

        SendSystem(conn, $"Spawned combat NPC '{name}' (Level {level}, HP: {combatNpc.MaxHitpoints}, DMG: {combatNpc.AttackDamage}, XP: {combatNpc.XpReward})");
        return true;
    }

    // ================== TEST TRANSFORM ==================

    // /spawntest <field> <value>
    // Fields: nameplate, imageset, profile, u67, u68, effect
    private static bool HandleSpawnTest(GatewayConnection conn, string[] parts)
    {
        if (parts.Length < 3)
        {
            SendSystem(conn, "Usage: /spawntest <field> <value>  — fields: nameplate, imageset, profile, u67, u68, effect, nameid, namescale, clone");
            return true;
        }
        int.TryParse(parts[2], out var value);
        float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fvalue);

        var field = parts[1].ToLowerInvariant();

        var zone = conn.Player.Zone;
        if (zone is null)
        {
            SendSystem(conn, "You are not in a zone.");
            return true;
        }

        if (!zone.TryCreateNpc(out var npc))
        {
            SendSystem(conn, "Failed to create NPC.");
            return true;
        }

        var vendorModel = zone.Npcs.FirstOrDefault(n => n.CursorId == 5)?.ModelId ?? 9240;
        var origin = conn.Player.Position;
        var rotation = conn.Player.Rotation;

        npc.ModelId = vendorModel;
        npc.NameId = 0;
        npc.Name = $"{field}={value}";
        npc.CursorId = 5;
        npc.Scale = 1f;
        npc.Disposition = 1;

        switch (field)
        {
            case "nameplate": npc.NameplateImageId = value; break;
            case "imageset":  npc.ImageSetId = value; break;
            case "profile":   npc.ActiveProfile = value; break;
            case "u67":       npc.NotificationImageSetId = value; break;
            case "u68":       npc.Unknown68 = value; break;
            case "effect":    npc.CompositeEffectId = value; break;
            case "notif":     npc.NotificationImageSetId = value; break;
            case "nameid":    npc.NameId = value; npc.Name = null; break;
            case "namescale":
                npc.NameplateImageId = 22663;
                npc.NameScale = fvalue;
                break;
            case "clone":
                // Spawn an exact copy of vendor GUID <value> to see if badge follows
                if (zone.TryGetNpc((ulong)value, out var src))
                {
                    npc.ModelId      = src.ModelId;
                    npc.NameId       = src.NameId;
                    npc.Name         = src.Name;
                    npc.SubTextNameId = src.SubTextNameId;
                    npc.ActiveProfile = src.ActiveProfile;
                    npc.ImageSetId   = src.ImageSetId;
                    npc.NameplateImageId = src.NameplateImageId;
                    npc.TextureAlias = src.TextureAlias;
                }
                else
                {
                    SendSystem(conn, $"NPC {value} not found.");
                    return true;
                }
                break;
            default:
                SendSystem(conn, $"Unknown field '{field}'. Use: nameplate, imageset, profile, u67, u68, effect, nameid, clone");
                return true;
        }

        npc.Visible = false;
        var pos = origin with { X = origin.X + 3f };
        npc.UpdatePosition(pos, rotation);

        npc.Visible = true;
        zone.GetTileFromPosition(pos).Entities.TryAdd(npc.Guid, npc);
        conn.Player.OnAddVisibleNpcs([npc]);

        SendSystem(conn, $"Spawned NPC with {field}={value}.");
        return true;
    }

    // /testsubtext [start] [end]  — spawns a row of NPCs with SubTextNameId from start to end (default 2910-2940)
    private static bool HandleTestSubText(GatewayConnection conn, string[] parts)
    {
        var zone = conn.Player.Zone;
        if (zone is null)
        {
            SendSystem(conn, "You are not in a zone.");
            return true;
        }

        int start = 2910;
        int end = 2940;
        if (parts.Length >= 2) int.TryParse(parts[1], out start);
        if (parts.Length >= 3) int.TryParse(parts[2], out end);
        if (end < start) end = start + 30;

        var origin = conn.Player.Position;
        var rotation = conn.Player.Rotation;
        var vendorModel = zone.Npcs.FirstOrDefault(n => n.CursorId == 5)?.ModelId ?? 9240;
        const int cols = 6;

        int count = 0;
        int i = 0;
        for (int subTextId = start; subTextId <= end; subTextId++, i++)
        {
            if (!zone.TryCreateNpc(out var npc))
                continue;

            int col = i % cols;
            int row = i / cols;
            var pos = origin with
            {
                X = origin.X + col * 4f,
                Z = origin.Z + 5f + row * 5f
            };

            npc.ModelId = vendorModel;
            npc.NameId = 0;
            npc.Name = $"ST={subTextId}";
            npc.SubTextNameId = subTextId;
            npc.NotificationImageSetId = 294;
            npc.ActiveProfile = 200;
            npc.CursorId = 5;
            npc.Scale = 1f;
            npc.Disposition = 1;

            npc.UpdatePosition(pos, rotation);
            npc.Visible = true;
            zone.GetTileFromPosition(pos).Entities.TryAdd(npc.Guid, npc);
            conn.Player.OnAddVisibleNpcs([npc]);
            count++;
        }

        SendSystem(conn, $"Spawned {count} NPCs with SubTextNameId {start}-{end}.");
        return true;
    }

    // /testtransform <modelId>  — triggers the NPC overlay transform for all nearby players to see.
    // /testtransform 0          — removes the active transform.
    private static bool HandleTestIcons(GatewayConnection conn)
    {
        var zone = conn.Player.Zone;
        if (zone is null)
        {
            SendSystem(conn, "You are not in a zone.");
            return true;
        }

        int[] values = [281, 282, 283, 284, 285, 286, 287, 288, 289, 290, 291, 292, 293, 294, 295, 296, 297, 298, 299, 300, 301, 302, 303, 304, 305, 306, 307, 308, 309, 310, 311, 312, 313, 314, 315, 316, 317, 318, 319, 320];
        const int cols = 6;

        var origin = conn.Player.Position;
        var rotation = conn.Player.Rotation;
        var vendorModel = zone.Npcs.FirstOrDefault(n => n.CursorId == 5)?.ModelId ?? 9240;

        var created = new List<Sanctuary.Game.Entities.Npc>();

        for (int i = 0; i < values.Length; i++)
        {
            if (!zone.TryCreateNpc(out var npc))
                continue;

            npc.ModelId = vendorModel;
            npc.NameId = 0;
            npc.Name = $"NP {values[i]}";
            npc.NameplateImageId = values[i];
            npc.ImageSetId = 381;
            npc.CursorId = 5;
            npc.Scale = 1f;
            npc.Disposition = 1;

            int col = i % cols;
            int row = i / cols;
            var pos = origin with
            {
                X = origin.X + col * 4f,
                Z = origin.Z + 5f + row * 5f
            };
            npc.UpdatePosition(pos, rotation);
            npc.Visible = true;

            zone.GetTileFromPosition(pos).Entities.TryAdd(npc.Guid, npc);
            created.Add(npc);
        }

        conn.Player.OnAddVisibleNpcs(created);
        SendSystem(conn, $"Spawned {created.Count} test icon NPCs at your position.");
        return true;
    }

    private static bool HandleFly(GatewayConnection conn)
    {
        var guid = conn.Player.Guid;
        bool enabling = _flyingPlayers.Add(guid); // returns false if already present → toggle off
        if (!enabling)
            _flyingPlayers.Remove(guid);

        var packet = new ClientUpdatePacketUpdateStat { Guid = guid };

        if (enabling)
        {
            packet.Stats.AddRange([
                new CharacterStat(CharacterStatId.GlideEnabled, 1),
                new CharacterStat(CharacterStatId.GlideDefaultForwardSpeed, 50f),
                new CharacterStat(CharacterStatId.GlideMinForwardSpeed, 0f),
                new CharacterStat(CharacterStatId.GlideMaxForwardSpeed, 100f),
                new CharacterStat(CharacterStatId.GlideAccel, 50f),
                new CharacterStat(CharacterStatId.GlideFallSpeed, 0f),
                new CharacterStat(CharacterStatId.GlideFallTime, 999999f),
                new CharacterStat(CharacterStatId.MaxMovementSpeed, 50f),
            ]);
            SendSystem(conn, "Fly mode ON — jump to activate glide.");
        }
        else
        {
            packet.Stats.AddRange([
                new CharacterStat(CharacterStatId.GlideEnabled, 0),
                new CharacterStat(CharacterStatId.GlideDefaultForwardSpeed, 0f),
                new CharacterStat(CharacterStatId.GlideMinForwardSpeed, 0f),
                new CharacterStat(CharacterStatId.GlideMaxForwardSpeed, 0f),
                new CharacterStat(CharacterStatId.GlideAccel, 0f),
                new CharacterStat(CharacterStatId.GlideFallSpeed, 0f),
                new CharacterStat(CharacterStatId.GlideFallTime, 0f),
                new CharacterStat(CharacterStatId.MaxMovementSpeed, 8f),
            ]);
            SendSystem(conn, "Fly mode OFF.");
        }

        conn.Player.SendTunneled(packet);
        return true;
    }

    private static bool HandleTestTransform(GatewayConnection conn, string[] parts)
    {
        if (!RequireAdmin(conn))
            return true;

        if (parts.Length < 2 || !int.TryParse(parts[1], out var modelId))
        {
            SendSystem(conn, "Usage: /testtransform <modelId>  (use 0 to revert)");
            SendSystem(conn, "Examples: /testtransform 50 (cat)  /testtransform 176 (wolf)  /testtransform 0 (revert)");
            return true;
        }

        if (modelId == 0)
        {
            AbilityPacketClientRequestStartAbilityHandler.RemoveTransform(conn);
            SendSystem(conn, "Transform removed.");
        }
        else
        {
            AbilityPacketClientRequestStartAbilityHandler.ApplyTransform(conn, modelId, 60_000);
            SendSystem(conn, $"Applied NPC overlay transform modelId={modelId} for 60s — check the 2nd screen.");
        }

        return true;
    }
}
