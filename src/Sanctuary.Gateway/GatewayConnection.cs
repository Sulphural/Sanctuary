using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sanctuary.Core.Configuration;
using Sanctuary.Core.Cryptography;
using Sanctuary.Core.Helpers;
using Sanctuary.Core.IO;
using Sanctuary.Database;
using Sanctuary.Database.Entities;
using Sanctuary.Game;
using Sanctuary.Game.Entities;
using Sanctuary.Gateway.Handlers;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.UdpLibrary;
using Sanctuary.UdpLibrary.Enumerations;

namespace Sanctuary.Gateway;

public class GatewayConnection : UdpConnection
{
    private readonly ILogger _logger;
    private readonly LoginClient _loginClient;
    private readonly IZoneManager _zoneManager;
    private readonly GatewayServer _gatewayServer;
    private readonly GatewayServerOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly IResourceManager _resourceManager;
    private readonly IDbContextFactory<DatabaseContext> _dbContextFactory;

    private ICipher _cipher;
#pragma warning disable CS0649
    private bool _useEncryption; // Hardcoded in the client.
#pragma warning restore CS0649

    // Player will only be null during login.
    public Player Player { get; private set; } = null!;

    public string Locale { get; set; } = "en_US";

    public GatewayConnection(ILogger<GatewayConnection> logger, IOptions<GatewayServerOptions> options, IZoneManager zoneManager, LoginClient loginClient, GatewayServer gatewayServer, IResourceManager resourceManager, IServiceProvider serviceProvider, IDbContextFactory<DatabaseContext> dbContextFactory, SocketAddress socketAddress, int connectCode) : base(gatewayServer, socketAddress, connectCode)
    {
        _logger = logger;
        _options = options.Value;
        _loginClient = loginClient;
        _zoneManager = zoneManager;
        _gatewayServer = gatewayServer;
        _resourceManager = resourceManager;
        _serviceProvider = serviceProvider;
        _dbContextFactory = dbContextFactory;

        _cipher = new CipherCCM();
    }

    public void InitializeCipher(string key)
    {
        _cipher.Initialize(Encoding.ASCII.GetBytes(key));
    }

    public override void OnTerminated()
    {
        var reason = DisconnectReason == DisconnectReason.OtherSideTerminated
            ? OtherSideDisconnectReason
            : DisconnectReason;

        _logger.LogInformation("{connection} disconnected. {reason}", this, reason);

        // Just in case check if player is null.
        if (Player is null)
            return;

        SendFriendOffline();

        _loginClient.SendCharacterLogout(GuidHelper.GetPlayerId(Player.Guid));

        SavePlayerToDatabase();

        Player.Dispose();
    }

    public override void OnRoutePacket(Span<byte> data)
    {
        if ((!_useEncryption || !_cipher.Decrypt(data, out var finalLength))
            && (_useEncryption || !PacketUtils.UnwrapPacket(data, out finalLength, _cipher)))
        {
            _logger.LogError("{connection} failed to unwrap/decrypt packet. ( Data: {data} )", this, Convert.ToHexString(data));
            return;
        }

        OnHandlePacket(data.Slice(0, finalLength));
    }

    private void OnHandlePacket(Span<byte> data)
    {
        var reader = new PacketReader(data);

        if (!reader.TryRead(out short opCode))
            return;

        var handled = opCode switch
        {
            PacketLogin.OpCode => PacketLoginHandler.HandlePacket(this, data),
            PacketTunneledClientPacket.OpCode => PacketTunneledClientPacketHandler.HandlePacket(this, data),
            PacketTunneledClientWorldPacket.OpCode => PacketTunneledClientWorldPacketHandler.HandlePacket(this, data),
            _ => false
        };

#if DEBUG
        if (!handled)
        {
            _logger.LogWarning("{connection} received an unhandled packet. ( OpCode: {opcode}, Data: {data} )", this, opCode, Convert.ToHexString(data));
        }
#endif
    }

    public override void OnCrcReject(Span<byte> data)
    {
        _logger.LogError("[CrcReject] Guid: {guid}, Data: {data}", Player?.Guid, Convert.ToHexString(data));
    }

    public override void OnPacketCorrupt(Span<byte> data, UdpCorruptionReason reason)
    {
        _logger.LogError("[PacketCorrupt] Guid: {guid}, Reason: {reason}, Data: {data}", Player?.Guid, reason, Convert.ToHexString(data));
    }

