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
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// The launcher's Achievements page. What there is to earn comes from the server's
/// spiraldb-extra/Achievements/achievements.json, and what has been earned from Server/Achievements/progress.json, which
/// the server writes as wizards play (Game/Runs/Achievements.cs). An achievement counts as earned on this PC once any
/// wizard has earned it, so a wizard lost to Die = Delete takes nothing with them.
/// </summary>
internal sealed partial class LauncherWindow {

    private sealed record AchievementInfo(string Id, string Name, string Description, bool Hidden);

    private sealed record EarnedBy(string Wizard, DateTime When);

    private static readonly JsonDocumentOptions s_achievementJson = new() {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private void OpenAchievements() {
        if (!_manager.IsInstalled) {
            Hint("Set the gauntlet up first; its achievements come with it.", isError: true);

            return;
        }

        var server = Path.GetDirectoryName(_manager.ConfigFolder.TrimEnd(Path.DirectorySeparatorChar, '/'))!;
        var all = ReadAchievements(Path.Combine(server, "spiraldb-extra", "Achievements", "achievements.json"));
        var earned = ReadEarned(Path.Combine(server, "Achievements", "progress.json"));

        _bookMustAnswer = false;
        _bookAnswer.IsVisible = false;
        _settingsActions.IsVisible = false;
        _settingsStatus.IsVisible = false;
        if (_bookClose is not null) {
            _bookClose.IsVisible = true;
        }

        _bookTitle.Text = "Achievements";
        _bookPages.Children.Clear();
        if (all.Count == 0) {
            _bookPages.Children.Add(Small("No achievements yet: update the gauntlet to get them."));
        }
        else {
            var count = all.Count(a => earned.ContainsKey(a.Id));
            _bookPages.Children.Add(new TextBlock {
                Text = $"{count} of {all.Count} earned. Any of your wizards can earn them, and they stay earned on this PC even if that wizard is lost.",
                FontSize = 14, Foreground = TEXT, TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 0, 8, 10),
            });
            foreach (var achievement in all) {
                _bookPages.Children.Add(AchievementRow(achievement, earned.GetValueOrDefault(achievement.Id)));
            }
        }

        _bookScroll.Offset = new Vector(0, 0);
        _book.IsVisible = true;
    }

    private static Control AchievementRow(AchievementInfo achievement, EarnedBy? earned) {
        var secret = achievement.Hidden && earned is null;
        var badge = new Border {
            Width = 18,
            Height = 18,
            Margin = new Thickness(6, 4, 18, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.Parse(earned is null ? "#1B1412" : "#5A1D14")),
            BorderBrush = earned is null ? SOFT : GOLD,
            BorderThickness = new Thickness(2),
            RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("rotate(45deg)"),
            BoxShadow = earned is null ? default : BoxShadows.Parse("0 0 10 1 #99E9783C"),
        };
        var text = new StackPanel {
            Spacing = 1,
            Children = {
                new TextBlock {
                    Text = secret ? "???" : achievement.Name,
                    FontSize = 15,
                    FontWeight = FontWeight.Bold,
                    Foreground = earned is null ? SOFT : GOLD,
                },
                new TextBlock {
                    Text = secret ? "A hidden achievement. Keep playing to find it." : achievement.Description,
                    FontSize = 13,
                    Foreground = earned is null ? SOFT : TEXT,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };
        if (earned is not null) {
            text.Children.Add(new TextBlock {
                Text = $"Earned by {earned.Wizard} on {earned.When:MMMM d, yyyy}",
                FontSize = 12,
                Foreground = GOLD_DIM,
            });
        }

        var row = new DockPanel { Margin = new Thickness(0, 4, 8, 8), Opacity = earned is null ? 0.75 : 1 };
        DockPanel.SetDock(badge, Dock.Left);
        row.Children.Add(badge);
        row.Children.Add(text);

        return row;
    }

    private static List<AchievementInfo> ReadAchievements(string path) {
        var list = new List<AchievementInfo>();
        try {
            using var document = JsonDocument.Parse(File.ReadAllText(path), s_achievementJson);
            foreach (var entry in document.RootElement.EnumerateArray()) {
                string Text(string name) => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                var id = Text("Id");
                if (id.Length == 0) {
                    continue;
                }

                var hidden = entry.TryGetProperty("Hidden", out var h) && h.ValueKind == JsonValueKind.True;
                list.Add(new AchievementInfo(id, Text("Name"), Text("Description"), hidden));
            }
        }
        catch (Exception) {
            // Not installed yet, or an older server: no achievements to show.
        }

        return list;
    }

    // Each achievement's first earning on this PC: which wizard, and when.
    private static Dictionary<string, EarnedBy> ReadEarned(string path) {
        var earned = new Dictionary<string, EarnedBy>(StringComparer.OrdinalIgnoreCase);
        try {
            using var document = JsonDocument.Parse(File.ReadAllText(path), s_achievementJson);
            if (!document.RootElement.TryGetProperty("Wizards", out var wizards)) {
                return earned;
            }

            foreach (var wizard in wizards.EnumerateObject()) {
                var name = wizard.Value.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                if (!wizard.Value.TryGetProperty("Earned", out var list)) {
                    continue;
                }

                foreach (var item in list.EnumerateObject()) {
                    if (item.Value.TryGetDateTime(out var when)
                        && (!earned.TryGetValue(item.Name, out var first) || when < first.When)) {
                        earned[item.Name] = new EarnedBy(name.Length > 0 ? name : "a wizard", when);
                    }
                }
            }
        }
        catch (Exception) {
            // Nothing earned yet (no file), or it is being written right now: show what can be read.
        }

        return earned;
    }

}
