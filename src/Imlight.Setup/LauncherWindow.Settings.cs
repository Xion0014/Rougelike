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
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// The launcher's Settings page: the run's rates, chances and rules (PlayerSettings.Groups), edited in place and saved to
/// Imlight.ini. What a player changes here is kept through updates; "Restore defaults" puts back the release's values.
/// </summary>
internal sealed partial class LauncherWindow {

    private readonly StackPanel _settingsActions = new() {
        Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right,
        Margin = new Thickness(0, 14, 8, 0), IsVisible = false,
    };
    private readonly TextBlock _settingsStatus = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 8, 0), IsVisible = false };
    private readonly List<(PlayerSetting Setting, Func<string> Read, Action<string> Write)> _settingFields = [];
    private readonly Border _settingsPill;

    private void BuildSettingsActions() {
        var defaults = Pill("Restore defaults", () => {
            RestoreDefaults();

            return Task.CompletedTask;
        }, evenWhenBusy: true);
        var save = Pill("Save", () => {
            SaveSettingsPage();

            return Task.CompletedTask;
        }, gold: true, evenWhenBusy: true);
        _settingsActions.Children.AddRange([defaults, save]);
    }

    private void OpenSettings() {
        if (!_manager.IsInstalled) {
            Hint("Set the gauntlet up first; its settings come with it.", isError: true);

            return;
        }

        _bookMustAnswer = false;
        _bookAnswer.IsVisible = false;
        if (_bookClose is not null) {
            _bookClose.IsVisible = true;
        }

        _bookTitle.Text = "Settings";
        _bookPages.Children.Clear();
        _settingFields.Clear();
        _bookPages.Children.Add(new TextBlock {
            Text = "Tune your own runs. Changes are saved to your server and kept through updates, and take effect the next " +
                   "time the server wakes up. Restore defaults fills in the values the gauntlet ships with.",
            FontSize = 14, Foreground = TEXT, TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 0, 8, 6),
        });

        var folder = _manager.ConfigFolder;
        foreach (var (group, settings) in PlayerSettings.Groups) {
            var shown = settings.Where(s => _manager.GetSetting(s.Section, s.Key) is not null).ToList();
            if (shown.Count == 0) {
                continue;
            }

            _bookPages.Children.Add(new TextBlock {
                Text = group.ToUpperInvariant(), FontFamily = HEADING_FONT, LetterSpacing = 2, FontSize = 15,
                FontWeight = FontWeight.Bold, Foreground = GOLD_DIM, Margin = new Thickness(0, 14, 0, 2),
            });
            foreach (var setting in shown) {
                _bookPages.Children.Add(SettingRow(setting, _manager.GetSetting(setting.Section, setting.Key)!,
                                                   PlayerSettings.Default(folder, setting)));
            }
        }

        _settingsActions.IsVisible = true;
        _settingsStatus.IsVisible = false;
        _bookScroll.Offset = new Vector(0, 0);
        _book.IsVisible = true;
    }

    private Control SettingRow(PlayerSetting setting, string value, string? defaultValue) {
        Control editor;
        Func<string> read;
        Action<string> write;
        if (setting.Kind == SettingKind.Switch) {
            var toggle = new ToggleSwitch {
                OnContent = null, OffContent = null, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center,
                IsChecked = value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase),
            };
            read = () => toggle.IsChecked == true ? "true" : "false";
            write = v => toggle.IsChecked = v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            editor = toggle;
        }
        else {
            var box = new TextBox {
                Text = value.Trim(),
                Width = setting.Kind == SettingKind.Text ? 240 : 110,
                CornerRadius = new CornerRadius(0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            read = () => box.Text ?? "";
            write = v => box.Text = v.Trim();
            editor = box;
        }

        _settingFields.Add((setting, read, write));

        var shownDefault = defaultValue is null ? ""
            : setting.Kind == SettingKind.Switch
                ? $" Default: {(defaultValue.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) ? "on" : "off")}."
                : $" Default: {defaultValue.Trim()}.";
        var text = new StackPanel {
            Spacing = 1,
            Margin = new Thickness(0, 0, 16, 0),
            Children = {
                new TextBlock { Text = setting.Label, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = TEXT },
                new TextBlock { Text = setting.Help + shownDefault, FontSize = 12, Foreground = SOFT, TextWrapping = TextWrapping.Wrap },
            },
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 4, 8, 4) };
        Grid.SetColumn(editor, 1);
        row.Children.AddRange([text, editor]);

        return row;
    }

    private void SettingsStatus(string text, bool isError = false) {
        _settingsStatus.Text = text;
        _settingsStatus.Foreground = isError ? ERROR : GOLD;
        _settingsStatus.IsVisible = true;
    }

    private void RestoreDefaults() {
        var folder = _manager.ConfigFolder;
        var filled = 0;
        foreach (var (setting, _, write) in _settingFields) {
            if (PlayerSettings.Default(folder, setting) is { } value) {
                write(value);
                filled++;
            }
        }

        SettingsStatus(filled > 0
            ? "The defaults are filled in. Press Save to keep them."
            : "The defaults arrive with the next update. Update once, then try again.", isError: filled == 0);
    }

    private void SaveSettingsPage() {
        if (_busy) {
            SettingsStatus("Hold on, the update is still busy. Save once it is done.", isError: true);

            return;
        }

        var values = _settingFields.Select(f => (f.Setting, Value: f.Read().Trim())).ToList();
        foreach (var (setting, value) in values) {
            if (setting.Check(value) is { } problem) {
                SettingsStatus(problem, isError: true);

                return;
            }
        }

        long Whole(string key) => long.TryParse(values.FirstOrDefault(v => v.Setting.Key == key).Value, NumberStyles.Integer,
                                                CultureInfo.InvariantCulture, out var n) ? n : 0;
        if (values.Any(v => v.Setting.Key == "ChestGoldMin") && values.Any(v => v.Setting.Key == "ChestGoldMax")
            && Whole("ChestGoldMin") > Whole("ChestGoldMax")) {
            SettingsStatus("Chest gold, least can't be more than Chest gold, most.", isError: true);

            return;
        }

        var folder = _manager.ConfigFolder;
        var changedIds = PlayerSettings.Changed(folder);
        var changes = new List<Setting>();
        foreach (var (setting, value) in values) {
            var current = _manager.GetSetting(setting.Section, setting.Key) ?? "";
            var differs = !PlayerSettings.Same(setting, current, value);
            if (differs) {
                changes.Add(new Setting(setting.Section, setting.Key, value, ""));
            }

            // Back at the release's value: updates may refresh it again. Changed by the player: updates keep it.
            if (PlayerSettings.Default(folder, setting) is { } release && PlayerSettings.Same(setting, release, value)) {
                changedIds.Remove(setting.Id);
            }
            else if (differs) {
                changedIds.Add(setting.Id);
            }
        }

        try {
            var running = changes.Count > 0 && _manager.SaveSettings(changes);
            PlayerSettings.SaveChanged(folder, changedIds);
            if (changes.Count == 0) {
                SettingsStatus("Nothing changed.");
            }
            else {
                var saved = changes.Count == 1 ? "1 setting" : $"{changes.Count} settings";
                SettingsStatus(running
                    ? $"Saved {saved}. The server is awake, so they take effect after it rests (Rest the server) and wakes up again."
                    : $"Saved {saved}. They take effect the next time you begin a run.");
            }
        }
        catch (Exception ex) {
            SettingsStatus($"The settings couldn't be saved: {ex.Message}", isError: true);
        }
    }

}
