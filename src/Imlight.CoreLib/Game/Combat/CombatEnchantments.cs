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
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Combat;

/// <summary>
/// Enchantment cards (Epic, Sharpen Blade, Potent Trap and the like): played onto another card in the hand,
/// they change that card's numbers until it is cast.
/// </summary>
internal static class CombatEnchantments {

    // These are methods, not static sets: a static field that fails to set up breaks the whole class for
    // the rest of the run, and enchantments then fail without a word in the log.
    // Delayed damage (Whiplash and the like) and split damage are damage too.
    private static bool IsDamage(kSpellEffects type) => type is kSpellEffects.kDamage or kSpellEffects.kDamageNoCrit
        or kSpellEffects.kStealHealth or kSpellEffects.kDamageOverTime or kSpellEffects.kMaxHealthDamage
        or kSpellEffects.kDeferredDamage or kSpellEffects.kDivideDamage;

    private static bool IsHeal(kSpellEffects type) => type is kSpellEffects.kHeal or kSpellEffects.kHealOverTime;

    // Blades and other charms.
    private static bool IsCharm(kSpellEffects type) => type is kSpellEffects.kModifyOutgoingDamage
        or kSpellEffects.kModifyAccuracy or kSpellEffects.kModifyOutgoingHeal or kSpellEffects.kModifyOutgoingArmorPiercing;

    // Traps, shields and absorbs.
    private static bool IsWard(kSpellEffects type) => type is kSpellEffects.kModifyIncomingDamage
        or kSpellEffects.kAbsorbDamage or kSpellEffects.kModifyIncomingHeal;

    /// <summary>
    /// True when the spell is an enchantment: every effect it has changes another card.
    /// </summary>
    internal static bool IsEnchantment(SpellTemplate template)
        => Leaves(template?.m_effects).ToList() is { Count: > 0 } effects && effects.All(e => IsCardModifier(e.m_effectType));

    /// <summary>
    /// The plain effects of a spell, looking inside effect lists, random, variable, count based and conditional
    /// effects (some enchantments, such as Nightbringer, keep their card changes inside those).
    /// </summary>
    internal static IEnumerable<SpellEffect> Leaves(IEnumerable<SpellEffect> effects) {
        foreach (var effect in effects ?? []) {
            var children = effect switch {
                ConditionalSpellEffect conditional => conditional.m_elements?.Select(e => e?.m_pEffect).ToList(),
                _ => Children(effect),
            };
            if (children is null) {
                if (effect is not null) {
                    yield return effect;
                }

                continue;
            }

            foreach (var leaf in Leaves(children)) {
                yield return leaf;
            }
        }
    }

    private static bool IsCardModifier(kSpellEffects type) => type is kSpellEffects.kModifyCardCloak
        or kSpellEffects.kModifyCardDamage or kSpellEffects.kModifyCardAccuracy or kSpellEffects.kModifyCardMutation
        or kSpellEffects.kModifyCardRank or kSpellEffects.kModifyCardArmorPiercing or kSpellEffects.kModifyCardHeal
        or kSpellEffects.kModifyCardCharm or kSpellEffects.kModifyCardWard or kSpellEffects.kModifyCardOutgoingDamage
        or kSpellEffects.kModifyCardOutgoingAccuracy or kSpellEffects.kModifyCardOutgoingHeal
        or kSpellEffects.kModifyCardOutgoingArmorPiercing or kSpellEffects.kModifyCardIncomingDamage
        or kSpellEffects.kModifyCardAbsorbDamage or kSpellEffects.kModifyCardDamagebyRank;

