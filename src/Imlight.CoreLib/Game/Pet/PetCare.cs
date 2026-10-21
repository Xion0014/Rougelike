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
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Pet;

/// <summary>
/// Feeding pets and telling the game what changed. Used by the pet messages and the .pet commands.
/// </summary>
internal static class PetCare {

    private const byte DEFAULT_OVERALL_RATING = 30;

    internal static WizClientObjectItem GetWornPet(Wizard wizard)
        => wizard?.EquipmentBehavior?.GetItemInSlot(EquipmentSlotType.Pet);

    /// <summary>
    /// Finds a pet the wizard owns, worn or in the backpack.
    /// </summary>
    internal static WizClientObjectItem FindPet(Wizard wizard, ulong globalId) {
        var item = wizard.EquipmentBehavior?.EquippedItems?.FirstOrDefault(i => i.m_globalID == globalId)
            ?? wizard.InventoryBehavior?.GetItem(globalId);

        return PetProgress.GetBehavior(item) is null ? null : item;
    }

    /// <summary>
    /// Feeds snacks from the snack bag to a pet: experience, stats and happiness.
    /// </summary>
    /// <param name="wizard">The owner.</param>
    /// <param name="pet">The pet.</param>
    /// <param name="snackTemplateId">The snack's template.</param>
    /// <param name="count">How many of that snack to feed.</param>
    /// <param name="send">Sends a message to the owner's game.</param>
    /// <param name="growth">What the snacks did.</param>
    /// <returns>Null when it worked, otherwise the reason in plain words.</returns>
    internal static string Feed(Wizard wizard, WizClientObjectItem pet, ulong snackTemplateId, int count,
            Action<IMessage> send, out PetProgress.PetGrowth growth) {
        growth = new PetProgress.PetGrowth();
        var behavior = PetProgress.GetBehavior(pet);
        if (behavior is null) {
            return "You need to wear a pet to feed it.";
        }

        if (behavior.m_level < PetProgress.BABY_LEVEL) {
            return "That egg has not hatched yet.";
        }

        if (CoreObjectFactory.GetCoreTemplate(snackTemplateId) is not PetSnackItemTemplate snackTemplate) {
            return "That is not a pet snack.";
        }

        PetProgress.EnsureInitialized(pet);
        var fed = 0;
        for (var i = 0; i < Math.Max(1, count); i++) {
            var snack = wizard.PetSnackBehavior.GetSnack(snackTemplateId);
            if (snack is null || !wizard.RemoveSnack(snack.m_globalID, out var left)) {
                break;
            }

            fed++;
            send(left.m_quantity > 0
                ? new PET_9_PROTOCOL.MSG_PETSNACKUPDATE {
                    GlobalID = wizard.GameObjectID,
                    ItemID = left.m_globalID,
                    Quantity = left.m_quantity,
                }
                : new PET_9_PROTOCOL.MSG_PETSNACKREMOVE {
                    GlobalID = wizard.GameObjectID,
                    ItemID = left.m_globalID,
                });

            PetProgress.AddXp(pet, PetProgress.SnackXp(pet, snackTemplate), growth);
            PetProgress.ApplyStatChanges(pet, PetProgress.SnackStatChanges(snackTemplate), growth);
            if (behavior.m_maximumHappiness > 0) {
                behavior.m_happiness = Math.Min(behavior.m_maximumHappiness, behavior.m_happiness + (uint) Math.Max(1, snackTemplate.m_rank));
            }
        }

        if (fed == 0) {
            return "You do not have that snack.";
        }

        Finish(wizard, pet, growth, send);

        return null;
    }

    /// <summary>
    /// Saves a pet, updates the owner's stats if the pet is worn, and shows the game the experience and level-ups.
    /// </summary>
    internal static void Finish(Wizard wizard, WizClientObjectItem pet, PetProgress.PetGrowth growth, Action<IMessage> send) {
        var behavior = PetProgress.GetBehavior(pet);

        // The talent rows of the pet window read the ratings: a new talent shows its name there.
        PetProgression.EnsureInitialized(pet);
        if (!WizardItemCollection.SaveItemBehaviors(pet)) {
            Logger.Warning("Could not save pet {0} of {1}.", Logger.Args(pet.m_globalID, wizard.CharId));
        }

        if (ReferenceEquals(GetWornPet(wizard), pet)) {
            EquipmentExtras.Refresh(wizard);

        }

        // Commands like .pet stats change the pet without growth, and the pet window only shows it after this update.
        if (growth is null && behavior is not null && PetBehaviorUpdate.TryCreate(pet, out var plainUpdate)) {
            send(plainUpdate);
        }

        if (growth is null || behavior is null) {
            return;
        }

        try {
            // Level-ups first, then the pet's new state so the pet window shows the XP and stats, then the XP popup.
            foreach (var (level, talent, derby) in growth.LevelUps) {
                send(new PET_9_PROTOCOL.MSG_PETLEVELUP {
                    GlobalID = pet.m_globalID,
                    OverallRating = (byte) Math.Min(byte.MaxValue, behavior.m_overallRating > 0 ? behavior.m_overallRating : DEFAULT_OVERALL_RATING),
                    ActiveRating = (byte) Math.Min(byte.MaxValue, behavior.m_activeRating),
                    PetLevel = level,
                    NewTalent = talent is null ? 0 : PetData.TalentId(talent.m_talentName),
                    NewDerbyPower = derby is null ? 0 : PetData.TalentId(derby.m_talentName),
                    NewJewel = 0,
                    Display = 1,
                });
            }

            if (PetBehaviorUpdate.TryCreate(pet, out var update)) {
                send(update);
            }

            if (growth.XpGained > 0) {
                send(new WIZARD2_53_PROTOCOL.MSG_GAINPETXP {
                    PetGID = pet.m_globalID,
                    XP = growth.XpGained,
                });
            }
        }
        catch (Exception e) {
            Logger.Error("Could not tell the game about pet {0}: {1}", Logger.Args(pet.m_globalID, e));
        }
    }

    /// <summary>
    /// One or two sentences about what happened to a pet, for the chat window.
    /// </summary>
    internal static string Summary(WizClientObjectItem pet, PetProgress.PetGrowth growth) {
        var behavior = PetProgress.GetBehavior(pet);
        var parts = new List<string>();
        if (growth.XpGained > 0) {
            parts.Add($"+{growth.XpGained} pet XP");
        }

        if (growth.StatChanges.Count > 0) {
            parts.Add(string.Join(", ", growth.StatChanges
                .GroupBy(c => c.Stat)
                .Select(g => $"+{g.Sum(c => c.Change)} {g.Key}")));
        }

        var text = parts.Count > 0 ? string.Join(", ", parts) + "." : "Nothing changed.";
        if (growth.LevelsGained > 0 && behavior is not null) {
            text += $" Your pet is now {PetProgress.LevelName(behavior.m_level)}!";
        }

        var talents = growth.NewTalents.Concat(growth.NewDerbyTalents).Select(PetProgress.TalentLabel).ToList();
        if (talents.Count > 0) {
            text += $" New talents: {string.Join(", ", talents)}.";
        }

        if (behavior is not null) {
            text += behavior.m_requiredXP > 0
                ? $" XP {behavior.m_XP}/{behavior.m_requiredXP}."
                : $" XP {behavior.m_XP} (max level).";
        }

        return text;
    }

}
