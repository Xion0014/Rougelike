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
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// The launcher's Music page: a track for each tier's street and each tier's boss fight, or Random. Saved to
/// Imlight.ini's [Music] TierNStreet and TierNBoss, which the server plays on a run (RoguelikeService). Updates keep
/// what the player chose ([Music] is not part of the run design an update refreshes).
/// </summary>
internal sealed partial class LauncherWindow {

    private const string RANDOM = "Random (the run's music)";

    // The tracks the server picks from at random (RoguelikeConfig's MusicTracks and BattleMusicTracks defaults); an
    // Imlight.ini that lists its own is read instead.
    private const string DEFAULT_ZONE_TRACKS = "|Music|WorldData|WC/WC_RavenWood.ogg, |Sound|WorldData|Sound/WizardCity2M.mp3, |Sound|WorldData|Sound/WizardCity3M.mp3, |Sound|WorldData|Sound/WizardCity4M.mp3, |Sound|WorldData|Sound/WizardCity_HauntedCaves.mp3, |Music|WorldData|MB/Marleybone4M.mp3, |DragonSpire|WorldData|Sound/Music/DragonSpyre3.mp3, |Music|WorldData|MS/SB/GNT_SB_Theme.mp3, |Zafaria|WorldData|Sound/Music/ZF_Hub.mp3, |Zafaria|WorldData|Sound/Music/ZF_DrumJungle.mp3, |Zafaria|WorldData|Sound/Music/ZF_Savannah.mp3, |Zafaria|WorldData|Sound/Music/ZF_Zamunda.mp3, |Music|WorldData|AV/AV_Hub.mp3, |Music|WorldData|AV/AV_AbbeyRoad.mp3, |Music|WorldData|AV/AV_Wyrd.mp3, |Music|WorldData|AZ/AZ_City_Theme.mp3, |Music|WorldData|AZ/AZ_Swamp_Tense.mp3, |Music|WorldData|KR/KR_Bastion.mp3, |Music|WorldData|KR/KR_LastWood.mp3, |Music|WorldData|KR/KR_MoonCliffs.mp3, |Music|WorldData|PL/PL_Walruskberg_Theme.mp3, |Music|WorldData|PL/PL_FrozenForest_Theme.mp3, |Music|WorldData|MR/MR_City_Theme.mp3, |Music|WorldData|MR/MR_DesertCanyons_Theme.mp3, |Music|WorldData|EM/EM_Aeriel_Theme.ogg, |Music|WorldData|EM/EM_Jungle_Theme.ogg, |Music|WorldData|EM/EM_Zanadu_Theme.ogg, |Music|WorldData|EM/EM_2/EM_NightmareRealm_Theme.ogg, |Music|WorldData|KM/KM_CityTheme.ogg, |Music|WorldData|KM/KM_RuralTheme.ogg, |Music|WorldData|LM/LM_JunglePulp_Theme.ogg, |Music|WorldData|LM/LM_UrsaiVillage_Theme.ogg, |Music|WorldData|DM/DM_Graveholm_Theme.ogg, |Music|WorldData|DM/DM_Outsiders_Theme.ogg, |Music|WorldData|AQ/AQ_Olympus.mp3";
    private const string DEFAULT_BATTLE_TRACKS = "|Music|WorldData|WC/Combat1Test.ogg, |Music|WorldData|WC/WC_HauntedWay_Combat.ogg, |Music|WorldData|MB/MarleyCombatM.mp3, |DragonSpire|WorldData|Sound/Music/DragonSpyreCombat.mp3, |Music|WorldData|MS/SB/GNT_SB_Combat.mp3, |Zafaria|WorldData|Sound/Music/ZF_Combat.mp3, |Music|WorldData|AV/AV_Combat.mp3, |Music|WorldData|AZ/AZ_Combat.mp3, |Music|WorldData|KR/KR_CombatBastion.mp3, |Music|WorldData|KR/KR_CombatArachna.mp3, |Music|WorldData|PL/PL_Walruskberg_Combat.mp3, |Music|WorldData|PL/PL_FrozenCombat.mp3, |Music|WorldData|MR/MR_CityCombat.mp3, |Music|WorldData|MR/MR_CanyonDesert_Combat.mp3, |Music|WorldData|EM/EM_Zanadu_Combat.ogg, |Music|WorldData|EM/EM_FlotsamStorm_Combat.ogg, |Music|WorldData|EM/EM_2/EM_Reverie_Combat.ogg, |Music|WorldData|KM/KM_CityCombat.ogg, |Music|WorldData|KM/KM_RuralCombat.ogg, |Music|WorldData|LM/LM_JunglePulp_Combat.ogg, |Music|WorldData|DM/DM_Graveholm_Combat.ogg, |Music|WorldData|AQ/AQ_Combat.mp3";

