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
using System.Globalization;
using System.IO;
using System.Linq;

namespace Imlight.Setup;

/// <summary>How a setting is edited in the launcher's Settings page.</summary>
internal enum SettingKind { Whole, Decimal, Switch, Text }

/// <summary>One setting the launcher's Settings page shows: where it lives in Imlight.ini and how it is edited.</summary>
internal sealed record PlayerSetting(string Section, string Key, string Label, string Help, SettingKind Kind,
                                     double Min = 0, double Max = 0) {

    internal string Id => $"{Section}.{Key}";

    /// <summary>Null when the value is fine, else why not (shown to the player).</summary>
    internal string? Check(string value) {
        value = value.Trim();
        switch (Kind) {
            case SettingKind.Whole:
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)) {
                    return $"{Label} must be a whole number.";
                }

                return whole < Min || whole > Max ? $"{Label} must be from {Min:0.##} to {Max:0.##}." : null;
            case SettingKind.Decimal:
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) {
                    return $"{Label} must be a number (use a dot for decimals, like 2.5).";
                }

                return number < Min || number > Max ? $"{Label} must be from {Min:0.##} to {Max:0.##}." : null;
            case SettingKind.Text:
                return value.Length == 0 ? $"{Label} can't be empty." : null;
            default:
                return null;
        }
    }

}

/// <summary>
/// The run settings players can change from the launcher, and which ones they have changed. Updates refresh the run
/// design from the release (IniFile.CopyRunDesign), but leave alone every setting the player changed here.
/// </summary>
internal static class PlayerSettings {

