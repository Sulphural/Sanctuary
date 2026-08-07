using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// "Start this Flash minigame now": [string LuaClass][string Swf][bool]. Client-verified 2026-08-06 (ctor
// @0x00A9B850, reader @0x00A9D180 = string, string, byte; handler MiniGameManager::StartFlashGame
// @0x009BD650, which logs "MiniGame:StartFlashMiniGame" to FreeRealms.log and loads the movie into the
// named Lua window class).
//
// This is also what puts the DAILY WHEEL on screen - LuaClass "MiniGameFlash", Swf "game_wheel.gfx" - in
// reply to the client's own 26/11 CommandPacketInteractionStartWheel. See BaseCommandPacketHandler.
public class CommandPacketStartFlashGame : BaseCommandPacket, ISerializablePacket
{
    public new const short OpCode = 12;

    public string? LuaClass;
    public string? Swf;

    public bool Unknown;

    public CommandPacketStartFlashGame() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        base.Write(writer);

        writer.Write(LuaClass);
        writer.Write(Swf);
        writer.Write(Unknown);

        return writer.Buffer;
    }
}