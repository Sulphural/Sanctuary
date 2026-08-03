namespace Sanctuary.Game.Resources.Definitions;

public class PetDefinition
{
    public int Id { get; set; }

    public int NameId { get; set; }

    // Server-side only - not sent over the wire (NameId is what the client resolves to display
    // text). Used as the default name for a newly-acquired pet, before the player renames it.
    public string DisplayName { get; set; } = null!;

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
