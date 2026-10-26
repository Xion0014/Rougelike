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
 * Achievements on a run.
 *
 * What there is to earn lives in spiraldb-extra/Achievements/achievements.json (one entry each: Id, Name, Description,
 * Hidden, When and its numbers); see the README there. What each wizard has done and earned is kept in
 * Achievements/progress.json in the server's folder, which the launcher reads for its Achievements page.
 *
 * When:
 *   TierCleared         beat tier Tier's boss (any tier when Tier is 0).
 *   TierClearedNoDeath  beat a tier's boss (Tier, or any) without dying since the last tier's boss.
 *   TierClearedDieDelete beat tier Tier's boss with Die = Delete on.
 *   BossInRounds        beat a tier's boss in at most Amount rounds.
 *   Counter             Counter (BattlesWon, Kills, BossesBeaten, Deaths, Flees, JewelsFound, PetsFound, SpellsLearned)
 *                       reaches Amount.
 *   Level               reach level Amount.
 *   Gold                hold Amount gold at once.
 *   PetAge              find a pet of age Amount (1 Baby ... 7 Ultra) or older.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Runs;

internal static class Achievements {

    internal sealed class Achievement {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public bool Hidden { get; set; }
        public string When { get; set; } = "";
        public int Tier { get; set; }
        public long Amount { get; set; }
        public string Counter { get; set; } = "";
    }

    internal sealed class WizardProgress {
        public string Name { get; set; } = "";
        public Dictionary<string, long> Counters { get; set; } = [];
        public Dictionary<string, DateTime> Earned { get; set; } = [];
    }

    internal sealed class ProgressFile {
        public Dictionary<string, WizardProgress> Wizards { get; set; } = [];
    }

    /// <summary>What a moment in the run was, for the achievements that look at one (a boss beaten...).</summary>
    internal sealed record Moment(string When, int Tier = 0, long Amount = 0);

