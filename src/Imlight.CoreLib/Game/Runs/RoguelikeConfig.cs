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

    /// <summary>How long the street lasts before the boss, in seconds (TierNMinutes, else StreetMinutes).</summary>
    public int StreetSeconds { get; init; }

    /// <summary>Quests handed to the wizard when they reach this tier's street (TierNQuests).</summary>
    public IReadOnlyList<string> Quests { get; init; } = [];

    /// <summary>Quests handed to the wizard when they reach this tier's boss zone (TierNBossQuests).</summary>
    public IReadOnlyList<string> BossQuests { get; init; } = [];

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

    /// <summary>Every new run starts with a random mount in the wizard's backpack.</summary>
    internal static readonly bool GiveMount = ConfigurationManager.GetValue("Roguelike.GiveMount", true);

    /// <summary>The speed bonus, in percent, of any mount a wizard on a run rides (0 leaves mounts as they are).</summary>
    internal static readonly int MountSpeed = Math.Clamp(ConfigurationManager.GetValue("Roguelike.MountSpeed", 100), 0, 500);

    /// <summary>
    /// The speed a mount gives a wizard on a run, replacing the mount's own, or null to leave the mount as it is.
    /// </summary>
    internal static int? RunMountSpeed(Wizard wizard, Imcodec.ObjectProperty.TypeCache.WizItemTemplate template)
        => MountSpeed > 0 && IsOnRun(wizard)
           && template?.m_behaviors?.OfType<Imcodec.ObjectProperty.TypeCache.MountItemBehaviorTemplate>().Any() == true
            ? MountSpeed
            : null;

    /// <summary>NPCs (by template id) that also sell potion refills, like Hilda Brewer in the Commons.</summary>
    internal static readonly HashSet<ulong> PotionVendors = [.. SplitList(ConfigurationManager.GetValue("Roguelike.PotionVendors", "1560179"))
        .Select(part => ulong.TryParse(part, out var id) ? id : 0)
        .Where(id => id != 0)];

    /// <summary>
    /// Objects (by template id) that offer the trip to the trainers and the next street after a boss: the Rate My
    /// Stitch tome in the Shopping District.
    /// </summary>
    internal static readonly HashSet<ulong> TravelBooks = [.. SplitList(ConfigurationManager.GetValue("Roguelike.TravelBooks", "1560247"))
        .Select(part => ulong.TryParse(part, out var id) ? id : 0)
        .Where(id => id != 0)];

    /// <summary>
    /// On a run, the Commons, Home, world gate and recall buttons do nothing, and the zones in BlockedZones (and
    /// houses) cannot be entered: a wizard who gets there is sent back to their run.
    /// </summary>
    internal static readonly bool BlockTravel = ConfigurationManager.GetValue("Roguelike.BlockTravel", true);

    internal static readonly HashSet<string> BlockedZones = new(
        SplitList(ConfigurationManager.GetValue("Roguelike.BlockedZones", "WizardCity/WC_Hub, WizardCity/WC_Streets/WC_OldeTown")),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>True when this wizard is on a run that keeps them to its own zones.</summary>
    internal static bool TravelBlockedFor(Wizard wizard) => Enabled && BlockTravel && wizard?.Roguelike is not null;

    /// <summary>True for a zone a wizard on a run may not enter: the Commons, Olde Town and houses by default.</summary>
    internal static bool IsBlockedZone(string zonePath)
        => !string.IsNullOrEmpty(zonePath)
           && (BlockedZones.Contains(zonePath)
               || zonePath.StartsWith("Housing/", StringComparison.OrdinalIgnoreCase)
               || Imlight.CoreLib.Game.Housing.HouseZoneIndex.IsHouseZone(zonePath));

    internal const string TravelBlockedText = "That way is closed during your run.";

    /// <summary>On a run, NPCs only offer main story quests.</summary>
    internal static readonly bool MainQuestsOnly = ConfigurationManager.GetValue("Roguelike.MainQuestsOnly", true);

    /// <summary>
    /// True when an NPC may offer this quest to this wizard: always off a run, and on a run only main story quests
    /// (the game marks them mainline; a "-MAIN-" in the quest's code counts too).
    /// </summary>
    internal static bool OffersQuest(Wizard wizard, QuestTemplate quest)
        => !(Enabled && MainQuestsOnly && wizard?.Roguelike is not null)
           || quest is null
           || quest.m_mainline
           || ((string) quest.m_questName ?? "").Contains("-MAIN-", StringComparison.OrdinalIgnoreCase);

    /// <summary>When a run's boss falls, every quest the wizard has is completed, with its rewards.</summary>
    internal static readonly bool CompleteQuestsAfterBoss = ConfigurationManager.GetValue("Roguelike.CompleteQuestsAfterBoss", true);

    /// <summary>The chance, 0 to 100, that completing a quest on a run also gives a random piece of gear.</summary>
    internal static readonly int QuestItemChance = Math.Clamp(ConfigurationManager.GetValue("Roguelike.QuestItemChance", 50), 0, 100);

    /// <summary>On a run, the Crown Shop button shows the wizard's stats in chat instead of opening the shop.</summary>
    internal static readonly bool ShopButtonShowsStats = ConfigurationManager.GetValue("Roguelike.ShopButtonShowsStats", true);

    /// <summary>
    /// NPCs (template ids) that teach another trainer's spells, as "npc:trainer" pairs: Mellori teaches what the Death
    /// professor Dworgyn does, and Monstrologist Burke what Mortis does.
    /// </summary>
    internal static readonly Dictionary<ulong, ulong> TrainerStandIns = SplitList(
            ConfigurationManager.GetValue("Roguelike.TrainerStandIns", "1381151:82209, 1401108:39390"))
        .Select(pair => pair.Split(':'))
        .Where(parts => parts.Length == 2 && ulong.TryParse(parts[0].Trim(), out _) && ulong.TryParse(parts[1].Trim(), out _))
        .GroupBy(parts => ulong.Parse(parts[0].Trim()))
        .ToDictionary(group => group.Key, group => ulong.Parse(group.First()[1].Trim()));

    /// <summary>The name the travel tome shows while it offers its choices.</summary>
    internal static readonly string TravelBookName = ConfigurationManager.GetValue("Roguelike.TravelBookName", "Tome of Travels");

    /// <summary>Show the street's time left in the game's own zone timer at the top of the screen.</summary>
    internal static readonly bool ShowTimer = ConfigurationManager.GetValue("Roguelike.ShowTimer", true);

    /// <summary>The zone timer's title.</summary>
    internal static readonly string TimerTitle = ConfigurationManager.GetValue("Roguelike.TimerTitle", "Boss arrives in");

    /// <summary>
    /// How the timer token's expire time is written: "absolute" (Unix seconds when it runs out), "relative" (seconds
    /// left) or "zero" (leave it to the duration). Try them with .run timerui.
    /// </summary>
    internal static readonly string TimerExpireMode = ConfigurationManager.GetValue("Roguelike.TimerExpireMode", "absolute");

    /// <summary>Where .run train sends a wizard during the shop phase, for the spell trainers.</summary>
    internal static readonly string TrainZone = ConfigurationManager.GetValue("Roguelike.TrainZone", "WizardCity/WC_Ravenwood");

    internal static readonly IReadOnlyList<RoguelikeTier> Tiers = ReadTiers();

    internal static int TierCount => Tiers.Count;

    /// <summary>How long a tier's street lasts before its boss, in seconds.</summary>
    internal static int StreetSecondsFor(int tier) => GetTier(tier) is { StreetSeconds: > 0 } t ? t.StreetSeconds : StreetSeconds;

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

            var minutes = ConfigurationManager.GetValue($"Roguelike.Tier{i}Minutes", 0);
            tiers.Add(new RoguelikeTier(i, parts[0], parts[1]) {
                Quests = SplitList(ConfigurationManager.GetValue($"Roguelike.Tier{i}Quests", "")),
                BossQuests = SplitList(ConfigurationManager.GetValue($"Roguelike.Tier{i}BossQuests", "")),
                StreetSeconds = minutes > 0 ? minutes * 60 : StreetSeconds,
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