    /// <summary>The Settings page, group by group.</summary>
    internal static readonly (string Group, PlayerSetting[] Settings)[] Groups = [
        ("Rewards", [
            new("Roguelike", "XpMultiplier", "Experience multiplier", "Experience from battles, drops and quests is multiplied by this.", SettingKind.Decimal, 0, 1000),
            new("Roguelike", "GoldMultiplier", "Gold multiplier", "Gold from battles, quests and gold wisps (not from selling) is multiplied by this.", SettingKind.Decimal, 0, 10000),
            new("Roguelike", "BossGold", "Gold per boss", "Gold every tier's boss gives, as it is (not multiplied).", SettingKind.Whole, 0, 100000000),
            new("Roguelike", "BossGoldFromTier", "Boss gold from tier", "The first tier whose boss gives the gold per boss.", SettingKind.Whole, 1, 100),
            new("Roguelike", "FlatRatesFromTier", "x1 rates from tier", "From this tier's boss on, battles give experience and gold at x1 (the bosses give the levels instead). 0: never.", SettingKind.Whole, 0, 100),
            new("Roguelike", "BossTrainingPoints", "Training points per boss", "Training points for beating each tier's boss.", SettingKind.Whole, 0, 100),
            new("Roguelike", "BoostChance", "Stat potion chance (%)", "The chance a won battle gives a stat potion.", SettingKind.Whole, 0, 100),
            new("Roguelike", "QuestItemChance", "Gear from quests (%)", "The chance a completed quest also gives a random piece of gear.", SettingKind.Whole, 0, 100),
        ]),
        ("Drops", [
            new("Roguelike", "CrownDropChance", "Crown gear drop (%)", "The chance a won battle drops a random piece of crown gear.", SettingKind.Whole, 0, 100),
            new("Roguelike", "JewelDropChance", "Jewel drop (%)", "The chance a won battle drops a random jewel or pin.", SettingKind.Whole, 0, 100),
            new("Roguelike", "PetDropChance", "Pet drop (%)", "The chance a won battle drops a random pet.", SettingKind.Whole, 0, 100),
            new("Roguelike", "BossPetDropChance", "Boss pet drop (%)", "The chance a tier's boss drops a random pet.", SettingKind.Whole, 0, 100),
            new("Roguelike", "BattleSpellChance", "Spell after a battle (%)", "The chance a won battle teaches a new spell.", SettingKind.Whole, 0, 100),
            new("Roguelike", "QuestSpellChance", "Spell after a quest (%)", "The chance a completed quest teaches a new spell.", SettingKind.Whole, 0, 100),
            new("Roguelike", "BossSpellChance", "Spell after a boss (%)", "The chance a tier's boss teaches a new spell.", SettingKind.Whole, 0, 100),
        ]),
        ("Chests", [
            new("Roguelike", "ChestRewards", "Chest rewards", "Weights for what a chest holds, as kind:weight (gold, gear, jewel, spell, pet), comma-separated.", SettingKind.Text),
            new("Roguelike", "ChestGoldMin", "Chest gold, least", "The least gold a chest holds.", SettingKind.Whole, 0, 10000000),
            new("Roguelike", "ChestGoldMax", "Chest gold, most", "The most gold a chest holds.", SettingKind.Whole, 0, 10000000),
        ]),
        ("Critical and block", [
            new("Roguelike", "SimpleCriticals", "Simple criticals", "On: a wizard's chance to crit (and block) is their rating / 10 in percent, and a crit does double damage. Off: the game's own formula.", SettingKind.Switch),
            new("Roguelike", "CreatureCritByTier", "Creature crit by tier (%)", "Each tier's creature crit chance, tier 1 first, comma-separated.", SettingKind.Text),
            new("Roguelike", "CreatureBlockByTier", "Creature block by tier (%)", "Each tier's creature block chance, tier 1 first, comma-separated.", SettingKind.Text),
        ]),
        ("Creatures", [
            new("Roguelike", "SwapStreetCreatures", "Random creatures", "On: streets and their dungeons show random creatures of a similar level. Off: they keep their own creatures.", SettingKind.Switch),
            new("Roguelike", "RandomCreatureDecks", "Random creature decks", "On: street creatures (bosses aside) fight with a random deck of normal spells for their level and school.", SettingKind.Switch),
            new("Roguelike", "BossCheats", "Boss cheats", "On: bosses with a cheat file cast free spells and show lines mid-battle, as in the real game.", SettingKind.Switch),
            new("Roguelike", "BossCheatsFromTier", "Boss cheats from tier", "The first tier whose bosses cheat (6: arc 2).", SettingKind.Whole, 1, 100),
        ]),
        ("The run", [
            new("Roguelike", "StreetMinutes", "Minutes per street", "How long each street lasts before the boss (tiers 1 to 5 have their own below).", SettingKind.Whole, 1, 240),
            new("Roguelike", "Tier1Minutes", "Tier 1 street minutes (Wizard City)", "How long tier 1's street lasts before its boss. 0: Minutes per street.", SettingKind.Whole, 0, 240),
            new("Roguelike", "Tier2Minutes", "Tier 2 street minutes (Krokotopia)", "How long tier 2's street lasts before its boss. 0: Minutes per street.", SettingKind.Whole, 0, 240),
            new("Roguelike", "Tier3Minutes", "Tier 3 street minutes (Marleybone)", "How long tier 3's street lasts before its boss. 0: Minutes per street.", SettingKind.Whole, 0, 240),
            new("Roguelike", "Tier4Minutes", "Tier 4 street minutes (MooShu)", "How long tier 4's street lasts before its boss. 0: Minutes per street.", SettingKind.Whole, 0, 240),
            new("Roguelike", "Tier5Minutes", "Tier 5 street minutes (Dragonspyre)", "How long tier 5's street lasts before its boss. 0: Minutes per street.", SettingKind.Whole, 0, 240),
            new("Roguelike", "StartingSpells", "Starting spells", "Random spells a new wizard starts with.", SettingKind.Whole, 0, 50),
            new("Character", "StartingPotions", "Potion bottles", "How many potion bottles every wizard has.", SettingKind.Whole, 0, 20),
            new("Character", "MaxInventoryItems", "Backpack size", "How many items the backpack holds.", SettingKind.Whole, 10, 1000),
            new("Roguelike", "MountSpeed", "Mount speed (%)", "The speed every mount gives on a run (0 leaves mounts as they are).", SettingKind.Whole, 0, 500),
            new("Roguelike", "MaxGoalCount", "Most per quest goal", "The most creatures a \"defeat\" goal, or items a \"collect\" goal, asks for (0 leaves them as they are).", SettingKind.Whole, 0, 100),
        ]),
        ("Shops and trainers", [
            new("Roguelike", "MaxStockLevel", "Highest stock level", "Shops and trainers never stock above this level.", SettingKind.Whole, 1, 200),
            new("Roguelike", "StockLevelsBelow", "Stock levels below you", "How many levels below the wizard shops and trainers stock.", SettingKind.Whole, 0, 200),
            new("Roguelike", "StockLevelsAbove", "Stock levels above you", "How many levels above the wizard shops and trainers stock.", SettingKind.Whole, 0, 200),
            new("Roguelike", "TrainerSpells", "Spells per trainer", "How many random spells each trainer offers.", SettingKind.Whole, 1, 100),
            new("Roguelike", "PetShopCount", "Pets in the pet shop", "How many random pets the pet shop sells.", SettingKind.Whole, 1, 50),
            new("Roguelike", "JewelShopRange", "Jewel shop level range", "The jewel shop sells jewels this many levels below to above the wizard.", SettingKind.Whole, 0, 100),
        ]),
        ("Pets", [
            new("Roguelike", "PetMayCastShare", "May Cast talents (%)", "The chance each talent of a run pet is a May Cast talent instead of a stat talent.", SettingKind.Whole, 0, 100),
            new("PetCasting", "OnCast", "May Cast on a cast (%)", "The chance a May Cast goes off when you or a teammate casts.", SettingKind.Whole, 0, 100),
            new("PetCasting", "OnDamage", "May Cast on damage (%)", "The chance a May Cast goes off when you or a teammate takes damage.", SettingKind.Whole, 0, 100),
            new("PetCasting", "OnHeal", "May Cast on a heal (%)", "The chance a May Cast goes off when you or a teammate is healed.", SettingKind.Whole, 0, 100),
            new("PetCasting", "MaxPerRound", "May Casts per round", "The most May Casts one pet can do in a round.", SettingKind.Whole, 0, 20),
        ]),
        ("Pips", [
            new("Battle", "GainSchoolPips", "Gain school pips", "A wizard's school pip gauge fills each round.", SettingKind.Switch),
            new("Battle", "AnySchoolPips", "Any school pip pays", "A school pip of any school pays any spell's school pip cost.", SettingKind.Switch),
            new("Archmastery", "AllLevels", "Archmastery at any level", "Wizards under level 50 get Archmastery too.", SettingKind.Switch),
        ]),
    ];

