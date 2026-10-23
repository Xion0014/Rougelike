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

using System;
using System.Linq;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Combat;

/// <summary>
/// Checks the requirements of a conditional spell effect (ConditionalSpellEffect): "if the target is Fire", "if the
/// target has a ward", "if the caster has 5 pips" and so on. Arch Master, Chromatic Shield and the Fusion spells are
/// built from these. Before this nothing read them, so those spells did nothing on the server.
/// </summary>
internal static class CombatRequirements {

    /// <summary>
    /// True when the requirement holds for this caster and target. A requirement this server does not know counts as
    /// met, so a spell never does less than before because of it; it is logged once per kind.
    /// </summary>
    internal static bool Met(Requirement requirement, CombatDuelSubCircle caster, CombatDuelSubCircle target) {
        if (requirement is null) {
            return true;
        }

        var met = requirement switch {
            RequirementList list => ListMet(list, caster, target),
            ReqIsSchool school => IsSchool(Who(school.m_targetType, caster, target), (string) school.m_magicSchoolName),
            ReqIsSecondarySchool => true,
            ReqHangingWard ward => InRange(CountHanging(Who(ward.m_targetType, caster, target), IsWard, ward.m_disposition),
                                           ward.m_minCount, ward.m_maxCount),
            ReqHangingCharm charm => InRange(CountHanging(Who(charm.m_targetType, caster, target), IsCharm, charm.m_disposition),
                                             charm.m_minCount, charm.m_maxCount),
            ReqHangingOverTime overTime => InRange(CountHanging(Who(overTime.m_targetType, caster, target), IsOverTime,
                                                                overTime.m_disposition), overTime.m_minCount, overTime.m_maxCount),
            ReqHangingAura aura => HasAura(Who(aura.m_targetType, caster, target), aura, caster),
            ReqHangingEffectType effectType => HangingOfType(Who(effectType.m_targetType, caster, target), effectType),
            ReqCombatHealth health => HealthIn(Who(health.m_targetType, caster, target), health.m_fMinPercent, health.m_fMaxPercent),
            ReqCombatStatus status => HasStatus(Who(status.m_targetType, caster, target), status.m_status),
            ReqPipCount pips => InRange(PipTotal(Who(pips.m_targetType, caster, target)), pips.m_minPips, pips.m_maxPips),
            ReqShadowPipCount shadow => InRange(Who(shadow.m_targetType, caster, target)?.CombatParticipant?.m_pipCount?.m_shadowPips ?? 0,
                                                shadow.m_minPips, shadow.m_maxPips),
            ReqPvPCombat => caster?._duelActor?.Duel?.m_bPVP == true,
            ReqMinion minion => MinionMet(minion, caster, target),
            _ => Unknown(requirement),
        };

        return requirement.m_applyNOT ? !met : met;
    }

    // Each requirement joins the ones before it with its own operator (AND or OR).
    private static bool ListMet(RequirementList list, CombatDuelSubCircle caster, CombatDuelSubCircle target) {
        var requirements = (list.m_requirements ?? []).Where(r => r is not null).ToList();
        if (requirements.Count == 0) {
            return true;
        }

        var result = Met(requirements[0], caster, target);
        foreach (var requirement in requirements.Skip(1)) {
            var met = Met(requirement, caster, target);
            result = requirement.m_operator == Operator.ROP_OR ? result || met : result && met;
        }

        return result;
    }

    private static CombatDuelSubCircle Who(RequirementTarget who, CombatDuelSubCircle caster, CombatDuelSubCircle target)
        => who == RequirementTarget.RT_Caster ? caster : target ?? caster;

    // The game's data writes "no upper limit" as a max of 100; a max below the min (0) is read the same way.
    private static bool InRange(int count, int min, int max)
        => count >= min && (max < min || count <= max);

    internal static uint SchoolOf(CombatDuelSubCircle who) {
        if (who?.CombatParticipant is { m_primaryMagicSchoolID: not 0 } participant) {
            return (uint) participant.m_primaryMagicSchoolID;
        }

        return who?.ParticipantGameStats?.m_schoolID ?? 0;
    }

    private static bool IsSchool(CombatDuelSubCircle who, string school)
        => who is not null && !string.IsNullOrEmpty(school) && SchoolOf(who) == StringHash.Compute(school);

    internal static bool IsWard(SpellEffect e) => e.m_effectType is kSpellEffects.kModifyIncomingDamage
        or kSpellEffects.kAbsorbDamage or kSpellEffects.kModifyIncomingHeal or kSpellEffects.kModifyIncomingDamageType
        or kSpellEffects.kModifyIncomingArmorPiercing or kSpellEffects.kModifyIncomingDamageFlat
        or kSpellEffects.kModifyIncomingHealFlat or kSpellEffects.kStunBlock or kSpellEffects.kMaximumIncomingDamage
        && e.m_numRounds <= 0;

    internal static bool IsCharm(SpellEffect e) => e.m_effectType is kSpellEffects.kModifyOutgoingDamage
        or kSpellEffects.kModifyOutgoingHeal or kSpellEffects.kModifyAccuracy or kSpellEffects.kModifyOutgoingArmorPiercing
        or kSpellEffects.kModifyOutgoingDamageFlat or kSpellEffects.kModifyOutgoingHealFlat or kSpellEffects.kCritBoost
        or kSpellEffects.kCritBlock or kSpellEffects.kDispel
        && e.m_numRounds <= 0;

