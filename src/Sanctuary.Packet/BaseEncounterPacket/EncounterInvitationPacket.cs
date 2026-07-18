using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// op41/sub102 EncounterInvitationPacket (S2C) — the retail packet that (per RE) raises a party member's
// "accept/reject" GAQ popup and puts the leader in the "Waiting for all group members to accept or reject
// invitation" state. Wire format reversed from the client reader FUN_00a9ceb0:
//   [op41][sub102][int EncounterId][int InstanceId]  (BaseEncounter header, FUN_008d6690)
//   [ulong Guid]  (obj +0x18/+0x1c, read as one 8-byte value)
//   [int A]       (obj +0x20)
//   [int B]       (obj +0x24)
// Guid/A/B semantics are being nailed down live (Frida on the client's op41 dispatcher FUN_00aa36c0), so
// they're plain fields the !ginvite test command can sweep. EncounterId/InstanceId ride in the base header
// (Unknown/Unknown2).
public class EncounterInvitationPacket : BaseEncounterPacket, ISerializablePacket
{
    public new const short OpCode = 102;

    public ulong Guid;
    public int A;
    public int B;

    public EncounterInvitationPacket() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer); // [op41][sub102][int Unknown=EncounterId][int Unknown2=InstanceId]

        writer.Write(Guid);
        writer.Write(A);
        writer.Write(B);

        return writer.Buffer;
    }
}
