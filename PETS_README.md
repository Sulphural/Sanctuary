# Pet System Documentation

## Overview

The pet system allows players to own, summon, and interact with companion pets that follow them around the game world. This system is similar to the mount system but designed for companion creatures.

## Features

- **Pet Collection**: Players can own multiple pets stored in their pet collection
- **Pet Summoning**: Summon and recall pets using the pet panel
- **Pet Customization**: Pets can have custom names and tints
- **Pet Following**: Pets follow their owner around the world
- **Database Persistence**: All pet data is saved to the database

## Architecture

### Database Layer

- **DbPet Entity** (`Sanctuary.Database.Entities/DbPet.cs`): Represents a pet in the database

  - `Id`: Unique pet ID per character
  - `Name`: Custom name for the pet (max 32 characters)
  - `Tint`: Color/tint ID for the pet
  - `Definition`: Pet definition ID from Pets.json
  - `CharacterId`: Foreign key to the owning character
  - `Created`: Timestamp when pet was created

- **Database Migration** (`20251210000000_AddPets.cs`): Creates the Pets table
  - Also adds missing Tint and Definition columns to Mounts table
  - Includes unique index on (Tint, Definition, CharacterGuid)

### Game Layer

- **PetDefinition** (`Sanctuary.Game/Resources/Definitions/PetDefinition.cs`): Defines pet properties

  - ModelId, NameId, ImageSetId
  - Scale (default 0.5f for smaller size)
  - IsNameable flag
  - Stats (movement speed, etc.)

- **PetDefinitionCollection** (`Sanctuary.Game/Resources/PetDefinitionCollection.cs`): Manages pet definitions loaded from Pets.json

- **Pet Entity** (`Sanctuary.Game/Entities/Pet.cs`): Runtime pet instance
  - Inherits from Npc
  - Follows owner using FollowGuid
  - Can be spawned/despawned dynamically

### Packet Layer

- **PacketPetInfo** (`Sanctuary.Packet.Common/PacketPetInfo.cs`): Pet information sent to client
- **PetBasePacket** (`Sanctuary.Packet/PetBasePacket.cs`): Base packet for all pet operations (OpCode 53)
- **PetListPacket**: Sends list of owned pets to client
- **PetSummonRecallPacket**: Client request to summon/recall a pet
- **PetActivePacket**: Notifies clients when a pet is summoned

### Handler Layer

- **PetBasePacketHandler** (`Sanctuary.Gateway/Handlers/PetBasePacketHandler.cs`): Routes pet packets
- **PetSummonRecallPacketHandler**: Handles pet summon/recall requests
  - Spawns pet entity in the world
  - Manages pet visibility and following behavior
  - Handles recall (despawn) when called again

## Resource Files

### Pets.json

Located at `src/Resources/Pets.json`. Each pet definition contains:

```json
{
  "Comment": "Pet description",
  "Id": 150001,
  "NameId": 6638,
  "ImageSetId": 6947,
  "TintAlias": "dyetint",
  "ModelId": 3339,
  "IsNameable": true,
  "Stats": {
    "MaxMovementSpeed": 8.0
  }
}
```

**Sample Pets Included:**

- Pixie Dragon (ID: 150001)
- Mini Robot (ID: 150002)
- Baby Dragon (ID: 150003)

## Adding Pets to Players

### Using SQL

Use the provided `add_test_pets.sql` script:

```sql
INSERT INTO Pets (Id, CharacterGuid, Name, Tint, Definition, Created)
VALUES (1, YOUR_CHARACTER_ID, 'Pet Name', 0, 150001, NOW());
```

### Via In-Game Purchase System

Pets can be added through the coin store system by creating item definitions with Type 20 (or similar pet type).

## How It Works

1. **Login**: When a player logs in, their pets are loaded from the database via `CreatePlayerFromDatabase`
2. **Pet List**: The pet collection is sent to the client as part of ClientPcData
3. **Summon**: Player clicks on a pet in their collection
   - Client sends `PetSummonRecallPacket` with the pet ID
   - Server creates a Pet entity in the zone
   - Server sends `PetActivePacket` to all visible players
   - Pet appears next to the player and follows them
4. **Recall**: Player clicks the active pet button
   - Client sends `PetSummonRecallPacket` again
   - Server despawns the pet entity
5. **Zone Transfer**: Pets follow their owner to new zones via `TeleportToZone`

## Technical Details

### Pet Spawning

- Pets are spawned as Npc entities with `FollowGuid` set to owner
- Scale is typically 0.5f (smaller than mounts)
- Disposition set to 1 (friendly)
- HideNamePlate = false (show pet name)
- CompositeEffectId 46 used for spawn effect (PFX_Teleport_Flash)

### Pet Following

Pets use the game's existing NPC following system via the `FollowGuid` property, which makes them automatically follow their owner.

### Differences from Mounts

- **Size**: Pets are smaller (Scale ~0.5f vs 1.0f for mounts)
- **Mounting**: Pets don't have riders, they follow beside the player
- **Nameable**: Pets can have custom names, mounts use predefined NameIds
- **Movement**: Pets don't affect player movement speed
- **Visibility**: Pet names are shown, mount names are hidden

## Extending the System

### Adding New Pets

1. Add pet definition to `Pets.json`
2. Use unique ID (recommend 150000+ range to avoid conflicts)
3. Set appropriate ModelId, NameId, ImageSetId from game data
4. Configure scale and stats as needed

### Adding Pet Features

Consider implementing:

- Pet tricks/emotes (PetPerformTrickPacket exists)
- Pet utility items (PetUtilityPacket exists)
- Pet moods (PetMoodListPacket exists)
- Pet grooming (PetUtilityGroomPacket exists)
- Pet feeding (PetUtilityFeedPacket exists)
- Pet renaming (PetSetNamePacket exists)

These packet handlers exist in the protocol but are not yet implemented in this codebase.

## Migration Notes

If you're upgrading from a previous version:

1. Run the `20251210000000_AddPets` migration
2. This will also add missing columns to the Mounts table
3. Back up your database before running migrations
4. The migration is reversible via the Down() method

## Troubleshooting

**Pet doesn't appear:**

- Check that pet definition exists in Pets.json
- Verify Definition ID matches between database and Pets.json
- Check server logs for resource loading errors

**Pet list not showing:**

- Ensure Pets.json is in the correct location (src/Resources/)
- Check that ResourceManager.Load() returns true for pets
- Verify database query includes `.Include(x => x.Pets)`

**Pet doesn't follow:**

- Pet entity must have FollowGuid set to owner's Guid
- Check that UpdatePosition is called on pet after spawn
- Verify pet is added to the zone correctly
