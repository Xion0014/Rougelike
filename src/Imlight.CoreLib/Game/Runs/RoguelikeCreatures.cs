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

        // The creature that replaced each quest adjective ("Lost_Soul") on this run's street, for quest text.
        public readonly ConcurrentDictionary<string, ulong> ByAdjective = new(StringComparer.OrdinalIgnoreCase);

    }

    private static readonly ConcurrentDictionary<ulong, RunInfo> s_runs = new();

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
        if (info.Seed != run.Seed || info.Tier != run.Tier) {
            info.ByAdjective.Clear();
        }

        info.Seed = run.Seed;
        info.Tier = run.Tier;
        info.Street = street;
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

        if (!string.Equals(info.Street, zonePath, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        var seed = SeededShuffle.Mix(info.Seed ^ SeededShuffle.Mix((ulong) info.Tier) ^ SeededShuffle.Fnv1a("street"));
        mapped = Randomizer.MapCreatureSeeded(templateId, seed);
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
