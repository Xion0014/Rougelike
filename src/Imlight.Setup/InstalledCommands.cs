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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Imlight.Setup;

/// <summary>
/// What the installed Imlight.exe does from its shortcuts: start the server, play, and uninstall.
/// </summary>
internal static class InstalledCommands {

    internal const string INSTALLED_EXE = Edition.INSTALLED_EXE;
    internal const string SHORTCUT_FOLDER = Edition.NAME;
    internal const string APP_SHORTCUT = Edition.NAME + ".lnk";
    internal const string START_SHORTCUT = "Start " + Edition.NAME + ".lnk";
    internal const string PLAY_SHORTCUT = "Play Wizard101 on " + Edition.NAME + ".lnk";
    internal const string PANEL_SHORTCUT = Edition.NAME + " Control Panel.lnk";
    private const string UNINSTALL_KEY = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Edition.REGISTRY_KEY;
    private const string DEFAULT_PATCH_URL = "http://127.0.0.1:12369";

    internal static string ServerFolder(string installFolder) => Path.Combine(installFolder, "Server");

    internal static string ServerExe(string installFolder) => Path.Combine(ServerFolder(installFolder),
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Imlight.Director.exe" : "Imlight.Director");

    internal static string ConfigFile(string installFolder) => Path.Combine(ServerFolder(installFolder), "Config", "Imlight.ini");

    // ---------------------------------------------------------------------
    // Start
    // ---------------------------------------------------------------------

    /// <summary>
    /// Serves the game files from the player's Wizard101 folder, then runs the server until it closes.
    /// </summary>
    internal static async Task<int> StartAsync(string installFolder, bool noWait = false) {
        Console.Title = Edition.NAME;
        var serverExe = ServerExe(installFolder);
        if (!File.Exists(serverExe)) {
            return Fail($"Imlight is not installed in this folder yet. Run {Edition.RELEASE_ASSET} first.");
        }

        if (IsServerRunning(installFolder)) {
            return Fail("The Imlight server is already running in another window.");
        }

        using var gameFiles = StartGameDataServer(installFolder);

        var startInfo = new ProcessStartInfo(serverExe) {
            WorkingDirectory = ServerFolder(installFolder),
            UseShellExecute = false,
        };
        using var server = Process.Start(startInfo);
        if (server is null) {
            return Fail("The server could not be started.");
        }

        // Ctrl+C reaches the server too; wait for it to shut down instead of leaving it behind.
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        await server.WaitForExitAsync();

        Console.WriteLine();
        if (server.ExitCode != 0) {
            ShowLastErrors(installFolder);
        }

        Console.WriteLine($"The server stopped (exit code {server.ExitCode}). Its log is in {Path.Combine(ServerFolder(installFolder), "logs")}.");
        if (!noWait) {
            WaitForKey();
        }

        return server.ExitCode;
    }

    /// <summary>
    /// Prints the end of the newest server log after a crash, so the reason is on screen for a screenshot.
    /// </summary>
    private static void ShowLastErrors(string installFolder) {
        var logs = Path.Combine(ServerFolder(installFolder), "logs");
        var file = Directory.Exists(logs)
            ? new DirectoryInfo(logs).EnumerateFiles().OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
            : null;
        if (file is null) {
            return;
        }

        try {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().ReplaceLineEndings("\n").TrimEnd().Split('\n');
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("The last lines the server wrote before it stopped:");
            Console.ResetColor();
            foreach (var line in lines.TakeLast(25)) {
                Console.WriteLine(line);
            }

            Console.WriteLine();
        }
        catch (IOException) {
            // The log could not be read; its folder is printed below.
        }
    }

    private static GameDataServer? StartGameDataServer(string installFolder) {
        var info = InstallInfo.Load(installFolder);
        var url = IniFile.Get(ConfigFile(installFolder), "Patch Server", "PatchServerInternalUrl") ?? DEFAULT_PATCH_URL;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsLoopback) {
            Console.WriteLine($"Using the patch server at {url}.");

            return null;
        }

        if (info.GameDataFolder is not { } gameData || !Directory.Exists(gameData)) {
            Console.WriteLine("Imlight does not know where Wizard101 is. Click \"Update Imlight\" on the Tools tab of the app once to fix that.");

            return null;
        }

        var server = GameDataServer.TryStart(gameData, uri.Port);
        Console.WriteLine(server is null
            ? $"Another patch server (such as Aurorium) is already using port {uri.Port}, so Imlight will use it."
            : $"Giving the server game files from {gameData}.");

        return server;
    }

    // ---------------------------------------------------------------------
    // Play
    // ---------------------------------------------------------------------

    /// <summary>
    /// Starts the player's Wizard101 connected to the Imlight server on this computer.
    /// </summary>
    internal static int Play(string installFolder) {
        var info = InstallInfo.Load(installFolder);
        var client = info.GameFolder is { } game ? Path.Combine(game, "Bin", "WizardGraphicalClient.exe") : null;
        if (client is null || !File.Exists(client)) {
            return Fail("Wizard101 could not be found. Click \"Update Imlight\" on the Tools tab of the app once, then try again.");
        }

        var portText = IniFile.Get(ConfigFile(installFolder), "Login Server", "LoginServerPort");
        var port = int.TryParse(portText, out var parsed) ? parsed : 12000;
        if (!IsListening(port)) {
            return Fail($"The Imlight server is not running yet. Open \"Start {Edition.NAME}\", wait until it says " +
                "\"Imlight may now be connected to.\", then try again.");
        }

        var startInfo = new ProcessStartInfo(client) {
            WorkingDirectory = Path.GetDirectoryName(client)!,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-L");
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.ArgumentList.Add(port.ToString());
        Process.Start(startInfo)?.Dispose();
        Console.WriteLine("Starting Wizard101. Log in with the admin account you made during setup.");

        return 0;
    }

    private static bool IsListening(int port) {
        try {
            using var client = new TcpClient();

            return client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromSeconds(2)) && client.Connected;
        }
        catch (AggregateException) {
            return false;
        }
    }