    // Damage and heal over time, and bombs (deferred damage), which also count down their rounds.
    internal static bool IsOverTime(SpellEffect e) => e.m_effectType is kSpellEffects.kDamageOverTime or kSpellEffects.kHealOverTime
        or kSpellEffects.kDeferredDamage;

    // A charm, ward or aura is good (beneficial) or bad (harmful) by its disposition, or else by its sign.
    internal static bool IsBeneficial(SpellEffect e) {
        if (e.m_disposition != kHangingDisposition.kBoth) {
            return e.m_disposition == kHangingDisposition.kBeneficial;
        }

        return e.m_effectType switch {
            kSpellEffects.kHealOverTime or kSpellEffects.kAbsorbDamage or kSpellEffects.kStunBlock
                or kSpellEffects.kCritBoost or kSpellEffects.kCritBlock => true,
            kSpellEffects.kDamageOverTime or kSpellEffects.kDeferredDamage or kSpellEffects.kDispel => false,
            // A shield lowers incoming damage; a trap raises it.
            kSpellEffects.kModifyIncomingDamage or kSpellEffects.kModifyIncomingDamageFlat
                or kSpellEffects.kModifyIncomingArmorPiercing => e.m_effectParam < 0,
            kSpellEffects.kModifyIncomingDamageType => false,
            _ => e.m_effectParam >= 0,
        };
    }

    private static bool Disposed(SpellEffect e, kHangingDisposition disposition) => disposition switch {
        kHangingDisposition.kBeneficial => IsBeneficial(e),
        kHangingDisposition.kHarmful => !IsBeneficial(e),
        _ => true,
    };

    private static int CountHanging(CombatDuelSubCircle who, Func<SpellEffect, bool> kind, kHangingDisposition disposition)
        => who?._hangingEffects?.Count(e => e is not null && kind(e) && Disposed(e, disposition)) ?? 0;

    internal static bool IsAura(SpellEffect e) => e is not null && e.m_numRounds > 0 && !IsOverTime(e);

    private static bool HasAura(CombatDuelSubCircle who, ReqHangingAura req, CombatDuelSubCircle caster) {
        if (req.m_globalEffect) {
            var bubbles = caster?._duelActor?.Duel?.m_duelModifier?.m_battlefieldEffects ?? [];

            return bubbles.Any(e => e is not null && (req.m_anyType || e.m_effectType == req.m_effectType) && Disposed(e, req.m_disposition));
        }

        return who?._hangingEffects?.Any(e => IsAura(e) && (req.m_anyType || e.m_effectType == req.m_effectType)
                                              && Disposed(e, req.m_disposition)) == true;
    }

    private static bool HangingOfType(CombatDuelSubCircle who, ReqHangingEffectType req) {
        var count = who?._hangingEffects?.Count(e => e is not null && e.m_effectType == req.m_effectType && Disposed(e, req.m_disposition)
            && (req.m_param_low == 0 && req.m_param_high == 0
                || e.m_effectParam >= req.m_param_low && (req.m_param_high <= 0 || e.m_effectParam <= req.m_param_high))) ?? 0;

        return InRange(count, Math.Max(1, req.m_min_count), req.m_max_count);
    }

    // m_fMinPercent and m_fMaxPercent are fractions of the maximum health (0.5 = half).
    private static bool HealthIn(CombatDuelSubCircle who, float min, float max) {
        var stats = who?.ParticipantGameStats;
        if (stats is null || stats.m_baseHitpoints <= 0) {
            return false;
        }

        var fraction = stats.m_currentHitpoints / (float) stats.m_baseHitpoints;
        if (min > 1f || max > 1f) {
            fraction *= 100f;
        }

        return fraction >= min && (max <= 0f || fraction <= max);
    }

    private static bool HasStatus(CombatDuelSubCircle who, kStatusEffect status) => status switch {
        kStatusEffect.kStunned => who?.CombatParticipant?.m_stunned > 0,
        kStatusEffect.kConfused => who?.CombatParticipant?.m_confused > 0,
        _ => false,
    };

    private static int PipTotal(CombatDuelSubCircle who) {
        if (who?.CombatParticipant?.m_pipCount is not { } pips) {
            return 0;
        }

        return pips.m_genericPips + pips.m_powerPips * 2;
    }

    private static bool MinionMet(ReqMinion req, CombatDuelSubCircle caster, CombatDuelSubCircle target) {
        var who = Who(req.m_targetType, caster, target);
        if (who is null) {
            return false;
        }

        var circles = who._duelActor?.ActiveSubCircles ?? [];
        bool HasMinion(CombatDuelSubCircle owner)
            => circles.Any(c => c.IsSummonedMinion && c.IsAlive && c.CombatParticipant?.m_minionSubCircle == owner.SlotIndex);

        // MinionType reuses its numbers: 0 is MT_NONE and Is_Minion, 1 is MT_ACCOMPANY and Has_Minion, and so on.
        return (int) req.m_minionType switch {
            0 => who.IsSummonedMinion,
            1 => HasMinion(who),
            2 => circles.Any(c => c.IsSummonedMinion && c.IsAlive && c.OccupiedTeam == who.OccupiedTeam),
            3 => circles.Any(c => c.IsSummonedMinion && c.IsAlive && c.OccupiedTeam != who.OccupiedTeam),
            _ => circles.Any(c => c.IsSummonedMinion && c.IsAlive),
        };
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> s_unknown = new();

    private static bool Unknown(Requirement requirement) {
        var name = requirement.GetType().Name;
        if (s_unknown.TryAdd(name, true)) {
            Logger.Warning("Spell requirement {0} is not supported; it counts as met.", Logger.Args(name));
        }

        return true;
    }

}