    public void SendTunneled(ISerializablePacket packet, bool reliable = true, bool secure = false)
    {
        var packetTunneled = new PacketTunneledClientPacket
        {
            Payload = packet.Serialize()
        };

        Send(packetTunneled, reliable, secure);
    }

    public void Send(ISerializablePacket packet, bool reliable = true, bool secure = false)
    {
        var data = packet.Serialize();

        if (secure)
            InternalSendSecure(data);
        else
            InternalSend(data, reliable);
    }

    private void InternalSend(Span<byte> data, bool reliable)
    {
        if (_useEncryption)
        {
            InternalSendSecure(data);
            return;
        }

        Send(reliable ? UdpChannel.Reliable1 : UdpChannel.Unreliable, data);
    }

    private void InternalSendSecure(Span<byte> data)
    {
        if (_cipher is null || !_cipher.IsInitialized)
            return;

        using var writer = new PacketWriter();

        if (_useEncryption)
        {
            if (!_cipher.Encrypt(data, writer))
                return;
        }
        else
        {
            if (!PacketUtils.WrapPacket(data, writer, true, _cipher))
                return;
        }

        Send(UdpChannel.Reliable1, writer.Buffer);
    }

    public bool CreatePlayerFromDatabase(DbCharacter dbCharacter)
    {
        var startingZone = _zoneManager.StartingZone;

        if (!startingZone.TryCreatePlayer(GuidHelper.GetPlayerGuid(dbCharacter.Id), this, out var player))
        {
            _logger.LogError("Failed to create player entity.");
            return false;
        }

        Player = player;
        Player.CharacterId = dbCharacter.Id; // Store database character ID

        // Start - ClientPcData

        Player.Model = dbCharacter.Model;

        Player.Head = dbCharacter.Head;
        Player.HeadId = dbCharacter.HeadId;

        Player.Hair = dbCharacter.Hair;
        Player.HairId = dbCharacter.HairId;

        Player.HairColor = dbCharacter.HairColor;
        Player.EyeColor = dbCharacter.EyeColor;

        Player.SkinTone = dbCharacter.SkinTone;
        Player.SkinToneId = dbCharacter.SkinToneId;

        Player.FacePaint = dbCharacter.FacePaint;
        Player.FacePaintId = dbCharacter.FacePaintId ?? 0;

        Player.ModelCustomization = dbCharacter.ModelCustomization;
        Player.ModelCustomizationId = dbCharacter.ModelCustomizationId ?? 0;

        var position = dbCharacter.PositionX.HasValue && dbCharacter.PositionY.HasValue && dbCharacter.PositionZ.HasValue
            ? new Vector4(dbCharacter.PositionX.Value, dbCharacter.PositionY.Value, dbCharacter.PositionZ.Value, 1f)
            : startingZone.SpawnPosition;

        var rotation = dbCharacter.RotationX.HasValue && dbCharacter.RotationZ.HasValue
            ? new Quaternion(dbCharacter.RotationX.Value, 0f, dbCharacter.RotationZ.Value, 0f)
            : startingZone.SpawnRotation;

        Player.UpdatePosition(position, rotation);

        Player.Name.FirstName = dbCharacter.FirstName;
        Player.Name.LastName = dbCharacter.LastName ?? string.Empty;

        Player.Coins = dbCharacter.Coins;

        Player.Birthday = dbCharacter.Created;
        Player.PlayTime = dbCharacter.PlayTime;

        Player.MembershipStatus = dbCharacter.MembershipStatus;
        Player.ShowMemberNagScreen = _options.ShowMemberNagScreen;

        foreach (var dbProfile in dbCharacter.Profiles)
        {
            if (!_resourceManager.Profiles.TryGetValue(dbProfile.Id, out var profileData))
                continue;

            var clientPcProfile = new ClientPcProfile();

            clientPcProfile.Id = dbProfile.Id;

            clientPcProfile.NameId = profileData.NameId;
            clientPcProfile.DescriptionId = profileData.DescriptionId;

            clientPcProfile.Type = profileData.Type;
            clientPcProfile.Icon = profileData.Icon;

            clientPcProfile.AbilityBgImageSet = profileData.AbilityBgImageSet;
            clientPcProfile.BadgeImageSet = profileData.BadgeImageSet;
            clientPcProfile.ButtonImageSet = profileData.ButtonImageSet;

            clientPcProfile.MembersOnly = profileData.MembersOnly;

            clientPcProfile.ItemClasses = profileData.ItemClasses;

            clientPcProfile.Rank = dbProfile.Level;
            clientPcProfile.RankPercent = dbProfile.LevelXP;

            foreach (var dbItem in dbProfile.Items)
            {
                if (!_resourceManager.ClientItemDefinitions.TryGetValue(dbItem.Definition, out var clientItemDefinition))
                    continue;

                if (clientPcProfile.Items.TryGetValue(clientItemDefinition.Slot, out var profileItem))
                    profileItem.Id = dbItem.Id;
                else
                {
                    profileItem = new ProfileItem
                    {
                        Id = dbItem.Id,
                        Slot = clientItemDefinition.Slot
                    };

                    clientPcProfile.Items.Add(clientItemDefinition.Slot, profileItem);
                }
            }

            Player.Profiles.Add(clientPcProfile);

            if (!Player.ProfileTypes.Any(x => x.Type == profileData.Type))
            {
                Player.ProfileTypes.Add(new ProfileTypeEntry
                {
                    Type = profileData.Type,
                    ProfileId = profileData.Id
                });
            }
        }

        Player.ActiveProfileId = dbCharacter.ActiveProfileId;

        foreach (var dbItem in dbCharacter.Items)
        {
            Player.Items.Add(new ClientItem
            {
                Id = dbItem.Id,
                Tint = dbItem.Tint,
                Count = dbItem.Count,
                Definition = dbItem.Definition
            });
        }

        Player.Gender = dbCharacter.Gender;

        foreach (var dbMount in dbCharacter.Mounts)
        {
            if (!_resourceManager.Mounts.TryGetValue(dbMount.Definition, out var mountDefinition))
                continue;

            Player.Mounts.Add(new PacketMountInfo
            {
                Id = dbMount.Id,
                Definition = mountDefinition.Id,
                NameId = mountDefinition.NameId,
                ImageSetId = mountDefinition.ImageSetId,
                TintId = dbMount.Tint,
                TintAlias = mountDefinition.TintAlias,
                MembersOnly = mountDefinition.MembersOnly,
                IsUpgradable = mountDefinition.IsUpgradable,
                IsUpgraded = dbMount.IsUpgraded,
            });
        }

        _logger.LogInformation("Loading pets for character {characterId}. DbPets count: {dbPetsCount}", dbCharacter.Id, dbCharacter.Pets.Count);

        foreach (var dbPet in dbCharacter.Pets)
        {
            if (!_resourceManager.Pets.TryGetValue(dbPet.Definition, out var petDefinition))
            {
                _logger.LogWarning("Pet definition {definition} not found in Pets.json for DbPet {petId}", dbPet.Definition, dbPet.Id);
                continue;
            }

            var petInfo = new PacketPetInfo
            {
                Id = dbPet.Id,
                Definition = petDefinition.Id,
                NameId = petDefinition.NameId,
                ImageSetId = petDefinition.ImageSetId,
                TintId = dbPet.Tint,
                TintAlias = petDefinition.TintAlias ?? string.Empty,
                MembersOnly = petDefinition.MembersOnly,
                IsNameable = petDefinition.IsNameable, // Server-side only
                IsUpgradable = false, // Match mount structure - pets don't upgrade
                IsUpgraded = false, // Match mount structure
                Guid = 0 // Keep at 0 in ClientPcData (like mounts), calculate only when needed for world spawning
            };

            Player.Pets.Add(petInfo);

            _logger.LogInformation("Loaded pet: PetId={petId}, Definition={definition}, NameId={nameId}, ImageSetId={imageSetId}, Guid={guid}, TintId={tintId}, TintAlias={tintAlias}",
                petInfo.Id, petInfo.Definition, petInfo.NameId, petInfo.ImageSetId, petInfo.Guid, petInfo.TintId, petInfo.TintAlias);
        }

        _logger.LogInformation("Pets loaded and will be sent via PetListPacket. TotalPetsCount={count}", Player.Pets.Count);

        // Note: Pets are sent via PetListPacket (OpCode 5) in StartingZone.cs after zone initialization
        // PetListPacket is also sent after purchase or when client explicitly requests it

        // TODO

        // Start - Store on DB
        var clientActionBar = new ClientActionBar();

        clientActionBar.Id = 2; // ItemActionBar

        clientActionBar.Slots.Add(0, new ActionBarSlot() { IsEmpty = true });
        clientActionBar.Slots.Add(1, new ActionBarSlot() { IsEmpty = true });
        clientActionBar.Slots.Add(2, new ActionBarSlot() { IsEmpty = true });
        clientActionBar.Slots.Add(3, new ActionBarSlot() { IsEmpty = true });

        Player.ActionBars.Add(clientActionBar.Id, clientActionBar);
        // End - Store on DB

        foreach (var dbTitle in dbCharacter.Titles)
        {
            if (!_resourceManager.PlayerTitles.TryGetValue(dbTitle.Id, out var playerTitle))
                continue;

            Player.Titles.Add(playerTitle);
        }

        Player.ActiveTitle = dbCharacter.ActiveTitleId ?? 0;

        Player.VipRank = dbCharacter.VipRank;

        // End ClientPcData

        Player.ChatBubbleForegroundColor = dbCharacter.ChatBubbleForegroundColor;
        Player.ChatBubbleBackgroundColor = dbCharacter.ChatBubbleBackgroundColor;
        Player.ChatBubbleSize = dbCharacter.ChatBubbleSize;

        foreach (var dbFriend in dbCharacter.Friends)
        {
            var friendData = new FriendData
            {
                Name =
                {
                    FirstName = dbFriend.FriendCharacter.FirstName,
                    LastName = dbFriend.FriendCharacter.LastName ?? string.Empty
                },
                Guid = GuidHelper.GetPlayerGuid(dbFriend.FriendCharacterId),
                IsLocal = true,
                IsInStaticZone = true
            };

            if (_zoneManager.TryGetPlayer(GuidHelper.GetPlayerGuid(dbFriend.FriendCharacterId), out var friendPlayer))
            {
                friendData.Online = true;

                friendData.Status.ProfileId = friendPlayer.ActiveProfile.Id;
                friendData.Status.ProfileRank = friendPlayer.ActiveProfile.Rank;
                friendData.Status.ProfileIconId = friendPlayer.ActiveProfile.Icon;
                friendData.Status.ProfileNameId = friendPlayer.ActiveProfile.NameId;
                friendData.Status.ProfileBackgroundImageId = friendPlayer.ActiveProfile.BadgeImageSet;
            }

            Player.Friends.Add(friendData);
        }

        foreach (var dbIgnore in dbCharacter.Ignores)
        {
            var ignoreData = new IgnoreData
            {
                Guid = GuidHelper.GetPlayerGuid(dbIgnore.IgnoreCharacterId),
                Name = dbIgnore.IgnoreCharacter.FullName
            };

            Player.Ignores.Add(ignoreData);
        }

        Player.StationCash = dbCharacter.StationCash;

        return true;
    }

