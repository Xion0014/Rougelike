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
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Imcodec.Wad;

namespace Imlight.Setup;

/// <summary>
/// Thrown when a step cannot continue. The message is written for the person running setup.
/// </summary>
internal sealed class SetupException(string message) : Exception(message);

/// <summary>
/// The individual setup steps. Each one is safe to run again, so re-running setup updates an install.
/// </summary>
internal sealed class SetupSteps : IDisposable {

    private const string DOTNET_CHANNEL = "10.0";

    private readonly SetupOptions _options;
    private readonly HttpClient _http;

    internal SetupSteps(SetupOptions options) {
        _options = options;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ImlightSetup", SelfUpdate.CurrentVersion.ToString()));
    }

    internal string SourceFolder => Path.Combine(_options.InstallFolder, "Source");
    internal string ServerFolder => Path.Combine(_options.InstallFolder, "Server");
    internal string ConfigFile => Path.Combine(ServerFolder, "Config", "Imlight.ini");
    private string ImcodecFolder => Path.Combine(SourceFolder, "submodule", "Imcodec");

    // ---------------------------------------------------------------------
    // 1. Game client
    // ---------------------------------------------------------------------

    /// <summary>
    /// Finds Root.wad in the person's Wizard101 install, asking when it is not in a usual place,
    /// and remembers it so starting the server and the game needs no questions.
    /// </summary>
    internal string FindRootWad() {
        var info = InstallInfo.Load(_options.InstallFolder);
        if (_options.GameFolder is null && info.RootWad is { } saved && File.Exists(saved)) {
            SetupLog.Info($"Using Wizard101 at {info.GameFolder ?? info.GameDataFolder}.");

            return saved;
        }

        info.RootWad = Path.GetFullPath(LocateRootWad());
        info.Save(_options.InstallFolder);

        return info.RootWad;
    }

    private string LocateRootWad() {
        if (_options.GameFolder is { } given) {
            var rootWad = ResolveRootWad(given) ?? throw new SetupException($"Root.wad was not found in {given}.");
            SetupLog.Info($"Using Wizard101 at {given}.");

            return rootWad;
        }

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidates = new[] {
            Path.Combine(programData, "KingsIsle Entertainment", "Wizard101"),
            Path.Combine(programFilesX86, "Steam", "steamapps", "common", "Wizard101"),
            Path.Combine(programFiles, "Steam", "steamapps", "common", "Wizard101"),
            Path.Combine(programFilesX86, "KingsIsle Entertainment", "Wizard101"),
            Path.Combine(programFiles, "KingsIsle Entertainment", "Wizard101"),
        };

        foreach (var candidate in candidates) {
            if (ResolveRootWad(candidate) is { } found) {
                SetupLog.Info($"Found Wizard101 at {candidate}.");

                return found;
            }
        }

        if (_options.Unattended) {
            throw new SetupException("Wizard101 was not found. Run setup again with --game \"<your Wizard101 folder>\".");
        }

        while (true) {
            var answer = Prompt.Ask("Wizard101 was not found. Paste the folder it is installed in (the one with Data inside)");
            if (ResolveRootWad(answer.Trim('"', ' ')) is { } found) {
                return found;
            }

            SetupLog.Info("Root.wad is not in that folder. It should be at <folder>\\Data\\GameData\\Root.wad.");
        }
    }

    private static string? ResolveRootWad(string path) {
        if (File.Exists(path) && Path.GetFileName(path).Equals("Root.wad", StringComparison.OrdinalIgnoreCase)) {
            return path;
        }

        var inGame = Path.Combine(path, "Data", "GameData", "Root.wad");
        if (File.Exists(inGame)) {
            return inGame;
        }

        var inGameData = Path.Combine(path, "Root.wad");

        return File.Exists(inGameData) ? inGameData : null;
    }

    // ---------------------------------------------------------------------
    // 2. .NET SDK
    // ---------------------------------------------------------------------

