using System;
using System.Linq;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.Helpers;
using Sanctuary.Database;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Login.Handlers;

[PacketHandler]
public static class CharacterDeleteRequestHandler
{
    private static ILogger _logger = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(CharacterDeleteRequestHandler));

        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
    }

    public static bool HandlePacket(LoginConnection connection, Span<byte> data)
    {
        if (!CharacterDeleteRequest.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(CharacterDeleteRequest));
            return false;
        }

        _logger.LogTrace("Received {name} packet. ( {packet} )", nameof(CharacterDeleteRequest), packet);

        var characterDeleteReply = new CharacterDeleteReply();

        if (connection.UserId == 0)
        {
            characterDeleteReply.Status = 2;

            connection.Send(characterDeleteReply);

            return true;
        }

        using var dbContext = _dbContextFactory.CreateDbContext();

        var characterId = GuidHelper.GetPlayerId(packet.EntityKey);
        var character = dbContext.Characters
            .SingleOrDefault(x => x.UserId == connection.UserId && x.Id == characterId);

        if (character is null)
        {
            characterDeleteReply.Status = 2;

            connection.Send(characterDeleteReply);

            return true;
        }

        try
        {
            // Disable foreign key constraints temporarily (must be done outside transaction)
            dbContext.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");

            using var transaction = dbContext.Database.BeginTransaction();

            try
            {
                // Delete related entities first
                dbContext.Database.ExecuteSqlRaw("DELETE FROM Items WHERE CharacterId = {0}", characterId);
                dbContext.Database.ExecuteSqlRaw("DELETE FROM Titles WHERE CharacterId = {0}", characterId);
                dbContext.Database.ExecuteSqlRaw("DELETE FROM Mounts WHERE CharacterId = {0}", characterId);
                dbContext.Database.ExecuteSqlRaw("DELETE FROM Friends WHERE CharacterId = {0}", characterId);
                dbContext.Database.ExecuteSqlRaw("DELETE FROM Ignores WHERE CharacterId = {0}", characterId);
                dbContext.Database.ExecuteSqlRaw("DELETE FROM Profiles WHERE CharacterId = {0}", characterId);

                // Now delete the character
                dbContext.Database.ExecuteSqlRaw("DELETE FROM Characters WHERE Id = {0}", characterId);

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
            finally
            {
                // Re-enable foreign key constraints
                dbContext.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete character {characterId}", characterId);
            characterDeleteReply.Status = 2;

            connection.Send(characterDeleteReply);

            return true;
        }

        characterDeleteReply.Status = 1;
        characterDeleteReply.EntityKey = packet.EntityKey;

        connection.Send(characterDeleteReply);

        return true;
    }
}