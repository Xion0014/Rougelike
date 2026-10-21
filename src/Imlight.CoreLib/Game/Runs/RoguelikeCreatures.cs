/*
 * Imlight
 * Copyright (C) 2025 Revive101
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
 */

/*
 * Different creatures on every run's streets.
 *
 * A run's street is a private copy of the zone, owned by the wizard. Every creature that spawns there (wisps
 * aside) is swapped for one of a similar level, picked by the run's seed and tier, so each run and each tier
 * meets different creatures. A swapped creature still counts for quests as the one it replaced, and quest goals
 * show the replacement's name ("Defeat Lost Soul" becomes "Defeat <whatever replaced it>").
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Randomization;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Runs;

internal static class RoguelikeCreatures {

    internal static readonly bool RandomStreetCreatures
        = ConfigurationManager.GetValue("Roguelike.RandomStreetCreatures", true);

    private sealed class RunInfo {

        public ulong Seed;
        public int Tier;
        public string Street;
        public RoguelikeRun Run;

        // The creature that replaced each quest adjective ("Lost_Soul") on this run's street, for quest text.
        public readonly ConcurrentDictionary<string, ulong> ByAdjective = new(StringComparer.OrdinalIgnoreCase);

    }

    private static readonly ConcurrentDictionary<ulong, RunInfo> s_runs = new();

    // The creatures that spawn in each zone (by zone path), bosses and wisps left out.
    private static readonly ConcurrentDictionary<string, ulong[]> s_rosters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// "Zone" (default) swaps a street's creatures among the ones that spawn in that zone, which always show.
    /// "Any" swaps in any creature of a similar level from the whole game, but the game only loads the models of
    /// a zone's own creatures, so many of those fight invisibly.
    /// </summary>
    private static readonly bool s_anyCreature = !string.Equals(
        ConfigurationManager.GetValue("Roguelike.StreetCreaturePool", "Any"), "Zone", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// If true, every spawn on a run's street rolls a fresh random creature. If false, each kind of creature is
    /// replaced by the same random one for the whole tier, and quest goals name it.
    /// </summary>
    private static readonly bool s_everySpawn = ConfigurationManager.GetValue("Roguelike.StreetCreaturesChangeEverySpawn", false);

    /// <summary>Records the creatures that spawn in a zone, as it loads.</summary>
    internal static void RegisterRoster(string zonePath, IEnumerable<ulong> templateIds) {
        if (!Active || string.IsNullOrEmpty(zonePath) || templateIds is null || s_rosters.ContainsKey(zonePath)) {
            return;
        }

        var roster = templateIds
            .Distinct()
            .Where(id => CoreObjectFactory.GetCoreTemplate(id) is GameObjectTemplate template
                         && template.m_behaviors.OfType<NPCBehaviorTemplate>().FirstOrDefault() is { m_bossMob: false }
                         && template.m_behaviors.OfType<DuelistBehaviorTemplate>().Any())
            .OrderBy(id => id)
            .ToArray();
        s_rosters.TryAdd(zonePath, roster);
    }

    /// <summary>True when a run's street creatures are swapped.</summary>
    private static bool Active => RoguelikeConfig.Enabled && RandomStreetCreatures;

    /// <summary>
    /// Records where the wizard's run is, so their private street spawns the run's creatures. Called whenever the
    /// run starts, moves to a new tier, or the wizard arrives somewhere.
    /// </summary>
    internal static void Register(Wizard wizard) {
        var run = wizard?.Roguelike;
        if (!Active || run is null) {
            return;
        }

        var street = RoguelikeConfig.ResolveZone(RoguelikeConfig.GetTier(run.Tier)?.Street);
        var info = s_runs.GetOrAdd(wizard.CharId, _ => new RunInfo());

        // Each reroll (a fresh login onto the street) gives the street a new set of creatures.
        var seed = run.Rerolls == 0 ? run.Seed : SeededShuffle.Mix(run.Seed ^ SeededShuffle.Mix((ulong) run.Rerolls));
        if (info.Seed != seed || info.Tier != run.Tier) {
            info.ByAdjective.Clear();
        }

        info.Seed = seed;
        info.Tier = run.Tier;
        info.Street = street;
        info.Run = run;
    }

    /// <summary>
    /// [Roguelike] RandomDungeonCreatures: while a tier's street is on, the dungeons and rooms its quests lead
    /// into (Krokotopia's Hall of Champions, Grand Arena...) swap their creatures too, not just the street.
    /// </summary>
    private static readonly bool s_streetDungeons = ConfigurationManager.GetValue("Roguelike.RandomDungeonCreatures", true);

    /// <summary>True when creatures in this zone are swapped for this run: its street, or (with
    /// RandomDungeonCreatures) any place the wizard goes to while the street phase is on.</summary>
    private static bool IsRunZone(RunInfo info, string zonePath)
        => string.Equals(info.Street, zonePath, StringComparison.OrdinalIgnoreCase)
           || (s_streetDungeons && info.Run is { Phase: RoguelikePhase.Street } && !RoguelikeConfig.IsBlockedZone(zonePath));

    // Characters picked on the character screen whose game attach has not happened yet.
    private static readonly ConcurrentDictionary<ulong, DateTime> s_freshLogins = new();

    /// <summary>Notes that a character was picked on the character screen (a real login, not a zone change).</summary>
    internal static void MarkLogin(ulong charId) {
        if (Active) {
            s_freshLogins[charId] = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// On a fresh login onto the run's street, rolls new creatures for it. The private street is kept while it
    /// sits idle, so without this the wizard would log back in to the same creatures (and the same leftovers).
    /// </summary>
    /// <returns>True when the street's old copy must be dropped, so it is built again with the new creatures.</returns>
    internal static bool RerollOnLogin(Wizard wizard, string zonePath) {
        if (wizard is null || !s_freshLogins.TryRemove(wizard.CharId, out var pickedAt)
            || DateTime.UtcNow - pickedAt > TimeSpan.FromMinutes(2)) {
            return false;
        }

        var run = wizard.Roguelike;
        if (!Active || run is null || run.Phase != RoguelikePhase.Street) {
            return false;
        }

        var street = RoguelikeConfig.ResolveZone(RoguelikeConfig.GetTier(run.Tier)?.Street);
        if (!string.Equals(street, zonePath, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        run.Rerolls++;
        WizardCollection.UpdateCharacterRoguelike(wizard);
        Register(wizard);
        Logger.Information("[Roguelike] {0} logged in on the run street {1}: new creatures (reroll {2}).",
            Logger.Args(wizard.CharId, zonePath, run.Rerolls));

        return true;
    }

    /// <summary>
    /// The creature that spawns in place of <paramref name="templateId"/> in a zone, when the zone is the private
    /// street of its owner's run.
    /// </summary>
    /// <returns>True when the creature is swapped.</returns>
    internal static bool TryMap(ulong ownerCharId, string zonePath, ulong templateId, out ulong mapped) {
        mapped = templateId;
        if (!Active || ownerCharId == 0 || string.IsNullOrEmpty(zonePath)) {
            return false;
        }

        if (!s_runs.TryGetValue(ownerCharId, out var info)) {
            // The zone can load before the wizard's session registers the run (logging in on the street).
            Register(WizardCollection.GetCharacterUnloaded(ownerCharId));
            if (!s_runs.TryGetValue(ownerCharId, out info)) {
                return false;
            }
        }

        // The tier's boss zone may put another boss in the boss's place (TierNBossSwap).
        if (info.Run is { Phase: RoguelikePhase.Boss } bossRun && IsBoss(templateId)
            && RoguelikeConfig.GetTier(bossRun.Tier) is { BossSwap: > 0 } tier
            && string.Equals(RoguelikeConfig.ResolveZone(tier.Boss), zonePath, StringComparison.OrdinalIgnoreCase)
            && CoreObjectFactory.GetCoreTemplate(tier.BossSwap) is GameObjectTemplate) {
            mapped = tier.BossSwap;
            if (tier.BossSwapOwnStats) {
                s_ownStats.TryAdd(mapped, 0);
            }

            return mapped != templateId;
        }

        if (!IsRunZone(info, zonePath)) {
            return false;
        }

        // Bosses keep their place.
        if (IsBoss(templateId)) {
            return false;
        }

        var seed = SeededShuffle.Mix(info.Seed ^ SeededShuffle.Mix((ulong) info.Tier) ^ SeededShuffle.Fnv1a("street"));
        if (s_anyCreature) {
            mapped = s_everySpawn ? Randomizer.PickCreature(templateId) : Randomizer.MapCreatureSeeded(templateId, seed);
        }
        else if (s_rosters.TryGetValue(zonePath, out var roster) && roster.Length > 1
                 && Array.BinarySearch(roster, templateId) >= 0) {
            // One of the zone's own creatures, picked by the run's seed for each kind the spawn point asks for.
            mapped = roster[(int) (SeededShuffle.Mix(seed ^ templateId) % (ulong) roster.Length)];
        }

        if (mapped == templateId) {
            return false;
        }

        // Remember which creature now stands for each of the original's adjectives, for quest goal text.
        if (CoreObjectFactory.GetCoreTemplate(templateId) is GameObjectTemplate original) {
            foreach (string adjective in original.m_adjectiveList) {
                if (!string.IsNullOrEmpty(adjective)) {
                    info.ByAdjective.TryAdd(adjective, mapped);
                }
            }
        }

        return true;
    }

    // The creature each run-swapped spawn stands in for, by the spawn's global id, so it fights with its stats.
    private static readonly ConcurrentDictionary<ulong, ulong> s_statSources = new();
    private const int MAX_STAT_SOURCES = 50_000;

    /// <summary>
    /// If true (default), a creature a run swaps in fights with the health, level, school and deck of the creature
    /// it stands in for, so a street is as hard as it normally is whatever shows up.
    /// </summary>
    private static readonly bool s_keepStats = ConfigurationManager.GetValue("Roguelike.SwappedCreaturesKeepStats", true);

    // Swapped-in bosses that fight as themselves (TierNBossSwapStats = Own).
    private static readonly ConcurrentDictionary<ulong, byte> s_ownStats = new();

    /// <summary>Remembers which creature a run-swapped spawn stands in for.</summary>
    internal static void RememberStatSource(ulong spawnGlobalId, ulong originalTemplateId, ulong spawnedTemplateId = 0) {
        if (!s_keepStats || s_ownStats.ContainsKey(spawnedTemplateId)) {
            return;
        }

        // Spawns are not forgotten when they leave, so the list is emptied now and then.
        if (s_statSources.Count >= MAX_STAT_SOURCES) {
            s_statSources.Clear();
        }

        s_statSources[spawnGlobalId] = originalTemplateId;
    }

    /// <summary>True when a run swapped this creature in (so it fights with a random deck, RandomCreatureDecks).</summary>
    internal static bool IsRunSwapped(Zone.Core.ZoneEntity entity)
        => entity.ActiveGameObject?.m_globalID.Full is { } id && id != 0 && s_statSources.ContainsKey(id);

    /// <summary>
    /// The template a creature's battle stats and deck come from: the creature it stands in for when a run swapped
    /// it in, else its own.
    /// </summary>
    internal static CoreTemplate StatTemplateFor(Zone.Core.ZoneEntity entity) {
        var own = entity.Template;
        var globalId = entity.ActiveGameObject?.m_globalID.Full ?? 0;
        if (globalId == 0 || !s_statSources.TryGetValue(globalId, out var originalId)) {
            return own;
        }

        return CoreObjectFactory.GetCoreTemplate(originalId) is GameObjectTemplate original
               && original.m_behaviors.OfType<NPCBehaviorTemplate>().Any()
            ? original
            : own;
    }

    /// <summary>
    /// How many creatures of a won battle count for a "defeat" goal regardless of which creature the goal names:
    /// the battle's non-boss creatures, when the wizard is on their run's street and the goal is for that zone.
    /// Zero otherwise, leaving the normal adjective match to decide.
    /// </summary>
    internal static int WildcardKills(Wizard wizard, string goalZone, IEnumerable<ulong> defeatedTemplateIds) {
        var run = wizard?.Roguelike;
        if (!Active || run is null || run.Phase != RoguelikePhase.Street || defeatedTemplateIds is null) {
            return 0;
        }

        if (!s_runs.TryGetValue(wizard.CharId, out var info) || string.IsNullOrEmpty(wizard.Zone)
            || !IsRunZone(info, wizard.Zone)) {
            return 0;
        }

        // A goal for somewhere else still needs its own creatures; one for here takes any of them.
        if (!string.IsNullOrEmpty(goalZone) && !string.Equals(goalZone, wizard.Zone, StringComparison.OrdinalIgnoreCase)) {
            return 0;
        }

        return defeatedTemplateIds.Count(id => !IsBoss(id));
    }

    private static bool IsBoss(ulong templateId)
        => CoreObjectFactory.GetCoreTemplate(templateId) is GameObjectTemplate template
           && template.m_behaviors.OfType<NPCBehaviorTemplate>().FirstOrDefault() is { m_bossMob: true };

    /// <summary>
    /// The display name key of the creature that replaced a bounty goal's creature on the wizard's run, or null.
    /// </summary>
    internal static string ReplacementNameKey(ulong charId, IEnumerable<string> goalAdjectives) {
        if (!Active || goalAdjectives is null || !s_runs.TryGetValue(charId, out var info)) {
            return null;
        }

        foreach (var adjective in goalAdjectives) {
            if (adjective is not null && info.ByAdjective.TryGetValue(adjective, out var replacement)
                && CoreObjectFactory.GetCoreTemplate(replacement) is GameObjectTemplate template) {
                string key = template.m_displayName;
                if (!string.IsNullOrEmpty(key)) {
                    return key;
                }
            }
        }

        return null;
    }

}