    private void SavePlayerToDatabase()
    {
        try
        {
            using var dbContext = _dbContextFactory.CreateDbContext();

            var dbCharacter = dbContext.Characters
                .Include(c => c.Profiles)
                .FirstOrDefault(x => x.Id == GuidHelper.GetPlayerId(Player.Guid));

            if (dbCharacter is null)
            {
                _logger.LogError("Failed to get character data from database. Character ID: {characterId}", GuidHelper.GetPlayerId(Player.Guid));
                return;
            }

            // Start - ClientPcData

            Vector4 position;
            Quaternion rotation;

            if (Player.Zone == _zoneManager.StartingZone)
            {
                position = Player.Position;
                rotation = Player.Rotation;
            }
            else
            {
                position = Player.StartingZonePosition;
                rotation = Player.StartingZoneRotation;
            }

            dbCharacter.PositionX = float.IsNaN(position.X) ? null : position.X;
            dbCharacter.PositionY = float.IsNaN(position.Y) ? null : position.Y;
            dbCharacter.PositionZ = float.IsNaN(position.Z) ? null : position.Z;

            dbCharacter.RotationX = float.IsNaN(rotation.X) ? null : rotation.X;
            dbCharacter.RotationZ = float.IsNaN(rotation.Z) ? null : rotation.Z;

            dbCharacter.ActiveProfileId = Player.ActiveProfileId;

            dbCharacter.ActiveTitleId = Player.ActiveTitle;

            // End ClientPcData

            dbCharacter.ChatBubbleForegroundColor = Player.ChatBubbleForegroundColor;
            dbCharacter.ChatBubbleBackgroundColor = Player.ChatBubbleBackgroundColor;
            dbCharacter.ChatBubbleSize = Player.ChatBubbleSize;

            // Save profile levels/XP
            foreach (var profile in Player.Profiles)
            {
                var dbProfile = dbCharacter.Profiles.FirstOrDefault(p => p.Id == profile.Id);
                if (dbProfile is not null)
                {
                    dbProfile.Level = profile.Rank;
                    dbProfile.LevelXP = profile.RankPercent;
                }
            }

            var changesSaved = dbContext.SaveChanges();

            if (changesSaved <= 0)
                _logger.LogWarning("No changes saved to database for character {characterId}", GuidHelper.GetPlayerId(Player.Guid));
            else
                _logger.LogDebug("Successfully saved {changeCount} changes for character {characterId}", changesSaved, GuidHelper.GetPlayerId(Player.Guid));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception occurred while saving character data to database. Character ID: {characterId}, Name: {characterName}",
                GuidHelper.GetPlayerId(Player.Guid),
                Player.Name.FirstName);
        }
<<<<<<< Updated upstream
        else
        {
            position = Player.StartingZonePosition;
            rotation = Player.StartingZoneRotation;
        }

