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
 *
 * ========================================================================
 * JEWEL SOCKETS
 * ========================================================================
 *
 * PURPOSE:
 * Jewels and pins in the sockets of gear and pets.
 *
 * NOTE:
 * - An item's sockets are m_jewelSockets on the JewelSocketBehaviorTemplate of its template. Pets have one
 *   pet socket. Socket numbers are positions in that list, starting at 0.
 * - What sits in each socket is kept on the item's ClientJewelSocketBehavior (socket number and jewel template),
 *   which is saved with the item and sent to the game with it.
 * - A jewel's shape is an adjective on its template: CircleJewel, SquareJewel, TriangleJewel, TearJewel,
 *   PetJewel, PinSquarePip, PinSquareShield or PinSquareSword.
 * - Gear jewels give their equip effects while the gear is worn. Pet jewels give their pet talent while the pet
 *   is worn.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Shared.Items;

internal static class JewelSockets {

    private const string SOCKET_BEHAVIOR_NAME = "JewelSocketBehavior";

    private static readonly Dictionary<string, JewelSocketType> s_shapes = new(StringComparer.OrdinalIgnoreCase) {
        ["SquareJewel"] = JewelSocketType.SOCKETTYPE_SQUARE,
        ["CircleJewel"] = JewelSocketType.SOCKETTYPE_CIRCLE,
        ["TriangleJewel"] = JewelSocketType.SOCKETTYPE_TRIANGLE,
        ["TearJewel"] = JewelSocketType.SOCKETTYPE_TEAR,
        ["PetJewel"] = JewelSocketType.SOCKETTYPE_PET,
        ["PinSquarePip"] = JewelSocketType.SOCKETTYPE_PINSQUAREPIP,
        ["PinSquareShield"] = JewelSocketType.SOCKETTYPE_PINSQUARESHIELD,
        ["PinSquareSword"] = JewelSocketType.SOCKETTYPE_PINSQUARESWORD,
    };

    internal static List<JewelSocket> GetSockets(WizItemTemplate template)
        => template?.m_behaviors?.OfType<JewelSocketBehaviorTemplate>().FirstOrDefault()?.m_jewelSockets ?? [];

    internal static bool IsJewel(WizItemTemplate template) => GetShape(template) is not null;

    /// <summary>
    /// Whether a jewel is No Shatter, so taking it out returns it instead of destroying it. The No Shatter jewels
    /// (the Blackhawk and AM pet jewels: Stuff of Legends Peridot, Lifesaver Jade...) are the jewels marked No Sell,
    /// and carry an extra behavior (DeletedBehavior, or a second PetJewelBehavior on Lifesaver Jade) (Justin, 2026-10-20).
    /// </summary>
    internal static bool IsNoShatter(WizItemTemplate template) {
        if (template is null) {
            return false;
        }

        var adjectives = (template.m_adjectiveList ?? []).Select(a => (string) a ?? "").ToList();
        var behaviors = (template.m_behaviors ?? []).Select(b => (string) b?.m_behaviorName ?? "").ToList();

        return adjectives.Any(a => a.Equals("FLAG_NoSell", StringComparison.OrdinalIgnoreCase)
                || a.Contains("Shatter", StringComparison.OrdinalIgnoreCase))
            || behaviors.Contains("DeletedBehavior")
            || behaviors.Count(b => b == "PetJewelBehavior") > 1;
    }

    internal static JewelSocketType? GetShape(WizItemTemplate template) {
        foreach (var adjective in template?.m_adjectiveList ?? []) {
            var name = (string) adjective;
            if (!string.IsNullOrEmpty(name) && s_shapes.TryGetValue(name, out var shape)) {
                return shape;
            }
        }

        return null;
    }

