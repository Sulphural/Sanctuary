using System.Numerics;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// Server -> client: play a SOUND EMITTER by id, optionally anchored on a target.
//
// ★ Why this packet matters: it is the only server-side route to a sound that has no COMPOSITE effect
// wrapping it. PlayCompositeEffect takes composite ids (ActorCompositeEffectDefinitions.xml); this takes
// ids from the entirely separate sound-emitter table (ActorSoundEmitterDefinitions.xml). Plenty of real
// audio only exists in the latter - Bruce's own performance track MX_Bruce_ItsYourWorld (17715) among it -
// and is unreachable any other way.
//
// Opcode 26 sub 39 (BaseCommandPacket header = short OpCode + short SubOpCode).
//
// ── The trace ────────────────────────────────────────────────────────────────────────────────────────
//   FUN_00aa2560   opcode-26 dispatcher, case 39 ->
//   FUN_00aa0be0   sub-39 handler ->
//   FUN_00a98680   the CONSTRUCTOR, which names the class and lays it out:
//                    *this      = CommandPacketPlaySoundIdOnTarget::vftable
//                    this+0x10  = 0                      <- SoundId
//                    this+0x14  = Target::vftable        <- an inline polymorphic Target
//                    this+0x18  = 0                      <- Target's payload pointer
//   FUN_00a9d360   the deserializer: sub-opcode short, then int -> +0x10, then Target's own reader.
//
// ── The Target member (FUN_0101c850, = Target::vftable slot +0x04) ───────────────────────────────────
// It reads a 4-byte TYPE ID and, only if that id is non-zero, calls factory FUN_0101c300 to construct a
// subclass and lets it deserialize itself through its own vtable slot +0x38:
//
//   type 0 : nothing at all - the client skips the factory entirely. Provably safe.
//   type 1 : Vector4 + ulong guid        (alloc 0x30; readers FUN_0101c7c0)
//   type 2 : three Vector4s              (alloc 0x40; FUN_0101c790)
//   type 3 : Vector4 + ulong guid + int  (FUN_0101c810)
//   type 4 : Vector4 + ulong guid + ...  (FUN_0101c820)
//
// The Vector4 in each is read by FUN_008e2410, which pulls FOUR floats with NaN guards.
//
// ✔ LIVE-CONFIRMED 2026-08-15: type 1 with a real position + guid plays the requested emitter, no crash
// (tested with 17716 on the invoker via /playsound before any world npc was allowed to send it).
//
// ⚠ HOW THIS CRASHED THE CLIENT (2026-08-15), because it is an easy mistake to repeat: the first attempt
// wrote [int SoundId][ulong Guid], assuming "OnTarget" meant a bare guid. The guid's first four bytes were
// then parsed as the TYPE ID - a huge non-zero number - so the client called the factory with a garbage
// type and invoked a vtable slot on what came back. Anyone entering the zone crashed. A malformed body
// here is NOT harmlessly rejected; write TargetType 0 unless the payload is genuinely being supplied.
public class CommandPacketPlaySoundIdOnTarget : BaseCommandPacket, ISerializablePacket
{
    public new const short OpCode = 39;

    // Target type ids, as switched on by the client's factory (FUN_0101c300).
    public const int TargetNone = 0;
    public const int TargetPositionAndActor = 1;

    // Id from ActorSoundEmitterDefinitions.xml - NOT a composite effect id.
    public int SoundId;

    // Defaults to "no target": the one value the client is guaranteed not to build an object for.
    public int TargetType = TargetNone;

    // Only written for TargetPositionAndActor.
    public Vector4 TargetPosition;
    public ulong TargetGuid;

    public CommandPacketPlaySoundIdOnTarget() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        base.Write(writer); // short OpCode(26) + short SubOpCode(39)

        writer.Write(SoundId);    // +0x10
        writer.Write(TargetType); // the Target's type id

        if (TargetType == TargetPositionAndActor)
        {
            writer.Write(TargetPosition.X);
            writer.Write(TargetPosition.Y);
            writer.Write(TargetPosition.Z);
            writer.Write(TargetPosition.W);
            writer.Write(TargetGuid);
        }

        return writer.Buffer;
    }
}
