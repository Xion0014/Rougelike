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
using Imlight.Common;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Runs;

/// <summary>
/// One tier of a roguelike run: the street the wizard fights on, then the boss that ends it.
/// </summary>
internal sealed record RoguelikeTier(int Number, string Street, string Boss) {

    /// <summary>Quests handed to the wizard when they reach this tier's street (TierNQuests).</summary>
    public IReadOnlyList<string> Quests { get; init; } = [];

}

/// <summary>
/// Reads the [Roguelike] section of Imlight.ini.
/// </summary>
internal static class RoguelikeConfig {

    internal static readonly bool Enabled = ConfigurationManager.GetValue("Roguelike.Enabled", false);

    /// <summary>
    /// New wizards get a random school and a random name, whatever was picked on the creation screens.
    /// </summary>
    internal static readonly bool RandomCharacters = Enabled && ConfigurationManager.GetValue("Roguelike.RandomCharacters", true);

    /// <summary>A wizard that dies in battle is deleted, the same as [Die Delete] Enabled.</summary>
    internal static readonly bool Permadeath = ConfigurationManager.GetValue("Roguelike.Permadeath", true);

    /// <summary>How long the wizard fights on each street before the boss, in seconds.</summary>
    internal static readonly int StreetSeconds
        = Math.Max(1, ConfigurationManager.GetValue("Roguelike.StreetMinutes", 10)) * 60;

    internal static readonly string ShopZone = ConfigurationManager.GetValue("Roguelike.ShopZone", "WizardCity/WC_Shop_Area");

    /// <summary>How much more experience a wizard on a run earns from battles, drops and quests.</summary>
    internal static readonly double XpMultiplier = Math.Max(0, ConfigurationManager.GetValue("Roguelike.XpMultiplier", 5.0));

    /// <summary>How much more gold a wizard on a run earns from battles, quests and gold wisps (not from selling).</summary>
    internal static readonly double GoldMultiplier = Math.Max(0, ConfigurationManager.GetValue("Roguelike.GoldMultiplier", 3.0));

    /// <summary>Training points given for beating each tier's boss.</summary>
    internal static readonly int BossTrainingPoints = Math.Clamp(ConfigurationManager.GetValue("Roguelike.BossTrainingPoints", 2), 0, 100);

    private static bool IsOnRun(Wizard wizard) => Enabled && wizard?.Roguelike is not null;

    /// <summary>Earned experience, scaled for a wizard on a run.</summary>
    internal static int ScaleXp(Wizard wizard, int xp)
        => xp > 0 && IsOnRun(wizard) ? (int) Math.Min(int.MaxValue, Math.Round(xp * XpMultiplier)) : xp;

    /// <summary>Earned gold, scaled for a wizard on a run.</summary>
    internal static int ScaleGold(Wizard wizard, int gold)
        => gold > 0 && IsOnRun(wizard) ? (int) Math.Min(int.MaxValue, Math.Round(gold * GoldMultiplier)) : gold;

    /// <summary>Where .run train sends a wizard during the shop phase, for the spell trainers.</summary>
    internal static readonly string TrainZone = ConfigurationManager.GetValue("Roguelike.TrainZone", "WizardCity/WC_Ravenwood");

    internal static readonly IReadOnlyList<RoguelikeTier> Tiers = ReadTiers();

    internal static int TierCount => Tiers.Count;

    internal static RoguelikeTier GetTier(int number)
        => Tiers.Count == 0 ? null : Tiers[Math.Clamp(number, 1, Tiers.Count) - 1];

    private const int MAX_TIERS = 99;

    /// <summary>
    /// Reads Tier1, Tier2, ... ("street zone, boss zone") until one is missing. Without any, falls back to the
    /// Streets and Bosses lists, then to Unicorn Way and the Haunted Cave.
    /// </summary>
    private static List<RoguelikeTier> ReadTiers() {
        var tiers = new List<RoguelikeTier>();
        for (var i = 1; i <= MAX_TIERS; i++) {
            var parts = SplitList(ConfigurationManager.GetValue($"Roguelike.Tier{i}", ""));
            if (parts.Length == 0) {
                break;
            }

            if (parts.Length != 2) {
                Logger.Warning("[Roguelike] Tier{0} needs a street and a boss zone separated by a comma. Tiers stop here.",
                    Logger.Args(i));
                break;
            }

            tiers.Add(new RoguelikeTier(i, parts[0], parts[1]) {
                Quests = SplitList(ConfigurationManager.GetValue($"Roguelike.Tier{i}Quests", "")),
            });
        }

        if (tiers.Count > 0) {
            return tiers;
        }

        var streets = SplitList(ConfigurationManager.GetValue("Roguelike.Streets", "WizardCity/WC_Streets/WC_Unicorn"));
        var bosses = SplitList(ConfigurationManager.GetValue("Roguelike.Bosses", "WizardCity/WC_Streets/WC_HauntedCave"));
        if (streets.Length != bosses.Length) {
            Logger.Warning("[Roguelike] Streets has {0} entries but Bosses has {1}. Extra entries are ignored.",
                Logger.Args(streets.Length, bosses.Length));
        }

        var count = Math.Min(streets.Length, bosses.Length);

        return [.. Enumerable.Range(0, count).Select(i => new RoguelikeTier(i + 1, streets[i], bosses[i]))];
    }

    private static string[] SplitList(string value)
        => (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(StripKey)
            .Where(part => part.Length > 0)
            .ToArray();

    /// <summary>
    /// Removes a pasted "Key =" from the front of a value, so "Tier1 = Tier1 = WizardCity/..." still works.
    /// Zone paths and quest names never contain '=', so anything before the last '=' is a stray key.
    /// </summary>
    private static string StripKey(string part) {
        var equals = part.LastIndexOf('=');

        return (equals >= 0 ? part[(equals + 1)..] : part).Trim().Trim('"');
    }

    /// <summary>
    /// Turns a zone name from the config into a full zone path the client knows. Takes an exact path, the
    /// end of one ("WC_Unicorn"), or any part of one. Returns null when nothing matches.
    /// </summary>
    internal static string ResolveZone(string configured) {
        if (string.IsNullOrWhiteSpace(configured)) {
            return null;
        }

        configured = StripKey(configured);
        var zones = AccessPassManager.Zones;

        return zones.FirstOrDefault(z => z.Equals(configured, StringComparison.OrdinalIgnoreCase))
            ?? zones.FirstOrDefault(z => z.EndsWith("/" + configured, StringComparison.OrdinalIgnoreCase))
            ?? zones.FirstOrDefault(z => z.Contains(configured, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every zone path containing <paramref name="filter"/>, for finding names to put in the config.</summary>
    internal static IEnumerable<string> FindZones(string filter)
        => AccessPassManager.Zones.Where(z => string.IsNullOrEmpty(filter)
                                              || z.Contains(filter, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the creature template is flagged as a boss.</summary>
    internal static bool IsBossMob(ulong mobTemplateId) {
        var template = CoreObjectFactory.GetCoreTemplate(mobTemplateId) as GameObjectTemplate;
        var npc = template?.m_behaviors?.OfType<NPCBehaviorTemplate>().FirstOrDefault();

        return npc?.m_bossMob == true;
    }

}
