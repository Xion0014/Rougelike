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

namespace Imlight.Setup;

/// <summary>
/// What the program was asked to do. Install also updates an existing install.
/// </summary>
internal enum SetupMode { Install, Start, Play, Uninstall, Panel }

/// <summary>
/// What setup installs and where, from the command line. The defaults install original Imlight.
/// </summary>
internal sealed class SetupOptions {

    /// <summary>What to do. The shortcuts pass --start, --update or --uninstall; the installed Imlight.exe opens the app by default.</summary>
    public SetupMode Mode { get; private set; } = Edition.IS_ROGUELIKE || IsInstalledCopy() ? SetupMode.Panel : SetupMode.Install;

    /// <summary>The GitHub repository (owner/repo) to build Imlight from.</summary>
    public string Repository { get; private set; } = Edition.DEFAULT_REPOSITORY;

    /// <summary>The branch to build.</summary>
    public string Branch { get; private set; } = Edition.DefaultBranch;

    /// <summary>Where Imlight is installed.</summary>
    public string InstallFolder { get; private set; } = DefaultInstallFolder();

    /// <summary>The Wizard101 folder, when the person names it.</summary>
    public string? GameFolder { get; private set; }

    /// <summary>A wiztype type dump to use instead of the one Imcodec ships.</summary>
    public string? TypeDump { get; private set; }

    /// <summary>The admin password, for unattended installs.</summary>
    public string? AdminPassword { get; private set; }

    /// <summary>If true, Start closes as soon as the server stops instead of waiting for a key.</summary>
    public bool NoWait { get; private set; }

    /// <summary>If true, setup never stops to ask anything.</summary>
    public bool Unattended { get; private set; }

    internal const string USAGE = """
        Usage: ImlightSetup [options]

          --dir <folder>             Where to install. Default: %LOCALAPPDATA%\ImlightServer
          --game <folder>            Your Wizard101 folder, if setup cannot find it.
          --type-dump <file>         A wiztype type dump to use instead of the bundled one.
          --repo <owner/repo>        Build from another GitHub repository. Default: Revive101/Imlight (Xion0014/Rougelike for Imlight Manager)
          --branch <name>            Build another branch. Default: the branch this release was built from
          --admin-password <text>    The admin password, for --yes installs.
          --yes                      Do not ask anything; use the defaults.

        The installed Imlight.exe also takes:
          (nothing)                  Open the app: run the server, play, change settings and manage accounts.
          --update                   Update Imlight, the same as running the setup again.
          --start                    Start the server in a window, with game files from your Wizard101 folder.
          --play                     Start Wizard101 connected to the server on this computer.
          --uninstall                Remove Imlight from this computer.
          --panel                    Open the app (the same as no option).
        """;

    internal static SetupOptions Parse(string[] args) {
        var options = new SetupOptions();
        for (var i = 0; i < args.Length; i++) {
            string Next() => i + 1 < args.Length
                ? args[++i]
                : throw new SetupException($"{args[i]} needs a value.\n\n{USAGE}");

            switch (args[i].ToLowerInvariant()) {
                case "--dir": options.InstallFolder = Path.GetFullPath(Next()); break;
                case "--game": options.GameFolder = Next(); break;
                case "--type-dump": options.TypeDump = Path.GetFullPath(Next()); break;
                case "--repo": options.Repository = Next(); break;
                case "--branch": options.Branch = Next(); break;
                case "--admin-password": options.AdminPassword = Next(); break;
                case "--yes": options.Unattended = true; break;
                case "--start": options.Mode = SetupMode.Start; break;
                case "--play": options.Mode = SetupMode.Play; break;
                case "--uninstall": options.Mode = SetupMode.Uninstall; break;
                case "--panel": options.Mode = SetupMode.Panel; break;
                case "--update": options.Mode = SetupMode.Install; break;
                case "--no-wait": options.NoWait = true; break;
                default: throw new SetupException($"Unknown option {args[i]}.\n\n{USAGE}");
            }
        }

        return options;
    }

    /// <summary>
    /// True when this program is the Imlight.exe that setup installed, rather than the setup download.
    /// </summary>
    private static bool IsInstalledCopy() {
        var self = Environment.ProcessPath;

        return self is not null
            && string.Equals(Path.GetFileName(self), InstalledCommands.INSTALLED_EXE, StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(Path.GetDirectoryName(self)!, InstallInfo.FILE_NAME));
    }

    /// <summary>
    /// The folder this program runs from when it is an installed Imlight.exe, otherwise %LOCALAPPDATA%\ImlightServer.
    /// </summary>
    private static string DefaultInstallFolder() {
        var ownFolder = Environment.ProcessPath is { } self ? Path.GetDirectoryName(self) : null;
        if (ownFolder is not null && File.Exists(Path.Combine(ownFolder, InstallInfo.FILE_NAME))) {
            return ownFolder;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Edition.INSTALL_FOLDER_NAME);
    }

}
