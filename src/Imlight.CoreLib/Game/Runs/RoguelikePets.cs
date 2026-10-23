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
using System.Threading;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Runs;

/// <summary>
/// Pets on a roguelike run: creatures and bosses sometimes drop one, at a random age from Baby to Ultra, with a
/// random pool of stat talents only (no May Casts or card talents), every stat at its maximum (260 by default).
/// A pet that is Mega or older shows five talents.
/// </summary>
internal static class RoguelikePets {

    /// <summary>[Roguelike] PetDropChance: the chance (0 to 100) that a won battle drops a pet.</summary>
    internal static readonly int PetDropChance = Math.Clamp(ConfigurationManager.GetValue("Roguelike.PetDropChance", 5), 0, 100);

    /// <summary>[Roguelike] BossPetDropChance: the chance (0 to 100) that beating a tier's boss drops a pet.</summary>
    internal static readonly int BossPetDropChance = Math.Clamp(ConfigurationManager.GetValue("Roguelike.BossPetDropChance", 50), 0, 100);

    /// <summary>[Roguelike] PetTalentPool: how many talents a dropped pet's pool holds.</summary>
    private static readonly int s_poolSize = Math.Clamp(ConfigurationManager.GetValue("Roguelike.PetTalentPool", 10), 5, 30);

