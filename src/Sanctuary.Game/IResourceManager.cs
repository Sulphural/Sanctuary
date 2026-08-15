using Sanctuary.Game.Resources;

namespace Sanctuary.Game;

public interface IResourceManager
{
    IdToStringLookup HairMappings { get; }
    IdToStringLookup HeadMappings { get; }
    IdToStringLookup SkinToneMappings { get; }
    IdToStringLookup FacePaintMappings { get; }
    IdToStringLookup ModelCustomizationMappings { get; }

    ModelDefinitionCollection Models { get; }

    ClientItemDefinitionCollection ClientItemDefinitions { get; }

    CoinStoreItemCollection CoinStoreItems { get; }

    ItemClassDefinitionCollection ItemClasses { get; }
    ItemCategoryDefinitionCollection ItemCategories { get; }
    ItemCategoryGroupDefinitionCollection ItemCategoryGroups { get; }

    StoreDefinitionCollection Stores { get; }
    StoreBundleGroupDefinitionCollection StoreBundleGroups { get; }
    StoreBundleCategoryNodeCollection StoreBundleCategories { get; }
    StoreBundleCategoryGroupDefinitionCollection StoreBundleCategoryGroups { get; }

    ClientActivityDefinitionCollection ClientActivityDefinitions { get; }

    ZoneDefinitionCollection Zones { get; }
    HouseDefinitionCollection Houses { get; }
    MountDefinitionCollection Mounts { get; }
    PetDefinitionCollection Pets { get; }
    PlayerTitleCollection PlayerTitles { get; }
    ProfileDefinitionCollection Profiles { get; }
    QuickChatDefinitionCollection QuickChats { get; }
    PointOfInterestDefinitionCollection PointOfInterests { get; }
    NpcVendorCollection NpcVendors { get; }
    NpcDefinitionCollection Npcs { get; }


    ConsumableCollection Consumables { get; }

    QuestDefinitionCollection Quests { get; }

    // "Spin For The Win!" daily prize wheels (game_wheel.gfx), keyed by wheel id.
    DailyWheelDefinitionCollection DailyWheels { get; }

    // Loaded ".map" waypoint graphs, keyed by zone name - see MapGraphCollection.
    MapGraphCollection Maps { get; }

    bool Load();
}
