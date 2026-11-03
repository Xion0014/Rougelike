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
 * Shadow wizards (3.2): an eighth school, made on the server alone (the game already knows the Shadow school, its
 * icon and its spells; nothing on the game's side changes).
 *
 * The creation screen only offers the seven schools, so the launcher's "Next new wizard is Shadow" setting
 * ([Roguelike] NextWizardShadow) turns the next wizard made into a Shadow wizard, whatever school was picked; the
 * server turns the setting back off once that wizard is made.
 *
 * A Shadow wizard:
 *   - has the health, mana and level numbers of ShadowBaseSchool (the game has none for Shadow);
 *   - masters every school: power pips count double for every school's cards, not only its own (the game shows this
 *     too, as it does for a mastery amulet), since there are far fewer Shadow spells than any school has;
 *   - learns Shadow spells, the boss ones included, from the random trainers (ShadowSpellShare % of picks) and after
 *     battles, and starts with ShadowStartingSpells of them;
 *   - can wear any school's gear, and shops and drops roll from all of it.
 */

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Imlight.Common;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Runs;

internal static class ShadowSchool {

    /// <summary>[Roguelike] ShadowBaseSchool: the school whose health, mana and level numbers a Shadow wizard has.</summary>
    internal static readonly string BaseSchool = ConfigurationManager.GetValue("Roguelike.ShadowBaseSchool", "Balance")?.Trim() is { Length: > 0 } s
        ? s : "Balance";

    /// <summary>[Roguelike] ShadowSpellShare: the share (%) of a Shadow wizard's trainer picks that are Shadow spells.</summary>
    internal static readonly int SpellShare = Math.Clamp(ConfigurationManager.GetValue("Roguelike.ShadowSpellShare", 35), 0, 100);

    /// <summary>[Roguelike] ShadowStartingSpells: Shadow spells a new Shadow wizard starts with, on top of StartingSpells.</summary>
    internal static readonly int StartingSpells = Math.Clamp(ConfigurationManager.GetValue("Roguelike.ShadowStartingSpells", 2), 0, 20);

    internal static bool Is(MagicSchool school) => school == MagicSchool.Shadow;

    internal static bool Is(Wizard wizard) => wizard?.MagicSchoolBehavior?.MagicSchool == MagicSchool.Shadow;

    /// <summary>The seven schools, whose gear and spells a Shadow wizard counts as their own.</summary>
    internal static bool IsPrimarySchool(string school)
        => Enum.TryParse<MagicSchool>(school, true, out var parsed)
           && parsed is MagicSchool.Fire or MagicSchool.Ice or MagicSchool.Storm or MagicSchool.Myth
                     or MagicSchool.Life or MagicSchool.Death or MagicSchool.Balance;

    /// <summary>
    /// A Shadow wizard masters every school: power pips count double for every school's spells. Set after the gear,
    /// so the game shows it too (cards it would grey out for too few pips stay castable).
    /// </summary>
    internal static void GiveMastery(ServerWizGameStats stats) {
        if (stats is null || !Is(stats.MagicSchool)) {
            return;
        }

        stats.m_balanceMastery = Math.Max(1, stats.m_balanceMastery);
        stats.m_deathMastery = Math.Max(1, stats.m_deathMastery);
        stats.m_fireMastery = Math.Max(1, stats.m_fireMastery);
        stats.m_iceMastery = Math.Max(1, stats.m_iceMastery);
        stats.m_lifeMastery = Math.Max(1, stats.m_lifeMastery);
        stats.m_mythMastery = Math.Max(1, stats.m_mythMastery);
        stats.m_stormMastery = Math.Max(1, stats.m_stormMastery);
    }

    private static readonly object s_lock = new();
    private static readonly Regex s_setting = new(@"^(\s*NextWizardShadow\s*=\s*)(\S+)(.*)$", RegexOptions.IgnoreCase);

    /// <summary>
    /// True when the next wizard made should be a Shadow wizard ([Roguelike] NextWizardShadow, the launcher's "Next
    /// new wizard is Shadow"), and turns the setting off. The file itself is read, so a change the launcher made while
    /// the server was awake counts too.
    /// </summary>
    internal static bool TakeNextWizard() {
        lock (s_lock) {
            var path = ConfigurationManager.FilePath;
            try {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) {
                    return false;
                }

                var lines = File.ReadAllLines(path);
                var section = "";
                for (var i = 0; i < lines.Length; i++) {
                    var trimmed = lines[i].Trim();
                    if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) {
                        section = trimmed[1..^1].Trim();
                        continue;
                    }

                    if (!section.Equals("Roguelike", StringComparison.OrdinalIgnoreCase)
                        || s_setting.Match(lines[i]) is not { Success: true } match) {
                        continue;
                    }

                    if (!match.Groups[2].Value.Equals("true", StringComparison.OrdinalIgnoreCase)) {
                        return false;
                    }

                    lines[i] = match.Groups[1].Value + "false" + match.Groups[3].Value;
                    var temp = path + ".tmp";
                    File.WriteAllLines(temp, lines);
                    File.Move(temp, path, overwrite: true);
                    Logger.Information("[Shadow] The next new wizard is a Shadow wizard; NextWizardShadow is off again.");

                    return true;
                }
            }
            catch (Exception e) {
                Logger.Warning("[Shadow] Could not read or turn off NextWizardShadow in {0}: {1}", Logger.Args(path, e.Message));
            }

            return false;
        }
    }

}
