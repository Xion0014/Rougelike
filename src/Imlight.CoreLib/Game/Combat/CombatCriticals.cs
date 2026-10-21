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
 *
 * ========================================================================
 * COMBAT CRITICAL STRIKE SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * Owns the critical hit / block stat lookups, the level-scaled rating to
 * chance conversion, and the single roll that decides whether a cast crits.
 * 
 * USAGE EXAMPLE:
 * Called from CombatActionResolver once per cast; the resulting multiplier
 * threads into CombatEffectApplicator through the action resolver.
 * 
 * NOTE:
 * Modern model: the crit chance depends on BOTH ratings (crit vs
 * crit + K * block), so it cannot be computed from the attacker's rating
 * alone. One roll decides the outcome; there is no separate block roll.
 * The landed multiplier varies between 1 and 2 by the crit/block ratio.
 * K(level) is the client's level-scaled crit divisor (100 + 3 * level in
 * WizStatisticEffectConfig), clamped into the 200-400 band. Heals roll the
 * rating against K alone, so they can never be blocked, and land the full
 * x2. Vengeance and Conviction add to their side of the roll, then are
 * consumed. Crits are gated by the config's level threshold.
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using System;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Effects;

namespace Imlight.CoreLib.Game.Combat;

internal static class CombatCriticals {

    private const float MAX_CHANCE = 0.95f; // Client has a soft cap.
    private const float RATING_K_MIN = 200f;
    private const float RATING_K_MAX = 400f;
    private const float RATING_K_SLOPE = 1.25f;
    private const int RATING_K_BASE_LEVEL = 10;

    internal static float RatingToChance(float rating, int level) {
        if (rating <= 0f) {
            return 0f;
        }

        return Math.Min(MAX_CHANCE, rating / (rating + GetRatingK(level)));
    }

    // ---------------------------------------------------------------------
    // Roguelike critical hits: linear chances, double damage, a separate block roll
    // ---------------------------------------------------------------------

    /// <summary>
    /// [Roguelike] SimpleCriticals: on a run, critical hits are simple. A wizard's chance is their critical rating
    /// over 1000 (100 = 10%, 1000 = 100%); a creature's is CreatureCritByTier for the run's tier. Only attacks crit,
    /// for double damage. A critical can then be blocked: a wizard's block rating over 1000, a creature's
    /// CreatureBlockByTier. A blocked critical does normal damage.
    /// </summary>
    private static readonly bool s_simpleCrits = Imlight.Common.ConfigurationManager.GetValue("Roguelike.SimpleCriticals", true);
    private static readonly int[] s_creatureCrit = ParsePercents(
        Imlight.Common.ConfigurationManager.GetValue("Roguelike.CreatureCritByTier", "5, 10, 15, 20, 25, 30, 35, 40, 45, 50"));
    private static readonly int[] s_creatureBlock = ParsePercents(
        Imlight.Common.ConfigurationManager.GetValue("Roguelike.CreatureBlockByTier", "5, 10, 15, 20, 25, 30, 35, 40, 45, 50"));

