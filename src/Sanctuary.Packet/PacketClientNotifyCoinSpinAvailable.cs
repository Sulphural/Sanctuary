using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// ★ THE DAILY WHEEL'S REAL TRIGGER (op171).
//
// The "Spin For The Win!" wheel is NOT opened through the minigame activity/JoinActivity pipeline like a
// normal Flash minigame - launching it that way runs the whole MiniGame state machine (GroupInfo ->
// JoinGame -> BeginLoad -> OnGameStarted -> ShowMiniGameHud, all confirmed in FreeRealms.log) and then
// loads no movie at all, because nothing in that path ever names the wheel widget.
//
// Reversed 2026-08-06 out of the client binary + ScriptsBase.bin: the wheel is the COIN/LOYALTY wheel and
// the client opens it itself. `HUD:ShowCoinWheel` (Lua, hud.lua) is called by the client's handler for
// this packet; it calls the native `MiniGameFlashC:StartWheel()`, which loads game_wheel.swf into
// wndMiniGameFlash. From there the widget talks over the minigame payload channel - see DailyWheelGame.
// (The Activity Portal's own "startWheelMinigame" button calls the same MiniGameFlashC:StartWheel.)
//
// If the wheel can't open right now (the HUD's main bar is hidden, or a job trial screen is up), the Lua
// sets HUD.needToShowCoinWheel and opens it the moment the HUD comes back.
//
// BODY = TWO ints, read straight off the client's own constructor. Traced 2026-08-06 by walking the RTTI
// for ".?AUPacketClientNotifyCoinSpinAvailable@@" to its Complete Object Locator, to its vtable, to the
// single .text reference to that vtable (the ctor @0x49c08c), which reads:
//
//     mov dword ptr [eax], 0x018011ec       ; base packet vtable
//     mov dword ptr [eax+4], 0xAB           ; <- opcode 171, confirms the id
//     mov dword ptr [eax+8], ecx            ; <- field 1, zero-initialised
//     mov dword ptr [eax+0Ch], ecx          ; <- field 2, zero-initialised
//
// so the struct carries two 4-byte members past the opcode. Their meaning isn't nailed down; the obvious
// reading is a spin COUNT plus a timer/id, and sending both as 0 ("no spins") does nothing at all - which
// matches the client staying silent when we first sent it empty.
public class PacketClientNotifyCoinSpinAvailable : ISerializablePacket
{
    public const short OpCode = 171;

    public int Unknown1 = 1;
    public int Unknown2;

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        writer.Write(OpCode);

        writer.Write(Unknown1);
        writer.Write(Unknown2);

        return writer.Buffer;
    }
}
