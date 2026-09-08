using System;
using System.Collections.Generic;
using System.Numerics;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet.Common;

public class ClientPcData
{
    public ulong LaunchTicket;
    public ulong Guid { get; init; }

    public int Model;

    public string Head = null!;
    public string Hair = null!;

    public int HairColor;
    public int EyeColor;

    public string SkinTone = null!;

    public string? FacePaint;
    public string? ModelCustomization;

    public int HeadId;
    public int HairId;
    public int SkinToneId;
    public int FacePaintId;
    public int ModelCustomizationId;

    public Vector4 Position { get; protected set; }
    public Quaternion Rotation { get; protected set; }

    public NameData Name = new();

    public int Coins;

    public DateTimeOffset Birthday;
    public int Age;
    public int PlayTime;

    public bool IsUnderage;
    public bool IsOpenChatEnabled;

    public int MembershipStatus;

    public bool ShowMemberNagScreen;


    public int ChatCountryId;


    public int ChatLanguageId;


    public int PreferredLanguage;

    public int ChatLanguage;

    public int LoginCount;

    public bool Grandfathered;

    public int ActiveVehicleLoadout_KartRace;
    public int ActiveVehicleLoadout_DemoDerby;

    public List<ClientPcProfile> Profiles = new();

    public int ActiveProfileId;

    public List<ProfileTypeEntry> ProfileTypes = new();

    public List<ClientCollection> Collections = new();

    public List<ClientItem> Items = new();

    public int Gender;

    public int NonMemberQuestLimit = 30;
    public int MemberQuestLimit = 30;



    public int ActivePetId;
    public ulong ActivePetGuid;

    public List<PacketMountInfo> Mounts = new();

    public Dictionary<int, ClientActionBar> ActionBars = new();



    public List<NameChangeInfo> PendingNameChanges = new();

    public Dictionary<CharacterStatId, CharacterStat> Stats = new();


    public List<PlayerTitleData> Titles = new();

    public int ActiveTitle;

    public float VipRank;
    public int VipIconId;
    public int VipTitle;