    private static readonly (string Code, string World)[] s_musicWorlds = [
        ("WizardCity", "Wizard City"), ("WC", "Wizard City"), ("KT", "Krokotopia"), ("Marley", "Marleybone"), ("MB", "Marleybone"),
        ("MS", "MooShu"), ("DragonSpyre", "Dragonspyre"), ("DragonSpire", "Dragonspyre"), ("DS", "Dragonspyre"), ("GH", "Grizzleheim"),
        ("CL", "Celestia"), ("ZF", "Zafaria"), ("AV", "Avalon"), ("AZ", "Azteca"), ("KR", "Khrysalis"), ("PL", "Polaris"),
        ("MR", "Mirage"), ("EM", "Empyrea"), ("KM", "Karamelle"), ("LM", "Lemuria"), ("DM", "Darkmoor"), ("AQ", "Aquila"),
    ];

    private readonly List<(string Key, ComboBox Box, List<string> Paths)> _musicFields = [];
    private bool _musicPage;

    private void OpenMusic() {
        if (!_manager.IsInstalled) {
            Hint("Set the gauntlet up first; its music settings come with it.", isError: true);

            return;
        }

        if (_manager.GetSetting("Music", "Tier1Street") is null) {
            Hint("Update the gauntlet first; the music settings arrive with the update.", isError: true);

            return;
        }

        _musicPage = true;
        _bookMustAnswer = false;
        _bookAnswer.IsVisible = false;
        if (_bookClose is not null) {
            _bookClose.IsVisible = true;
        }

        var zoneTracks = Tracks(_manager.GetSetting("Roguelike", "MusicTracks"), DEFAULT_ZONE_TRACKS);
        var battleTracks = Tracks(_manager.GetSetting("Roguelike", "BattleMusicTracks"), DEFAULT_BATTLE_TRACKS);
        var streetChoices = zoneTracks.Concat(battleTracks.Except(zoneTracks)).ToList();
        var bossChoices = battleTracks.Concat(zoneTracks.Except(battleTracks)).ToList();

        _bookTitle.Text = "Music";
        _bookPages.Children.Clear();
        _musicFields.Clear();
        _bookPages.Children.Add(new TextBlock {
            Text = "Pick the music for each tier's street and each tier's boss fight. Random keeps the run's own music. " +
                   "Changes take effect the next time the server wakes up; the in-game Music volume still applies.",
            FontSize = 14, Foreground = TEXT, TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 0, 8, 6),
        });

        for (var tier = 1; tier <= _tiers.Worlds.Count; tier++) {
            if (_manager.GetSetting("Music", $"Tier{tier}Street") is not { } street) {
                break;
            }

            _bookPages.Children.Add(new TextBlock {
                Text = $"TIER {tier}: {_tiers.Worlds[tier - 1].ToUpperInvariant()}", FontFamily = HEADING_FONT, LetterSpacing = 2,
                FontSize = 15, FontWeight = FontWeight.Bold, Foreground = GOLD_DIM, Margin = new Thickness(0, 14, 0, 2),
            });
            _bookPages.Children.Add(MusicRow("Street", $"Tier{tier}Street", street, streetChoices));
            _bookPages.Children.Add(MusicRow("Boss fight", $"Tier{tier}Boss", _manager.GetSetting("Music", $"Tier{tier}Boss") ?? "", bossChoices));
        }

