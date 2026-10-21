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
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Imlight.Setup;

/// <summary>
/// The roguelike launcher (SpiralRoguelike.exe): one playful window that installs the roguelike server on this PC,
/// keeps it updated, and plays Wizard101 on it. Unlike the manager it opens before anything is installed.
/// </summary>
internal sealed class LauncherApp : Application {

    private static ServerManager? s_manager;

    internal static int Run(string installFolder) {
        Console.Title = Edition.NAME;
        s_manager = new ServerManager(installFolder);
        var exitCode = 0;

        var ui = new Thread(() => {
            try {
                AppBuilder.Configure<LauncherApp>().UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime([]);
            }
            catch (Exception ex) {
                Console.WriteLine($"The {Edition.NAME} window could not be opened: {ex.Message}");
                exitCode = 1;
            }
        });
        if (OperatingSystem.IsWindows()) {
            ui.SetApartmentState(ApartmentState.STA);
        }

        ui.Start();
        ui.Join();

        // The server keeps running only while someone can see it is: closing the launcher puts it to sleep.
        s_manager.StopServer();

        return exitCode;
    }

    public override void Initialize() {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
        Name = Edition.NAME;

        // Purple switches, sliders and focus rings instead of the default blue.
        Resources["SystemAccentColor"] = Color.Parse("#9B6BFF");
        Resources["SystemAccentColorLight1"] = Color.Parse("#B08AFF");
        Resources["SystemAccentColorLight2"] = Color.Parse("#C6A9FF");
        Resources["SystemAccentColorLight3"] = Color.Parse("#DCC9FF");
        Resources["SystemAccentColorDark1"] = Color.Parse("#7F4FE6");
        Resources["SystemAccentColorDark2"] = Color.Parse("#6638C7");
        Resources["SystemAccentColorDark3"] = Color.Parse("#4D25A0");
    }

    public override void OnFrameworkInitializationCompleted() {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && s_manager is not null) {
            desktop.MainWindow = new LauncherWindow(s_manager);
            HideConsole();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void HideConsole() {
        if (OperatingSystem.IsWindows() && GetConsoleWindow() is var handle && handle != IntPtr.Zero) {
            ShowWindow(handle, 0);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr handle, int command);

}
