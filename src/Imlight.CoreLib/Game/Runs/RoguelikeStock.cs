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
    private static readonly bool s_shopDropGear = ConfigurationManager.GetValue("Roguelike.ShopDropGear", true);
    private static readonly bool s_shopWorldGear = ConfigurationManager.GetValue("Roguelike.ShopWorldGear", true);

    /// <summary>
    /// Gear from anywhere in the game's files, by its slot folder (ObjectData/EM Equipment/Hats/..., ObjectData/Gauntlet/
    /// Catacombs/Rings/..., ObjectData/Tier6/Robes/...): world drops, gauntlet, dungeon and boss gear that SpiralDB's
    /// vendor and drop tables leave out. Not crown, pack, PvP, tourney, raid, polymorph, blank, monster or henchman gear.
    /// </summary>
    private static readonly string[] s_notShopGearFolders = [
        "CrownItems", "World Pack Gear", "SpecialSets", "Blanks", "PVP", "Tourney", "Raids", "Polymorph", "MonsterItems",
        "HenchmenEquipment", "NewEquipment", "Community Event", "Housing", "Pets", "Mounts", "QA",
    ];

    private static bool IsWorldGearPath(string path) {
        var parts = (path ?? "").Replace('\\', '/').Split('/');
        if (parts.Length < 3 || !parts[0].Equals("ObjectData", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        // ObjectData/Decks/ holds the creatures' decks (Mdeck-...), not decks a wizard can buy.
        if (parts[1].Equals("Decks", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        return !parts.Take(parts.Length - 1).Any(folder => s_notShopGearFolders.Any(
            no => folder.StartsWith(no, StringComparison.OrdinalIgnoreCase)));
    }

    // Shops and trainers stock up to MaxStockLevel, or the highest tier level (TierNLevel) when that is higher.
    private static readonly int s_maxLevel = Math.Clamp(
        Math.Max(ConfigurationManager.GetValue("Roguelike.MaxStockLevel", 50), RoguelikeConfig.HighestTierLevel), 1, 200);
    private static readonly int s_levelsBelow = Math.Max(0, ConfigurationManager.GetValue("Roguelike.StockLevelsBelow", 10));
    private static readonly int s_levelsAbove = Math.Max(0, ConfigurationManager.GetValue("Roguelike.StockLevelsAbove", 5));

    /// <summary>The chance, 0 to 100, that winning a battle on a run drops a random piece of crown gear.</summary>
    internal static readonly int CrownDropChance = Math.Clamp(ConfigurationManager.GetValue("Roguelike.CrownDropChance", 10), 0, 100);

    // How many random crown items to look at for one that fits the wizard, before giving up on the drop.
    private const int CROWN_DROP_TRIES = 300;

    // Crown gear template ids by slot, from the CrownItems folders. Only paths are read up front; templates load on
    // demand. Grouped by slot so every slot drops as often: there is far more crown headgear than crown athames.
    private static readonly Lazy<Dictionary<string, uint[]>> s_crownGearIds = new(BuildCrownGearIds, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>How many random spells each trainer offers a wizard on a run (the school professors and the rest).</summary>
    internal static readonly int TrainerSpellCount = Math.Clamp(ConfigurationManager.GetValue("Roguelike.TrainerSpells", 15), 1, 50);

    /// <summary>[Roguelike] TreeSpells: how many random spells each of Ravenwood's school trees offers on a run.</summary>
    internal static readonly int TreeSpellCount = Math.Clamp(ConfigurationManager.GetValue("Roguelike.TreeSpells", 8), 1, 50);

    /// <summary>
    /// [Roguelike] TreeTrainers: the trainers that offer TreeSpells instead of TrainerSpells (template ids): Ravenwood's
    /// school trees, Fire 44207, Ice 82294, Life 39158, Storm 39178, Myth 38275 and Death (Mortis) 39390.
    /// </summary>
    internal static readonly HashSet<ulong> TreeTrainers = [.. ConfigurationManager.GetValue("Roguelike.TreeTrainers", "44207, 82294, 39158, 39178, 38275, 39390")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(id => ulong.TryParse(id, out var v) ? v : 0).Where(v => v != 0)];

    // Wearable gear, told apart by the folder its template lives in (ObjectData/Tier1/Hats/Hat-T1-042.xml):
    // the templates' object type does not reliably name the slot. Housing, pets, mounts, reagents, jewels and
    // the like stay as the vendor sells them.
    private static readonly Dictionary<string, string> s_slotFolders = new(StringComparer.OrdinalIgnoreCase) {
        ["Hats"] = "Hat", ["Hat"] = "Hat",
        ["Robes"] = "Robe", ["Robe"] = "Robe",
        ["Shoes"] = "Shoes", ["Boot"] = "Shoes", ["Boots"] = "Shoes",
        ["Wands"] = "Wand", ["Wand"] = "Wand",
        ["Athames"] = "Athame", ["Athame"] = "Athame",
        ["Amulets"] = "Amulet", ["Amulet"] = "Amulet",
        ["Rings"] = "Ring", ["Ring"] = "Ring",
        ["Decks"] = "Deck", ["Deck"] = "Deck",
    };

    private static readonly Lazy<Dictionary<ulong, string>> s_templatePaths
        = new(BuildTemplatePaths, LazyThreadSafetyMode.ExecutionAndPublication);

    private sealed record GearEntry(uint Id, int Level, string School);

    private sealed record SpellEntry(uint Id, int Level);

    private static readonly Lazy<Dictionary<string, List<GearEntry>>> s_gear
        = new(BuildGearPool, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<List<SpellEntry>> s_spells
        = new(BuildSpellPool, LazyThreadSafetyMode.ExecutionAndPublication);

    // The spells of the test trainers a run retires (Mr. Lincoln, Mellori, Merle Ambrose), for the random trainers.
    private static readonly Lazy<List<SpellEntry>> s_retiredSpells
        = new(BuildRetiredSpellPool, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The chance, 0 to 100, that each spell a random trainer offers comes from the retired test trainers' spells
    /// (enchantments, Shadow spells, monster and boss spells) rather than the normal trainer spells.
    /// </summary>
    private static readonly int s_retiredSpellShare
        = Math.Clamp(ConfigurationManager.GetValue("Roguelike.TrainerSpecialSpellShare", 25), 0, 100);

    /// <summary>
    /// True for a starter item a run must not begin with: QA gear (cheat resist and speed stats) and mounts (the
    /// run gives its own).
    /// </summary>
    internal static bool IsCheatStarterItem(ulong templateId)
        => s_templatePaths.Value.TryGetValue(templateId, out var path)
           && (path.Contains("/QA_", StringComparison.OrdinalIgnoreCase)
               || path.Contains("QA_Gear", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("ObjectData/Mounts/", StringComparison.OrdinalIgnoreCase));

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
    internal static List<GID> ShopFor(Wizard wizard, ulong vendorTemplateId, IReadOnlyList<GID> original, string shopTitle = null) {
        if (!ShopsActiveFor(wizard) || original is null) {
            return original?.ToList() ?? [];
        }

        // The pet and jewel vendors get stock of their own kind instead of gear.
        if (PetShopFor(wizard, vendorTemplateId, original) is { } pets) {
            return pets;
        }

        if (JewelShopFor(wizard, vendorTemplateId, original, shopTitle) is { } jewels) {
            return jewels;
        }

        var (low, high) = LevelWindow(wizard);
        var school = wizard.MagicSchoolBehavior.MagicSchool.ToString();
        var random = new Random(Seed(wizard, "shop", vendorTemplateId));
        var used = new HashSet<uint>();
        var stock = new List<GID>(original.Count);

        // Shuffled candidates per slot, drawn from in order so the same item is not offered twice.
        var queues = new Dictionary<string, Queue<GearEntry>>();

        foreach (var gid in original) {
            if (SlotOf(gid.MParts.TemplateId) is not { } slot) {
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

    /// <summary>[Roguelike] PetShopCount: how many random pets a pet vendor sells on a run.</summary>
    internal static readonly int PetShopCount = Math.Clamp(ConfigurationManager.GetValue("Roguelike.PetShopCount", 5), 1, 30);

    /// <summary>[Roguelike] JewelShopRange: a jewel vendor sells jewels this many levels above or below the wizard.</summary>
    internal static readonly int JewelShopRange = Math.Clamp(ConfigurationManager.GetValue("Roguelike.JewelShopRange", 5), 0, 100);

    /// <summary>[Roguelike] JewelShopMaxLevel: the highest level of jewel a jewel vendor sells.</summary>
    internal static readonly int JewelShopMaxLevel = Math.Clamp(ConfigurationManager.GetValue("Roguelike.JewelShopMaxLevel", 100), 1, 200);

    /// <summary>[Roguelike] JewelShopCount: how many jewels a jewel vendor sells (0 keeps the vendor's own count).</summary>
    internal static readonly int JewelShopCount = Math.Clamp(ConfigurationManager.GetValue("Roguelike.JewelShopCount", 0), 0, 60);

    // A vendor whose stock is mostly of one kind is that kind of shop.
    private static bool MostlyOf(IReadOnlyList<GID> original, Func<uint, bool> isKind) {
        var count = original.Count(g => isKind(g.MParts.TemplateId));

        return count > 0 && count * 2 >= original.Count;
    }

    /// <summary>
    /// [Roguelike] JewelShops: vendors that sell random jewels on a run even with no stock of their own, by NPC
    /// template id or shop title key. Default: Katherine RockHammer's Jewel Works in the Shopping District.
    /// </summary>
    private static readonly HashSet<string> s_jewelShops = new(
        ConfigurationManager.GetValue("Roguelike.JewelShops", "1233878, NPCs_01748438, WizardShops_00000180")
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// [Roguelike] JewelShopWindow: the shop window kind (m_furnitureShop) a jewel vendor opens. 7 is the game's jewel
    /// shop window, read from Katherine RockHammer's own shop (2 reagents, 5 pet snacks, 0 the robe shop).
    /// </summary>
    internal static readonly int JewelShopWindow = ConfigurationManager.GetValue("Roguelike.JewelShopWindow", 7);

    /// <summary>True when a vendor sells jewels: it is listed in JewelShops, or most of its stock is jewels.</summary>
    internal static bool IsJewelVendor(ulong vendorTemplateId, string shopTitle, IReadOnlyList<GID> stock)
        => IsJewelShop(vendorTemplateId, shopTitle) || (stock is { Count: > 0 } && MostlyOf(stock, IsJewelTemplate));

    private static bool IsJewelShop(ulong vendorTemplateId, string shopTitle)
        => s_jewelShops.Contains(vendorTemplateId.ToString())
           || (!string.IsNullOrEmpty(shopTitle) && s_jewelShops.Contains(shopTitle));

    private static bool IsJewelTemplate(uint id)
        => CoreObjectFactory.GetCoreTemplate(id) is WizItemTemplate t && Imlight.CoreLib.Shared.Items.JewelSockets.IsJewel(t);

    /// <summary>
    /// A pet vendor's stock on a run: <see cref="PetShopCount"/> random pets in place of its own (anything else it
    /// sells stays). The pet's age, talents and name are rolled when it is bought. Null when this is not a pet vendor.
    /// </summary>
    private static List<GID> PetShopFor(Wizard wizard, ulong vendorTemplateId, IReadOnlyList<GID> original) {
        if (!MostlyOf(original, Imlight.CoreLib.Game.Pet.PetFactory.IsPetTemplate)) {
            return null;
        }

        var random = new Random(Seed(wizard, "petshop", vendorTemplateId));
        var upper = original.First(g => Imlight.CoreLib.Game.Pet.PetFactory.IsPetTemplate(g.MParts.TemplateId)).Full & 0xFFFFFFFF00000000UL;
        var picks = RoguelikePets.ShopPool
            .Where(IsGoldShopPet)
            .OrderBy(_ => random.Next())
            .Take(PetShopCount)
            .Select(id => new GID(upper | id))
            .ToList();

        var stock = original.Where(g => !Imlight.CoreLib.Game.Pet.PetFactory.IsPetTemplate(g.MParts.TemplateId)).ToList();
        stock.InsertRange(0, picks);

        Logger.Information("Roguelike pet shop {Vendor} for {Wizard}: {Count} random pets.",
            Logger.Args(vendorTemplateId, wizard.PlayerNameBehavior?.GetWizardName() ?? wizard.CharId.ToString(), picks.Count));

        return stock;
    }

    /// <summary>
    /// A jewel vendor's stock on a run: random jewels and pins (no Magic Weaving, no crown jewels) from
    /// <see cref="JewelShopRange"/> levels below to that many above the wizard, never above
    /// <see cref="JewelShopMaxLevel"/>. Null when this is not a jewel vendor.
    /// </summary>
    private static List<GID> JewelShopFor(Wizard wizard, ulong vendorTemplateId, IReadOnlyList<GID> original, string shopTitle) {
        if (!MostlyOf(original, IsJewelTemplate) && !IsJewelShop(vendorTemplateId, shopTitle)) {
            return null;
        }

        // As for gear, a wizard above JewelShopMaxLevel shops as one at it (the window slides down).
        var level = Math.Clamp((int) wizard.MagicSchoolBehavior.Level, 1, JewelShopMaxLevel);
        var high = Math.Min(level + JewelShopRange, JewelShopMaxLevel);
        var low = Math.Max(1, Math.Min(level - JewelShopRange, high));
        var vendorCount = original.Count(g => IsJewelTemplate(g.MParts.TemplateId));
        // At least 30, so every shape (square, circle, triangle, tear, pet, pins) has some.
        var count = JewelShopCount > 0 ? JewelShopCount : Math.Max(30, vendorCount);

        var random = new Random(Seed(wizard, "jewelshop", vendorTemplateId));
        var upper = original.Where(g => IsJewelTemplate(g.MParts.TemplateId)).Select(g => g.Full).FirstOrDefault() & 0xFFFFFFFF00000000UL;

        // Shape by shape in turn, so pins (most of the high-level pool) don't crowd the jewels out.
        var byShape = s_jewels.Value
            .Where(j => j.Level >= low && j.Level <= high)
            .GroupBy(j => j.Shape)
            .Select(g => new Queue<JewelEntry>(g.OrderBy(_ => random.Next())))
            .OrderBy(_ => random.Next())
            .ToList();
        var chosen = new List<JewelEntry>();
        while (chosen.Count < count && byShape.Any(q => q.Count > 0)) {
            foreach (var queue in byShape.Where(q => q.Count > 0)) {
                if (chosen.Count >= count) {
                    break;
                }

                chosen.Add(queue.Dequeue());
            }
        }

        var picks = chosen
            .OrderBy(j => j.Shape)
            .ThenBy(j => j.Level)
            .Select(j => new GID(upper | j.Id))
            .ToList();

        if (picks.Count == 0) {
            return null;
        }

        var stock = original.Where(g => !IsJewelTemplate(g.MParts.TemplateId)).ToList();
        stock.InsertRange(0, picks);

        Logger.Information("Roguelike jewel shop {Vendor} for {Wizard}: {Count} random jewels, levels {Low}-{High}.",
            Logger.Args(vendorTemplateId, wizard.PlayerNameBehavior?.GetWizardName() ?? wizard.CharId.ToString(), picks.Count, low, high));

        return stock;
    }

    private static IEnumerable<GearEntry> Candidates(string slot, string school, int low, int high) {
        if (!s_gear.Value.TryGetValue(slot, out var all)) {
            return [];
        }

        var inWindow = all.Where(g => g.Level >= low && g.Level <= high).ToList();
        if (inWindow.Count < 4) {
            // Too little in the window (few decks are that high): the highest pieces below it as well.
            inWindow = [.. inWindow.Concat(all.Where(g => g.Level < low).OrderByDescending(g => g.Level).Take(8))];
        }

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
        var normal = new Queue<SpellEntry>(s_spells.Value.OrderBy(_ => random.Next()));
        var special = new Queue<SpellEntry>(s_retiredSpells.Value.OrderBy(_ => random.Next()));
        var picks = new List<SpellEntry>();
        var seen = new HashSet<uint>();

        while (picks.Count < count && (normal.Count > 0 || special.Count > 0)) {
            var fromSpecial = special.Count > 0 && (normal.Count == 0 || random.Next(100) < s_retiredSpellShare);
            var pick = (fromSpecial ? special : normal).Dequeue();
            if (seen.Add(pick.Id)) {
                picks.Add(pick);
            }
        }

        return [.. picks.OrderBy(s => s.Level)];
    }

    private static List<SpellEntry> BuildRetiredSpellPool() {
        var pool = new List<SpellEntry>();
        try {
            foreach (var (kind, entries) in Imlight.CoreLib.Game.Zone.Components.InteractTrainerComponent.RetiredTrainerSpells()) {
                foreach (var entry in entries) {
                    if (entry.TemplateID is > 0 and <= uint.MaxValue
                        && CoreObjectFactory.GetCoreTemplate(entry.TemplateID) is SpellTemplate spell) {
                        pool.Add(new SpellEntry((uint) entry.TemplateID, LevelForPips(spell)));
                    }
                }
            }
        }
        catch (Exception ex) {
            Logger.Error("Roguelike could not read the test trainers' spells. {Exception}", Logger.Args(ex));
        }

        Logger.Information("Roguelike: {Count} enchantment and Shadow spells join the random trainers ({Share}% of picks).",
            Logger.Args(pool.Count, s_retiredSpellShare));

        return pool;
    }

    /// <summary>
    /// The level a retired trainer's spell needs on a run: those trainers teach at level 1, so it comes from the
    /// spell's pip cost instead (free spells at 1, then 7 levels a pip, at most the cap).
    /// </summary>
    private static int LevelForPips(SpellTemplate spell) {
        // An X-pip spell counts as three pips; a Shadow pip counts on top of the normal pips.
        var pips = spell.m_spellRank is { } rank
            ? (rank.m_xPipSpell ? 3 : (int) rank.m_spellRank) + (int) rank.m_shadowPips
            : 0;

        return Math.Clamp(pips * 7, 1, s_maxLevel);
    }

    // ---------------------------------------------------------------------
    // Crown gear drops
    // ---------------------------------------------------------------------

    /// <summary>
    /// Rolls the rare crown gear drop for a battle won on a run. Returns a piece of crown gear within the wizard's
    /// level window, for their school or any school when one turns up, or null when the roll fails.
    /// </summary>
    internal static uint? RollCrownDrop(Wizard wizard) {
        if (!RoguelikeConfig.Enabled || wizard?.Roguelike is null || CrownDropChance <= 0
            || Random.Shared.Next(100) >= CrownDropChance) {
            return null;
        }

        return PickCrownGear(wizard);
    }

    // ---------------------------------------------------------------------
    // Jewel and pin drops
    // ---------------------------------------------------------------------

    /// <summary>[Roguelike] JewelDropChance: the chance (0 to 100) that a won battle drops a jewel or pin.</summary>
    internal static readonly int JewelDropChance = Math.Clamp(ConfigurationManager.GetValue("Roguelike.JewelDropChance", 100), 0, 100);

    private sealed record JewelEntry(uint Id, int Level, JewelSocketType Shape);

    private static readonly Lazy<List<JewelEntry>> s_jewels = new(BuildJewelPool, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Rolls the battle's jewel drop: a random jewel or pin (any shape: square, circle, triangle, tear, pet, or a pin)
    /// the wizard has the level for, or null.
    /// </summary>
    internal static uint? RollJewel(Wizard wizard) {
        if (!RoguelikeConfig.Enabled || wizard?.Roguelike is null || JewelDropChance <= 0
            || Random.Shared.Next(100) >= JewelDropChance) {
            return null;
        }

        return PickJewel(wizard);
    }

    /// <summary>
    /// A random jewel or pin the wizard has the level for, of a shape when one is named (square, circle, triangle,
    /// tear, pet or pin). Null when none fits.
    /// </summary>
    internal static uint? PickJewel(Wizard wizard, string shape = null) {
        var level = Math.Max(1, (int) (wizard?.MagicSchoolBehavior.Level ?? 1));
        var fits = s_jewels.Value
            .Where(j => j.Level <= level
                        && (string.IsNullOrEmpty(shape)
                            || Imlight.CoreLib.Shared.Items.JewelSockets.ShapeName(j.Shape).Contains(shape, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (fits.Count == 0) {
            return null;
        }

        // A shape first, each as likely as the others, then a jewel of it: so pet jewels and pins turn up as often as
        // square jewels, however many of each the game has.
        var shapes = fits.Select(j => j.Shape).Distinct().ToList();
        var picked = shapes[Random.Shared.Next(shapes.Count)];
        var ofShape = fits.Where(j => j.Shape == picked).ToList();

        return ofShape[Random.Shared.Next(ofShape.Count)].Id;
    }

    /// <summary>A developer test item: "test" in its file name, or "TEST" in the name it shows.</summary>
    private static bool IsTestItem(string path, ItemTemplate template) {
        if (path.Contains("test", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        string key = template.m_displayName;

        return !string.IsNullOrEmpty(key)
               && Imlight.CoreLib.Shared.Resources.Locale.GetEnglishName(key) is { Length: > 0 } shown
               && shown.Contains("test", StringComparison.OrdinalIgnoreCase);
    }

    private static List<JewelEntry> BuildJewelPool() {
        var list = new List<JewelEntry>();
        try {
            foreach (var (id, path) in s_templatePaths.Value) {
                if (id is 0 or > uint.MaxValue || !path.Contains("Jewel", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("Recipe", StringComparison.OrdinalIgnoreCase)
                    || CoreObjectFactory.GetCoreTemplate(id) is not WizItemTemplate template
                    || !Imlight.CoreLib.Shared.Items.JewelSockets.IsJewel(template)
                    // Magic Weaving is not on this server.
                    || Imlight.CoreLib.Shared.Items.JewelSockets.IsMagicWeaving(template)
                    // Developer test jewels ("TESTING PURPOSES", the ...TEST jewels).
                    || IsTestItem(path, template)
                    // Crown-only and no-trade jewels stay out, like crown gear in the gold shops.
                    || IsCrownItem(id)) {
                    continue;
                }

                list.Add(new JewelEntry((uint) id, Math.Max(1, EquipLevel(template)), Imlight.CoreLib.Shared.Items.JewelSockets.GetShape(template)!.Value));
            }
        }
        catch (Exception ex) {
            Logger.Error("Roguelike could not build the jewel pool. {0}", Logger.Args(ex.Message));
        }

        Logger.Information("Roguelike: {0} jewels and pins can drop.", Logger.Args(list.Count));

        return list;
    }

    /// <summary>
    /// Rolls the random gear a quest completed on a run may give (QuestItemChance): crown gear or ordinary gear,
    /// as likely as each other, in the wizard's level window and for their school when one turns up.
    /// </summary>
    internal static uint? RollQuestItem(Wizard wizard) {
        if (!RoguelikeConfig.Enabled || wizard?.Roguelike is null || RoguelikeConfig.QuestItemChance <= 0
            || Random.Shared.Next(100) >= RoguelikeConfig.QuestItemChance) {
            return null;
        }

        return Random.Shared.Next(2) == 0
            ? PickCrownGear(wizard) ?? PickGear(wizard)
            : PickGear(wizard) ?? PickCrownGear(wizard);
    }

    // ---------------------------------------------------------------------
    // Spells learned from battles, quests and bosses
    // ---------------------------------------------------------------------

    internal static readonly int BattleSpellChance = Math.Clamp(ConfigurationManager.GetValue("Roguelike.BattleSpellChance", 10), 0, 100);
    internal static readonly int QuestSpellChance = Math.Clamp(ConfigurationManager.GetValue("Roguelike.QuestSpellChance", 25), 0, 100);
    internal static readonly int BossSpellChance = Math.Clamp(ConfigurationManager.GetValue("Roguelike.BossSpellChance", 100), 0, 100);

    /// <summary>
    /// Rolls a spell for the wizard to learn, with the given chance (0 to 100): one the random trainers could offer
    /// (the trainer spells, plus the enchantment and Shadow spells of the trainers a run retires), that the wizard
    /// does not know yet and has the level for. Null when the roll fails or there is nothing left to learn.
    /// </summary>
    internal static uint? RollSpellToLearn(Wizard wizard, int chance) {
        if (!RoguelikeConfig.Enabled || wizard?.Roguelike is null || chance <= 0 || Random.Shared.Next(100) >= chance) {
            return null;
        }

        var level = Math.Max(1, (int) wizard.MagicSchoolBehavior.Level);
        var candidates = s_spells.Value.Concat(s_retiredSpells.Value)
            .Where(spell => spell.Level <= level && !wizard.SpellbookBehavior.HasSpell(spell.Id))
            .ToList();

        return candidates.Count == 0 ? null : candidates[Random.Shared.Next(candidates.Count)].Id;
    }

    // ---------------------------------------------------------------------
    // Random creature decks
    // ---------------------------------------------------------------------

    /// <summary>
    /// [Roguelike] RandomCreatureDecks: the creatures a run swaps in fight with a random deck of normal trainer
    /// spells (no developer, monster or boss cards) for their level and school, instead of their own.
    /// </summary>
    internal static readonly bool RandomCreatureDecks = ConfigurationManager.GetValue("Roguelike.RandomCreatureDecks", true);
    private static readonly int s_creatureAttacks = Math.Clamp(ConfigurationManager.GetValue("Roguelike.CreatureDeckAttacks", 4), 1, 10);
    private static readonly int s_creatureSupport = Math.Clamp(ConfigurationManager.GetValue("Roguelike.CreatureDeckSupport", 3), 0, 10);
    // Spells up to this many levels above the creature's own can turn up, so low creatures still get a few.
    private static readonly int s_creatureLevelLead = Math.Max(0, ConfigurationManager.GetValue("Roguelike.CreatureDeckLevelLead", 2));

    private static readonly int s_bossDeckExtra = Math.Clamp(ConfigurationManager.GetValue("Roguelike.BossDeckExtra", 3), 0, 10);
    private static readonly string[] s_attackTypes = ["Damage", "AOE", "Steal"];
    private static readonly string[] s_supportTypes = ["Heal", "Charm", "Ward", "Curse", "Global"];

    /// <summary>
    /// A random deck for a creature on a run: CreatureDeckAttacks attack spells and CreatureDeckSupport others
    /// (blades, shields, heals, traps, globals), all spells the trainers teach at up to the creature's level, of
    /// its school where there are enough. Empty when nothing fits.
    /// </summary>
    internal static List<uint> RollCreatureDeck(int level, string school, bool isBoss = false) {
        var deck = new List<uint>();
        try {
            // A boss's level is its tier's (TierNLevel): spells up to it, no lead.
            var cap = Math.Max(1, level) + (isBoss ? 0 : s_creatureLevelLead);
            var usable = s_spells.Value
                .Where(entry => entry.Level <= cap)
                .Select(entry => (entry.Id, Spell: CoreObjectFactory.GetCoreTemplate(entry.Id) as SpellTemplate))
                .Where(x => x.Spell is not null && !x.Spell.m_Treasure && x.Spell.m_spellRank is { m_shadowPips: 0 }
                            && !Imlight.CoreLib.Game.Combat.CombatEnchantments.IsEnchantment(x.Spell))
                .ToList();

            bool OfType(SpellTemplate spell, string[] types)
                => types.Any(t => string.Equals((string) spell.m_sTypeName, t, StringComparison.OrdinalIgnoreCase));
            bool OfSchool(SpellTemplate spell)
                => string.IsNullOrEmpty(school) || string.Equals((string) spell.m_sMagicSchoolName, school, StringComparison.OrdinalIgnoreCase);

            void Take(string[] types, int count) {
                // Its own school first; other schools fill in when it has too few.
                var own = usable.Where(x => OfType(x.Spell, types) && OfSchool(x.Spell)).Select(x => x.Id).ToList();
                var other = usable.Where(x => OfType(x.Spell, types) && !OfSchool(x.Spell)).Select(x => x.Id).ToList();
                foreach (var pool in new[] { own, other }) {
                    while (count > 0 && pool.Count > 0) {
                        var i = Random.Shared.Next(pool.Count);
                        if (!deck.Contains(pool[i])) {
                            deck.Add(pool[i]);
                            count--;
                        }

                        pool.RemoveAt(i);
                    }
                }
            }

            // Bosses get BossDeckExtra more of each.
            Take(s_attackTypes, s_creatureAttacks + (isBoss ? s_bossDeckExtra : 0));
            Take(s_supportTypes, s_creatureSupport + (isBoss ? s_bossDeckExtra : 0));
        }
        catch (Exception ex) {
            Logger.Warning("Roguelike could not build a creature deck. {Message}", Logger.Args(ex.Message));
        }

        return deck;
    }

    /// <summary>A random piece of the gear random shops sell, in the wizard's level window.</summary>
    /// <summary>A random piece of gear for a chest: ordinary or crown gear, half and half, in the wizard's level range.</summary>
    internal static uint? PickChestGear(Wizard wizard)
        => Random.Shared.Next(2) == 0 ? PickCrownGear(wizard) ?? PickGear(wizard) : PickGear(wizard) ?? PickCrownGear(wizard);

    private static uint? PickGear(Wizard wizard) {
        var (low, high) = LevelWindow(wizard);
        var school = wizard.MagicSchoolBehavior.MagicSchool.ToString();
        foreach (var slot in s_gear.Value.Keys.OrderBy(_ => Random.Shared.Next())) {
            var candidates = Candidates(slot, school, low, high).ToList();
            if (candidates.Count > 0) {
                return candidates[Random.Shared.Next(candidates.Count)].Id;
            }
        }

        return null;
    }

    private static uint? PickCrownGear(Wizard wizard) {
        var bySlot = s_crownGearIds.Value;
        if (bySlot.Count == 0) {
            return null;
        }

        var (low, high) = LevelWindow(wizard);
        var school = wizard.MagicSchoolBehavior.MagicSchool.ToString();
        uint? anySchool = null;

        // Pick the slot first, then a piece for it; a slot with nothing in the wizard's window passes to the next.
        foreach (var slot in bySlot.Keys.OrderBy(_ => Random.Shared.Next())) {
            var ids = bySlot[slot];
            for (var i = 0; i < CROWN_DROP_TRIES / 3; i++) {
                var id = ids[Random.Shared.Next(ids.Length)];

                // Most crown gear names its level (Crowns-S41-Deck-L150-...), so out-of-window pieces are skipped
                // without loading them.
                if (LevelFromName(id) is { } named && (named > high || named + 5 < low)) {
                    continue;
                }

                if (CoreObjectFactory.GetCoreTemplate(id) is not WizItemTemplate item) {
                    continue;
                }

                var level = Math.Max(1, EquipLevel(item));
                if (level < low || level > high) {
                    continue;
                }

                string itemSchool = item.m_school;
                if (IsWearableBy(itemSchool, school)) {
                    return id;
                }

                anySchool ??= id;
            }

            if (anySchool is not null) {
                return anySchool;
            }
        }

        return anySchool;
    }

    private static readonly System.Text.RegularExpressions.Regex s_levelInName
        = new(@"[-_]L(\d+)[-_.]", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static int? LevelFromName(ulong templateId)
        => s_templatePaths.Value.TryGetValue(templateId, out var path)
           && s_levelInName.Match(System.IO.Path.GetFileName(path)) is { Success: true } match
           && int.TryParse(match.Groups[1].Value, out var level)
            ? level
            : null;

    private static Dictionary<string, uint[]> BuildCrownGearIds() {
        var bySlot = s_templatePaths.Value
            .Where(pair => pair.Key is > 0 and <= uint.MaxValue
                           && pair.Value.Contains("CrownItems", StringComparison.OrdinalIgnoreCase))
            .Select(pair => (Id: (uint) pair.Key, Slot: SlotOf(pair.Key)))
            .Where(entry => entry.Slot is not null
                            // Pieces named above the level cap can never drop.
                            && !(LevelFromName(entry.Id) > s_maxLevel))
            .GroupBy(entry => entry.Slot, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.Id).ToArray(), StringComparer.OrdinalIgnoreCase);

        Logger.Information("Roguelike: crown gear drops by slot: {Slots}.",
            Logger.Args(string.Join(", ", bySlot.Select(pair => $"{pair.Key} {pair.Value.Length}"))));

        return bySlot;
    }

    // ---------------------------------------------------------------------
    // Run mount
    // ---------------------------------------------------------------------

    private const int MOUNT_PICK_TRIES = 300;

    // Mount template ids, from the Mounts folders. Only paths are read up front; templates load on demand.
    private static readonly Lazy<uint[]> s_mountIds = new(() => [.. s_templatePaths.Value
        .Where(pair => pair.Key is > 0 and <= uint.MaxValue
                       && pair.Value.Replace('\\', '/').Contains("/Mounts/", StringComparison.OrdinalIgnoreCase)
                       // Two-person mounts are not supported.
                       && !pair.Value.Contains("2P", StringComparison.Ordinal))
        .Select(pair => (uint) pair.Key)], LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// A random permanent, one-person mount that has a speed effect (which a run raises to MountSpeed), or null.
    /// </summary>
    internal static uint? PickRunMount() {
        var ids = s_mountIds.Value;
        for (var i = 0; i < MOUNT_PICK_TRIES && ids.Length > 0; i++) {
            var id = ids[Random.Shared.Next(ids.Length)];
            if (CoreObjectFactory.GetCoreTemplate(id) is WizItemTemplate item
                && item.m_behaviors.OfType<MountItemBehaviorTemplate>().Any()
                && item.m_equipEffects.OfType<SpeedEffectInfo>().Any()
                && !Imlight.CoreLib.Game.Rentals.RentalItems.TryGetRentalTemplate(item, out _)) {
                return id;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------------
    // Shared
    // ---------------------------------------------------------------------

    /// <summary>The level range stock is picked from: around the wizard's level, never above the cap.</summary>
    private static (int Low, int High) LevelWindow(Wizard wizard) {
        // A wizard above MaxStockLevel shops as one at it: the window slides down instead of shrinking to
        // MaxStockLevel alone (a level 110 wizard saw only level 100 stock, and the deck shop had none).
        var level = Math.Clamp((int) wizard.MagicSchoolBehavior.Level, 1, s_maxLevel);
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

    private static Dictionary<string, List<GearEntry>> BuildGearPool() {
        // Gear some vendor in the game sells, and (with ShopDropGear) gear creatures and bosses drop. Crown shop
        // gear is left out wherever it comes from: a gold shop cannot sell it, and it is a rare drop on a run instead.
        var vendorIds = SpiralDB.NpcInventories.Values
            .Where(inventory => inventory?.Inventory is not null)
            .SelectMany(inventory => inventory.Inventory)
            .Select(gid => (ulong) gid.MParts.TemplateId)
            .ToHashSet();
        var dropIds = s_shopDropGear
            ? SpiralDB.DropTables.Values
                .Where(table => table?.Items is not null)
                .SelectMany(table => table.Items)
                .Select(item => ulong.TryParse(item.ItemId, out var id) ? id : 0)
                .Where(id => !vendorIds.Contains(id))
                .ToHashSet()
            : [];
        // ShopWorldGear: gear from anywhere in the game's files (each world's own drops, the gauntlets' such as the
        // Catacombs' level 130 Vanguard gear, dungeon and boss sets at 50 and 100), which SpiralDB's tables mostly leave
        // out, so every slot has gear all the way up to the stock cap (IsWorldGearPath says what stays out).
        var worldIds = s_shopWorldGear
            ? s_templatePaths.Value
                .Where(pair => SlotOf(pair.Key) is not null && IsWorldGearPath(pair.Value)
                               && !vendorIds.Contains(pair.Key) && !dropIds.Contains(pair.Key))
                .Select(pair => pair.Key)
                .ToHashSet()
            : [];
        var ids = vendorIds.Concat(dropIds).Concat(worldIds)
            .Where(id => id is > 0 and <= uint.MaxValue && !IsCrownItem(id));
        var fromWorld = 0;

        var pool = new Dictionary<string, List<GearEntry>>(StringComparer.OrdinalIgnoreCase);
        var fromDrops = 0;
        foreach (var id in ids) {
            if (SlotOf(id) is not { } slot || CoreObjectFactory.GetCoreTemplate(id) is not WizItemTemplate item) {
                continue;
            }

            // Crown-priced gear is a rare drop on a run instead (RollCrownDrop), not shop stock.
            if (item.m_creditsCost > 0) {
                continue;
            }

            // A dropped piece is sold for its own value; one the game never priced would be free.
            if (dropIds.Contains(id) || worldIds.Contains(id)) {
                if (item.m_baseCost <= 0 || IsTestItem(s_templatePaths.Value.GetValueOrDefault(id, ""), item)) {
                    continue;
                }

                if (worldIds.Contains(id)) {
                    fromWorld++;
                }
                else {
                    fromDrops++;
                }
            }

            var level = Math.Max(1, EquipLevel(item));
            if (level > s_maxLevel) {
                continue;
            }

            string school = item.m_school;
            if (!pool.TryGetValue(slot, out var list)) {
                pool[slot] = list = [];
            }

            list.Add(new GearEntry((uint) id, level, school ?? ""));
        }

        Logger.Information("Roguelike: {Count} pieces of gear up to level {Max} for random shops, {Drops} of them dropped by creatures and {World} world and gauntlet gear ({Slots}).",
            Logger.Args(pool.Values.Sum(l => l.Count), s_maxLevel, fromDrops, fromWorld,
                string.Join(", ", pool.Select(p => $"{p.Key} {p.Value.Count}, {p.Value.Count(g => g.Level > 1)} above level 1, up to level {p.Value.Max(g => g.Level)}"))));

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
                            && CoreObjectFactory.GetCoreTemplate(entry.Id) is SpellTemplate { m_Treasure: false } spell
                            // Minigame and duel-only cards some vendors teach (Diego's Rhoshambo spells) stay out.
                            && !(s_templatePaths.Value.TryGetValue(entry.Id, out var path)
                                 && Imlight.CoreLib.Game.Zone.Components.InteractTrainerComponent.IsDevOrMonsterSpell(path, spell)))
            .ToList();

        Logger.Information("Roguelike: {Count} trainer spells up to level {Max} for random trainers.",
            Logger.Args(spells.Count, s_maxLevel));

        return spells;
    }

    /// <summary>
    /// True for gear a gold shop should not sell: crown shop gear (ObjectData/CrownItems/...), stat-less cosmetic
    /// blanks (.../Blanks/...) and PvP gear (ObjectData/PVP/...), which the game shows as crown items.
    /// </summary>
    private static bool IsCrownItem(ulong templateId)
        => HasAdjective(templateId, "FLAG_CrownsOnly")
           || s_templatePaths.Value.TryGetValue(templateId, out var path)
           && (path.Contains("CrownItems", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/Crown-", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/Blanks/", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/PVP/", StringComparison.OrdinalIgnoreCase));

    private static bool HasAdjective(ulong templateId, string adjective)
        => templateId <= uint.MaxValue
           && CoreObjectFactory.GetCoreTemplate((uint) templateId) is WizItemTemplate template
           && template.m_adjectiveList?.Any(a => string.Equals(a.ToString(), adjective, StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>
    /// True when a run pet shop may sell this pet: a gold price and no crown or arena price, not crown-only, not an
    /// exclusive (No Sell, pack and hatch-only pets), not retired or arena-only, and not Magic Weaving.
    /// </summary>
    private static bool IsGoldShopPet(uint id)
        => !IsCrownItem(id)
           && !HasAdjective(id, "FLAG_NoSell")
           && !HasAdjective(id, "FLAG_ArenaPointsOnly")
           && !HasAdjective(id, "FLAG_Retired")
           && CoreObjectFactory.GetCoreTemplate(id) is WizItemTemplate template
           && template.m_baseCost > 0
           // A crown or arena price means a crown shop or arena pet, even without the crowns-only flag.
           && template.m_creditsCost <= 0
           && template.m_arenaPointCost <= 0
           && template.m_pvpCurrencyCost <= 0
           && template.m_pvpTourneyCurrencyCost <= 0
           && !Imlight.CoreLib.Shared.Items.JewelSockets.IsMagicWeaving(template);

    /// <summary>The gear slot of an item, from the folder its template is in, or null when it is not gear.</summary>
    private static string SlotOf(ulong templateId) {
        if (!s_templatePaths.Value.TryGetValue(templateId, out var path)) {
            return null;
        }

        // The folder right above the file: ObjectData/Tier1/Hats/Hat-T1-042.xml -> Hats.
        var parts = path.Replace('\\', '/').Split('/');

        return parts.Length >= 2 && s_slotFolders.TryGetValue(parts[^2], out var slot) ? slot : null;
    }

    private static Dictionary<ulong, string> BuildTemplatePaths() {
        var paths = new Dictionary<ulong, string>();
        foreach (var location in CoreObjectFactory.TemplateManifest.m_serializedTemplates) {
            if (location is null) {
                continue;
            }

            string path = location.m_filename;
            if (!string.IsNullOrEmpty(path)) {
                paths.TryAdd(location.m_id, path);
            }
        }

        return paths;
    }

    private static int EquipLevel(ItemTemplate item)
        => (int) (item.m_equipRequirements?.m_requirements?
            .OfType<ReqMagicLevel>()
            .Select(req => req.m_numericValue)
            .DefaultIfEmpty(0)
            .Max() ?? 0);

}