    /// <summary>
    /// Plays <paramref name="enchantment"/> onto <paramref name="target"/>, a card in the hand. Returns false
    /// (and changes nothing) when the card is already enchanted, is itself an enchantment, or the enchantment
    /// would not change it.
    /// </summary>
    internal static bool Enchant(Spell target, Spell enchantment) {
        if (target is null || enchantment is null || target.m_enchantment != 0) {
            return false;
        }

        if (CoreObjectFactory.GetCoreTemplate(target.m_templateID) is not SpellTemplate targetTemplate
            || CoreObjectFactory.GetCoreTemplate(enchantment.m_templateID) is not SpellTemplate enchantTemplate
            || !IsEnchantment(enchantTemplate) || IsEnchantment(targetTemplate)) {
            return false;
        }

        var enchanted = Apply(targetTemplate, enchantTemplate, out var changed);
        if (!changed) {
            return false;
        }

        target.m_enchantment = enchantment.m_templateID;
        target.m_enchantedThisCombat = true;
        target.m_enchantmentSpellIsItemCard = enchantment.m_itemCard;
        target.m_accuracy = (byte) Math.Clamp(enchanted.m_accuracy, 0, 100);
        // The game draws a card's pip cost from its template plus m_regularAdjust, so a pip change
        // (Elucidate and the like) goes there as well as into the cost the server charges.
        var baseRank = target.m_pipCost?.m_spellRank ?? targetTemplate.m_spellRank?.m_spellRank ?? 0;
        target.m_regularAdjust += (enchanted.m_spellRank?.m_spellRank ?? baseRank) - baseRank;
        target.m_pipCost = enchanted.m_spellRank;
        target.m_cloaked = enchanted.m_cloaked;

        return true;
    }

    /// <summary>
    /// The spell as it is once enchanted. The template itself is shared and never changed.
    /// </summary>
    internal static SpellTemplate Apply(SpellTemplate spell, SpellTemplate enchantment, out bool changed) {
        changed = false;
        var result = spell with { m_effects = [.. spell.m_effects ?? []] };

        foreach (var modifier in Leaves(enchantment.m_effects)) {
            var amount = modifier.m_effectParam;
            switch (modifier.m_effectType) {
                case kSpellEffects.kModifyCardAccuracy:
                    result = result with { m_accuracy = result.m_accuracy + amount };
                    changed = true;
                    break;
                // [Battle] NoPipsFromDaybreakerNightbringer: those two keep their damage but not their extra pip.
                case kSpellEffects.kModifyCardRank when amount > 0 && NoPipsFromDayNight && IsDaybreakerOrNightbringer(enchantment):
                    // Still an enchantment: the card's cast is delayed (see CombatResolver), just without the pips.
                    changed = true;
                    break;
                case kSpellEffects.kModifyCardRank when result.m_spellRank is not null:
                    result = result with {
                        m_spellRank = result.m_spellRank with {
                            m_spellRank = (byte) Math.Clamp(result.m_spellRank.m_spellRank + amount, 0, 14),
                        },
                    };
                    changed = true;
                    break;
                case kSpellEffects.kModifyCardCloak:
                    result = result with { m_cloaked = true };
                    changed = true;
                    break;
                case kSpellEffects.kModifyCardDamage:
                    changed |= Modify(result, IsDamage, e => e with { m_effectParam = e.m_effectParam + amount });
                    break;
                case kSpellEffects.kModifyCardDamagebyRank:
                    var rank = result.m_spellRank?.m_spellRank ?? 0;
                    changed |= Modify(result, IsDamage, e => e with { m_effectParam = e.m_effectParam + amount * rank });
                    break;
                case kSpellEffects.kModifyCardArmorPiercing:
                    changed |= Modify(result, IsDamage, e => e with { m_armorPiercingParam = e.m_armorPiercingParam + amount });
                    break;
                case kSpellEffects.kModifyCardHeal:
                    changed |= Modify(result, IsHeal, e => e with { m_effectParam = e.m_effectParam + amount });
                    break;
                case kSpellEffects.kModifyCardCharm:
                    changed |= Modify(result, IsCharm, e => Grow(e, amount));
                    break;
                case kSpellEffects.kModifyCardWard:
                    changed |= Modify(result, IsWard, e => Grow(e, amount));
                    break;
                case kSpellEffects.kModifyCardOutgoingDamage:
                    changed |= Modify(result, t => t == kSpellEffects.kModifyOutgoingDamage, e => Grow(e, amount));
                    break;
                case kSpellEffects.kModifyCardOutgoingAccuracy:
                    changed |= Modify(result, t => t == kSpellEffects.kModifyAccuracy, e => Grow(e, amount));
                    break;
                case kSpellEffects.kModifyCardOutgoingHeal:
                    changed |= Modify(result, t => t == kSpellEffects.kModifyOutgoingHeal, e => Grow(e, amount));
                    break;
                case kSpellEffects.kModifyCardOutgoingArmorPiercing:
                    changed |= Modify(result, t => t == kSpellEffects.kModifyOutgoingArmorPiercing, e => Grow(e, amount));
                    break;
                case kSpellEffects.kModifyCardIncomingDamage:
                    changed |= Modify(result, t => t == kSpellEffects.kModifyIncomingDamage, e => Grow(e, amount));
                    break;
                case kSpellEffects.kModifyCardAbsorbDamage:
                    changed |= Modify(result, t => t == kSpellEffects.kAbsorbDamage, e => Grow(e, amount));
                    break;
            }
        }

        return result;
    }

