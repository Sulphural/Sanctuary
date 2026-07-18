using System;
using System.IO;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Sanctuary.Game;
using Sanctuary.Packet;
using Sanctuary.UdpLibrary;
using Sanctuary.UdpLibrary.Configuration;
using Sanctuary.UdpLibrary.Enumerations;

namespace Sanctuary.Gateway;

public class GatewayServer : UdpManager<GatewayConnection>
{
    private readonly ILogger _logger;
    private readonly IResourceManager _resourceManager;

    public GatewayServer(ILogger<GatewayServer> logger, IResourceManager resourceManager, UdpParams udpParams, IServiceProvider serviceProvider) : base(udpParams, serviceProvider)
    {
        _logger = logger;
        _resourceManager = resourceManager;
    }

    public override bool OnConnectRequest(UdpConnection udpConnection)
    {
        _logger.LogInformation("{connection} connected.", udpConnection);

        return true;
    }

    public void OnStarted()
    {
        _resourceManager.Zones.CollectionChanged += Zones_CollectionChanged;
        StartDevCommandRunner();
    }

    // Dev command runner: run slash/bang commands by dropping them into a text file, bypassing the FR client's
    // exe-level chat throttle (which rate-limits typed commands to ~1 every few seconds and can't be reached
    // from the server). Each NEW line in dev_commands.txt runs once, as if the player typed it. Line forms:
    //   !dungeon 37            -> runs for the first connected player
    //   Reagan: !ginvite ...   -> runs for the named player (first name or full name)
    // Lines starting with # are ignored. Clear the file to reset. The "!"/"/" prefix is optional.
    private void StartDevCommandRunner()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "dev_commands.txt");
        _logger.LogInformation("Dev command runner watching {path} — drop commands here to bypass the client chat throttle.", path);

        _ = Task.Run(async () =>
        {
            var processed = 0;
            while (true)
            {
                try
                {
                    await Task.Delay(500);
                    if (!File.Exists(path))
                        continue;

                    string[] lines;
                    try { lines = await File.ReadAllLinesAsync(path); }
                    catch (IOException) { continue; } // being written to — try again next tick

                    if (lines.Length < processed) // file was cleared/shrunk — restart from the top
                        processed = 0;
                    if (lines.Length <= processed)
                        continue;

                    for (var i = processed; i < lines.Length; i++)
                        RunDevCommandLine(lines[i]);
                    processed = lines.Length;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Dev command runner tick failed.");
                }
            }
        });
    }

    private void RunDevCommandLine(string line)
    {
        var raw = line.Trim();
        if (raw.Length == 0 || raw.StartsWith("#"))
            return;

        // Optional "Name: command" targets a specific player; otherwise the first connected player.
        string? targetName = null;
        var command = raw;
        var sep = raw.IndexOf(':');
        if (sep > 0 && sep < 24 && !raw[..sep].Contains(' '))
        {
            targetName = raw[..sep].Trim();
            command = raw[(sep + 1)..].Trim();
        }
        if (command.Length == 0)
            return;
        if (!command.StartsWith("!") && !command.StartsWith("/"))
            command = "!" + command;

        GatewayConnection? conn = null;
        foreach (var c in ConnectionList)
        {
            if (c.Player is null)
                continue;
            if (targetName is null) { conn = c; break; }
            var name = c.Player.Name;
            if (name is not null &&
                (string.Equals(name.FullName, targetName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(name.FirstName, targetName, StringComparison.OrdinalIgnoreCase)))
            { conn = c; break; }
        }

        if (conn is null)
        {
            _logger.LogWarning("Dev command '{command}': no {who} connected.", command, targetName ?? "player");
            return;
        }

        _logger.LogInformation("Dev command (file) for {name}: {command}", conn.Player.Name, command);
        try { Commands.CommandRouter.TryHandle(conn, command); }
        catch (Exception ex) { _logger.LogError(ex, "Dev command '{command}' threw.", command); }
    }

    private void Zones_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
    }

    public void OnStopping()
    {
        var packetNotice = new PacketWorldShutdownNotice();

        // Scheduled maintenance and updates.
        packetNotice.ReasonId = 418992;

        var packetTunneled = new PacketTunneledClientPacket();

        packetTunneled.Payload = packetNotice.Serialize();

        var packetData = packetTunneled.Serialize();

        foreach (var connection in ConnectionList)
        {
            connection.Send(UdpChannel.Reliable1, packetData);
        }
    }
}