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

namespace Imlight.Setup;

/// <summary>
/// Reads and edits values in Imlight.ini, keeping every comment and every other line as it was.
/// </summary>
internal static class IniFile {

    /// <summary>
    /// The roguelike launcher's run design: the tiers (every TierN... key), how long streets last, where arcs end, the
    /// rewards, shop and drop levels, potions, backpack size, randomizer and pip rules. Each update takes these from the release, so every player
    /// runs the same tiers; the switches players flip themselves (music, Die = Delete) are left alone, and so are the
    /// settings in <paramref name="keep"/> (Section.Key: the ones the player changed in the launcher's Settings page).
    /// </summary>
    internal static int CopyRunDesign(List<string> lines, IReadOnlyList<string> defaults, IReadOnlySet<string>? keep = null) {
        // Section.Key, plus every [Roguelike] TierN... key.
        string[] keys = [
            "Roguelike.StreetMinutes", "Roguelike.ArcEndTiers", "Roguelike.XpMultiplier", "Roguelike.GoldMultiplier",
            "Roguelike.BossTrainingPoints", "Roguelike.MaxStockLevel", "Roguelike.CrownDropChance", "Roguelike.NoCrowns",
            "Roguelike.CreatureCritByTier", "Roguelike.CreatureBlockByTier", "Roguelike.MaxSpellDamage", "Roguelike.BannedSpells",
            "Roguelike.ChestRewards", "Roguelike.ChestGoldMin", "Roguelike.ChestGoldMax",
            "Roguelike.PetMayCastShare", "PetCasting.Enabled", "PetCasting.OnCast", "PetCasting.OnDamage", "PetCasting.OnHeal",
            "PetCasting.OnPass", "PetCasting.MaxPerRound", "PetCasting.ShowAsPetCast",
            "Character.StartingPotions", "Character.MaxInventoryItems",
            "Randomizer.RandomizeSchool", "Randomizer.RandomizeSpells", "Randomizer.RandomizeDrops",
            "Randomizer.CreaturesChangeEverySpawn", "Randomizer.RandomizeCreatureDecks", "Randomizer.RandomizeItemCards",
            "Randomizer.RandomizePips",
            "Battle.GainSchoolPips", "Battle.AnySchoolPips", "Archmastery.AllLevels",
        ];
        var changed = 0;
        string? section = null;
        foreach (var line in defaults) {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) {
                section = trimmed[1..^1].Trim();

                continue;
            }

            var equals = trimmed.IndexOf('=');
            if (section is null || trimmed.StartsWith(';') || equals <= 0) {
                continue;
            }

            var key = trimmed[..equals].Trim();
            var value = trimmed[(equals + 1)..].Trim();
            var isTier = section.Equals("Roguelike", StringComparison.OrdinalIgnoreCase)
                && key.Length > 4 && key.StartsWith("Tier", StringComparison.OrdinalIgnoreCase) && char.IsDigit(key[4]);
            if (!isTier && !keys.Contains($"{section}.{key}", StringComparer.OrdinalIgnoreCase)) {
                continue;
            }

            if (keep?.Contains($"{section}.{key}") == true) {
                continue;
            }

            if (GetFrom(lines, section, key) == value) {
                continue;
            }

            Set(lines, section, key, value);
            changed++;
        }

        return changed;
    }

    /// <summary>
    /// Adds the sections and keys a newer default file has that the person's file lacks, with their comments,
    /// so new settings show up after an update. Values already there are never changed.
    /// </summary>
    /// <returns>How many keys were added.</returns>
    internal static int AddMissing(List<string> lines, IReadOnlyList<string> defaults) {
        var added = 0;
        string? section = null;
        var comments = new List<string>();

        foreach (var line in defaults) {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) {
                section = trimmed[1..^1].Trim();
                comments.Clear();

                continue;
            }

            if (trimmed.StartsWith(';')) {
                comments.Add(line);

                continue;
            }

            var equals = trimmed.IndexOf('=');
            if (section is null || equals <= 0) {
                comments.Clear();

                continue;
            }

            var key = trimmed[..equals].Trim();
            if (Find(lines, section, key) < 0) {
                Set(lines, section, key, trimmed[(equals + 1)..].Trim());
                var at = Find(lines, section, key);
                lines.InsertRange(at, comments);
                added++;
            }

            comments.Clear();
        }

        return added;
    }

    private static int Find(List<string> lines, string section, string key) {
        var inSection = false;
        for (var i = 0; i < lines.Count; i++) {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) {
                inSection = trimmed[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase);

                continue;
            }

            var equals = trimmed.IndexOf('=');
            if (inSection && !trimmed.StartsWith(';') && equals > 0
                && trimmed[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Returns a value from an ini file, or null when the file, section or key is missing.
    /// </summary>
    private static string? GetFrom(IEnumerable<string> lines, string section, string key) {
        var inSection = false;
        foreach (var line in lines) {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) {
                inSection = trimmed[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase);

                continue;
            }

            var equals = trimmed.IndexOf('=');
            if (inSection && !trimmed.StartsWith(';') && equals > 0
                && trimmed[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) {
                return trimmed[(equals + 1)..].Trim();
            }
        }

        return null;
    }

    internal static string? Get(string path, string section, string key) {
        if (!System.IO.File.Exists(path)) {
            return null;
        }

        var inSection = false;
        foreach (var line in System.IO.File.ReadLines(path)) {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) {
                inSection = trimmed[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase);

                continue;
            }

            var equals = trimmed.IndexOf('=');
            if (inSection && !trimmed.StartsWith(';') && equals > 0
                && trimmed[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) {
                return trimmed[(equals + 1)..].Trim();
            }
        }

        return null;
    }

    internal static void Set(List<string> lines, string section, string key, string value) {
        var inSection = false;
        var sectionEnd = -1;

        for (var i = 0; i < lines.Count; i++) {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) {
                if (inSection) {
                    break;
                }

                inSection = trimmed[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase);
                if (inSection) {
                    sectionEnd = i + 1;
                }

                continue;
            }

            if (!inSection) {
                continue;
            }

            if (trimmed.Length > 0) {
                sectionEnd = i + 1;
            }

            var equals = trimmed.IndexOf('=');
            if (!trimmed.StartsWith(';') && equals > 0
                && trimmed[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) {
                lines[i] = $"{key} = {value}";

                return;
            }
        }

        if (sectionEnd < 0) {
            lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key} = {value}");

            return;
        }

        lines.Insert(sectionEnd, $"{key} = {value}");
    }

}