    /// <summary>
    /// Returns a dotnet that has the .NET 10 SDK, installing a private copy when the computer has none.
    /// </summary>
    internal async Task<string> EnsureDotnetAsync() {
        var privateDotnet = Path.Combine(_options.InstallFolder, "dotnet",
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet");

        foreach (var dotnet in new[] { "dotnet", privateDotnet }) {
            if (HasSdk(dotnet)) {
                SetupLog.Info($"The .NET {DOTNET_CHANNEL} SDK is installed.");

                return dotnet;
            }
        }

        SetupLog.Info($"Installing the .NET {DOTNET_CHANNEL} SDK for Imlight only. This does not change the rest of your computer.");
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var script = Path.Combine(Path.GetTempPath(), isWindows ? "dotnet-install.ps1" : "dotnet-install.sh");
        await DownloadFileAsync(isWindows ? "https://dot.net/v1/dotnet-install.ps1" : "https://dot.net/v1/dotnet-install.sh", script);

        var installDir = Path.GetDirectoryName(privateDotnet)!;
        var exitCode = isWindows
            ? await ProcessRunner.RunAsync("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script,
                "-Channel", DOTNET_CHANNEL, "-InstallDir", installDir])
            : await ProcessRunner.RunAsync("bash", [script, "--channel", DOTNET_CHANNEL, "--install-dir", installDir]);
        if (exitCode != 0 || !HasSdk(privateDotnet)) {
            throw new SetupException($"The .NET {DOTNET_CHANNEL} SDK could not be installed. Install it yourself from https://dotnet.microsoft.com/download and run setup again.");
        }

        return privateDotnet;
    }

    private static bool HasSdk(string dotnet) {
        try {
            var output = ProcessRunner.Capture(dotnet, ["--list-sdks"]);

            return output.Split('\n').Any(line => line.TrimStart().StartsWith(DOTNET_CHANNEL.Split('.')[0] + ".", StringComparison.Ordinal));
        }
        catch (Exception) {
            // Not installed, or not on the PATH.
            return false;
        }
    }

    // ---------------------------------------------------------------------
    // 3. Source code
    // ---------------------------------------------------------------------

    /// <summary>
    /// Downloads the latest Imlight source and the exact Imcodec version it uses. Git is not needed.
    /// </summary>
    internal async Task DownloadSourceAsync() {
        var repo = _options.Repository;
        var branch = _options.Branch;

        SetupLog.Info($"Downloading Imlight from github.com/{repo} ({branch})..");
        var submodule = await GetJsonAsync($"https://api.github.com/repos/{repo}/contents/submodule/Imcodec?ref={Uri.EscapeDataString(branch)}");
        var imcodecCommit = submodule.GetProperty("sha").GetString()
            ?? throw new SetupException("Could not tell which Imcodec version Imlight uses.");
        var imcodecRepo = RepositoryFromUrl(submodule.TryGetProperty("submodule_git_url", out var url) ? url.GetString() : null)
            ?? "Jooty/Imcodec";
        SetupLog.Detail($"Imcodec: {imcodecRepo} at {imcodecCommit}");

        if (Directory.Exists(SourceFolder)) {
            Directory.Delete(SourceFolder, recursive: true);
        }

        await DownloadAndExtractAsync($"https://codeload.github.com/{repo}/zip/refs/heads/{branch}", SourceFolder);
        SetupLog.Info("Downloading Imcodec..");
        await DownloadAndExtractAsync($"https://codeload.github.com/{imcodecRepo}/zip/{imcodecCommit}", ImcodecFolder);
    }

    private static string? RepositoryFromUrl(string? gitUrl) {
        // https://github.com/Jooty/Imcodec.git -> Jooty/Imcodec
        if (gitUrl is null || !gitUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        var path = gitUrl[(gitUrl.IndexOf("github.com", StringComparison.OrdinalIgnoreCase) + "github.com".Length)..]
            .Trim('/', ':');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) {
            path = path[..^4];
        }

        return path.Count(c => c == '/') == 1 ? path : null;
    }

