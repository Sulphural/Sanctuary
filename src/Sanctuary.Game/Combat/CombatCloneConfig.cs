namespace Sanctuary.Game.Combat;

// Config for BaseZone.SummonCombatClones - lets each job's summon special (Ninja's Shadow Army, Medic's
// Nurse!) reuse the same shared spawn/chase/attack/despawn engine with its own model/name/anims/FX/damage.
// Generalized 2026-07-29 from the Ninja-only "Shadow Army" prototype, which was hardcoded to chase ONE
// fixed training dummy in the tutorial zone (StartingZone) only - it silently did nothing anywhere else.
// The generalized engine targets the nearest real hostile NPC within LeashRange of the SUMMONER, in any
// zone, using the same nearby-hostile query AbilityPacketClientRequestStartAbilityHandler.SplashShockPaddles
// already uses for Medic's Shock Paddles splash.
public sealed record CombatCloneConfig(
    int ModelId,
    string Name,
    int RunAnim,
    int WalkAnim,
    int StandAnim,
    int AttackAnim,
    int AttackDamage,
    int AttackCooldownMs,
    int HitFx,
    int SpawnPoofFx,
    float MoveSpeed = 9f,
    float AttackRange = 2.5f,
    float LeashRange = 20f,
    int TickMs = 300);
