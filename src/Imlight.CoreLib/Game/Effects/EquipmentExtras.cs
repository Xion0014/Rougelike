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
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.Cryptography;
using Imcodec.CoreObject;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Effects;

/// <summary>
/// The stats a wizard gets from the worn pet's talents and from jewels in worn gear, on top of the gear itself.
/// </summary>
internal static class EquipmentExtras {

    /// <summary>
    /// Takes off the pet and jewel stats added before and adds them again for what is worn now.
    /// Call it after anything that changes the worn pet, a pet's stats or talents, or a socket.
    /// </summary>
    internal static void Refresh(Wizard wizard) {
        try {
            Remove(wizard);
            Apply(wizard);
        }
        catch (Exception e) {
            Logger.Error("Could not update pet and jewel stats for {0}: {1}", Logger.Args(wizard?.CharId, e));
        }
    }

    /// <summary>
    /// Forgets what was added without taking it off, for when the stats were just rebuilt from scratch.
    /// </summary>
    internal static void Forget(Wizard wizard) {
        // Rebuilt stats do not touch the cards, so those still come off: left in, every rebuild added the pet's
        // cards again (4 Black Mantles instead of 2).
        foreach (var info in wizard.AppliedJewelEffects) {
            if (info is ProvideSpellEffectInfo spell) {
                RemoveCards(wizard, spell);
            }
        }

        wizard.AppliedPetEffects = [];
        wizard.AppliedJewelEffects = [];
        wizard.AppliedCardEffects = [];
    }

    /// <summary>
    /// The card effects taken off and put on by the last refreshes, for the equipment service to tell the game.
    /// Cleared by reading them.
    /// </summary>
    internal static (List<GameEffectBase> Removed, List<GameEffectBase> Added) TakeCardEffectChanges(Wizard wizard) {
        var changes = (wizard.RemovedCardEffects, wizard.AddedCardEffects);
        wizard.RemovedCardEffects = [];
        wizard.AddedCardEffects = [];

        return changes;
    }

    /// <summary>
    /// The messages that tell the game which pet and jewel effects came off and went on since it was last told,
    /// for callers outside the equipment service, such as feeding a pet. Clears the changes like
    /// <see cref="TakeCardEffectChanges"/>.
    /// </summary>
    internal static List<IMessage> TakeCardEffectMessages(Wizard wizard) {
        var (removed, added) = TakeCardEffectChanges(wizard);
        var messages = new List<IMessage>();
        foreach (var effect in removed) {
            messages.Add(new GAME_5_PROTOCOL.MSG_REMOVEEFFECT {
                GameObjectID = wizard.GameObjectID,
                EffectNameID = effect.m_effectNameID,
                InternalID = effect.m_internalID,
            });
        }

        foreach (var effect in added) {
            if (!s_effectSerializer.Serialize(effect, PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit, out var data)) {
                Logger.Error("Failed to serialize effect {0} for {1}.", Logger.Args(effect.m_effectNameID, wizard.CharId));

                continue;
            }

            messages.Add(new GAME_5_PROTOCOL.MSG_ADDEFFECT {
                GameObjectID = wizard.GameObjectID,
                EffectData = data,
            });
        }

        return messages;
    }

    private static readonly CoreObjectSerializer s_effectSerializer = new(versionable: false, behaviors: SerializerFlags.None);

    private static void Remove(Wizard wizard) {
        foreach (var (name, effect) in wizard.AppliedPetEffects) {
            CharacterEffectHelper.RemoveStatisticEffectFromStats(wizard.GameStats, name, effect);
        }

        foreach (var info in wizard.AppliedJewelEffects) {
            // Cards come off in Forget.
            if (info is ProvideSpellEffectInfo) {
                continue;
            }

            CharacterEffectHelper.RemoveGameEffectFromStats(wizard.GameStats, info);
        }

        // The cards' effects tell the game to list them with the item cards, like a wand's.
        foreach (var effect in wizard.AppliedCardEffects) {
            wizard.GameEffects.Remove(effect);
            if (!wizard.AddedCardEffects.Remove(effect)) {
                wizard.RemovedCardEffects.Add(effect);
            }
        }

        Forget(wizard);
    }

