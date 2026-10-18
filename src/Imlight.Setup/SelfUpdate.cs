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
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace Imlight.Setup;

/// <summary>
/// Before updating, fetches a newer ImlightSetup.exe from the releases of the repository it came from,
/// so fixes to setup itself reach existing installs.
/// </summary>
internal static class SelfUpdate {

    /// <summary>Where ImlightSetup.exe is released.</summary>
    internal const string RELEASE_REPOSITORY = Edition.RELEASE_REPOSITORY;

    private const string ASSET = Edition.RELEASE_ASSET;

    internal static Version CurrentVersion { get; } = ParseVersion(typeof(SelfUpdate).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion) ?? new Version(0, 0, 0);

    /// <summary>
    /// Returns true when a newer setup was started and this one should exit.
    /// </summary>
    internal static async Task<bool> TryRunNewerAsync(string[] args) {
        // 0.0.0 is a local build; never replace it.
        if (CurrentVersion == new Version(0, 0, 0) || !OperatingSystem.IsWindows()) {
            return false;
        }

        try {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ImlightSetup", CurrentVersion.ToString()));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var document = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{RELEASE_REPOSITORY}/releases/latest"));
            var release = document.RootElement;
            var version = ParseVersion(release.GetProperty("tag_name").GetString());
            var asset = release.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => string.Equals(a.GetProperty("name").GetString(), ASSET, StringComparison.OrdinalIgnoreCase));
            if (version is null || version <= CurrentVersion || asset.ValueKind != JsonValueKind.Object) {
                return false;
            }

            Console.WriteLine($"Getting the newer Imlight Setup {version}..");
            var path = Path.Combine(Path.GetTempPath(), $"{Path.GetFileNameWithoutExtension(ASSET)}-{version}.exe");
            var bytes = await http.GetByteArrayAsync(asset.GetProperty("browser_download_url").GetString());
            if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } sha
                && sha.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                && !Convert.ToHexString(SHA256.HashData(bytes)).Equals(sha["sha256:".Length..], StringComparison.OrdinalIgnoreCase)) {
                Console.WriteLine("The download did not match its checksum, so this version carries on.");

                return false;
            }

            await File.WriteAllBytesAsync(path, bytes);
            var startInfo = new ProcessStartInfo(path) { UseShellExecute = false };
            foreach (var arg in args) {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo);
            if (process is null) {
                return false;
            }

            // Stay open until the new setup finishes, so the window does not vanish.
            await process.WaitForExitAsync();

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or KeyNotFoundException or IOException or InvalidOperationException) {
            // Offline or GitHub is busy: this version can still do the update.
            return false;
        }
    }

    /// <summary>
    /// The newest released version if it is newer than this one, else null (also when offline or on a local build).
    /// </summary>
    internal static async Task<Version?> NewerReleaseAsync() {
        if (CurrentVersion == new Version(0, 0, 0)) {
            return null;
        }

        try {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ImlightSetup", CurrentVersion.ToString()));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var document = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{RELEASE_REPOSITORY}/releases/latest"));
            var release = document.RootElement;
            var version = ParseVersion(release.GetProperty("tag_name").GetString());
            var hasAsset = release.GetProperty("assets").EnumerateArray()
                .Any(a => string.Equals(a.GetProperty("name").GetString(), ASSET, StringComparison.OrdinalIgnoreCase));

            return version is not null && version > CurrentVersion && hasAsset ? version : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or KeyNotFoundException or InvalidOperationException) {
            return null;
        }
    }

    internal static Version? ParseVersion(string? text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return null;
        }

        text = text.Trim().TrimStart('v', 'V');
        var end = text.IndexOfAny(['-', '+']);
        if (end >= 0) {
            text = text[..end];
        }

        if (!Version.TryParse(text.Contains('.') ? text : text + ".0", out var version)) {
            return null;
        }

        return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }

}