    internal static IEnumerable<PlayerSetting> All => Groups.SelectMany(g => g.Settings);

    // Next to Imlight.ini: the release's own values (written by every install and update), and the settings the player changed.
    internal static string DefaultsFile(string configFolder) => Path.Combine(configFolder, "Imlight.defaults.ini");

    private static string ChangedFile(string configFolder) => Path.Combine(configFolder, "player-settings.txt");

    /// <summary>The settings (Section.Key) the player changed from the launcher, which updates leave alone.</summary>
    internal static HashSet<string> Changed(string configFolder) {
        try {
            var file = ChangedFile(configFolder);
            if (File.Exists(file)) {
                return new HashSet<string>(File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith(';')),
                                           StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Treated as none changed.
        }

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    internal static void SaveChanged(string configFolder, IEnumerable<string> ids) {
        var lines = new List<string> { "; Settings changed in the Spiral Gauntlet launcher. Updates leave these alone." };
        lines.AddRange(ids.OrderBy(i => i, StringComparer.OrdinalIgnoreCase));
        File.WriteAllLines(ChangedFile(configFolder), lines);
    }

    /// <summary>The release's value for a setting, or null when there is no defaults file (installed before this).</summary>
    internal static string? Default(string configFolder, PlayerSetting setting) {
        var file = DefaultsFile(configFolder);

        return File.Exists(file) ? IniFile.Get(file, setting.Section, setting.Key) : null;
    }

    /// <summary>Whether two values are the same setting value (2.5 and 2.50, true and True).</summary>
    internal static bool Same(PlayerSetting setting, string a, string b) {
        a = a.Trim();
        b = b.Trim();
        if (setting.Kind is SettingKind.Whole or SettingKind.Decimal
            && double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) {
            return x.Equals(y);
        }

        if (setting.Kind == SettingKind.Text) {
            static string Squash(string s) => string.Join(",", s.Split(',').Select(p => p.Trim()));

            return Squash(a).Equals(Squash(b), StringComparison.OrdinalIgnoreCase);
        }

        return a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }

}