    private static readonly Lazy<List<string>> s_statTalents = new(BuildStatTalents, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// [Roguelike] PetMayCastShare: the chance (0 to 100) that each talent in a run pet's pool is a May Cast talent
    /// (Guardian Wall: May Cast Tower Shield, and the like; [PetCasting] sets how often they go off). The rest are stat
    /// talents. 0 keeps pets to stat talents only.
    /// </summary>
    private static readonly int s_mayCastShare = Math.Clamp(ConfigurationManager.GetValue("Roguelike.PetMayCastShare", 30), 0, 100);

    private static readonly Lazy<List<string>> s_mayCastTalents = new(BuildMayCastTalents, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<List<uint>> s_pets = new(BuildPetPool, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Rolls the battle's pet drop. Null when nothing drops.</summary>
    internal static bool RollsDrop(Wizard wizard, bool boss) {
        var chance = boss ? BossPetDropChance : PetDropChance;

        return RoguelikeConfig.Enabled && wizard?.Roguelike is not null && chance > 0 && Random.Shared.Next(100) < chance;
    }

    /// <summary>
    /// Builds a run pet: a random pet (or <paramref name="templateId"/>) at a random age (or <paramref name="level"/>,
    /// 1 Baby to 7 Ultra), with a random pool of stat talents and every stat at its maximum. Not in any backpack yet.
    /// </summary>
    internal static WizClientObjectItem Build(Wizard wizard, byte? level = null, uint? templateId = null) {
        var pets = s_pets.Value;
        if (wizard is null || (templateId is null && pets.Count == 0)) {
            return null;
        }

        var pet = PetFactory.CreateHatchedPet(wizard.CharId, templateId ?? pets[Random.Shared.Next(pets.Count)]);
        if (pet is null) {
            return null;
        }

        // A name of its own, so a new pet is easy to find in the backpack.
        if (Imlight.CoreLib.Shared.Resources.WizardNameBank.RandomPetName() is var nameKeys and not 0) {
            PetFactory.TrySetPetName(pet, nameKeys);
        }

        PetProgress.EnsureInitialized(pet);
        var behavior = PetProgress.GetBehavior(pet);
        if (behavior is null) {
            return null;
        }

        // A pool of stat and May Cast talents (PetMayCastShare): the talents come out of it as the pet ages, up to
        // five at Mega.
        var talents = s_statTalents.Value;
        if (talents.Count > 0) {
            var stats = new Queue<string>(talents.OrderBy(_ => Random.Shared.Next()));
            var mayCasts = new Queue<string>(s_mayCastTalents.Value.OrderBy(_ => Random.Shared.Next()));
            var pool = new List<string>();
            while (pool.Count < s_poolSize && (stats.Count > 0 || mayCasts.Count > 0)) {
                var wantMayCast = mayCasts.Count > 0 && (stats.Count == 0 || Random.Shared.Next(100) < s_mayCastShare);
                pool.Add(wantMayCast ? mayCasts.Dequeue() : stats.Dequeue());
            }

            behavior.m_allTalents = [.. pool.Select(PetData.TalentId)];
            behavior.m_expressedTalents = [];
        }

        var age = level ?? (byte) Random.Shared.Next(PetProgress.BABY_LEVEL, PetProgress.MAX_LEVEL + 1);
        if (age > PetProgress.BABY_LEVEL) {
            PetProgress.SetLevel(pet, age);
        }

        // Every stat at its maximum.
        foreach (var stat in behavior.m_currentStats?.Select(s => (string) s.m_name).ToList() ?? []) {
            PetProgress.SetStat(pet, stat, "max", out _);
        }

        PetProgression.EnsureInitialized(pet);

        return pet;
    }

    /// <summary>The pets a run pet shop can sell: every pet with stat talents to roll.</summary>
    internal static IReadOnlyList<uint> ShopPool => s_pets.Value;

    /// <summary>The pet's given name ("Fluffy Moonbeam"), or empty.</summary>
    internal static string NameOf(WizClientObjectItem pet)
        => pet?.m_inactiveBehaviors?.OfType<ClientPetNameBehavior>().FirstOrDefault() is { } name && name.m_nameKeys != 0
            ? Imlight.CoreLib.Shared.Resources.WizardNameBank.GetPetEnglishName(name.m_nameKeys)?.Trim() ?? ""
            : "";

    /// <summary>The age name of a pet ("Mega").</summary>
    internal static string AgeOf(WizClientObjectItem pet)
        => PetProgress.LevelName(PetProgress.GetBehavior(pet)?.m_level ?? 0);

    /// <summary>The talents a pet shows, by name.</summary>
    internal static string ShownTalents(WizClientObjectItem pet)
        => string.Join(", ", (PetProgress.GetBehavior(pet)?.m_expressedTalents ?? [])
            .Select(PetData.GetTalent).Where(t => t is not null).Select(PetProgress.TalentLabel));

    // Talents whose every effect is a stat boost the game knows (Pain-Giver, the school-givers, Spell-Proof,
    // Pip O'Plenty...). Card talents, May Casts and derby talents are left out.
    private static List<string> BuildStatTalents() {
        var list = new List<string>();
        try {
            foreach (var talent in PetData.AllTalents.OfType<PetTalentTemplate>()) {
                var effects = talent.m_effectList ?? [];
                if (effects.Count == 0 || string.IsNullOrEmpty(talent.m_talentName)) {
                    continue;
                }

                if (effects.All(e => e is StatisticEffectInfo info && PetData.GetTalentEffect(info.m_effectName) is not null)) {
                    list.Add(talent.m_talentName);
                }
            }
        }
        catch (Exception ex) {
            Logger.Error("Roguelike could not read the pet talents. {0}", Logger.Args(ex.Message));
        }

        Logger.Information("Roguelike: {0} stat talents for dropped pets.", Logger.Args(list.Count));

        return list;
    }

    // May Cast talents whose card the server can cast.
    private static List<string> BuildMayCastTalents() {
        var list = new List<string>();
        if (s_mayCastShare <= 0 || !Imlight.CoreLib.Game.Combat.PetMayCasts.Enabled) {
            return list;
        }

        try {
            foreach (var talent in PetData.AllTalents.OfType<PetTalentTemplate>()) {
                if (!string.IsNullOrEmpty(talent.m_talentName) && Imlight.CoreLib.Game.Combat.PetMayCasts.IsCastable(talent)) {
                    list.Add(talent.m_talentName);
                }
            }
        }
        catch (Exception ex) {
            Logger.Error("Roguelike could not read the May Cast talents. {0}", Logger.Args(ex.Message));
        }

        Logger.Information("Roguelike: {0} May Cast talents for run pets.", Logger.Args(list.Count));

        return list;
    }

    // Pets that can be built: a pet item behavior with a talent pool to draw from.
    private static List<uint> BuildPetPool() {
        var list = PetFactory.PetTemplateIds
            .Where(id => PetFactory.GetPetItemBehaviorTemplate(id) is { } behavior
                         && ((behavior.m_talents?.Count ?? 0) + (behavior.m_guaranteedTalents?.Count ?? 0)) > 0)
            .ToList();
        Logger.Information("Roguelike: {0} pets can drop.", Logger.Args(list.Count));

        return list;
    }

}