    private static int[] ParsePercents(string list)
        => [.. (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p.Trim(), out var v) ? Math.Clamp(v, 0, 100) : 0)];

    /// <summary>The run whose rules a battle between these two follows (either side's wizard on a run).</summary>
    private static Imlight.CoreLib.WizardData.Models.Player.RoguelikeRun RunOf(CombatDuelSubCircle a, CombatDuelSubCircle b)
        => (a?._wizard?.Roguelike ?? b?._wizard?.Roguelike) is { } run && Imlight.CoreLib.Game.Runs.RoguelikeConfig.Enabled
            ? run : null;

    /// <summary>True when this cast follows the run's simple critical rules.</summary>
    internal static bool UsesSimpleCriticals(CombatDuelSubCircle caster, CombatDuelSubCircle target)
        => s_simpleCrits && RunOf(caster, target) is not null;

    private static float TierPercent(int[] table, int tier)
        => table.Length == 0 ? 0f : table[Math.Clamp(tier, 1, table.Length) - 1] / 100f;

    /// <summary>
    /// Rolls a simple critical for an attack. Returns (landed, blocked): a landed, unblocked critical doubles the damage.
    /// </summary>
    internal static (bool Landed, bool Blocked) RollSimpleCritical(CombatDuelSubCircle caster, CombatDuelSubCircle target, string school) {
        var run = RunOf(caster, target);
        if (run is null) {
            return (false, false);
        }

        var critChance = caster._wizard is not null
            ? GetCriticalRating(caster, school) / 1000f
            : TierPercent(s_creatureCrit, run.Tier);
        var landed = Rolls(Math.Clamp(critChance, 0f, 1f));
        var blockChance = target._wizard is not null
            ? GetBlockRating(target, school) / 1000f
            : TierPercent(s_creatureBlock, run.Tier);
        var blocked = landed && Rolls(Math.Clamp(blockChance, 0f, 1f));

        Logger.Information("[Roguelike] Critical roll: {0} ({1} school) {2:P0} to crit, target {3:P0} to block: {4}.",
            Logger.Args(caster._wizard is not null ? "wizard" : "creature", school, critChance, blockChance,
                        !landed ? "no crit" : blocked ? "crit, blocked" : "CRIT x2"));

        return (landed, blocked);
    }

    internal static bool RollsCritical(CombatDuelSubCircle caster, CombatDuelSubCircle target, string school, bool isHeal) {
        // One combined roll: the target's block is part of the crit chance, so a
        // separate block roll does not exist. Heals have no block side; they roll
        // the rating against K alone and can never be blocked.
        var level = GetLevel(caster);
        if (level < WizStatisticEffectConfigLoader.GetCriticalHitLevelThreshold()) {
            return false;
        }

        var crit = GetCriticalRating(caster, school);
        var block = isHeal ? 1f : GetBlockRating(target, school);
        var chance = Math.Min(MAX_CHANCE, crit / (crit + GetRatingK(level) * block));

        // Vengeance and Conviction modify only their side of the roll, then are
        // consumed. A heal roll has no block side, so Conviction does not apply.
        chance += GetCritBoostPercent(caster);
        if (!isHeal) {
            chance -= GetCritBlockPercent(target);
        }

        chance = Math.Clamp(chance, 0f, MAX_CHANCE);

        return Rolls(chance);
    }

    internal static float GetCritMultiplier(CombatDuelSubCircle caster, CombatDuelSubCircle target, string school, bool isHeal) {
        // The landed crit multiplier, from the crit/block ratio:
        // 2 - (3 * block) / (crit + 3 * block). Ranges from just above 1
        // (block near crit) to 2 (crit far above block). Heals are never
        // blocked, so they always land the full x2.
        var crit = GetCriticalRating(caster, school);
        var block = isHeal ? 0f : GetBlockRating(target, school);

        var denominator = crit + 3f * block;
        if (denominator <= 0f) {
            return 1f;
        }

        return 2f - 3f * block / denominator;
    }

    private static float GetCritBoostPercent(CombatDuelSubCircle caster) {
        var boost = caster._hangingEffects.FirstOrDefault(x => x.m_effectType == kSpellEffects.kCritBoost);
        if (boost is null) {
            return 0f;
        }

        caster._hangingEffects.Remove(boost);

        return boost.m_effectParam / 100f;
    }

    private static float GetCritBlockPercent(CombatDuelSubCircle target) {
        var block = target._hangingEffects.FirstOrDefault(x => x.m_effectType == kSpellEffects.kCritBlock);
        if (block is null) {
            return 0f;
        }

        target._hangingEffects.Remove(block);

        return block.m_effectParam / 100f;
    }

    private static float GetCriticalRating(CombatDuelSubCircle sc, string school) {
        var stats = sc.ParticipantGameStats;
        if (stats is null) {
            return 0f;
        }

        return stats.m_criticalHitRatingAll + sc.GetStatBySchool(stats.m_criticalHitRatingBySchool, school);
    }

    private static float GetBlockRating(CombatDuelSubCircle sc, string school) {
        var stats = sc.ParticipantGameStats;
        if (stats is null) {
            return 0f;
        }

        return stats.m_blockRatingAll + sc.GetStatBySchool(stats.m_blockRatingBySchool, school);
    }

    private static int GetLevel(CombatDuelSubCircle sc) => sc.ParticipantGameStats?.Level ?? 0;

    private static float GetRatingK(int level) {
        // The level-scaled K: the client's crit divisor (100 + 3 * level in the
        // shipped config), clamped into the 200-400 band. Falls back to a linear
        // curve when the config is missing.
        var divisor = WizStatisticEffectConfigLoader.GetCritDivisor(level);
        if (divisor <= 0f) {
            divisor = RATING_K_MIN + (level - RATING_K_BASE_LEVEL) * RATING_K_SLOPE;
        }

        return Math.Clamp(divisor, RATING_K_MIN, RATING_K_MAX);
    }

    private static bool Rolls(float chance) {
        if (chance <= 0f) {
            return false;
        }

        return Random.Shared.Next(0, 10000) < (int) (chance * 10000f);
    }

}
