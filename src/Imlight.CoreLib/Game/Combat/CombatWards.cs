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
 * COMBAT DEFENSIVE MODIFIER SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * Manages damage resistance and absorption (wards) that modify incoming spell 
 * effects based on magic school and effect type.
 * 
 * USAGE EXAMPLE:
 * var wards = CombatWards.FindAppliedWards(target, effect);
 * int modifiedDamage = CombatWards.GetIncomingDamageFromWards(wards, initialDamage);
 * 
 * NOTE:
 * Handles different ward types including damage reductions, absorb shields,
 * and damage type conversion effects. Maintains proper application order for
 * multiple ward effects.
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Spells;

namespace Imlight.CoreLib.Game.Combat;

/// <summary>
/// Provides utility methods for handling defensive combat modifiers (wards) in combat.
/// </summary>
/// <remarks>
/// Manages the selection and application of incoming damage modifiers, determining which
/// wards affect specific incoming spells based on damage type. Handles both beneficial
/// (damage reducing, absorbing) and harmful (damage increasing) wards.
/// </remarks>
internal static class CombatWards {

    // The wards that change incoming damage: shields, traps, absorbs, prisms and flat ones.
    private static bool IsDamageWard(SpellEffect x) => x?.m_effectType is kSpellEffects.kModifyIncomingDamage
        or kSpellEffects.kAbsorbDamage or kSpellEffects.kModifyIncomingDamageType or kSpellEffects.kModifyIncomingDamageFlat;

    /// <summary>
    /// The wards on the target that change incoming damage, in the order they were put on.
    /// </summary>
    /// <remarks>
    /// Wards used to carry no spell id, so "one of each spell" kept a single ward in all: a target with six traps took
    /// the hit as if it had one.
    /// </remarks>
    internal static List<SpellEffect> FindAppliedWards(CombatDuelSubCircle target,
                                                       SpellEffect spellEffect,
                                                       kHangingDisposition disposition = kHangingDisposition.kBoth)
        => target?._hangingEffects is { } hanging
            ? [.. hanging.Where(x => IsDamageWard(x) && CombatCharms.Disposed(x, disposition))]
            : [];

    /// <summary>
    /// Every ward on the participant of a disposition, newest first: what a ward removal or steal takes. Shields are
    /// the beneficial ones, traps the harmful ones.
    /// </summary>
    internal static List<SpellEffect> All(CombatDuelSubCircle target, kHangingDisposition disposition = kHangingDisposition.kBoth)
        => target?._hangingEffects is { } hanging
            ? [.. hanging.Where(x => x is not null && CombatRequirements.IsWard(x) && CombatCharms.Disposed(x, disposition)).Reverse()]
            : [];

    /// <summary>
    /// Calculates the incoming damage from the given wards.
    /// </summary>
    internal static int GetIncomingDamageFromWards(List<SpellEffect> wards, int initialDamage = 0) {
        var noPierce = 0f;

        return GetIncomingDamageFromWards(wards, initialDamage, ref noPierce);
    }

    /// <summary>
    /// Calculates the incoming damage from the given wards, with the caster's pierce (a fraction) taken off each shield
    /// in turn. What a shield does not use goes on to the next, and what is left (in pierce) to the target's resist.
    /// </summary>
    internal static int GetIncomingDamageFromWards(List<SpellEffect> wards, int initialDamage, ref float pierce) {
        foreach (var ward in wards) {
            if (ward.m_effectType == kSpellEffects.kAbsorbDamage) {
                // Absorbs will absorb the flat damage, up to the effect param.
                var absorbedDamage = Math.Max(0, Math.Min(initialDamage, ward.m_paramPerRound));
                ward.m_paramPerRound -= absorbedDamage;
                initialDamage -= absorbedDamage;

                continue;
            }

            // Type conversion wards change the school only, never the amount.
            if (ward.m_effectType == kSpellEffects.kModifyIncomingDamageType) {
                continue;
            }

            if (ward.m_effectType == kSpellEffects.kModifyIncomingDamageFlat) {
                initialDamage = Math.Max(0, initialDamage + ward.m_effectParam);

                continue;
            }

            var damageChange = ward.m_effectParam / 100.0f;
            if (damageChange < 0 && pierce > 0) {
                var pierced = Math.Min(pierce, -damageChange);
                damageChange += pierced;
                pierce -= pierced;
            }

            initialDamage = (int) Math.Floor(initialDamage * (1 + damageChange));
        }

        return Math.Max(0, initialDamage);
    }

    /// <summary>
    /// The wards that change a hit of <paramref name="school"/>, one of each spell. A prism for that school (or for all
    /// schools) goes first and turns the hit into its school (<paramref name="finalSchool"/>); the shields and traps of
    /// the new school then count.
    /// </summary>
    /// <remarks>
    /// A prism's m_effectParam is the new school's name hash. It was read as a school index, and went off on any school.
    /// </remarks>
    internal static List<SpellEffect> GetWardsBySchool(SpellEffect[] wards, string school, out string finalSchool) {
        finalSchool = school;
        var schoolWards = new List<SpellEffect>();

        var prism = wards.FirstOrDefault(w => w.m_effectType == kSpellEffects.kModifyIncomingDamageType
                                              && CombatCharms.SchoolMatches(w.m_sDamageType, school));
        if (prism is not null) {
            var converted = MagicSchools.GetMagicSchool((uint) prism.m_effectParam)?.m_schoolName
                            ?? MagicSchools.GetMagicSchool(prism.m_effectParam)?.m_schoolName;
            if (!string.IsNullOrEmpty(converted)) {
                finalSchool = converted;
            }

            schoolWards.Add(prism);
        }

        var newSchool = finalSchool;
        schoolWards.AddRange(CombatCharms.OnePerSpell(wards.Where(w => w.m_effectType != kSpellEffects.kModifyIncomingDamageType
                                                                       && CombatCharms.SchoolMatches(w.m_sDamageType, newSchool))));

        return schoolWards;
    }

    /// <summary>
    /// Takes the used wards off the target: a shield, trap or prism goes once used, an absorb once it has nothing left.
    /// Only these exact wards go (by reference), so a second copy of the same shield stays for the next hit.
    /// </summary>
    internal static void Consume(CombatDuelSubCircle target, IEnumerable<SpellEffect> used)
        => CombatCharms.RemoveExact(target?._hangingEffects,
                                    used.Where(x => x.m_effectType != kSpellEffects.kAbsorbDamage || x.m_paramPerRound <= 0));

}