    public ClientPcData()
    {
        Stats.Add(CharacterStatId.MaxHealth, new(CharacterStatId.MaxHealth, 2500));
        Stats.Add(CharacterStatId.MaxMovementSpeed, new(CharacterStatId.MaxMovementSpeed, 8f));
        Stats.Add(CharacterStatId.WeaponRange, new(CharacterStatId.WeaponRange, 5f));
        Stats.Add(CharacterStatId.HitPointRegen, new(CharacterStatId.HitPointRegen, 25));
        Stats.Add(CharacterStatId.MaxMana, new(CharacterStatId.MaxMana, 100));
        Stats.Add(CharacterStatId.ManaRegen, new(CharacterStatId.ManaRegen, 4));
        Stats.Add(CharacterStatId.Defense, new(CharacterStatId.Defense, 0));
        Stats.Add(CharacterStatId.MeleeAvoidance, new(CharacterStatId.MeleeAvoidance, 0));
        Stats.Add(CharacterStatId.MeleeCriticalHitChance, new(CharacterStatId.MeleeCriticalHitChance, 0));
        Stats.Add(CharacterStatId.MeleeCriticalHitMultiplier, new(CharacterStatId.MeleeCriticalHitMultiplier, 0f));
        Stats.Add(CharacterStatId.MeleeChanceToHit, new(CharacterStatId.MeleeChanceToHit, 100));
        Stats.Add(CharacterStatId.MeleeWeaponDamageMultiplier, new(CharacterStatId.MeleeWeaponDamageMultiplier, 1f));
        Stats.Add(CharacterStatId.MeleeHandToHandDamage, new(CharacterStatId.MeleeHandToHandDamage, 1));
        Stats.Add(CharacterStatId.EquippedMeleeWeaponDamage, new(CharacterStatId.EquippedMeleeWeaponDamage, 1));
        Stats.Add(CharacterStatId.MeleeAttackIntervalMs, new(CharacterStatId.MeleeAttackIntervalMs, 2000));
        Stats.Add(CharacterStatId.DamageReductionAmount, new(CharacterStatId.DamageReductionAmount, 0));
        Stats.Add(CharacterStatId.ExperienceBoostPercent, new(CharacterStatId.ExperienceBoostPercent, 0));
        Stats.Add(CharacterStatId.DamageReductionPercent, new(CharacterStatId.DamageReductionPercent, 0));
        Stats.Add(CharacterStatId.DamageAddition, new(CharacterStatId.DamageAddition, 0));
        Stats.Add(CharacterStatId.DamageMultiplier, new(CharacterStatId.DamageMultiplier, 1f));
        Stats.Add(CharacterStatId.HealingAddition, new(CharacterStatId.HealingAddition, 0));
        Stats.Add(CharacterStatId.HealingMultiplier, new(CharacterStatId.HealingMultiplier, 1f));
        Stats.Add(CharacterStatId.M3CollectableSpawnRate, new(CharacterStatId.M3CollectableSpawnRate, 0));
        Stats.Add(CharacterStatId.M3SpecialSpawnRate, new(CharacterStatId.M3SpecialSpawnRate, 0));
        Stats.Add(CharacterStatId.M3DetrementalSpawnRate, new(CharacterStatId.M3DetrementalSpawnRate, 0));
        Stats.Add(CharacterStatId.M3Magnitude, new(CharacterStatId.M3Magnitude, 0));
        Stats.Add(CharacterStatId.M3Timer, new(CharacterStatId.M3Timer, 0));
        Stats.Add(CharacterStatId.MimicRadius, new(CharacterStatId.MimicRadius, 0));
        Stats.Add(CharacterStatId.MimicCuttingMagnitude, new(CharacterStatId.MimicCuttingMagnitude, 0));
        Stats.Add(CharacterStatId.MimicPowerMagnitude, new(CharacterStatId.MimicPowerMagnitude, 0));
        Stats.Add(CharacterStatId.MimicGreenRange, new(CharacterStatId.MimicGreenRange, 0));
        Stats.Add(CharacterStatId.MimicSpeed, new(CharacterStatId.MimicSpeed, 0));
        Stats.Add(CharacterStatId.AbilityCriticalHitChance, new(CharacterStatId.AbilityCriticalHitChance, 0));
        Stats.Add(CharacterStatId.AbilityCriticalHitMultiplier, new(CharacterStatId.AbilityCriticalHitMultiplier, 1f));
        Stats.Add(CharacterStatId.Luck, new(CharacterStatId.Luck, 0));
        Stats.Add(CharacterStatId.HeadInflationPercent, new(CharacterStatId.HeadInflationPercent, 100));
        Stats.Add(CharacterStatId.GoldBoostPercent, new(CharacterStatId.GoldBoostPercent, 0));
        Stats.Add(CharacterStatId.M3PerMatchProc, new(CharacterStatId.M3PerMatchProc, 0));
        Stats.Add(CharacterStatId.SoccerKickPower, new(CharacterStatId.SoccerKickPower, 0));
        Stats.Add(CharacterStatId.SoccerFootwork, new(CharacterStatId.SoccerFootwork, 0));
        Stats.Add(CharacterStatId.SoccerSpeed, new(CharacterStatId.SoccerSpeed, 0));
        Stats.Add(CharacterStatId.SoccerToughness, new(CharacterStatId.SoccerToughness, 0));
        Stats.Add(CharacterStatId.SoccerTacklePower, new(CharacterStatId.SoccerTacklePower, 0));
        Stats.Add(CharacterStatId.FishingCastingSkill, new(CharacterStatId.FishingCastingSkill, 0));
        Stats.Add(CharacterStatId.FishingCastingStrength, new(CharacterStatId.FishingCastingStrength, 0));
        Stats.Add(CharacterStatId.FishingLineStrength, new(CharacterStatId.FishingLineStrength, 0));
        Stats.Add(CharacterStatId.FishingReelingSpeed, new(CharacterStatId.FishingReelingSpeed, 0));
        Stats.Add(CharacterStatId.FishingLuck, new(CharacterStatId.FishingLuck, 0));
        Stats.Add(CharacterStatId.FishingPerfectCastSkill, new(CharacterStatId.FishingPerfectCastSkill, 0));
        Stats.Add(CharacterStatId.Toughness, new(CharacterStatId.Toughness, 0));
        Stats.Add(CharacterStatId.AbilityCritVulnerability, new(CharacterStatId.AbilityCritVulnerability, 0));
        Stats.Add(CharacterStatId.MeleeCritVulnerability, new(CharacterStatId.MeleeCritVulnerability, 0));
        Stats.Add(CharacterStatId.RangeMultiplier, new(CharacterStatId.RangeMultiplier, 1f));
        Stats.Add(CharacterStatId.MaxShields, new(CharacterStatId.MaxShields, 0));
        Stats.Add(CharacterStatId.ShieldsRegen, new(CharacterStatId.ShieldsRegen, 0));
        Stats.Add(CharacterStatId.FactoryProductionModifier, new(CharacterStatId.FactoryProductionModifier, 1f));
        Stats.Add(CharacterStatId.FactoryYieldModifier, new(CharacterStatId.FactoryYieldModifier, 1f));
        Stats.Add(CharacterStatId.PlayerCastIllusionImmunity, new(CharacterStatId.PlayerCastIllusionImmunity, 0));
        Stats.Add(CharacterStatId.GlideDefaultForwardSpeed, new(CharacterStatId.GlideDefaultForwardSpeed, 0f));
        Stats.Add(CharacterStatId.GlideMinForwardSpeed, new(CharacterStatId.GlideMinForwardSpeed, 0f));
        Stats.Add(CharacterStatId.GlideMaxForwardSpeed, new(CharacterStatId.GlideMaxForwardSpeed, 0f));
        Stats.Add(CharacterStatId.GlideFallTime, new(CharacterStatId.GlideFallTime, 0f));
        Stats.Add(CharacterStatId.GlideFallSpeed, new(CharacterStatId.GlideFallSpeed, 0f));
        Stats.Add(CharacterStatId.GlideEnabled, new(CharacterStatId.GlideEnabled, 0));
        Stats.Add(CharacterStatId.InCombatHitPointRegen, new(CharacterStatId.InCombatHitPointRegen, 6));
        Stats.Add(CharacterStatId.InCombatManaRegen, new(CharacterStatId.InCombatManaRegen, 4));
        Stats.Add(CharacterStatId.GlideAccel, new(CharacterStatId.GlideAccel, 0f));
        Stats.Add(CharacterStatId.JumpHeight, new(CharacterStatId.JumpHeight, 0f));
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        writer.Write(LaunchTicket);
        writer.Write(Guid);

        writer.Write(Model);

        writer.Write(Head);
        writer.Write(Hair);

        writer.Write(HairColor);
        writer.Write(EyeColor);

        writer.Write(SkinTone);

        writer.Write(FacePaint);
        writer.Write(ModelCustomization);

        writer.Write(HeadId);
        writer.Write(HairId);
        writer.Write(SkinToneId);
        writer.Write(FacePaintId);
        writer.Write(ModelCustomizationId);

        writer.Write(Position);
        writer.Write(Rotation);

        Name.Serialize(writer);

        writer.Write(Coins);

        writer.Write(Birthday);
        writer.Write(Age);

        writer.Write(PlayTime);

        writer.Write(IsUnderage);
        writer.Write(IsOpenChatEnabled);

        writer.Write(MembershipStatus);
        writer.Write(ShowMemberNagScreen);

        writer.Write(ChatCountryId);
        writer.Write(ChatLanguageId);
        writer.Write(PreferredLanguage);
        writer.Write(ChatLanguage);

        writer.Write(LoginCount);

        writer.Write(Grandfathered);

        writer.Write(ActiveVehicleLoadout_KartRace);
        writer.Write(ActiveVehicleLoadout_DemoDerby);

        writer.Write(Profiles);

        writer.Write(ActiveProfileId);

        writer.Write(ProfileTypes);

        writer.Write(Collections);

        writer.Write(Items);

        writer.Write(Gender);

        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(false);
        writer.Write(NonMemberQuestLimit);
        writer.Write(MemberQuestLimit);

        writer.Write(0);
        writer.Write(0);

        writer.Write(0);

        writer.Write(0);

        writer.Write(0);

        writer.Write(ActivePetId);
        writer.Write(ActivePetGuid);

        writer.Write(Mounts);

        writer.Write(ActionBars);

        writer.Write(true);
        writer.Write(0);

        writer.Write(0);
        writer.Write(0);

        writer.Write(PendingNameChanges);

        writer.Write(Stats);

        writer.Write(0);
        writer.Write(0);

        writer.Write(Titles);

        writer.Write(ActiveTitle);

        writer.Write(VipRank);
        writer.Write(VipIconId);
        writer.Write(VipTitle);

        writer.Write(0);

        return writer.Buffer;
    }
}