# Housing System Implementation

## Overview
Complete housing system with player houses, furniture placement, editing, and persistence.

## Features Implemented

### 1. House Entry System
- **Auto-Creation**: Players automatically get a default house (Seaside Beach) on first entry
- **Zone Teleportation**: Players are teleported to their house's zone when entering
- **House Data Loading**: House properties and build areas are loaded and sent to client
- **Database Integration**: Houses are stored in `Houses` table with owner, definition, settings

### 2. House Instance Management
- **House Definitions**: 31 house types from `Houses.json` (Seaside Beach, Wilds, Cliffs, etc.)
- **Build Areas**: Each house has defined build areas where furniture can be placed
- **House Properties**:
  - Owner information
  - Custom name support
  - Lock status
  - Member-only flag
  - Flora allowed flag
  - Pet autospawn setting
  - Fixture count limits
  - Icon and description

### 3. Furniture Placement
- **Place Furniture**: Drag items from inventory into house (saved with position, rotation, scale)
- **Move Furniture**: Click and drag to reposition furniture (updates saved instantly)
- **Rotate & Scale**: Full 3D transformation support
- **Database Persistence**: All furniture placements saved to `HouseFixtures` table
- **Real-time Updates**: Changes visible immediately in-game

### 4. Furniture Management
- **Pickup Furniture**: Remove furniture from house (TODO: return to inventory)
- **Fixture Tracking**: Each piece tracked with unique GUID
- **Position Data**: Full Vector4 position and Quaternion rotation stored
- **Customization**: Support for future customization data (colors, styles, etc.)

### 5. Edit Mode
- **Toggle Edit Mode**: Switch between viewing and editing modes
- **Protected Editing**: Only house owner can edit their house
- **Visual Feedback**: Client shows edit UI when mode is active

## Database Schema

### Houses Table
```sql
- Id (GUID): Unique house identifier
- OwnerId (GUID): Character who owns the house
- HouseDefinitionId (int): Type of house (1-31)
- NameId (int): Localized name ID
- CustomName (string): Player-set custom name
- IsLocked (bool): Whether house is locked
- IsMembersOnly (bool): Members-only access
- IsFloraAllowed (bool): Can place plants
- PetAutospawn (bool): Pets spawn automatically
- MaxFixtureCount (int): Max furniture pieces
- MaxLandmarkCount (int): Max landmarks
- IconId (int): House icon
- Description (string): Custom description
- KeywordList (string): Search keywords
- Rating (float): Player rating
- Votes (int): Number of votes
- Created (datetime): Creation timestamp
- LastVisited (datetime): Last visit timestamp
```

### HouseFixtures Table
```sql
- Id (GUID): Unique fixture identifier
- HouseId (GUID): Parent house
- ItemDefinitionId (int): Type of furniture item
- PositionX/Y/Z/W (float): 3D position
- RotationX/Y/Z/W (float): Quaternion rotation
- Scale (float): Size multiplier
- CustomizationData (string): JSON customization
- Created (datetime): Placement timestamp
```

## Packet Handlers

### Client → Server
1. **ClientHousingPacketEnterRequest** (OpCode 19)
   - Player requests to enter house
   - Creates house if doesn't exist
   - Teleports player to house zone
   - Sends house data and furniture list

2. **ClientHousingPacketPlaceFixtureRequest** (OpCode 1)
   - Player places furniture from inventory
   - Creates fixture in database
   - Sends confirmation to client

3. **ClientHousingPacketSaveFixture** (OpCode 5)
   - Player moves/rotates furniture
   - Updates fixture position/rotation in database
   - No response needed (client updates visually)

4. **ClientHousingPacketPickupFixture** (OpCode 3)
   - Player removes furniture
   - Deletes fixture from database
   - Sends removal packet to client

5. **ClientHousingPacketSetEditMode** (OpCode 6)
   - Toggle edit mode on/off
   - Sends update to client

### Server → Client
1. **HousingPacketInstanceData** (OpCode 28)
   - Sends complete house information
   - Includes owner, settings, build areas, permissions

2. **HousingPacketFixtureItemList** (OpCode 43)
   - Sends all placed furniture
   - Includes fixture info and definitions

3. **HousingPacketPlaceFixture** (OpCode 2)
   - Confirms furniture placement
   - Provides fixture GUID

4. **HousingPacketRemoveFixture** (OpCode 44)
   - Confirms furniture removal

5. **HousingPacketUpdateHouseInfo** (OpCode varies)
   - Updates house settings

## Usage

### Entering Your House
1. Use in-game housing UI or command to enter house
2. System checks for existing house or creates default
3. Player is teleported to house zone
4. All furniture loads automatically

### Placing Furniture
1. Enter Edit Mode
2. Drag furniture item from inventory
3. Position it in build area
4. Item is saved automatically
5. (TODO: Item is removed from inventory)

### Moving Furniture
1. In Edit Mode, click furniture piece
2. Drag to new location
3. Rotate and scale as needed
4. Release - position saved automatically

### Removing Furniture
1. In Edit Mode, select furniture piece
2. Use pickup/delete action
3. Furniture removed from house
4. (TODO: Item returned to inventory)

## Default House Settings
- **Type**: Seaside Beach (Definition ID 1)
- **Max Fixtures**: 200
- **Max Landmarks**: 0
- **Flora Allowed**: Yes
- **Pet Autospawn**: No
- **Locked**: No
- **Members Only**: No

## Future Enhancements (TODO)
1. **Inventory Integration**: Remove items from inventory on place, return on pickup
2. **Permissions System**: Allow other players to visit with permissions
3. **House Visiting**: Visit other players' houses
4. **House Upgrades**: Unlock larger houses or more fixture slots
5. **Furniture Actions**: Interactive furniture (sit, sleep, etc.)
6. **Customization**: Color/style customization for furniture
7. **House Rating**: Vote and rate other players' houses
8. **House Search**: Find houses by name/keywords
9. **Upkeep System**: House maintenance costs
10. **Multiple Houses**: Let players own multiple houses

## Configuration
House definitions are loaded from `src/Resources/Houses.json`. Each definition includes:
- Zone ID and name
- Spawn position/rotation
- Build area boundaries
- Icon and display name

## Testing
1. Build the server: `cd src; dotnet build`
2. Start the server
3. Login and use housing UI to enter your house
4. (Optional) Run `add_furniture_items.sql` to add test items
5. Enter Edit Mode and try placing furniture

## Notes
- Houses are zone-instanced (each player has own instance)
- Build areas prevent furniture placement outside boundaries
- All furniture operations are database-persisted
- Player.CurrentHouseGuid tracks active house for operations
- Sky changes based on house zone (seaside/wilds)