    private static void RemoveCards(Wizard wizard, ProvideSpellEffectInfo spell) {
        // The card's id is the hash of its template's own name, which the effect may spell differently.
        var spellId = SpellFactory.GetSpell(spell.m_spellName)?.m_spellID ?? StringHash.Compute(spell.m_spellName);
        for (var i = 0; i < Math.Max(1, spell.m_numSpells); i++) {
            wizard.RemoveTemporarySpell(spellId);
        }
    }

    private static void Apply(Wizard wizard) {
        var equipped = wizard.EquipmentBehavior?.EquippedItems ?? [];
        foreach (var item in equipped.ToList()) {
            var template = ItemHelper.GetItemTemplate(item);
            if (template is null) {
                continue;
            }

            if (PetProgress.GetBehavior(item) is not null) {
                if (PetProgress.EnsureInitialized(item)) {
                    WizardItemCollection.SaveItemBehaviors(item);
                }
                foreach (var (name, effect) in PetProgress.ComputeEffects(item, JewelSockets.GetPetJewelTalents(item))) {
                    CharacterEffectHelper.AddStatisticEffectToStats(wizard.GameStats, name, effect);
                    wizard.AppliedPetEffects.Add((name, effect));

                    // Also listed as an effect of the pet's slot, like gear's: the stats page adds up those effects,
                    // so without it a talent showed only in the hover text, not as a number (Justin, 2026-10-19).
                    var shown = effect with {
                        m_itemSlotID = ItemHelper.GetItemSlotHash(template),
                        m_internalID = wizard.NextEffectId(),
                    };
                    wizard.GameEffects.Add(shown);
                    wizard.AppliedCardEffects.Add(shown);
                    wizard.AddedCardEffects.Add(shown);
                }

                // Card talents give their cards for battle; they are taken off like a jewel's cards.
                foreach (var info in PetProgress.CardEffects(item, JewelSockets.GetPetJewelTalents(item))) {
                    AddCards(wizard, info, ItemHelper.GetItemSlotHash(template));
                }

                continue;
            }

            foreach (var jewel in JewelSockets.GetSocketedJewels(item)) {
                foreach (var info in jewel.m_equipEffects ?? []) {
                    switch (info) {
                        case ProvideSpellEffectInfo spell:
                            AddCards(wizard, spell, ItemHelper.GetItemSlotHash(template));
                            break;
                        case StatisticEffectInfo or StartingPipEffectInfo:
                            if (CharacterEffectHelper.AddGameEffectToStats(wizard.GameStats, info) is not null) {
                                wizard.AppliedJewelEffects.Add(info);

                                // Also listed as an effect of the item's slot, like the pet's talents: the stats page
                                // adds up those effects, so a jewel's flat resist showed only in the hover text, not
                                // in the row under the schools (Justin, 2026-10-19).
                                if (GameEffectFactory.CreateEffectFromInfo(info, ItemHelper.GetItemSlotHash(template)) is { } shown) {
                                    shown.m_internalID = wizard.NextEffectId();
                                    wizard.GameEffects.Add(shown);
                                    wizard.AppliedCardEffects.Add(shown);
                                    wizard.AddedCardEffects.Add(shown);
                                }
                            }
                            break;
                    }
                }
            }
        }
    }

    private static void AddCards(Wizard wizard, ProvideSpellEffectInfo info, uint slotHash) {
        if (GameEffectFactory.CreateEffectFromInfo(info, slotHash) is ProvideSpellEffect provide) {
            foreach (var card in SpellFactory.CreateSpellsFromEffect(provide) ?? []) {
                wizard.AddTemporarySpell(card);
            }

            wizard.AppliedJewelEffects.Add(info);

            // Listed as an effect of the worn item, so the deck window shows the cards with the item cards.
            provide.m_internalID = wizard.NextEffectId();
            wizard.GameEffects.Add(provide);
            wizard.AppliedCardEffects.Add(provide);
            wizard.AddedCardEffects.Add(provide);
            Logger.Information("Item card effect {0} x{1} (slot {2}, id {3}) for {4}.",
                Logger.Args(info.m_spellName, info.m_numSpells, slotHash, provide.m_internalID, wizard.CharId));
        }
    }

}