    internal static readonly bool Enabled = ConfigurationManager.GetValue("Roguelike.Achievements", true);

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
    };

    private static readonly object s_lock = new();
    private static readonly Lazy<List<Achievement>> s_all = new(LoadAll);
    private static ProgressFile s_progress;

    /// <summary>The progress file, in the server's own folder (the launcher reads it).</summary>
    internal static string ProgressPath => Path.GetFullPath(Path.Combine("Achievements", "progress.json"));

    internal static IReadOnlyList<Achievement> All => s_all.Value;

    /// <summary>Adds to one of the wizard's counters (BattlesWon, Kills...).</summary>
    internal static void Count(Wizard wizard, string counter, long by = 1) {
        if (!Enabled || wizard is null || by == 0) {
            return;
        }

        lock (s_lock) {
            var progress = ProgressOf(wizard);
            progress.Counters[counter] = progress.Counters.GetValueOrDefault(counter) + by;
        }
    }

    /// <summary>Sets one of the wizard's counters (DeathsThisTier back to 0).</summary>
    internal static void Reset(Wizard wizard, string counter) {
        if (!Enabled || wizard is null) {
            return;
        }

        lock (s_lock) {
            ProgressOf(wizard).Counters[counter] = 0;
        }
    }

    internal static bool HasEarned(Wizard wizard, string id) {
        lock (s_lock) {
            return wizard is not null && ProgressOf(wizard).Earned.ContainsKey(id);
        }
    }

    internal static long CounterOf(Wizard wizard, string counter) {
        lock (s_lock) {
            return wizard is null ? 0 : ProgressOf(wizard).Counters.GetValueOrDefault(counter);
        }
    }

    /// <summary>
    /// Looks at every achievement the wizard has not earned yet (with what just happened, if anything), saves, and
    /// returns the ones just earned.
    /// </summary>
    internal static List<Achievement> Check(Wizard wizard, Moment moment = null) {
        if (!Enabled || wizard is null) {
            return [];
        }

        var earned = new List<Achievement>();
        lock (s_lock) {
            var progress = ProgressOf(wizard);
            foreach (var achievement in All) {
                if (progress.Earned.ContainsKey(achievement.Id) || !Met(achievement, wizard, progress, moment)) {
                    continue;
                }

                progress.Earned[achievement.Id] = DateTime.Now;
                earned.Add(achievement);
                Logger.Information("[Achievements] {0} ({1}) earned {2}.", Logger.Args(progress.Name, wizard.CharId, achievement.Name));
            }

            Save();
        }

        return earned;
    }

    private static bool Met(Achievement a, Wizard wizard, WizardProgress progress, Moment moment) {
        bool TierFits() => a.Tier <= 0 || moment.Tier == a.Tier;

        switch (a.When.ToLowerInvariant()) {
            case "tiercleared":
                return moment is { When: "TierCleared" } && TierFits();
            case "tierclearednodeath":
                return moment is { When: "TierCleared" } && TierFits() && progress.Counters.GetValueOrDefault("DeathsThisTier") == 0;
            case "tiercleareddiedelete":
                return moment is { When: "TierCleared" } && TierFits() && RoguelikeConfig.Permadeath;
            case "bossinrounds":
                return moment is { When: "TierCleared" } && TierFits() && moment.Amount > 0 && moment.Amount <= a.Amount;
            case "counter":
                return progress.Counters.GetValueOrDefault(a.Counter) >= a.Amount;
            case "level":
                return wizard.MagicSchoolBehavior?.Level >= a.Amount;
            case "gold":
                return wizard.GameStats?.m_currentGold >= a.Amount;
            case "petage":
                return moment is { When: "PetFound" } && moment.Amount >= a.Amount;
            default:
                return false;
        }
    }

    private static WizardProgress ProgressOf(Wizard wizard) {
        s_progress ??= LoadProgress();
        var key = wizard.CharId.ToString();
        if (!s_progress.Wizards.TryGetValue(key, out var progress)) {
            s_progress.Wizards[key] = progress = new WizardProgress();
        }

        var name = wizard.PlayerNameBehavior?.GetWizardName();
        if (!string.IsNullOrEmpty(name)) {
            progress.Name = name;
        }

        return progress;
    }

    private static ProgressFile LoadProgress() {
        try {
            if (File.Exists(ProgressPath)) {
                return JsonSerializer.Deserialize<ProgressFile>(File.ReadAllText(ProgressPath), s_options) ?? new ProgressFile();
            }
        }
        catch (Exception e) {
            Logger.Warning("[Achievements] {0} could not be read ({1}); starting a new one.", Logger.Args(ProgressPath, e.Message));
        }

        return new ProgressFile();
    }

    private static void Save() {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(ProgressPath)!);
            var temp = ProgressPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(s_progress, s_options));
            File.Move(temp, ProgressPath, overwrite: true);
        }
        catch (Exception e) {
            Logger.Warning("[Achievements] Could not save {0}: {1}", Logger.Args(ProgressPath, e.Message));
        }
    }

    private static List<Achievement> LoadAll() {
        var path = Path.Combine(SpiralDB.ExtraPath, "Achievements", "achievements.json");
        try {
            if (File.Exists(path)) {
                var list = JsonSerializer.Deserialize<List<Achievement>>(File.ReadAllText(path), s_options) ?? [];
                list = [.. list.Where(a => a is not null && a.Id.Length > 0)];
                Logger.Information("[Achievements] {0} achievements from {1}.", Logger.Args(list.Count, Path.GetFullPath(path)));

                return list;
            }

            Logger.Warning("[Achievements] {0} is missing; there are no achievements.", Logger.Args(Path.GetFullPath(path)));
        }
        catch (Exception e) {
            Logger.Warning("[Achievements] {0} could not be read: {1}", Logger.Args(path, e.Message));
        }

        return [];
    }

}
