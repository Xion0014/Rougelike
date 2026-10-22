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

using System.Linq;

namespace Imlight.Setup;

/// <summary>
/// The builds of this program (and SpiralRoguelike, built with -p:ImlightEdition=Roguelike). ImlightSetup installs original Imlight; ImlightManager (built with
/// -p:ImlightEdition=Manager) installs the Xion0014/Rougelike fork, which adds roguelike mode, the
/// randomizer and ID lookup. They install side by side under different names.
/// </summary>
internal static class Edition {

    /// <summary>
    /// The repository whose GitHub releases update this program and the launcher.
    /// </summary>
    internal const string RELEASE_REPOSITORY = "Xion0014/Rougelike";

    /// <summary>
    /// The branch installed and updated by default. The release workflow bakes in the branch it built
    /// from (-p:ImlightBranch=...); a local build falls back to quality-assurance.
    /// </summary>
    internal static string DefaultBranch { get; } = typeof(Edition).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .OfType<System.Reflection.AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "ImlightBranch" && !string.IsNullOrWhiteSpace(a.Value))?.Value
        ?? "quality-assurance";

#if ROGUELIKE_EDITION
    // The roguelike launcher (built with -p:ImlightEdition=Roguelike): a playful window that installs the roguelike
    // server on the player's own PC, keeps it updated, and plays Wizard101 on it. Accounts are made at first login.
    internal const bool IS_MANAGER = true;
    internal const bool IS_ROGUELIKE = true;
    // Spiral Gauntlet. The file and folder names keep the first name (SpiralRoguelike), so installs and their
    // updates carry on; releases also carry the launcher as SpiralGauntlet.exe for new downloads.
    internal const string NAME = "Spiral Gauntlet";
    internal const string DEFAULT_REPOSITORY = "Xion0014/Rougelike";
    internal const string INSTALL_FOLDER_NAME = "SpiralRoguelike";
    internal const string RELEASE_ASSET = "SpiralRoguelike.exe";
    internal const string REGISTRY_KEY = "SpiralRoguelike";
    internal const string INSTALLED_EXE = "SpiralRoguelike.exe";
#elif MANAGER_EDITION
    internal const bool IS_MANAGER = true;
    internal const bool IS_ROGUELIKE = false;
    internal const string INSTALLED_EXE = "Imlight.exe";
    internal const string NAME = "Imlight Manager";
    internal const string DEFAULT_REPOSITORY = "Xion0014/Rougelike";
    internal const string INSTALL_FOLDER_NAME = "ImlightManager";
    internal const string RELEASE_ASSET = "ImlightManager.exe";
    internal const string REGISTRY_KEY = "ImlightManager";
#else
    internal const bool IS_MANAGER = false;
    internal const bool IS_ROGUELIKE = false;
    internal const string INSTALLED_EXE = "Imlight.exe";
    internal const string NAME = "Imlight Server";
    internal const string DEFAULT_REPOSITORY = "Revive101/Imlight";
    internal const string INSTALL_FOLDER_NAME = "ImlightServer";
    internal const string RELEASE_ASSET = "ImlightSetup.exe";
    internal const string REGISTRY_KEY = "ImlightServer";
#endif

}