        _settingsActions.IsVisible = true;
        _settingsStatus.IsVisible = false;
        _bookScroll.Offset = new Vector(0, 0);
        _book.IsVisible = true;
    }

    private Control MusicRow(string label, string key, string current, List<string> choices) {
        current = current.Trim();
        var paths = new List<string> { "" };
        paths.AddRange(choices);
        if (current.Length > 0 && !current.Equals("Random", StringComparison.OrdinalIgnoreCase) && !paths.Contains(current)) {
            // A track typed into Imlight.ini by hand stays choosable.
            paths.Add(current);
        }

        var box = new ComboBox {
            ItemsSource = paths.Select(p => p.Length == 0 ? RANDOM : TrackName(p)).ToList(),
            SelectedIndex = Math.Max(0, paths.IndexOf(current.Equals("Random", StringComparison.OrdinalIgnoreCase) ? "" : current)),
            Width = 330,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _musicFields.Add((key, box, paths));

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 3, 8, 3) };
        var text = new TextBlock { Text = label, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = TEXT, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(box, 1);
        row.Children.AddRange([text, box]);

        return row;
    }

    private void RestoreMusic() {
        foreach (var (_, box, _) in _musicFields) {
            box.SelectedIndex = 0;
        }

        SettingsStatus("Every street and boss fight is back to Random. Press Save to keep it.");
    }

    private void SaveMusicPage() {
        if (_busy) {
            SettingsStatus("Hold on, the update is still busy. Save once it is done.", isError: true);

            return;
        }

        var changes = new List<Setting>();
        foreach (var (key, box, paths) in _musicFields) {
            var chosen = box.SelectedIndex > 0 && box.SelectedIndex < paths.Count ? paths[box.SelectedIndex] : "";
            var current = (_manager.GetSetting("Music", key) ?? "").Trim();
            if (current.Equals("Random", StringComparison.OrdinalIgnoreCase)) {
                current = "";
            }

            if (!string.Equals(current, chosen, StringComparison.Ordinal)) {
                changes.Add(new Setting("Music", key, chosen, ""));
            }
        }

        try {
            if (changes.Count == 0) {
                SettingsStatus("Nothing changed.");

                return;
            }

            var running = _manager.SaveSettings(changes);
            var saved = changes.Count == 1 ? "1 track" : $"{changes.Count} tracks";
            SettingsStatus(running
                ? $"Saved {saved}. The server is awake, so they play after it rests (Rest the server) and wakes up again."
                : $"Saved {saved}. They play the next time you begin a run.");
        }
        catch (Exception ex) {
            SettingsStatus($"The music couldn't be saved: {ex.Message}", isError: true);
        }
    }

    private static List<string> Tracks(string? configured, string defaults) {
        var list = string.IsNullOrWhiteSpace(configured) ? defaults : configured;

        return [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct()];
    }

    /// <summary>A track as players would name it: "|Music|WorldData|AV/AV_Combat.mp3" is "Avalon: Combat".</summary>
    internal static string TrackName(string path) {
        var file = Path.GetFileNameWithoutExtension(path.Replace('|', '/'));
        var folders = path.Replace('|', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? world = null;
        foreach (var (code, name) in s_musicWorlds) {
            if (folders.Any(f => f.Equals(code, StringComparison.OrdinalIgnoreCase))
                || file.StartsWith(code + "_", StringComparison.OrdinalIgnoreCase)
                || (code.Length > 3 && file.StartsWith(code, StringComparison.OrdinalIgnoreCase))) {
                world = name;
                break;
            }
        }

        var words = file;
        foreach (var (code, _) in s_musicWorlds.OrderByDescending(w => w.Code.Length)) {
            if (words.StartsWith(code, StringComparison.OrdinalIgnoreCase) && words.Length > code.Length) {
                words = words[code.Length..];
                break;
            }
        }

        words = string.Join(' ', words.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !w.Equals("Theme", StringComparison.OrdinalIgnoreCase)));
        if (words.Length == 0) {
            words = "Theme";
        }

        if (file.Contains("Combat", StringComparison.OrdinalIgnoreCase) && !words.Contains("Combat", StringComparison.OrdinalIgnoreCase)) {
            words += " (battle)";
        }

        return world is null ? words : $"{world}: {words}";
    }

}
