using System.Collections.Generic;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet.Common;

public class FixtureInstanceInfo : ISerializableType
{
    public CouplingDisplayData CouplingDisplay = new();

    public int Unknown3;

    public List<ulong> UnknownArray = new();

    public int Unknown4;
    public int Unknown5;

    public ulong FixtureGuid;
    public int ItemDefinitionId;

    public void Serialize(PacketWriter writer)
    {
        writer.Write(FixtureGuid);
        writer.Write(ItemDefinitionId);

        CouplingDisplay.Serialize(writer);
    }
}
