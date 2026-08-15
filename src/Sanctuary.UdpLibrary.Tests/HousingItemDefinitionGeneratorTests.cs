using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Game.Resources;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.GameCommerce;

namespace Sanctuary.UdpLibrary.Tests;

[TestClass]
public class HousingItemDefinitionGeneratorTests
{
    [TestMethod]
    public void GeneratesMissingHousingDefinitionsFromStoreBundles()
    {
        var items = new ClientItemDefinitionCollection(NullLogger.Instance);
        items.TryAdd(16866, CreateFixture(16866, "Juice Bar", "hsg_juicebar_01.adr", 57));
        items.TryAdd(16907, CreateFixture(16907, "Basic Dance Floor", "hsg_dance_floor_01.adr", 53));

        var stores = new StoreDefinitionCollection(NullLogger.Instance);
        var store = new StoreDefinition { Id = 1 };
        stores.TryAdd(store.Id, store);

        AddBundle(store, 4853, 119, 76878, "Juice Bar", 29789, 29794, "7018");
        AddBundle(store, 4854, 119, 10451, "Party Pool", 6633, 6634, "7019");
        AddBundle(store, 4972, 119, 16193, "Basic Dance Floor", 132544, 132655, "5546");
        AddBundle(store, 5018, 142, 22950, "Spooky Wallpaper", 8874, 8875, "7651");

        var added = HousingItemDefinitionGenerator.AddMissingDefinitions(items, stores);

        Assert.AreEqual(4, added);
        Assert.AreEqual("hsg_vip_juicebar_01.adr", items[76878].ModelName);
        Assert.AreEqual("vip-juicebar-L1", items[76878].TextureAlias);
        Assert.AreEqual("hsg_vip_party_pool_01.agr", items[10451].ModelName);
        Assert.AreEqual("hsg_dance_floor_01.adr", items[16193].ModelName);
        Assert.AreEqual(17, items[22950].Type);
        Assert.AreEqual(2, items[22950].Param1);
        Assert.AreEqual(59, items[22950].CategoryId);
        Assert.AreEqual("customization", items[22950].TextureAlias);
    }

    private static ClientItemDefinition CreateFixture(int id, string comment, string modelName, int categoryId)
    {
        return new ClientItemDefinition
        {
            Id = id,
            Comment = comment,
            Type = 1,
            ModelName = modelName,
            CategoryId = categoryId,
            MaxStackSize = -1,
            TextureAlias = "fixture",
            TintAlias = "dyetint"
        };
    }

    private static void AddBundle(
        StoreDefinition store,
        int bundleId,
        int categoryGroupId,
        int marketingItemId,
        string comment,
        int nameId,
        int descriptionId,
        string image)
    {
        store.Bundles.Add(bundleId, new AppStoreBundleDefinition
        {
            Id = bundleId,
            StoreId = store.Id,
            CategoryGroupId = categoryGroupId,
            Comment = comment,
            NameId = nameId,
            DescriptionId = descriptionId,
            Image = new ImageDataDefinition { Image = image },
            MemberDiscount = 10,
            Entries =
            [
                new MarketingBundleDefinition.Entry
                {
                    Quantity = 1,
                    MarketingItemId = marketingItemId
                }
            ]
        });
    }
}