        dbCharacter.PositionX = float.IsNaN(position.X) ? null : position.X;
        dbCharacter.PositionY = float.IsNaN(position.Y) ? null : position.Y;
        dbCharacter.PositionZ = float.IsNaN(position.Z) ? null : position.Z;

        dbCharacter.RotationX = float.IsNaN(rotation.X) ? null : rotation.X;
        dbCharacter.RotationZ = float.IsNaN(rotation.Z) ? null : rotation.Z;

        dbCharacter.ActiveProfileId = Player.ActiveProfileId;

        dbCharacter.ActiveTitleId = Player.ActiveTitle;

        if (dbCharacter.LastLogin.HasValue)
            dbCharacter.PlayTime += (int)(DateTimeOffset.UtcNow - dbCharacter.LastLogin.Value).TotalMinutes;

        // End ClientPcData

        dbCharacter.ChatBubbleForegroundColor = Player.ChatBubbleForegroundColor;
        dbCharacter.ChatBubbleBackgroundColor = Player.ChatBubbleBackgroundColor;
        dbCharacter.ChatBubbleSize = Player.ChatBubbleSize;

        if (dbContext.SaveChanges() <= 0)
            _logger.LogError("Failed to save character data to database");
=======
>>>>>>> Stashed changes
    }

    public void SendInitializationParameters()
    {
        var packetInitializationParameters = new PacketInitializationParameters();

        packetInitializationParameters.Environment = _options.Environment;

        SendTunneled(packetInitializationParameters);
    }

    public void SendZoneDetails()
    {
        var packetSendZoneDetails = new PacketSendZoneDetails
        {
            Name = Player.Zone.Name,
            Id = Player.Zone.Id
        };

        SendTunneled(packetSendZoneDetails);
    }

    public void ClientGameSettings()
    {
        var packetClientGameSettings = new PacketClientGameSettings
        {
            Unknown = 4,
            Unknown2 = 7,
            PowerHourEffectTag = 268,
            Unknown4 = true,
            GameTimeScalar = 1.0f
        };

        SendTunneled(packetClientGameSettings);
    }

    public void SendItemDefinitions()
    {
        var clientItemDefinitions = new List<ClientItemDefinition>();

        foreach (var item in Player.Items)
        {
            if (!_resourceManager.ClientItemDefinitions.TryGetValue(item.Definition, out var clientItemDefinition))
                continue;

            clientItemDefinitions.Add(clientItemDefinition);
        }

        using var writer = new PacketWriter();

        writer.Write(clientItemDefinitions);

        var playerUpdatePacketItemDefinitions = new PlayerUpdatePacketItemDefinitions();

        playerUpdatePacketItemDefinitions.Payload = writer.Buffer;

        SendTunneled(playerUpdatePacketItemDefinitions);
    }

    public void SendSelfToClient()
    {
        var packetSendSelfToClient = new PacketSendSelfToClient();

        packetSendSelfToClient.Payload = Player.Serialize();

        SendTunneled(packetSendSelfToClient);

        // TEST: Send PetListPacket immediately after ClientPcData, mimicking how mounts are sent
        // Try assigning unique Guids to pets (maybe Guid=0 prevents display?)
        ulong baseGuid = 999000000000; // Use a high base number for pet collection Guids
        int guidOffset = 0;
        foreach (var pet in Player.Pets)
        {
            // Generate a unique Guid for this pet for the collection UI
            pet.Guid = baseGuid + (ulong)guidOffset++;
        }
        var petListPacket = new PetListPacket { Pets = Player.Pets };
        Player.SendTunneled(petListPacket);

        // Send housing list immediately after pets
        SendHousingList();
    }

    private void SendHousingList()
    {
        using var dbContext = _dbContextFactory.CreateDbContext();

        var playerId = GuidHelper.GetPlayerId(Player.Guid);

        // Load all houses owned by this player
        var dbHouses = dbContext.Houses
            .Where(h => h.OwnerId == playerId)
            .ToList();

        var housingPacketInstanceList = new HousingPacketInstanceList
        {
            PlayerGuid = Player.Guid
        };

        foreach (var dbHouse in dbHouses)
        {
            // Get house definition to populate display info
            var houseDefinition = _resourceManager.Houses.TryGetValue(dbHouse.HouseDefinitionId, out var def) ? def : null;

            var instanceInfo = new PlayerHousingInstanceInfo
            {
                OwnerGuid = Player.Guid,
                InstanceGuid = dbHouse.Id,
                NameId = dbHouse.NameId,
                OwnerName = Player.Name.FirstName,
                HouseName = dbHouse.CustomName,
                IconId = dbHouse.IconId,
                FixtureCount = dbHouse.MaxFixtureCount, // Current fixture count
                FurnitureScore = 0, // TODO: Calculate furniture score
                LastVisited = dbHouse.LastVisited.DateTime,
                IsLocked = dbHouse.IsLocked,
                IsMembersOnly = dbHouse.IsMembersOnly,
                IsFloraAllowed = dbHouse.IsFloraAllowed,
                Description = dbHouse.Description,
                KeywordList = dbHouse.KeywordList,
                Rating = dbHouse.Rating,
                Votes = dbHouse.Votes,
                HasRating = dbHouse.Rating > 0,
                CanVote = false,
                FactoryPlotId = 0,
                WhenCreated = dbHouse.Created.ToUnixTimeSeconds()
            };

            housingPacketInstanceList.Instances.Add(instanceInfo);
        }

        _logger.LogInformation("Sending housing list with {count} houses to player {name}",
            housingPacketInstanceList.Instances.Count, Player.Name.FirstName);

        Player.SendTunneled(housingPacketInstanceList);
    }

    public void SendFriendOffline()
    {
        var friendOfflinePacket = new FriendOfflinePacket();

        friendOfflinePacket.Guid = Player.Guid;

        foreach (var friend in Player.Friends)
        {
            if (!_zoneManager.TryGetPlayer(friend.Guid, out var friendPlayer))
                continue;

            var otherFriendPlayer = friendPlayer.Friends.FirstOrDefault(x => x.Guid == Player.Guid);

            if (otherFriendPlayer is null)
                continue;

            otherFriendPlayer.Online = false;

            friendPlayer.SendTunneled(friendOfflinePacket);
        }
    }

    public bool SaveItemToDatabase(ClientItem item)
    {
        try
        {
            using var dbContext = _dbContextFactory.CreateDbContext();

            var dbItem = new DbItem
            {
                Tint = item.Tint,
                Count = item.Count,
                Definition = item.Definition,
                CharacterId = Player.CharacterId
            };

            dbContext.Items.Add(dbItem);
            dbContext.SaveChanges();

            item.Id = dbItem.Id;

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save item {definition} to database for character {characterId}",
                item.Definition, Player.CharacterId);
            return false;
        }
    }

    #region Packet Compression

    protected override int DecryptUserSupplied(Span<byte> destData, Span<byte> sourceData)
    {
        if (!_options.UseCompression)
            return base.DecryptUserSupplied(destData, sourceData);

        if (sourceData[0] == 1)
        {
            return ZLib.Decompress(sourceData.Slice(1), destData);
        }
        else
        {
            sourceData.Slice(1).CopyTo(destData);

            return sourceData.Length - 1;
        }
    }

    protected override int EncryptUserSupplied(Span<byte> destData, Span<byte> sourceData)
    {
        if (!_options.UseCompression)
            return base.EncryptUserSupplied(destData, sourceData);

        if (sourceData.Length >= 24)
        {
            var compressedLength = ZLib.Compress(sourceData, destData.Slice(1));

            if (compressedLength > 0 && compressedLength < sourceData.Length)
            {
                destData[0] = 1;

                return compressedLength + 1;
            }
        }

        destData[0] = 0;

        sourceData.CopyTo(destData.Slice(1));

        return sourceData.Length + 1;
    }

    #endregion
}