    private static bool NoPipsFromDayNight
        => Imlight.Common.ConfigurationManager.GetValue("Battle.NoPipsFromDaybreakerNightbringer", false);

    internal static bool IsDaybreakerOrNightbringer(SpellTemplate enchantment) {
        var name = enchantment.m_name.ToString() ?? "";

        return name.Contains("Daybreaker", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Nightbringer", StringComparison.OrdinalIgnoreCase);
    }

    // A buff gets bigger and a debuff gets worse: the size grows either way.
    private static SpellEffect Grow(SpellEffect effect, int amount)
        => effect with { m_effectParam = effect.m_effectParam >= 0 ? effect.m_effectParam + amount : effect.m_effectParam - amount };

    /// <summary>
    /// Replaces each effect of the given types (also inside random and variable effects) with a changed copy.
    /// </summary>
    private static bool Modify(SpellTemplate spell, Func<kSpellEffects, bool> types, Func<SpellEffect, SpellEffect> change) {
        var changed = false;
        for (var i = 0; i < spell.m_effects.Count; i++) {
            spell.m_effects[i] = ModifyEffect(spell.m_effects[i], types, change, ref changed);
        }

        return changed;
    }

    private static SpellEffect ModifyEffect(SpellEffect effect, Func<kSpellEffects, bool> types,
                                            Func<SpellEffect, SpellEffect> change, ref bool changed) {
        if (effect is null) {
            return null;
        }

        if (effect is ConditionalSpellEffect conditional && conditional.m_elements is { } elements) {
            var copies = new List<ConditionalSpellElement>();
            foreach (var element in elements) {
                copies.Add(element?.m_pEffect is null
                    ? element
                    : element with { m_pEffect = ModifyEffect(element.m_pEffect, types, change, ref changed) });
            }

            return conditional with { m_elements = copies };
        }

        if (Children(effect) is { } children) {
            var copies = new List<SpellEffect>();
            foreach (var child in children) {
                copies.Add(ModifyEffect(child, types, change, ref changed));
            }

            return effect switch {
                RandomSpellEffect random => random with { m_effectList = copies },
                VariableSpellEffect variable => variable with { m_effectList = copies },
                EffectListSpellEffect list => list with { m_effectList = copies },
                CountBasedSpellEffect counted => counted with { m_effectList = copies },
                _ => effect,
            };
        }

        if (!types(effect.m_effectType)) {
            return effect;
        }

        changed = true;

        return change(effect);
    }

    /// <summary>
    /// The effects written out for the log, nested ones in brackets.
    /// </summary>
    internal static string Describe(IEnumerable<SpellEffect> effects)
        => string.Join(", ", (effects ?? []).Select(DescribeOne));

    private static string DescribeOne(SpellEffect effect) {
        if (effect is null) {
            return "null";
        }

        var text = $"{effect.GetType().Name} {effect.m_effectType} {effect.m_effectParam}";
        var children = effect is ConditionalSpellEffect conditional
            ? conditional.m_elements?.Select(e => e?.m_pEffect).ToList()
            : Children(effect);

        return children is null ? text : $"{text} [{Describe(children)}]";
    }

    private static List<SpellEffect> Children(SpellEffect effect) => effect switch {
        RandomSpellEffect random => random.m_effectList,
        VariableSpellEffect variable => variable.m_effectList,
        EffectListSpellEffect list => list.m_effectList,
        CountBasedSpellEffect counted => counted.m_effectList,
        _ => null,
    };

}
