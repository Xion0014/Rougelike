/*
 * Imlight
 * Copyright (C) 2026 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * ========================================================================
 * PET PROGRESS
 * ========================================================================
 *
 * PURPOSE:
 * Pet stats, talents, experience and levels, read from the pet's template in Root.wad.
 *
 * NOTE:
 * - Levels: 1 Baby, 2 Teen, 3 Adult, 4 Ancient, 5 Epic, 6 Mega, 7 Ultra. A level's m_requiredXP in the
 *   template is the total experience the pet needs to reach the next level.
 * - Every level from Teen to Mega shows one talent and one derby talent from the pet's pool.
 * - A talent's value comes from the pet's stats: (first stat + second stat + third stat * modifier) times the
 *   effect's number, as in PetTalentEffects.xml. For example Pain-Giver is (Strength + Will + Power / 2) / 100 percent.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.IO;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Pet;

internal static class PetProgress {

    internal const byte BABY_LEVEL = 1;
    internal const byte MEGA_LEVEL = 6;
    internal const byte MAX_LEVEL = 7;
    // [Pets] in Imlight.ini.
    private static bool? s_doubleSnackXp;
    private static float? s_likedSnackBonus;

    // Double XP & Rewards, which the feed window shows: snack experience counts twice.
    private static bool DoubleSnackXp => s_doubleSnackXp ??= ReadSetting("Pets.DoubleSnackXp", true);

    // Renamed from FavoriteSnackBonus (was 1.5) so old ini values do not stick; Justin's rule has no liked bonus.
    private static float LikedSnackBonus => s_likedSnackBonus ??= Math.Max(0f, ReadSetting("Pets.LikedSnackBonus", 1.0f));

    private static T ReadSetting<T>(string key, T defaultValue) {
        try {
            return ConfigurationManager.GetValue(key, defaultValue);
        }
        catch (Exception) {
            return defaultValue;
        }
    }
    private const int DEFAULT_START_STAT = 10;

    private static readonly string[] s_levelNames = ["Egg", "Baby", "Teen", "Adult", "Ancient", "Epic", "Mega", "Ultra"];
    private static readonly string[] s_statNames = ["Strength", "Intellect", "Agility", "Will", "Power"];

    /// <summary>What a feeding or an XP grant did to a pet.</summary>
    internal sealed class PetGrowth {
        internal uint XpGained;
        internal int LevelsGained;
        internal readonly List<PetTalentBaseTemplate> NewTalents = [];
        internal readonly List<PetTalentBaseTemplate> NewDerbyTalents = [];
        internal readonly List<(string Stat, int Change)> StatChanges = [];
        internal readonly List<(byte Level, PetTalentBaseTemplate Talent, PetTalentBaseTemplate Derby)> LevelUps = [];
    }

    internal static string LevelName(int level) => level >= 0 && level < s_levelNames.Length ? s_levelNames[level] : level.ToString();

    internal static byte? ParseLevel(string text) {
        if (byte.TryParse(text, out var number)) {
            return number is >= BABY_LEVEL and <= MAX_LEVEL ? number : null;
        }

        var index = Array.FindIndex(s_levelNames, n => n.Equals(text, StringComparison.OrdinalIgnoreCase));

        return index >= BABY_LEVEL ? (byte) index : null;
    }

    internal static ClientPetItemBehavior GetBehavior(WizClientObjectItem pet) {
        var behavior = pet?.m_inactiveBehaviors?.OfType<ClientPetItemBehavior>().FirstOrDefault();
        if (behavior is not null) {
            // Remembered so the maximum stats, worked out from the behavior alone, can find the pet's raised ones.
            s_pets.AddOrUpdate(behavior, pet);
        }

        return behavior;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ClientPetItemBehavior, WizClientObjectItem> s_pets = new();

    internal static PetItemBehaviorTemplate GetTemplate(WizClientObjectItem pet)
        => pet is null ? null : PetFactory.GetPetItemBehaviorTemplate((uint) pet.m_templateID);

    /// <summary>
    /// Fills in the stats, talent pool and experience goal of a pet that does not have them yet.
    /// </summary>
    /// <returns>True if anything changed and the pet should be saved.</returns>
    internal static bool EnsureInitialized(WizClientObjectItem pet) {
        var behavior = GetBehavior(pet);
        var template = GetTemplate(pet);
        if (behavior is null || template is null || behavior.m_level < BABY_LEVEL) {
            return false;
        }

        var changed = false;
        if (behavior.m_allTalents is null || behavior.m_allTalents.Count == 0) {
            behavior.m_allTalents = TalentPool(template.m_guaranteedTalents, template.m_talents);
            changed = true;
        }

        if (behavior.m_allDerbyTalents is null || behavior.m_allDerbyTalents.Count == 0) {
            behavior.m_allDerbyTalents = TalentPool(template.m_guaranteedDerbyTalents, template.m_derbyTalents);
            changed = true;
        }

        if (behavior.m_expressedTalents is null) {
            behavior.m_expressedTalents = [];
            changed = true;
        }

        if (behavior.m_expressedDerbyTalents is null) {
            behavior.m_expressedDerbyTalents = [];
            changed = true;
        }

        if (behavior.m_currentStats is null || behavior.m_currentStats.Count == 0) {
            var start = template.m_startStats?.Count > 0 ? template.m_startStats : template.m_conversionStats;
            behavior.m_currentStats = [.. StatNames(template).Select(name => NewStat(name,
                start?.FirstOrDefault(s => SameStat(s?.m_name, name))?.m_value ?? DEFAULT_START_STAT))];
            changed = true;
        }

        var maxBefore = StatSummary(behavior.m_maxStats);
        RecalculateMaxStats(behavior, template);
        changed |= maxBefore != StatSummary(behavior.m_maxStats);

        var required = RequiredXp(template, behavior.m_level);
        if (behavior.m_requiredXP != required) {
            behavior.m_requiredXP = required;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Gives a pet experience and levels it up as far as the experience reaches.
    /// </summary>
    internal static PetGrowth AddXp(WizClientObjectItem pet, uint xp, PetGrowth growth = null) {
        growth ??= new PetGrowth();
        var behavior = GetBehavior(pet);
        var template = GetTemplate(pet);
        if (behavior is null || template is null) {
            return growth;
        }

        EnsureInitialized(pet);
        if (behavior.m_level >= MAX_LEVEL) {
            return growth;
        }

        behavior.m_XP += xp;
        growth.XpGained += xp;

        // m_XP counts from the start of the current level, like the pet window shows it (4/125, then 0/250), unless
        // RunningTotalXp keeps it as a total that each level's goal is measured against.
        while (behavior.m_level < MAX_LEVEL) {
            var required = RequiredXp(template, behavior.m_level);
            if (required == 0 || behavior.m_XP < required) {
                break;
            }

            if (!PetRules.RunningTotalXp) {
                behavior.m_XP -= required;
            }

            LevelUp(behavior, template, growth);

            // The real game keeps the extra for the next level; with KeepExtraXp off it is lost.
            if (!PetRules.KeepExtraXp && !PetRules.RunningTotalXp) {
                growth.XpGained -= Math.Min(growth.XpGained, behavior.m_XP);
                behavior.m_XP = 0;

                break;
            }
        }

        if (behavior.m_level >= MAX_LEVEL) {
            behavior.m_requiredXP = 0;
        }

        return growth;
    }

    /// <summary>
    /// Sets a pet to a level, showing the talents a pet gets on the way up.
    /// </summary>
    internal static PetGrowth SetLevel(WizClientObjectItem pet, byte level) {
        var growth = new PetGrowth();
        var behavior = GetBehavior(pet);
        var template = GetTemplate(pet);
        if (behavior is null || template is null) {
            return growth;
        }

        EnsureInitialized(pet);
        while (behavior.m_level < level && behavior.m_level < MAX_LEVEL) {
            // Each level starts at 0 experience.
            behavior.m_XP = 0;
            LevelUp(behavior, template, growth);
        }

        if (behavior.m_level > level) {
            behavior.m_level = level;
            behavior.m_XP = 0;
            behavior.m_requiredXP = RequiredXp(template, level);
        }

        return growth;
    }

    private static void LevelUp(ClientPetItemBehavior behavior, PetItemBehaviorTemplate template, PetGrowth growth) {
        behavior.m_level++;
        growth.LevelsGained++;

        PetTalentBaseTemplate talent = null, derby = null;
        if (behavior.m_level <= MEGA_LEVEL) {
            talent = Manifest(behavior.m_allTalents, behavior.m_expressedTalents);
            if (talent is not null) {
                growth.NewTalents.Add(talent);
                RecalculateMaxStats(behavior, template);
            }

            derby = Manifest(behavior.m_allDerbyTalents, behavior.m_expressedDerbyTalents);
            if (derby is not null) {
                growth.NewDerbyTalents.Add(derby);
            }
        }

        growth.LevelUps.Add((behavior.m_level, talent, derby));

        behavior.m_requiredXP = RequiredXp(template, behavior.m_level);
    }

    /// <summary>
    /// Picks a talent from the pool that the pet does not show yet. Higher ranked talents are rarer.
    /// </summary>
    private static PetTalentBaseTemplate Manifest(List<uint> pool, List<uint> expressed) {
        // A pet shows at most five of each kind, which talents claimed by hand may already fill.
        if (expressed.Count >= MAX_SHOWN_TALENTS) {
            return null;
        }

        var candidates = (pool ?? [])
            .Where(id => !expressed.Contains(id))
            .Select(PetData.GetTalent)
            .Where(t => t is not null)
            .ToList();
        if (candidates.Count == 0) {
            return null;
        }

        var weights = candidates.Select(t => Math.Max(1, 6 - t.m_rank)).ToList();
        var roll = Random.Shared.Next(weights.Sum());
        var index = 0;
        while (roll >= weights[index]) {
            roll -= weights[index];
            index++;
        }

        var picked = candidates[index];
        expressed.Add(PetData.TalentId(picked.m_talentName));

        return picked;
    }

    /// <summary>
    /// The most talents of one kind (combat or derby) a pet shows.
    /// </summary>
    internal const int MAX_SHOWN_TALENTS = 5;

    /// <summary>
    /// The talent pool of one kind, in the order the pet window lists it (slot 0 at the top).
    /// </summary>
    internal static List<uint> Pool(ClientPetItemBehavior behavior, bool derby)
        => derby ? behavior.m_allDerbyTalents ??= [] : behavior.m_allTalents ??= [];

    private static List<uint> Shown(ClientPetItemBehavior behavior, bool derby)
        => derby ? behavior.m_expressedDerbyTalents ??= [] : behavior.m_expressedTalents ??= [];

    /// <summary>
    /// Puts a talent into a slot of the pet's pool without showing it (`.pet talent get`). The talent that was in the
    /// slot leaves the pool and stops showing.
    /// </summary>
    /// <returns>Null when it worked, otherwise the reason in plain words.</returns>
    internal static string PutInPool(WizClientObjectItem pet, bool derby, int slot, PetTalentBaseTemplate talent) {
        var behavior = GetBehavior(pet);
        if (behavior is null || talent is null) {
            return "Wear a pet first.";
        }

        if ((talent is PetDerbyTalentTemplate) != derby) {
            return derby ? "That is not a derby talent. Use .pet talent for it." : "That is a derby talent. Use .pet derby for it.";
        }

        EnsureInitialized(pet);
        var pool = Pool(behavior, derby);
        var id = PetData.TalentId(talent.m_talentName);
        var at = pool.IndexOf(id);
        if (at >= 0 && at != slot) {
            return $"Your pet already has that talent in slot {at}.";
        }

        while (pool.Count <= slot) {
            pool.Add(0);
        }

        if (pool[slot] != id) {
            Shown(behavior, derby).Remove(pool[slot]);
            pool[slot] = id;
        }

        RecalculateMaxStats(behavior, GetTemplate(pet));

        return null;
    }

    /// <summary>
    /// Shows or hides the talent in a slot of the pet's pool (`.pet talent claim` and `unclaim`).
    /// </summary>
    /// <returns>Null when it worked, otherwise the reason in plain words.</returns>
    internal static string Claim(WizClientObjectItem pet, bool derby, int slot, bool claim, out PetTalentBaseTemplate talent) {
        talent = null;
        var behavior = GetBehavior(pet);
        if (behavior is null) {
            return "Wear a pet first.";
        }

        EnsureInitialized(pet);
        var pool = Pool(behavior, derby);
        if (slot >= pool.Count || pool[slot] == 0 || PetData.GetTalent(pool[slot]) is not { } found) {
            return $"Slot {slot} has no talent. Put one there with .pet {(derby ? "derby" : "talent")} get {slot} <name>.";
        }

        talent = found;
        var shown = Shown(behavior, derby);
        if (claim == shown.Contains(pool[slot])) {
            return claim ? "That talent is already claimed." : "That talent is not claimed.";
        }

        if (claim && shown.Count >= MAX_SHOWN_TALENTS) {
            return $"Your pet already shows {MAX_SHOWN_TALENTS} {(derby ? "derby talents" : "talents")}. Unclaim one first with .pet {(derby ? "derby" : "talent")} unclaim <slot>.";
        }

        if (claim) {
            // Kept in pool order, like the window lists them.
            shown.Add(pool[slot]);
            shown.Sort((a, b) => pool.IndexOf(a).CompareTo(pool.IndexOf(b)));
        }
        else {
            shown.Remove(pool[slot]);
        }

        RecalculateMaxStats(behavior, GetTemplate(pet));

        return null;
    }

    /// <summary>
    /// Sets one stat (Strength, Intellect, Agility, Will, Power) or Happiness to an amount or its maximum (`.pet stats`).
    /// </summary>
    /// <returns>Null when it worked, otherwise the reason in plain words.</returns>
    internal static string SetStat(WizClientObjectItem pet, string statName, string amount, out string shownAs) {
        shownAs = null;
        var behavior = GetBehavior(pet);
        if (behavior is null) {
            return "Wear a pet first.";
        }

        EnsureInitialized(pet);
        var useMax = string.Equals(amount, "max", StringComparison.OrdinalIgnoreCase);
        if (!useMax && !int.TryParse(amount, out _)) {
            return "Type an amount or max, such as .pet stats strength 150 or .pet stats will max.";
        }

        // An amount above the maximum raises the maximum to it (Justin, 2026-10-19: Strength 220 to 260).
        var wanted = useMax ? 0 : Math.Max(0, int.Parse(amount));
        if (string.Equals(statName, "happiness", StringComparison.OrdinalIgnoreCase)) {
            if (!useMax && wanted > behavior.m_maximumHappiness) {
                PetMaxRaises.Set(pet.m_globalID.Full, "Happiness", wanted);
                behavior.m_maximumHappiness = (uint) wanted;
            }

            var most = behavior.m_maximumHappiness;
            behavior.m_happiness = useMax ? most : (uint) Math.Min(wanted, (int) most);
            shownAs = $"Happiness {behavior.m_happiness}/{most}";

            return null;
        }

        var stat = behavior.m_currentStats?.FirstOrDefault(s => SameStat(s.m_name, statName));
        if (stat is null) {
            return "Pick Strength, Intellect, Agility, Will, Power or Happiness.";
        }

        if (!useMax && wanted > GetStat(behavior.m_maxStats, stat.m_name)) {
            PetMaxRaises.Set(pet.m_globalID.Full, stat.m_name, wanted);
            RecalculateMaxStats(behavior, GetTemplate(pet));
        }

        var max = GetStat(behavior.m_maxStats, stat.m_name);
        stat.m_value = useMax ? max : Math.Min(wanted, max);
        shownAs = $"{stat.m_name} {stat.m_value}/{max}";

        return null;
    }

    /// <summary>
    /// Makes a pet show a talent, from its pool or not. Derby talents go to the derby list.
    /// </summary>
    internal static bool AddTalent(WizClientObjectItem pet, PetTalentBaseTemplate talent) {
        var behavior = GetBehavior(pet);
        if (behavior is null || talent is null) {
            return false;
        }

        EnsureInitialized(pet);
        var id = PetData.TalentId(talent.m_talentName);
        var list = talent is PetDerbyTalentTemplate ? behavior.m_expressedDerbyTalents : behavior.m_expressedTalents;
        if (list.Contains(id)) {
            return false;
        }

        list.Add(id);
        RecalculateMaxStats(behavior, GetTemplate(pet));

        return true;
    }

    internal static bool RemoveTalent(WizClientObjectItem pet, PetTalentBaseTemplate talent) {
        var behavior = GetBehavior(pet);
        if (behavior is null || talent is null) {
            return false;
        }

        var id = PetData.TalentId(talent.m_talentName);
        var removed = (behavior.m_expressedTalents?.Remove(id) ?? false) | (behavior.m_expressedDerbyTalents?.Remove(id) ?? false);
        RecalculateMaxStats(behavior, GetTemplate(pet));

        return removed;
    }

    /// <summary>
    /// Raises the pet's stats by the changes, up to its maximum stats.
    /// </summary>
    internal static void ApplyStatChanges(WizClientObjectItem pet, IEnumerable<PetStatModification> changes, PetGrowth growth) {
        var behavior = GetBehavior(pet);
        if (behavior?.m_currentStats is null || changes is null) {
            return;
        }

        foreach (var change in changes) {
            var stat = behavior.m_currentStats.FirstOrDefault(s => SameStat(s.m_name, change?.m_name));
            if (stat is null || change.m_change == 0) {
                continue;
            }

            var max = GetStat(behavior.m_maxStats, stat.m_name);
            var newValue = Math.Clamp(stat.m_value + change.m_change, 0, max);
            if (newValue != stat.m_value) {
                growth?.StatChanges.Add((stat.m_name, newValue - stat.m_value));
                stat.m_value = newValue;
            }
        }
    }

    /// <summary>
    /// Sets every stat to its maximum.
    /// </summary>
    internal static void MaxStats(WizClientObjectItem pet) {
        var behavior = GetBehavior(pet);
        if (behavior?.m_currentStats is null) {
            return;
        }

        foreach (var stat in behavior.m_currentStats) {
            stat.m_value = GetStat(behavior.m_maxStats, stat.m_name);
        }
    }

    /// <summary>
    /// The experience a snack gives (Justin, 2026-10-19): the stat numbers on its card added up (Decaversary Cake:
    /// +15 Agility, +15 Intellect, +25 Power = 55), twice that with Double XP, times LikedSnackBonus for a snack the
    /// pet likes.
    /// </summary>
    internal static uint SnackXp(WizClientObjectItem pet, PetSnackItemTemplate snack) {
        float xp = (snack?.m_statModifierSet?.m_modifications ?? []).Where(change => change is not null).Sum(change => Math.Max(0, change.m_change));
        if (DoubleSnackXp) {
            xp *= 2;
        }

        if (IsFavoriteSnack(pet, snack)) {
            xp *= LikedSnackBonus;
        }

        return (uint) Math.Max(1, Math.Round(xp, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// The stat changes a snack gives: the numbers on its card, twice those with Double XP &amp; Rewards and
    /// DoubleSnackStats (Justin, 2026-10-19).
    /// </summary>
    internal static IEnumerable<PetStatModification> SnackStatChanges(PetSnackItemTemplate snack) {
        var changes = snack?.m_statModifierSet?.m_modifications ?? [];

        return DoubleSnackXp && PetRules.DoubleSnackStats
            ? changes.Select(change => change is null ? null : change with { m_change = change.m_change * 2 })
            : changes;
    }

    internal static bool IsFavoriteSnack(WizClientObjectItem pet, PetSnackItemTemplate snack) {
        var favorites = GetTemplate(pet)?.m_favoriteSnackCategories;
        if (favorites is null || snack?.m_adjectiveList is null) {
            return false;
        }

        return favorites.Any(f => snack.m_adjectiveList.Any(a => string.Equals(a, f, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The wizard stats a pet's talents give, worked out from the pet's current stats.
    /// </summary>
    /// <param name="pet">The pet.</param>
    /// <param name="extraTalents">Talents from a jewel in the pet's socket.</param>
    /// <returns>Pairs of a stat name in the form CharacterEffectHelper reads (Canonical&lt;School&gt;&lt;Kind&gt;) and the effect.</returns>
    internal static List<(string Name, WizStatisticEffect Effect)> ComputeEffects(WizClientObjectItem pet, IEnumerable<string> extraTalents = null) {
        var result = new List<(string, WizStatisticEffect)>();
        var behavior = GetBehavior(pet);
        if (behavior is null) {
            return result;
        }

        var talents = (behavior.m_expressedTalents ?? []).Select(PetData.GetTalent)
            .Concat((extraTalents ?? []).Select(PetData.GetTalent))
            .OfType<PetTalentTemplate>();
        foreach (var talent in talents) {
            foreach (var info in talent.m_effectList?.OfType<StatisticEffectInfo>() ?? []) {
                var template = PetData.GetTalentEffect(info.m_effectName);
                if (template is null) {
                    continue;
                }

                var basis = GetStat(behavior.m_currentStats, template.m_primaryStat1)
                    + GetStat(behavior.m_currentStats, template.m_primaryStat2)
                    + GetStat(behavior.m_currentStats, template.m_secondaryStat) * template.m_secondaryModifier;
                result.Add((StatName(template), ScaleEffect(template, basis)));
            }
        }

        return result;
    }

    /// <summary>
    /// The cards a pet's talents give its owner in battle, such as Hexster's Hex.
    /// </summary>
    internal static List<ProvideSpellEffectInfo> CardEffects(WizClientObjectItem pet, IEnumerable<string> extraTalents = null) {
        var behavior = GetBehavior(pet);
        if (behavior is null) {
            return [];
        }

        return [.. (behavior.m_expressedTalents ?? []).Select(PetData.GetTalent)
            .Concat((extraTalents ?? []).Select(PetData.GetTalent))
            .OfType<PetTalentTemplate>()
            .SelectMany(talent => talent.m_effectList?.OfType<ProvideSpellEffectInfo>() ?? [])
            .Concat(PowerCardEffects(pet))];
    }

    /// <summary>
    /// The pet's own card for its level, such as the Obsidian Mimic's Black Mantle (the card the pet window shows).
    /// The pet template lists it per level (m_Levels: m_powerCardName, 2 and 3, and m_powerCardCount).
    /// </summary>
    private static IEnumerable<ProvideSpellEffectInfo> PowerCardEffects(WizClientObjectItem pet) {
        var behavior = GetBehavior(pet);
        var level = GetTemplate(pet)?.m_Levels?.FirstOrDefault(info => info is not null && info.m_level == behavior?.m_level);
        if (level is null || level.m_powerCardCount <= 0) {
            yield break;
        }

        var names = new[] { (string) level.m_powerCardName, (string) level.m_powerCardName2, (string) level.m_powerCardName3 }
            .Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
        for (var i = 0; i < names.Count; i++) {
            // Copies beyond one per named card go to the first card.
            var copies = i == 0 ? Math.Max(1, (int) level.m_powerCardCount - (names.Count - 1)) : 1;
            yield return new ProvideSpellEffectInfo {
                // The game looks the effect up by this name in its own list (WizardEffects.xml): "ProvideSpell" is the
                // plain card-giving effect. A made-up name such as "PetPowerCard" matched nothing there, so the deck
                // window never listed the card with the item cards (Justin, 2026-10-19).
                m_effectName = "ProvideSpell",
                m_spellName = names[i],
                m_numSpells = copies,
            };
        }
    }

    private static WizStatisticEffect ScaleEffect(PetBoostPlayerStatEffectTemplate t, float basis) => new() {
        m_effectNameID = Imcodec.Cryptography.StringHash.Compute(t.m_effectName),
        m_damageBonusPercent = t.m_damageBonusPercent * basis,
        m_damageBonusFlat = t.m_damageBonusFlat * basis,
        m_accuracyBonusPercent = t.m_accuracyBonusPercent * basis,
        m_armorPiercingBonusPercent = t.m_armorPiercingBonusPercent * basis,
        m_damageReducePercent = t.m_damageReducePercent * basis,
        m_damageReduceFlat = t.m_damageReduceFlat * basis,
        m_healBonusPercent = t.m_healBonusPercent * basis,
        m_healIncBonusPercent = t.m_healIncBonusPercent * basis,
        m_hitPointBonus = MathF.Round(t.m_hitPointBonus * basis),
        m_manaBonus = MathF.Round(t.m_manaBonus * basis),
        m_powerPipBonusPercent = t.m_powerPipBonusPercent * basis,
        m_criticalHitRating = t.m_criticalHitRating * basis,
        m_blockRating = t.m_blockRating * basis,
        m_stunResistancePercent = t.m_stunResistancePercent * basis,
        m_pipConversionRating = t.m_pipConversionRating * basis,
        m_fishingLuckBonusPercent = t.m_fishingLuckBonusPercent * basis,
    };

    private static string StatName(PetBoostPlayerStatEffectTemplate t) {
        string category = t.m_effectCategory;
        if (string.IsNullOrEmpty(category)) {
            // One talent in the game files has no category; its effect name still says what it is.
            category = ((string) t.m_effectName ?? "").Replace("PetTalent", "").TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        }

        // CharacterEffectHelper reads the school after "Canonical" and treats "All" as every school.
        return category is "CriticalHit" or "Block" or "PipConversion" ? $"CanonicalAll{category}" : $"Canonical{category}";
    }

    /// <summary>
    /// A few lines about a pet for the chat window.
    /// </summary>
    internal static string Describe(WizClientObjectItem pet) {
        var behavior = GetBehavior(pet);
        if (behavior is null) {
            return "That item is not a pet.";
        }

        var text = new StringBuilder();
        text.Append($"{pet.m_debugName}: {LevelName(behavior.m_level)} (level {behavior.m_level}), XP {behavior.m_XP}");
        text.Append(behavior.m_requiredXP > 0 ? $"/{behavior.m_requiredXP}. " : ". ");
        text.Append("Stats: ");
        text.Append(string.Join(", ", (behavior.m_currentStats ?? []).Select(s => $"{s.m_name} {s.m_value}/{GetStat(behavior.m_maxStats, s.m_name)}")));
        text.Append(". Talents: ");
        text.Append(TalentList(behavior.m_expressedTalents));
        text.Append(". Derby: ");
        text.Append(TalentList(behavior.m_expressedDerbyTalents));
        text.Append(". Pool: ");
        text.Append(TalentList(behavior.m_allTalents));
        text.Append('.');

        return text.ToString();
    }

    internal static string TalentLabel(PetTalentBaseTemplate talent) {
        var shown = Locale.GetEnglishName(talent.m_displayName);

        return string.IsNullOrEmpty(shown) ? talent.m_talentName : $"{shown} ({talent.m_talentName})";
    }

    private static string TalentList(List<uint> ids) {
        if (ids is null || ids.Count == 0) {
            return "none";
        }

        return string.Join(", ", ids.Select(id => PetData.GetTalent(id) is { } t ? TalentLabel(t) : id.ToString()));
    }

    internal static uint RequiredXp(PetItemBehaviorTemplate template, byte level)
        => template?.m_Levels?.FirstOrDefault(l => l?.m_level == level)?.m_requiredXP ?? 0;

    private static void RecalculateMaxStats(ClientPetItemBehavior behavior, PetItemBehaviorTemplate template) {
        if (template is null) {
            return;
        }

        // Selfish talents raise the maximum: the pet's own and the one of a jewel in its socket (Mighty is +65
        // Strength). Taking the jewel out puts the maximum back (Justin, 2026-10-20).
        var pet = s_pets.TryGetValue(behavior, out var found) ? found : null;
        var bonus = (behavior.m_expressedTalents ?? [])
            .Select(PetData.GetTalent)
            .Concat(pet is null ? [] : JewelSockets.GetPetJewelTalents(pet).Select(PetData.GetTalent))
            .OfType<PetTalentTemplate>()
            .SelectMany(t => t.m_maxStatList ?? [])
            .Where(s => s is not null)
            .ToList();
        var petId = pet?.m_globalID.Full ?? 0UL;
        behavior.m_maxStats = [.. StatNames(template).Select(name => NewStat(name, Math.Max(
            BaseMaxStat(template, name) + bonus.Where(b => SameStat(b.m_name, name)).Sum(b => b.m_value),
            petId == 0 ? 0 : CappedRaise(petId, name))))];

        // Selfish talents raise only the maximum; the stat itself still has to be trained up (Justin, 2026-10-20).
        // 0.10.17 added their points to the stat too, so those points are taken back once.
        foreach (var stat in behavior.m_currentStats ?? []) {
            if (petId != 0 && PetSelfishStats.Get(petId, stat.m_name) is var added and not 0) {
                stat.m_value = Math.Max(0, stat.m_value - added);
                PetSelfishStats.Set(petId, stat.m_name, 0);
            }

            stat.m_value = Math.Min(stat.m_value, GetStat(behavior.m_maxStats, stat.m_name));
        }
    }

    // A raised maximum never goes past the server's maximum ([Pets] MaxStrength and the others): only selfish talents
    // do (Justin, 2026-10-20). 0.10.58 hatching saved raises above it, which this also undoes.
    private static int CappedRaise(ulong petId, string name) {
        var raise = PetMaxRaises.Get(petId, name);
        var most = PetRules.MaxStat(name);

        return most > 0 ? Math.Min(raise, most) : raise;
    }

    // [Pets] MaxStrength and the others (260 by default); 0 uses the pet's own maximum from the game files.
    private static int BaseMaxStat(PetItemBehaviorTemplate template, string name) {
        var configured = PetRules.MaxStat(name);

        return configured > 0 ? configured : GetStat(template.m_maxStats, name);
    }

    private static List<uint> TalentPool(List<ByteString> guaranteed, List<ByteString> talents)
        => [.. (guaranteed ?? []).Concat(talents ?? [])
            .Select(t => (string) t)
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(PetData.TalentId)
            .Distinct()];

    private static IEnumerable<string> StatNames(PetItemBehaviorTemplate template) {
        var names = template.m_maxStats?.Where(s => s is not null).Select(s => (string) s.m_name).ToList();

        return names is { Count: > 0 } ? names : s_statNames;
    }

    private static PetStat NewStat(string name, int value) => new() {
        m_name = name,
        m_statID = Imcodec.Cryptography.StringHash.Compute(name),
        m_value = value,
    };

    internal static int GetStat(List<PetStat> stats, string name)
        => stats?.FirstOrDefault(s => SameStat(s?.m_name, name))?.m_value ?? 0;

    private static bool SameStat(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string StatSummary(List<PetStat> stats)
        => string.Join(",", (stats ?? []).Select(s => $"{s?.m_name}={s?.m_value}"));

}
