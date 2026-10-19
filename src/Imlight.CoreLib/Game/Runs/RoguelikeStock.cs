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

/*
 * Random shop and trainer stock for roguelike runs.
 *
 * Every wizard on a run sees their own stock: each gear slot a vendor sells is filled with random gear of the
 * same slot, and each spell a trainer teaches is replaced by a random trainer spell from any school and world.
 * Everything is picked from a level window around the wizard's level ([Roguelike] StockLevelsBelow and
 * StockLevelsAbove), never above MaxStockLevel (50, Malistaire's level). The picks are seeded by the wizard,
 * the NPC and the tier, so they stay the same while browsing and buying, and change every tier.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.Randomization;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Runs;

internal static class RoguelikeStock {

    internal static readonly bool RandomShops = ConfigurationManager.GetValue("Roguelike.RandomShops", true);
    internal static readonly bool RandomTrainers = ConfigurationManager.GetValue("Roguelike.RandomTrainers", true);
    private static readonly int s_maxLevel = Math.Clamp(ConfigurationManager.GetValue("Roguelike.MaxStockLevel", 50), 1, 200);
    private static readonly int s_levelsBelow = Math.Max(0, ConfigurationManager.GetValue("Roguelike.StockLevelsBelow", 10));
    private static readonly int s_levelsAbove = Math.Max(0, ConfigurationManager.GetValue("Roguelike.StockLevelsAbove", 5));

    /// <summary>How many random spells each trainer offers a wizard on a run.</summary>
    internal static readonly int TrainerSpellCount = Math.Clamp(ConfigurationManager.GetValue("Roguelike.TrainerSpells", 10), 1, 50);

    // Wearable gear. Housing, pets, mounts, reagents and the like stay as the vendor sells them.
    private static readonly HashSet<ObjectType> s_gearTypes = [
        ObjectType.OT_EQUIP_HEAD, ObjectType.OT_EQUIP_CHEST, ObjectType.OT_EQUIP_LEGS,
        ObjectType.OT_EQUIP_HANDS, ObjectType.OT_EQUIP_FINGER, ObjectType.OT_EQUIP_FEET,
        ObjectType.OT_EQUIP_EAR,
    ];

    private sealed record GearEntry(uint Id, int Level, string School);

    private sealed record SpellEntry(uint Id, int Level);

    private static readonly Lazy<Dictionary<ObjectType, List<GearEntry>>> s_gear
        = new(BuildGearPool, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<List<SpellEntry>> s_spells
        = new(BuildSpellPool, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>True when this wizard's shops are randomized.</summary>
    internal static bool ShopsActiveFor(Wizard wizard)
        => RandomShops && RoguelikeConfig.Enabled && wizard?.Roguelike is not null;

    /// <summary>True when this wizard's trainers are randomized.</summary>
    internal static bool TrainersActiveFor(Wizard wizard)
        => RandomTrainers && RoguelikeConfig.Enabled && wizard?.Roguelike is not null;

    // ---------------------------------------------------------------------
    // Shops
    // ---------------------------------------------------------------------

    /// <summary>
    /// The vendor's stock as this wizard sees it. Each gear slot becomes random gear of the same slot, for the
    /// wizard's school or any school, within the wizard's level window. Anything else is left as it is.
    /// </summary>
    internal static List<GID> ShopFor(Wizard wizard, ulong vendorTemplateId, IReadOnlyList<GID> original) {
        if (!ShopsActiveFor(wizard) || original is null) {
            return original?.ToList() ?? [];
        }

        var (low, high) = LevelWindow(wizard);
        var school = wizard.MagicSchoolBehavior.MagicSchool.ToString();
        var random = new Random(Seed(wizard, "shop", vendorTemplateId));
        var used = new HashSet<uint>();
        var stock = new List<GID>(original.Count);

        // Shuffled candidates per slot, drawn from in order so the same item is not offered twice.
        var queues = new Dictionary<ObjectType, Queue<GearEntry>>();

        foreach (var gid in original) {
            var type = (CoreObjectFactory.GetCoreTemplate(gid.MParts.TemplateId) as WizItemTemplate)?.m_nObjectType;
            if (type is not { } slot || !s_gearTypes.Contains(slot)) {
                stock.Add(gid);
                continue;
            }

            if (!queues.TryGetValue(slot, out var queue)) {
                queue = new Queue<GearEntry>(Candidates(slot, school, low, high).OrderBy(_ => random.Next()));
                queues[slot] = queue;
            }

            GearEntry pick = null;
            while (queue.Count > 0) {
                var candidate = queue.Dequeue();
                if (used.Add(candidate.Id)) {
                    pick = candidate;
                    break;
                }
            }

            if (pick is null) {
                // Ran out of gear for this slot: keep the vendor's own item.
                if (used.Add(gid.MParts.TemplateId)) {
                    stock.Add(gid);
                }

                continue;
            }

            // Keep the upper bits of the vendor's entry; only the template changes.
            stock.Add(new GID((gid.Full & 0xFFFFFFFF00000000UL) | pick.Id));
        }

        Logger.Information("Roguelike shop {Vendor} for {Wizard}: {Swapped} of {Total} items are random gear, levels {Low}-{High}.",
            Logger.Args(vendorTemplateId, wizard.PlayerNameBehavior?.GetWizardName() ?? wizard.CharId.ToString(),
                stock.Count(g => !original.Contains(g)), original.Count, low, high));

        return stock;
    }

    private static IEnumerable<GearEntry> Candidates(ObjectType slot, string school, int low, int high) {
        if (!s_gear.Value.TryGetValue(slot, out var all)) {
            return [];
        }

        var inWindow = all.Where(g => g.Level >= low && g.Level <= high).ToList();
        var forSchool = inWindow.Where(g => IsWearableBy(g.School, school)).ToList();

        // Prefer gear the wizard can wear; fall back to any school if there is too little.
        return forSchool.Count >= 4 ? forSchool : inWindow;
    }

    private static bool IsWearableBy(string itemSchool, string wizardSchool)
        => string.IsNullOrEmpty(itemSchool)
           || itemSchool.Equals("Any", StringComparison.OrdinalIgnoreCase)
           || itemSchool.Equals("All", StringComparison.OrdinalIgnoreCase)
           || itemSchool.Equals(wizardSchool, StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------------
    // Trainers
    // ---------------------------------------------------------------------

    /// <summary>
    /// The spell this wizard is offered at a trainer's <paramref name="index"/>, and the level it needs, or null
    /// when trainers are not randomized for them. Spells come from every trainer in the game, any school, within
    /// the wizard's level window.
    /// </summary>
    internal static (uint SpellId, int Level)? TrainerSpellFor(Wizard wizard, ulong trainerTemplateId, int index, int count) {
        if (!TrainersActiveFor(wizard) || index < 0 || index >= count) {
            return null;
        }

        var picks = TrainerSpellsFor(wizard, trainerTemplateId, count);

        return index < picks.Count ? (picks[index].Id, picks[index].Level) : null;
    }

    private static List<SpellEntry> TrainerSpellsFor(Wizard wizard, ulong trainerTemplateId, int count) {
        // Trainers draw from every level up to the cap, not the wizard's window: higher spells show with the level
        // they need, as something to work toward.
        var random = new Random(Seed(wizard, "trainer", trainerTemplateId));

        return [.. s_spells.Value
            .OrderBy(_ => random.Next())
            .Take(Math.Min(count, TrainerSpellCount))
            .OrderBy(s => s.Level)];
    }

    // ---------------------------------------------------------------------
    // Shared
    // ---------------------------------------------------------------------

    /// <summary>The level range stock is picked from: around the wizard's level, never above the cap.</summary>
    private static (int Low, int High) LevelWindow(Wizard wizard) {
        var level = Math.Max(1, (int) wizard.MagicSchoolBehavior.Level);
        var high = Math.Clamp(level + s_levelsAbove, 1, s_maxLevel);
        var low = Math.Clamp(level - s_levelsBelow, 1, high);

        return (low, high);
    }

    private static int Seed(Wizard wizard, string what, ulong npcTemplateId) {
        var run = wizard.Roguelike;
        var seed = SeededShuffle.Mix(wizard.CharId)
                   ^ SeededShuffle.Fnv1a(what)
                   ^ SeededShuffle.Mix(npcTemplateId)
                   ^ SeededShuffle.Mix((ulong) (run?.Tier ?? 0) << 32 | (uint) (run?.BossesDefeated ?? 0));

        return (int) (SeededShuffle.Mix(seed) & 0x7FFFFFFF);
    }

    private static Dictionary<ObjectType, List<GearEntry>> BuildGearPool() {
        // Gear any vendor sells or any drop table gives, so everything is obtainable in the normal game.
        var ids = SpiralDB.NpcInventories.Values
            .Where(inventory => inventory?.Inventory is not null)
            .SelectMany(inventory => inventory.Inventory)
            .Select(gid => (ulong) gid.MParts.TemplateId)
            .Concat(SpiralDB.DropTables.Values
                .Where(table => table?.Items is not null)
                .SelectMany(table => table.Items)
                .Select(item => ulong.TryParse(item.ItemId, out var id) ? id : 0))
            .Where(id => id is > 0 and <= uint.MaxValue)
            .Distinct();

        var pool = new Dictionary<ObjectType, List<GearEntry>>();
        foreach (var id in ids) {
            if (CoreObjectFactory.GetCoreTemplate(id) is not WizItemTemplate item || !s_gearTypes.Contains(item.m_nObjectType)) {
                continue;
            }

            var level = Math.Max(1, EquipLevel(item));
            if (level > s_maxLevel) {
                continue;
            }

            string school = item.m_school;
            if (!pool.TryGetValue(item.m_nObjectType, out var list)) {
                pool[item.m_nObjectType] = list = [];
            }

            list.Add(new GearEntry((uint) id, level, school ?? ""));
        }

        Logger.Information("Roguelike: {Count} pieces of gear up to level {Max} for random shops.",
            Logger.Args(pool.Values.Sum(l => l.Count), s_maxLevel));

        return pool;
    }

    private static List<SpellEntry> BuildSpellPool() {
        // Every spell some trainer teaches, at the lowest level any trainer asks for it.
        var spells = SpiralDB.NpcSpellInventories.Values
            .Where(inventory => inventory?.Spells is not null)
            .SelectMany(inventory => inventory.Spells)
            .Where(entry => entry.TemplateID is > 0 and <= uint.MaxValue)
            .GroupBy(entry => entry.TemplateID)
            .Select(group => new SpellEntry((uint) group.Key, Math.Max(1, group.Min(entry => entry.Level))))
            .Where(entry => entry.Level <= s_maxLevel
                            && CoreObjectFactory.GetCoreTemplate(entry.Id) is SpellTemplate { m_Treasure: false })
            .ToList();

        Logger.Information("Roguelike: {Count} trainer spells up to level {Max} for random trainers.",
            Logger.Args(spells.Count, s_maxLevel));

        return spells;
    }

    private static int EquipLevel(ItemTemplate item)
        => (int) (item.m_equipRequirements?.m_requirements?
            .OfType<ReqMagicLevel>()
            .Select(req => req.m_numericValue)
            .DefaultIfEmpty(0)
            .Max() ?? 0);

}