    internal static string ShapeName(JewelSocketType shape) => shape switch {
        JewelSocketType.SOCKETTYPE_SQUARE => "square",
        JewelSocketType.SOCKETTYPE_CIRCLE => "circle",
        JewelSocketType.SOCKETTYPE_TRIANGLE => "triangle",
        JewelSocketType.SOCKETTYPE_TEAR => "tear",
        JewelSocketType.SOCKETTYPE_PET => "pet",
        JewelSocketType.SOCKETTYPE_PINSQUAREPIP => "power pin",
        JewelSocketType.SOCKETTYPE_PINSQUARESHIELD => "shield pin",
        JewelSocketType.SOCKETTYPE_PINSQUARESWORD => "sword pin",
        _ => shape.ToString(),
    };

    /// <summary>
    /// The socket state on an item, made (unlocked and empty) when the item has sockets but no state yet.
    /// </summary>
    internal static ClientJewelSocketBehavior GetState(WizClientObjectItem item, bool create) {
        if (item?.m_inactiveBehaviors is null) {
            return null;
        }

        var state = item.m_inactiveBehaviors.OfType<ClientJewelSocketBehavior>().FirstOrDefault();
        if (state is null && create) {
            var template = ItemHelper.GetItemTemplate(item);
            if (GetSockets(template).Count == 0) {
                return null;
            }

            state = new ClientJewelSocketBehavior {
                m_behaviorTemplateNameID = StringHash.Compute(SOCKET_BEHAVIOR_NAME),
            };

            // Items made from their template have a place for it at the template's behavior index.
            var index = template.m_behaviors.FindIndex(b => b is JewelSocketBehaviorTemplate);
            if (index >= 0 && index < item.m_inactiveBehaviors.Count && item.m_inactiveBehaviors[index] is null) {
                item.m_inactiveBehaviors[index] = state;
            }
            else {
                item.m_inactiveBehaviors.Add(state);
            }
        }

        if (state is not null) {
            state.m_publicJewelInfo ??= new PublicEquippedJewelInfo { m_bSocketsLocked = false };

            // This server never charges crowns to open sockets: every socket is open.
            state.m_publicJewelInfo.m_bSocketsLocked = false;
            state.m_publicJewelInfo.m_infoPairs ??= [];
        }

        return state;
    }

    internal static List<PublicSocketJewelPair> GetPairs(WizClientObjectItem item)
        => item?.m_inactiveBehaviors?.OfType<ClientJewelSocketBehavior>().FirstOrDefault()?.m_publicJewelInfo?.m_infoPairs ?? [];

    /// <summary>
    /// The templates of the jewels in an item's sockets.
    /// </summary>
    internal static IEnumerable<WizItemTemplate> GetSocketedJewels(WizClientObjectItem item)
        => GetPairs(item)
            .Where(p => p is not null && p.m_jewelTemplateID != 0)
            .Select(p => CoreObjectFactory.GetCoreTemplate(p.m_jewelTemplateID) as WizItemTemplate)
            .Where(t => t is not null);

    /// <summary>
    /// The pet talents of the jewel in a pet's socket.
    /// </summary>
    internal static IEnumerable<string> GetPetJewelTalents(WizClientObjectItem pet)
        => GetSocketedJewels(pet).SelectMany(JewelTalents);

    // Some talent jewels, such as Guardian Wall Sapphire (Jewel-ST-Talent-Combat-TowerShield), have no
    // PetJewelBehavior; their talent is the end of the jewel's name.
    private static IEnumerable<string> JewelTalents(WizItemTemplate jewel) {
        var named = (jewel.m_behaviors?.OfType<PetJewelBehaviorTemplate>() ?? [])
            .SelectMany(b => b.m_petTalentName ?? [])
            .Select(n => (string) n)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList();
        if (named.Count > 0) {
            return named;
        }

        string objectName = jewel.m_objectName;
        var start = objectName?.IndexOf("Talent-", StringComparison.OrdinalIgnoreCase) ?? -1;
        if (start < 0 || Imlight.CoreLib.Game.Pet.PetData.GetTalent(objectName[start..]) is null) {
            return [];
        }

        return [objectName[start..]];
    }