    // ---------------------------------------------------------------------
    // Uninstall
    // ---------------------------------------------------------------------

    /// <summary>
    /// Removes Imlight, its shortcuts and its Apps entry. Accounts and characters are kept unless the person says otherwise.
    /// </summary>
    internal static int Uninstall(string installFolder, bool unattended) {
        Console.Title = "Uninstall " + Edition.NAME;
        Console.WriteLine($"This removes the Imlight server from {installFolder}.");
        Console.WriteLine("Wizard101 itself is not touched.");
        if (!unattended && !Prompt.YesNo("Uninstall Imlight?", false)) {
            return 0;
        }

        if (IsServerRunning(installFolder)) {
            return Fail("Close the Imlight server window first, then run Uninstall again.");
        }

        var database = DatabaseFolder(installFolder);
        var keepData = database is not null && Directory.Exists(database)
            && (unattended || !Prompt.YesNo("Also delete every account and character on this server?", false));

        RemoveShortcuts();
        if (OperatingSystem.IsWindows()) {
            Registry.CurrentUser.DeleteSubKeyTree(UNINSTALL_KEY, throwOnMissingSubKey: false);
        }

        var self = Environment.ProcessPath is { } path ? Path.GetFullPath(path) : null;
        var failed = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(installFolder)) {
            var full = Path.GetFullPath(entry);
            if (string.Equals(full, self, StringComparison.OrdinalIgnoreCase)
                || (keepData && string.Equals(full, database, StringComparison.OrdinalIgnoreCase))) {
                continue;
            }

            try {
                if (Directory.Exists(full)) {
                    Directory.Delete(full, recursive: true);
                }
                else {
                    File.Delete(full);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                Console.WriteLine($"Could not remove {full}: {ex.Message}");
                failed++;
            }
        }

        // A running program cannot delete itself; a short-lived command removes it once this window closes.
        if (self is not null && Path.GetDirectoryName(self) is { } selfFolder
            && string.Equals(selfFolder, Path.GetFullPath(installFolder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            && OperatingSystem.IsWindows()) {
            var cleanup = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            cleanup.ArgumentList.Add("/c");
            cleanup.ArgumentList.Add($"ping -n 3 127.0.0.1 >nul & del /f /q \"{self}\" & rmdir \"{selfFolder}\"");
            Process.Start(cleanup)?.Dispose();
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "Imlight was uninstalled." : $"Imlight was uninstalled, but {failed} item(s) could not be removed. They are listed above.");
        if (keepData) {
            Console.WriteLine($"Your accounts and characters were kept in {database}. Installing again picks them up.");
        }

        WaitForKey();

        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// The embedded player database folder, when it lives inside the install folder.
    /// </summary>
    private static string? DatabaseFolder(string installFolder) {
        var setting = IniFile.Get(ConfigFile(installFolder), "Database", "EmbeddedDatabaseDataDirectory") ?? "../ImlightEmbeddedDatabase/";
        var full = Path.GetFullPath(Path.Combine(ServerFolder(installFolder), setting)).TrimEnd(Path.DirectorySeparatorChar, '/');
        var root = Path.GetFullPath(installFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static void RemoveShortcuts() {
        if (!OperatingSystem.IsWindows()) {
            return;
        }

        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), SHORTCUT_FOLDER);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        try {
            if (Directory.Exists(startMenu)) {
                Directory.Delete(startMenu, recursive: true);
            }

            foreach (var name in new[] { APP_SHORTCUT, START_SHORTCUT, PLAY_SHORTCUT, PANEL_SHORTCUT }) {
                File.Delete(Path.Combine(desktop, name));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Console.WriteLine($"Some shortcuts could not be removed: {ex.Message}");
        }
    }

    /// <summary>
    /// Lists Imlight under Settings, Apps so it can be uninstalled from there too. Per user, so no admin rights are needed.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static void RegisterUninstaller(string installFolder, string version) {
        var exe = Path.Combine(installFolder, INSTALLED_EXE);
        using var key = Registry.CurrentUser.CreateSubKey(UNINSTALL_KEY);
        key.SetValue("DisplayName", Edition.NAME);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "Imlight");
        key.SetValue("InstallLocation", installFolder);
        key.SetValue("DisplayIcon", exe);
        key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    // ---------------------------------------------------------------------
    // Shared
    // ---------------------------------------------------------------------

    internal static bool IsServerRunning(string installFolder) {
        var exe = Path.GetFullPath(ServerExe(installFolder));

        return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)).Any(p => {
            try {
                return string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) {
                // Another user's process; it cannot be ours.
                return false;
            }
            finally {
                p.Dispose();
            }
        });
    }

    private static int Fail(string message) {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ResetColor();
        WaitForKey();

        return 1;
    }

    private static void WaitForKey() {
        if (Console.IsInputRedirected) {
            return;
        }

        Console.WriteLine("Press any key to close this window.");
        Console.ReadKey(true);
    }

}
