namespace Sanctuary.Game.Entities;

// Difficulty tier for a combat enemy — gives HP/damage/XP variety so encounters aren't uniform. Classified
// from the enemy's NAME using the Free Realms bestiary naming (Robgoblin Initiate / Bruiser / Wizard / boss,
// Thugawug, Cray, etc. — https://everquest.allakhazam.com/wiki/Category:Free_Realms).
public enum EnemyTier
{
    Weak,   // trash — Initiate/Forager/Grunt/Pup/…
    Normal, // the default
    Tough,  // beefy melee — Bruiser/Brute/Guardian/Enforcer/…
    Elite,  // casters / minibosses — Adept/Wizard/Geomancer/Champion/Captain/…
    Boss,   // named bosses — Queen/King/Chief/Alpha/Warlord/…
}

public static class EnemyTiers
{
    // HP / damage / XP multipliers applied on top of the base level formula (see CombatNpc.InitializeFromLevel).
    public static (float Hp, float Damage, float Xp) Multipliers(this EnemyTier tier) => tier switch
    {
        EnemyTier.Weak  => (0.55f, 0.8f, 0.6f),
        EnemyTier.Tough => (1.9f,  1.3f, 1.7f),
        EnemyTier.Elite => (2.7f,  1.6f, 2.4f),
        EnemyTier.Boss  => (5.5f,  2.2f, 4.5f),
        _               => (1.0f,  1.0f, 1.0f), // Normal
    };

    // Keyword sets, checked most-specific first (a "Robgoblin Wizard King" is a Boss, not an Elite).
    private static readonly string[] BossWords  = { "queen", "king", "chief", "boss", "alpha", "lord", "master", "overlord", "warlord", "ancient", "elder", "matron", "baron", "titan", "prime", "the " };
    private static readonly string[] EliteWords = { "adept", "wizard", "geomancer", "shaman", "sorcerer", "mystic", "champion", "elite", "captain", "commander", "necromancer", "conjurer", "pondblaster" };
    private static readonly string[] ToughWords = { "bruiser", "brute", "guardian", "enforcer", "marauder", "snarler", "ambusher", "warrior", "juggernaut", "hulk", "ravager", "smasher", "crusher", "boomer" };
    private static readonly string[] WeakWords  = { "initiate", "forager", "camper", "prankster", "troublemaker", "grunt", "pup", "cub", "whelp", "straggler", "scout", "runt", "minion", "critter", "fishmonger", "hatchling" };

    // Classify an enemy from its display name; unknown / unnamed enemies are Normal.
    public static EnemyTier FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return EnemyTier.Normal;

        var n = name.ToLowerInvariant();
        foreach (var w in BossWords)  if (n.Contains(w)) return EnemyTier.Boss;
        foreach (var w in EliteWords) if (n.Contains(w)) return EnemyTier.Elite;
        foreach (var w in ToughWords) if (n.Contains(w)) return EnemyTier.Tough;
        foreach (var w in WeakWords)  if (n.Contains(w)) return EnemyTier.Weak;
        return EnemyTier.Normal;
    }
}
