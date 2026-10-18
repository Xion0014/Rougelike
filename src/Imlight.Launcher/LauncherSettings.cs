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
using System.IO;
using System.Text.Json;

namespace Imlight.Launcher;

/// <summary>
/// The launcher's settings, read from launcher.json next to the launcher.
/// </summary>
internal sealed class LauncherSettings {

    /// <summary>The public GitHub repository (owner/repo) whose releases are installed.</summary>
    public string Repository { get; set; } = "Xion0014/Rougelike";

    /// <summary>If true, pre-releases count as updates too.</summary>
    public bool IncludePrereleases { get; set; }

    /// <summary>If true, the launcher updates itself and the server before starting it.</summary>
    public bool CheckForUpdatesOnStart { get; set; } = true;

    /// <summary>How often to look for updates while the server runs. 0 turns it off.</summary>
    public int CheckIntervalMinutes { get; set; } = 30;

    /// <summary>
    /// If true, a server update found while running stops the server, installs the update and starts it again.
    /// Players are disconnected and anything not yet saved is lost, so this is off by default.
    /// </summary>
    public bool RestartServerForUpdates { get; set; }

    /// <summary>The server folder, relative to the launcher.</summary>
    public string ServerFolder { get; set; } = "Server";

    /// <summary>The server program inside <see cref="ServerFolder"/>.</summary>
    public string ServerExecutable { get; set; } = "Imlight.Director.exe";

    internal static LauncherSettings Load(string path) {
        if (!File.Exists(path)) {
            return new LauncherSettings();
        }

        try {
            return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path),
                new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                ?? new LauncherSettings();
        }
        catch (JsonException ex) {
            Console.WriteLine($"launcher.json could not be read ({ex.Message}). Using the defaults.");

            return new LauncherSettings();
        }
    }

}
