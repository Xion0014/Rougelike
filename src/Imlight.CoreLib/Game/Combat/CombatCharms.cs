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
 * COMBAT OFFENSIVE MODIFIER SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * Manages damage and healing boosts (charms) that modify outgoing spell effects
 * based on magic school and effect type.
 * 
 * USAGE EXAMPLE:
 * var charms = CombatCharms.FindAppliedCharms(caster, effects);
 * int modifiedDamage = CombatCharms.GetOutgoingDamageFromCharms(charms, initialDamage);
 * 
 * NOTE:
 * Distinguishes between beneficial and harmful charms, ensuring proper
 * application order and stacking behavior for damage and healing modifiers.
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using Imcodec.ObjectProperty.TypeCache;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Game.Combat;

/// <summary>
/// Provides utility methods for handling offensive combat modifiers (charms) in combat.
/// </summary>
/// <remarks>
/// Manages the selection and application of outgoing damage and healing modifiers, determining
/// which charms affect specific spells based on the caster, target, and spell school. Handles
/// both beneficial (damage/healing boosting) and harmful (damage reducing) charms.
/// </remarks>
internal static class CombatCharms {

    /// <summary>
    /// The charms on the caster that change the spell's effects: for damage the blades, weaknesses and spears of its
    /// school (or of all schools), for a heal the heal charms. One of each spell counts, as in the game.
    /// </summary>
    /// <remarks>
    /// Charms used to carry no spell id, so "one of each spell" kept just one charm in all: a stack of blades counted
    /// as one on the server while the game counted them all (Medulla took a fraction of the damage the game showed).
    /// </remarks>
    internal static List<SpellEffect> FindAppliedCharms(CombatDuelSubCircle caster,
                                                        SpellEffect[] effects,
                                                        kHangingDisposition disposition = kHangingDisposition.kBoth) {
        if (caster?._hangingEffects is not { } hanging) {
            return [];
        }

        var appliedCharms = new List<SpellEffect>();
        foreach (var effect in effects) {
            if (effect is null) {
                continue;
            }

            var isDamageEffect = effect.m_effectType is kSpellEffects.kDamage
                                                     or kSpellEffects.kDamageOverTime
                                                     or kSpellEffects.kDamageNoCrit
                                                     or kSpellEffects.kDamagePerTotalPipPower
                                                     or kSpellEffects.kDivideDamage
                                                     or kSpellEffects.kStealHealth;
            var isHealEffect = effect.m_effectType is kSpellEffects.kHeal or kSpellEffects.kHealOverTime;

            if (isDamageEffect) {
                appliedCharms = [.. hanging.Where(x => x is not null
                    && x.m_effectType is kSpellEffects.kModifyOutgoingDamage or kSpellEffects.kModifyOutgoingDamageFlat
                                         or kSpellEffects.kModifyOutgoingArmorPiercing
                    && SchoolMatches(x.m_sDamageType, effect.m_sDamageType))];
            }
            else if (isHealEffect) {
                appliedCharms = [.. hanging.Where(x => x is not null
                    && x.m_effectType is kSpellEffects.kModifyOutgoingHeal or kSpellEffects.kModifyOutgoingHealFlat)];
            }
        }

        return OnePerSpell(appliedCharms.Where(x => Disposed(x, disposition)));
    }

    /// <summary>
    /// Every charm on the participant of a disposition, newest first: what a charm removal or steal takes.
    /// </summary>
    internal static List<SpellEffect> All(CombatDuelSubCircle target, kHangingDisposition disposition = kHangingDisposition.kBoth)
        => target?._hangingEffects is { } hanging
            ? [.. hanging.Where(x => x is not null && CombatRequirements.IsCharm(x) && Disposed(x, disposition)).Reverse()]
            : [];

    internal static bool Disposed(SpellEffect effect, kHangingDisposition disposition) => disposition switch {
        kHangingDisposition.kBeneficial => CombatRequirements.IsBeneficial(effect),
        kHangingDisposition.kHarmful => !CombatRequirements.IsBeneficial(effect),
        _ => true,
    };

    /// <summary>A charm or ward of <paramref name="modifierSchool"/> changes a spell of <paramref name="school"/>.</summary>
    internal static bool SchoolMatches(string modifierSchool, string school)
        => string.Equals(modifierSchool, "All", StringComparison.OrdinalIgnoreCase)
           || string.Equals(modifierSchool, school, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Keeps the first of each spell's modifiers. One with no spell id (a boss cheat's, say) counts as its own.
    /// </summary>
    internal static List<SpellEffect> OnePerSpell(IEnumerable<SpellEffect> effects) {
        var seen = new HashSet<uint>();
        var kept = new List<SpellEffect>();
        foreach (var effect in effects) {
            if (effect.m_spellTemplateID == 0 || seen.Add(effect.m_spellTemplateID)) {
                kept.Add(effect);
            }
        }

        return kept;
    }

    /// <summary>
    /// Takes these exact effects off the participant (by reference, one entry each). Comparing by value took off every
    /// identical copy at once: two Tower Shields went when one was used.
    /// </summary>
    internal static void RemoveExact(List<SpellEffect> hanging, IEnumerable<SpellEffect> effects) {
        if (hanging is null) {
            return;
        }

        foreach (var effect in effects.ToList()) {
            var index = hanging.FindIndex(x => ReferenceEquals(x, effect));
            if (index >= 0) {
                hanging.RemoveAt(index);
            }
        }
    }

    /// <summary>
    /// The outgoing damage once the charms are applied: each percent charm multiplies, then flat ones add. Spears and
    /// heal charms do not change damage.
    /// </summary>
    internal static int GetOutgoingDamageFromCharms(SpellEffect[] charms, int initialDamage = 0) {
        foreach (var charm in charms.Where(c => c.m_effectType == kSpellEffects.kModifyOutgoingDamage)) {
            var damageChange = charm.m_effectParam / 100.0f;
            initialDamage = (int) Math.Floor(initialDamage * (1 + damageChange));
        }

        foreach (var charm in charms.Where(c => c.m_effectType == kSpellEffects.kModifyOutgoingDamageFlat)) {
            initialDamage += charm.m_effectParam;
        }

        return Math.Max(0, initialDamage);
    }

}
