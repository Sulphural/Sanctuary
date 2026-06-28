namespace Sanctuary.Game.Resources.Definitions;

public enum CakeItemType
{
    ScaredyCake,
    BossCake
}

public class CakeItemDefinition
{
    public int ItemId { get; set; }

    public CakeItemType Type { get; set; }

    public int CooldownMs { get; set; }
}