    private async Task DownloadAndExtractAsync(string url, string targetFolder) {
        var zipPath = Path.Combine(Path.GetTempPath(), $"imlight-setup-{Guid.NewGuid():N}.zip");
        try {
            await DownloadFileAsync(url, zipPath);
            Directory.CreateDirectory(targetFolder);
            var root = Path.GetFullPath(targetFolder) + Path.DirectorySeparatorChar;

            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries) {
                // GitHub puts everything in one top folder (Imlight-quality-assurance/); drop it.
                var slash = entry.FullName.IndexOf('/');
                var relative = slash < 0 ? "" : entry.FullName[(slash + 1)..];
                if (relative.Length == 0) {
                    continue;
                }

                var target = Path.GetFullPath(Path.Combine(root, relative));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                if (relative.EndsWith('/')) {
                    Directory.CreateDirectory(target);

                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }
        finally {
            File.Delete(zipPath);
        }
    }

    // ---------------------------------------------------------------------
    // 4. Generator inputs
    // ---------------------------------------------------------------------

    /// <summary>
    /// Puts the two files Imcodec needs in place: the type dump and the message definitions, which come
    /// from the person's own game client, so nothing copyrighted is ever downloaded or shared.
    /// </summary>
    internal void PrepareGeneratorInputs(string rootWadPath) {
        var typeInput = Path.Combine(ImcodecFolder, "src", "Imcodec.ObjectProperty", "GeneratorInput");
        var messageInput = Path.Combine(ImcodecFolder, "src", "Imcodec.MessageLayer", "GeneratorInput");
        Directory.CreateDirectory(typeInput);
        Directory.CreateDirectory(messageInput);

        // The type dump: the person's own, or the one Imcodec ships for its tests.
        var typeDump = _options.TypeDump
            ?? Directory.EnumerateFiles(Path.Combine(ImcodecFolder, "test", "CodeGen", "Inputs"), "*.json")
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
            ?? throw new SetupException("No type dump was found. Make one with wiztype and run setup again with --type-dump \"<file>\".");
        if (!File.Exists(typeDump)) {
            throw new SetupException($"The type dump {typeDump} does not exist.");
        }

        File.Copy(typeDump, Path.Combine(typeInput, Path.GetFileName(typeDump)), overwrite: true);
        SetupLog.Info($"Using type dump {Path.GetFileName(typeDump)}.");

        // The message definitions, read straight out of the game's Root.wad.
        using var stream = File.OpenRead(rootWadPath);
        var archive = ArchiveParser.Parse(stream)
            ?? throw new SetupException($"{rootWadPath} could not be read. Make sure the game is fully installed and patched.");

        var count = 0;
        foreach (var name in archive.Files.Keys) {
            var fileName = Path.GetFileName(name.Replace('\\', '/'));
            if (!fileName.Contains("Messages", StringComparison.OrdinalIgnoreCase)
                || !fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var data = archive.OpenFile(name)
                ?? throw new SetupException($"{name} could not be read from Root.wad.");
            File.WriteAllBytes(Path.Combine(messageInput, fileName), data.ToArray());
            SetupLog.Detail($"Extracted {name}");
            count++;
        }

        if (count == 0) {
            throw new SetupException("No message files were found in Root.wad. Make sure the game is fully installed and patched.");
        }

        SetupLog.Info($"Read {count} message files from your game.");
    }

    // ---------------------------------------------------------------------
    // 5. Build
    // ---------------------------------------------------------------------

    /// <summary>
    /// Builds the server into the server folder, keeping the configuration, logs and database already there.
    /// </summary>
    internal async Task BuildServerAsync(string dotnet) {
        var project = Path.Combine(SourceFolder, "src", "Imlight.Director", "Imlight.Director.csproj");
        var staging = Path.Combine(_options.InstallFolder, "Build");
        if (Directory.Exists(staging)) {
            Directory.Delete(staging, recursive: true);
        }

        SetupLog.Info("Building the server. The first build downloads its packages and can take several minutes.");
        var exitCode = await ProcessRunner.RunAsync(dotnet,
            ["publish", project, "-c", "Release", "-r", RuntimeIdentifier(), "--self-contained", "true", "-o", staging, "-nologo"],
            showProgress: true);
        if (exitCode != 0) {
            throw new SetupException("The server could not be built. The errors are in the setup log.");
        }

        var existingConfig = File.Exists(ConfigFile);
        CopyFolder(staging, ServerFolder, keepIfPresent: ["Config/Imlight.ini"]);

        // A kept configuration gets the settings this version added, so they show up in the app.
        var defaults = Path.Combine(staging, "Config", "Imlight.ini");
        if (existingConfig && File.Exists(defaults)) {
            var lines = File.ReadAllLines(ConfigFile).ToList();
            var added = IniFile.AddMissing(lines, File.ReadAllLines(defaults));
            var keep = PlayerSettings.Changed(Path.GetDirectoryName(ConfigFile)!);
            var refreshed = Edition.IS_ROGUELIKE ? IniFile.CopyRunDesign(lines, File.ReadAllLines(defaults), keep) : 0;
            if (added > 0 || refreshed > 0) {
                File.WriteAllLines(ConfigFile, lines);
                SetupLog.Detail($"Added {added} new settings to Config\\Imlight.ini and refreshed {refreshed} run settings.");
            }
        }

        // The release's own values, so the launcher's Settings page can put a setting back to them.
        if (File.Exists(defaults)) {
            File.Copy(defaults, PlayerSettings.DefaultsFile(Path.GetDirectoryName(ConfigFile)!), overwrite: true);
        }

        Directory.Delete(staging, recursive: true);
    }

    private static string RuntimeIdentifier() {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
            : "linux";
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

        return $"{os}-{arch}";
    }

    private static void CopyFolder(string from, string to, string[] keepIfPresent) {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)) {
            var relative = Path.GetRelativePath(from, file).Replace('\\', '/');
            var target = Path.Combine(to, relative);
            if (File.Exists(target) && keepIfPresent.Contains(relative, StringComparer.OrdinalIgnoreCase)) {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    // ---------------------------------------------------------------------
    // 6. Configuration
    // ---------------------------------------------------------------------

    /// <summary>
    /// Asks for the admin account on a first install. An existing configuration is left alone.
    /// </summary>
    internal void Configure(bool isNewConfig) {
        if (!isNewConfig) {
            SetupLog.Info("Kept your existing settings in Config\\Imlight.ini.");

            return;
        }

        var username = _options.Unattended ? "admin" : Prompt.Ask("Admin account username", "admin");
        var password = _options.AdminPassword
            ?? (_options.Unattended ? throw new SetupException("Pass --admin-password when running setup unattended.")
                                    : Prompt.AskSecret("Admin account password"));
        var lines = File.ReadAllLines(ConfigFile).ToList();
        IniFile.Set(lines, "Database", "AdminAccountUsername", username);
        IniFile.Set(lines, "Database", "AdminAccountPassword", password);
        // Imlight.exe serves the game files from the player's own Wizard101 at this address when the server starts.
        IniFile.Set(lines, "Patch Server", "PatchServerInternalUrl", "http://127.0.0.1:12369");
        if (Edition.IS_ROGUELIKE) {
            // A roguelike server on the player's own PC: runs on, and any new name at the login screen is an account.
            IniFile.Set(lines, "Roguelike", "Enabled", "true");
            IniFile.Set(lines, "Login Server", "AutoCreateAccounts", "true");
        }

        File.WriteAllLines(ConfigFile, lines);
        SetupLog.Info("Saved your settings to Config\\Imlight.ini.");
    }

    // ---------------------------------------------------------------------
    // 7. Shortcuts
    // ---------------------------------------------------------------------

    /// <summary>
    /// Installs Imlight.exe (this program, which starts, plays, updates and uninstalls), adds its desktop
    /// shortcut, and lists Imlight under Settings, Apps.
    /// </summary>
    internal async Task CreateShortcutsAsync() {
        var exe = Path.Combine(_options.InstallFolder, InstalledCommands.INSTALLED_EXE);
        InstallSelf(exe);

        // The launcher used to be installed as SpiralRoguelike.exe; the shortcut now points at SpiralGauntlet.exe.
        if (Edition.IS_ROGUELIKE) {
            foreach (var oldName in new[] { "SpiralRoguelike.exe", "SpiralRoguelike.exe.old" }) {
                try {
                    File.Delete(Path.Combine(_options.InstallFolder, oldName));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    // Still running (the launcher that started this update); removed next time.
                }
            }
        }

        // Left behind by the first version of setup.
        foreach (var old in new[] { "Start Imlight Server.cmd", "Update Imlight.exe" }) {
            File.Delete(Path.Combine(_options.InstallFolder, old));
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            return;
        }

        InstalledCommands.RegisterUninstaller(_options.InstallFolder, SelfUpdate.CurrentVersion.ToString());

        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), InstalledCommands.SHORTCUT_FOLDER);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Directory.CreateDirectory(Path.Combine(ServerFolder, "logs"));

        // The app holds everything, so its desktop shortcut is the only one. Earlier versions added a
        // Start menu folder and more shortcuts; they are removed.
        // "Spiral Roguelike.lnk" is the launcher's shortcut from before it was renamed Spiral Gauntlet.
        foreach (var old in new[] { InstalledCommands.PANEL_SHORTCUT, InstalledCommands.START_SHORTCUT, InstalledCommands.PLAY_SHORTCUT, "Spiral Roguelike.lnk" }) {
            File.Delete(Path.Combine(desktop, old));
        }

        try {
            if (Directory.Exists(startMenu)) {
                Directory.Delete(startMenu, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            SetupLog.Detail($"The old Start menu folder could not be removed: {ex.Message}");
        }

        var shortcuts = new List<(string Path, string Target, string Arguments)> {
            (Path.Combine(desktop, InstalledCommands.APP_SHORTCUT), exe, ""),
        };

        // The shortcut names its icon file outright: Windows keeps showing a cached icon for an exe whose icon changed.
        var icon = WriteIcon();

        // Windows has no .NET API for shortcuts; the shell's scripting object makes them.
        var script = string.Join("\n", shortcuts.Select(s =>
            $"$s = $shell.CreateShortcut('{Escape(s.Path)}'); $s.TargetPath = '{Escape(s.Target)}'; " +
            $"$s.Arguments = '{Escape(s.Arguments)}'; $s.WorkingDirectory = '{Escape(_options.InstallFolder)}'; " +
            // The app opens its own window, so its console starts minimized.
            (s.Target == exe && s.Arguments.Length == 0 ? "$s.WindowStyle = 7; " : "") +
            (icon is not null ? $"$s.IconLocation = '{Escape(icon)},0'; " : "") + "$s.Save()"));
        var exitCode = await ProcessRunner.RunAsync("powershell",
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", "$shell = New-Object -ComObject WScript.Shell\n" + script]);
        if (exitCode != 0) {
            SetupLog.Info($"The desktop shortcut could not be created. Open \"{exe}\" instead.");
        }

        // Ask Windows to redraw icons it has cached, so the new one shows straight away.
        if (icon is not null) {
            try {
                await ProcessRunner.RunAsync("ie4uinit.exe", ["-show"]);
            }
            catch (Exception ex) {
                SetupLog.Detail($"The icon cache was not refreshed: {ex.Message}");
            }
        }

        static string Escape(string value) => value.Replace("'", "''");
    }

    /// <summary>
    /// Writes the launcher's icon into the install folder (named after the program, so a new name is a new file and
    /// Windows shows it fresh) and returns its path, or null for builds without one.
    /// </summary>
    private string? WriteIcon() {
        try {
            using var source = typeof(SetupSteps).Assembly.GetManifestResourceStream("launcher.ico");
            if (source is null || !Edition.IS_ROGUELIKE) {
                return null;
            }

            var path = Path.Combine(_options.InstallFolder, Edition.NAME.Replace(" ", "") + ".ico");
            using var target = File.Create(path);
            source.CopyTo(target);

            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            SetupLog.Detail($"The icon could not be written: {ex.Message}");

            return null;
        }
    }

    /// <summary>
    /// Copies this program into the install folder as Imlight.exe. A running copy is moved aside first,
    /// which Windows allows, and removed the next time.
    /// </summary>
    private static void InstallSelf(string target) {
        var self = Environment.ProcessPath;
        var aside = target + ".old";
        try {
            File.Delete(aside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Still running from an earlier update; removed next time.
        }

        if (self is null || Path.GetFullPath(self).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        try {
            File.Copy(self, target, overwrite: true);
        }
        catch (IOException) when (File.Exists(target)) {
            // The installed copy is running (the app or launcher that started this update): Windows lets a running
            // program be renamed, so move it aside. An older one still running keeps the .old name, so use another.
            if (File.Exists(aside)) {
                aside = $"{target}.{DateTime.Now:yyyyMMddHHmmss}.old";
            }

            File.Move(target, aside);
            File.Copy(self, target);
        }
    }

    // ---------------------------------------------------------------------
    // Downloads
    // ---------------------------------------------------------------------

    private async Task DownloadFileAsync(string url, string path) {
        SetupLog.Detail($"GET {url}");
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode) {
            throw new SetupException($"Could not download {url} ({(int) response.StatusCode} {response.ReasonPhrase}). Check your internet connection and try again.");
        }

        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = File.Create(path);
        await source.CopyToAsync(target);
    }

    private async Task<JsonElement> GetJsonAsync(string url) {
        SetupLog.Detail($"GET {url}");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode) {
            throw new SetupException($"GitHub answered {(int) response.StatusCode} {response.ReasonPhrase} for {url}. Check your internet connection and try again later.");
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.Clone();
    }

    public void Dispose()
        => _http.Dispose();

}
