namespace Sanctuary.Game.Resources.Definitions;

public class PetDefinition
{
    public int Id { get; set; }

    public int NameId { get; set; }

    public int ImageSetId { get; set; }

    public string TextureAlias { get; set; } = null!;
    public string TintAlias { get; set; } = null!;

    public int TintId { get; set; }

    public bool MembersOnly { get; set; }

    public bool IsNameable { get; set; }

    public int ModelId { get; set; }

    public float Scale { get; set; } = 0.5f;

    public PetStats Stats { get; set; } = new();

    public class PetStats
    {
        public float MaxMovementSpeed { get; set; }
    }
}
