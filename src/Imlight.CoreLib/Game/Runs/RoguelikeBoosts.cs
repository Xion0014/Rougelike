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
 * Permanent stat potions for roguelike runs.
 *
 * Winning a battle on a run has BoostChance (75%) to give the wizard a potion of a random stat, drunk on the spot:
 * a random amount of that stat, kept for the rest of the run. The totals are saved on the run and added on top of
 * the gear every time the wizard's stats are worked out. Shadow pip chance, pip conserve and Archmastery are not
 * stats the server keeps, so battles read them from here.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.Common;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Runs;

internal static class RoguelikeBoosts {

    /// <summary>The chance, 0 to 100, that a battle won on a run gives a stat potion.</summary>
    internal static readonly int BoostChance = Math.Clamp(ConfigurationManager.GetValue("Roguelike.BoostChance", 75), 0, 100);

    /// <summary>On a run, wizards fill the shadow pip gauge at any level, like NaturalShadowPips.</summary>
    internal static readonly bool RunShadowPips = ConfigurationManager.GetValue("Roguelike.ShadowPips", true);

    /// <summary>On a run, wizards have Archmastery at any level, like [Archmastery] AllLevels.</summary>
    internal static readonly bool RunArchmastery = ConfigurationManager.GetValue("Roguelike.Archmastery", true);

    internal const string Mana = "Mana";
    internal const string Health = "Health";
    internal const string Damage = "Damage";
    internal const string Resist = "Resist";
    internal const string Accuracy = "Accuracy";
    internal const string Critical = "Critical";
    internal const string Block = "Block";
    internal const string Pierce = "Pierce";
    internal const string PowerPip = "PowerPip";
    internal const string ShadowPip = "ShadowPip";
    internal const string PipConserve = "PipConserve";
    internal const string Archmastery = "Archmastery";

    private sealed record Boost(string Key, string Potion, string Unit, int Min, int Max, Action<ServerWizGameStats, int> Apply);

    // Percent stats are kept as fractions (0.05 is 5%), as battles read them.
    private static readonly Boost[] s_boosts = [
        new(Mana, "Potion of Mana", " Max Mana", 1, 20, (stats, n) => stats.m_baseMana += n),
        new(Health, "Potion of Health", " Max Health", 1, 100, (stats, n) => stats.m_baseHitpoints += n),
        new(Damage, "Potion of Might", "% Damage", 1, 5, (stats, n) => stats.m_dmgBonusPercentAll += n / 100f),
        new(Resist, "Potion of Warding", "% Resist", 1, 2, (stats, n) => stats.m_dmgReducePercentAll += n / 100f),
        new(Accuracy, "Potion of Focus", "% Accuracy", 1, 2, (stats, n) => stats.m_accBonusPercentAll += n / 100f),
        new(Critical, "Potion of Fury", " Critical Rating", 1, 100, (stats, n) => stats.m_criticalHitRatingAll += n),
        new(Block, "Potion of Guarding", " Block Rating", 1, 100, (stats, n) => stats.m_blockRatingAll += n),
        new(Pierce, "Potion of Piercing", "% Armor Piercing", 1, 2, (stats, n) => stats.m_apBonusPercentAll += n / 100f),
        new(PowerPip, "Potion of Power", "% Power Pip Chance", 1, 10, (stats, n) => stats.m_powerPipBonusPercentAll += n / 100f),
        new(ShadowPip, "Potion of Shadow", " Shadow Pip Rating", 1, 10, null),
        new(PipConserve, "Potion of Thrift", "% Pip Conserve", 1, 10, null),
        new(Archmastery, "Potion of Mastery", " Archmastery", 1, 10, null),
    ];

    /// <summary>
    /// Adds the run's potion totals to freshly worked out stats. Called after the gear, every time the stats are
    /// rebuilt.
    /// </summary>
    internal static void ApplyTo(Wizard wizard) {
        if (!RoguelikeConfig.Enabled || wizard?.Roguelike?.Boosts is not { Count: > 0 } boosts || wizard.GameStats is null) {
            return;
        }

        foreach (var boost in s_boosts) {
            if (boost.Apply is not null && boosts.TryGetValue(boost.Key, out var total) && total != 0) {
                boost.Apply(wizard.GameStats, total);
            }
        }
    }

    /// <summary>
    /// After a battle won on a run, maybe gives a stat potion: a random stat, a random amount. The stat goes up at
    /// once and is saved on the run. Returns what the wizard should be told, or null when nothing dropped.
    /// </summary>
    internal static string RollAfterBattle(Wizard wizard) {
        var run = wizard?.Roguelike;
        if (!RoguelikeConfig.Enabled || run is null || BoostChance <= 0 || Random.Shared.Next(100) >= BoostChance) {
            return null;
        }

        var boost = s_boosts[Random.Shared.Next(s_boosts.Length)];
        var amount = Random.Shared.Next(boost.Min, boost.Max + 1);

        run.Boosts ??= [];
        run.Boosts[boost.Key] = run.Boosts.GetValueOrDefault(boost.Key) + amount;

        if (boost.Apply is not null && wizard.GameStats is { } stats) {
            boost.Apply(stats, amount);

            // New health and mana can be used right away.
            if (boost.Key == Health) {
                stats.m_currentHitpoints = Math.Min(stats.m_currentHitpoints + amount, stats.m_baseHitpoints);
            }
            else if (boost.Key == Mana) {
                stats.m_currentMana = Math.Min(stats.m_currentMana + amount, stats.m_baseMana);
            }
        }

        Logger.Information("[Roguelike] {0} drank a {1}: +{2}{3} (+{4} in all).",
            Logger.Args(wizard.CharId, boost.Potion, amount, boost.Unit, run.Boosts[boost.Key]));

        return $"You found and drank a {boost.Potion}: +{amount}{boost.Unit} for the rest of your run " +
               $"(+{run.Boosts[boost.Key]}{boost.Unit} so far).";
    }

    /// <summary>One line per stat the run's potions raised, for .run.</summary>
    internal static string Describe(RoguelikeRun run) {
        if (run?.Boosts is not { Count: > 0 } boosts) {
            return "No stat potions yet.";
        }

        return "Stat potions: " + string.Join(", ", s_boosts
            .Where(boost => boosts.GetValueOrDefault(boost.Key) != 0)
            .Select(boost => $"+{boosts[boost.Key]}{boost.Unit}")) + ".";
    }

    /// <summary>The run's total for a stat battles read from here (shadow pips, pip conserve, Archmastery).</summary>
    internal static int Get(Wizard wizard, string key)
        => RoguelikeConfig.Enabled && wizard?.Roguelike?.Boosts is { } boosts ? boosts.GetValueOrDefault(key) : 0;

    /// <summary>True when the wizard is on a run.</summary>
    internal static bool OnRun(Wizard wizard) => RoguelikeConfig.Enabled && wizard?.Roguelike is not null;

    /// <summary>Rolls the run's pip conserve chance for one spell.</summary>
    internal static bool RollPipConserve(Wizard wizard) {
        var chance = Get(wizard, PipConserve);

        return chance > 0 && Random.Shared.Next(100) < chance;
    }

}