    /// <summary>
    /// Checks whether a jewel can go in a socket.
    /// </summary>
    /// <returns>Null when it can, otherwise the reason in plain words.</returns>
    internal static string CheckSocket(Wizard wizard, WizClientObjectItem item, WizItemTemplate jewelTemplate, int socketNumber) {
        var itemTemplate = ItemHelper.GetItemTemplate(item);
        var sockets = GetSockets(itemTemplate);
        if (sockets.Count == 0) {
            return "That item has no sockets.";
        }

        if (socketNumber < 0 || socketNumber >= sockets.Count) {
            return $"That item has no socket {socketNumber + 1}.";
        }

        var shape = GetShape(jewelTemplate);
        if (shape is null) {
            return "That is not a jewel or a pin.";
        }

        if (sockets[socketNumber]?.m_socketType != shape) {
            return $"A {ShapeName(shape.Value)} jewel does not fit a {ShapeName(sockets[socketNumber].m_socketType)} socket.";
        }

        if (GetPairs(item).Any(p => p?.m_socketNumber == socketNumber && p.m_jewelTemplateID != 0)) {
            return "That socket already has a jewel in it. Take it out first.";
        }

        var requirementProblem = CheckRequirements(wizard, jewelTemplate.m_equipRequirements);
        if (requirementProblem is not null) {
            return requirementProblem;
        }

        var petJewel = jewelTemplate.m_behaviors?.OfType<PetJewelBehaviorTemplate>().FirstOrDefault();
        if (petJewel is not null) {
            var petLevel = item.m_inactiveBehaviors?.OfType<ClientPetItemBehavior>().FirstOrDefault()?.m_level ?? 0;
            if (petLevel < petJewel.m_minPetLevel) {
                return $"Your pet must be level {petJewel.m_minPetLevel} for that jewel.";
            }
        }

        return null;
    }

    /// <summary>
    /// Puts a jewel template in a socket. The caller has already checked it with <see cref="CheckSocket"/>.
    /// </summary>
    internal static void Socket(WizClientObjectItem item, int socketNumber, uint jewelTemplateId) {
        var state = GetState(item, create: true);
        state.m_publicJewelInfo.m_infoPairs.RemoveAll(p => p is null || p.m_socketNumber == socketNumber);
        state.m_publicJewelInfo.m_infoPairs.Add(new PublicSocketJewelPair {
            m_socketNumber = (uint) socketNumber,
            m_jewelTemplateID = jewelTemplateId,
        });
        state.m_publicJewelInfo.m_infoPairs.Sort((a, b) => a.m_socketNumber.CompareTo(b.m_socketNumber));
    }

    /// <summary>
    /// Empties a socket.
    /// </summary>
    /// <returns>The template of the jewel that was in it, or 0 if it was empty.</returns>
    internal static uint Unsocket(WizClientObjectItem item, int socketNumber) {
        var pairs = GetPairs(item);
        var pair = pairs.FirstOrDefault(p => p?.m_socketNumber == socketNumber);
        if (pair is null) {
            return 0;
        }

        pairs.Remove(pair);

        return pair.m_jewelTemplateID;
    }

    private static string CheckRequirements(Wizard wizard, RequirementList requirements) {
        foreach (var requirement in requirements?.m_requirements ?? []) {
            switch (requirement) {
                case ReqMagicLevel level when !level.m_applyNOT && wizard.MagicSchoolBehavior.Level < level.m_numericValue:
                    return $"You must be level {level.m_numericValue} to use that jewel.";
                case ReqSchoolOfFocus school when !school.m_applyNOT && !string.IsNullOrEmpty(school.m_magicSchool)
                        && !string.Equals(school.m_magicSchool, wizard.MagicSchoolBehavior.MagicSchool.ToString(), StringComparison.OrdinalIgnoreCase):
                    return $"Only {school.m_magicSchool} wizards can use that jewel.";
            }
        }

        return null;
    }

}
