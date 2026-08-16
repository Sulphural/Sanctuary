using Sanctuary.Core.IO;

namespace Sanctuary.Packet.Common;

public class MatchmakingQueueDefinition : ISerializableType
{
    public int Id;

    public int NameId;

    public int MatchType;

    public int MinPlayers;
    public int MaxPlayers;

    public int MinTeams;
    public int MaxTeams;

    public int MaxGameStartDelay;

    public int Param1;
    public int Param2;
    public int Param3;
    public int Param4;
    public int Param5;
    public int Param6;
    public int Param7;

    // ★ LIVE-PROVEN 2026-08-15 (`/snowball queuecol 15/16`): these two really are the description and the
    // icon - writing them changed the minigame description and picture on the Matchmaking panel. They were
    // briefly renamed PlayersWaiting/AverageWaitSeconds on a bad reading of the client's Lua; they are not
    // that. Which fields carry "N Waiting" and "Avg Wait" is still open - see MatchmakingQueueTable.
    //
    // (Their duplication with Param5/Param6 on every row is therefore a real property of the record, or of
    // the capture it was reconstructed from, rather than the mistake it looked like.)
    public int EncounterDescriptionId;

    public int EncounterIcon;

    public int Unknown;
    public int Unknown2;

    public bool MemberOnly;

    public bool Unknown3;

    public void Serialize(PacketWriter writer)
    {
        writer.Write(Id);

        writer.Write(NameId);

        writer.Write(MatchType);

        writer.Write(MinPlayers);
        writer.Write(MaxPlayers);

        writer.Write(MinTeams);
        writer.Write(MaxTeams);

        writer.Write(MaxGameStartDelay);

        writer.Write(Param1);
        writer.Write(Param2);
        writer.Write(Param3);
        writer.Write(Param4);
        writer.Write(Param5);
        writer.Write(Param6);
        writer.Write(Param7);

        writer.Write(EncounterDescriptionId);

        writer.Write(EncounterIcon);

        writer.Write(Unknown);
        writer.Write(Unknown2);

        writer.Write(MemberOnly);

        writer.Write(Unknown3);
    }
}