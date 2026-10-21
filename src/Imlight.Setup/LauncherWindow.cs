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
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Path = System.IO.Path;

namespace Imlight.Setup;

/// <summary>
/// The roguelike launcher's window: a night sky with a big Play button. The first time, the same button sets the
/// server up (the setup steps run in the background and show here); after that it wakes the server and opens
/// Wizard101 on it. It also updates, sets a password for a wizard's account, and flips a few run options.
/// </summary>
internal sealed partial class LauncherWindow : Window {

    // Colors.
    private static readonly Color SKY_TOP = Color.Parse("#1A0F3D");
    private static readonly Color SKY_MID = Color.Parse("#3A1A6B");
    private static readonly Color SKY_BOTTOM = Color.Parse("#16305A");
    private static readonly IBrush GOLD = new SolidColorBrush(Color.Parse("#FFD45C"));
    private static readonly IBrush TEXT = new SolidColorBrush(Color.Parse("#F4ECFF"));
    private static readonly IBrush SOFT = new SolidColorBrush(Color.Parse("#CDBDF5"));
    private static readonly IBrush FAINT = new SolidColorBrush(Color.Parse("#9B8CC4"));
    private static readonly IBrush CARD = new SolidColorBrush(Color.Parse("#C7241450"));
    private static readonly IBrush PANEL = new SolidColorBrush(Color.Parse("#B3140C32"));
    private static readonly IBrush PILL = new SolidColorBrush(Color.Parse("#1FFFFFFF"));
    private static readonly IBrush PILL_HOVER = new SolidColorBrush(Color.Parse("#36FFFFFF"));
    private static readonly IBrush PILL_EDGE = new SolidColorBrush(Color.Parse("#30FFFFFF"));
    private static readonly IBrush ERROR = new SolidColorBrush(Color.Parse("#FF9C9C"));
    private static readonly Color ORB_READY = Color.Parse("#5EE08A");
    private static readonly Color ORB_WAKING = Color.Parse("#FFC24A");
    private static readonly Color ORB_ASLEEP = Color.Parse("#A99BD6");
    private static readonly Color ORB_PROBLEM = Color.Parse("#FF7A7A");

    private static readonly string ASSETS = $"avares://{typeof(LauncherWindow).Assembly.GetName().Name}/Assets";
    private static readonly FontFamily TITLE_FONT = new($"{ASSETS}/Fonts#Luckiest Guy");
    private static readonly FontFamily BODY_FONT = new($"{ASSETS}/Fonts#Fredoka");

    private const int SETUP_STEPS = 7;

    // Little lines shown while the server is being set up.
    private static readonly string[] SETUP_TIPS = [
        "Polishing the crystal balls…",
        "Teaching the pixies their lines…",
        "Shuffling the spell decks…",
        "Hiding jewels under the cobblestones…",
        "Waking the bosses (gently)…",
        "Brewing a fresh batch of mana…",
        "Counting the stars. There are a lot…",
        "Feeding the pets. They are always hungry…",
        "Rolling the dice for your first run…",
    ];

    private readonly ServerManager _manager;
    private readonly Random _random = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _skyTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _tipTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    // Sky.
    private readonly Canvas _sky = new() { IsHitTestVisible = false };
    private readonly List<Ellipse> _stars = [];
    private readonly List<(TextBlock Glyph, double Speed, double Sway)> _sparkles = [];
    private readonly Control _moon;
    private double _skyTime;

    // The card in the middle.
    private readonly Border _orb = new() { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _statusText = new() { FontSize = 17, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _playButton;
    private readonly TextBlock _playLabel = new() { Text = "PLAY", FontSize = 44, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hint = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 470 };
    private readonly StackPanel _setupBox = new() { Spacing = 8, IsVisible = false };
    private readonly TextBlock _setupStep = new() { FontSize = 15, TextAlignment = TextAlignment.Center };
    private readonly ProgressBar _setupProgress = new() { Minimum = 0, Maximum = SETUP_STEPS, Height = 14, MinHeight = 14, CornerRadius = new CornerRadius(7), Width = 420 };
    private readonly TextBlock _setupTip = new() { FontSize = 13, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _setupDetail = new() { FontSize = 11, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 460 };
    private readonly WrapPanel _pills = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
    private readonly Border _updatePill;
    private readonly Border _sleepPill;
    private readonly Border _logsPill;
    private readonly Border _setupLogPill;
    private readonly Border _gameFolderPill;
    private readonly Border _rulesPill;
    private readonly Border _faqPill;
    private readonly Border _creditsPill;
    private readonly Border _noticePill;

    // The FAQ and credits, shown over the card.
    private readonly Border _book;
    private readonly TextBlock _bookTitle = new() { FontSize = 30 };
    private readonly StackPanel _bookPages = new() { Spacing = 6 };
    private readonly ScrollViewer _bookScroll = new() { MaxHeight = 430 };
    private readonly StackPanel _bookAnswer = new() { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 8, 0) };
    private Border? _bookClose;
    private bool _bookMustAnswer;

    // The panels at the bottom.
    private readonly Border _wizardPanel;
    private readonly Border _magicPanel;
    private readonly TextBox _username = new() { Watermark = "Wizard login name", CornerRadius = new CornerRadius(12) };
    private readonly TextBox _password = new() { Watermark = "Secret word", PasswordChar = '•', CornerRadius = new CornerRadius(12) };
    private readonly List<(string Section, string Key, ToggleSwitch Switch)> _switches = [];
    private bool _loadingSwitches;

    private bool _busy;
    private bool _closeAnyway;
    private string? _setupLogPath;
    private string? _gameFolder;
    private Version? _newerVersion;

    internal LauncherWindow(ServerManager manager) {
        _manager = manager;
        Title = Edition.NAME;
        Width = 980;
        Height = 700;
        MinWidth = 980;
        MinHeight = 700;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = BODY_FONT;
        Foreground = TEXT;
        Background = new LinearGradientBrush {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.6, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(SKY_TOP, 0), new GradientStop(SKY_MID, 0.45), new GradientStop(SKY_BOTTOM, 1) },
        };
        try {
            Icon = new WindowIcon(AssetLoader.Open(new Uri($"{ASSETS}/launcher.ico")));
        }
        catch (Exception) {
            // Only the editions built with the icon have it.
        }

        _moon = Moon();
        _playButton = PlayButton();
        _updatePill = Pill("✦ Check for updates", CheckForUpdatesAsync);
        _sleepPill = Pill("☾ Put the server to sleep", () => {
            Hint(_manager.StopServer() == "The server was stopped." ? "The server is fast asleep. Press Play to wake it." : "The server was already asleep.");

            return Task.CompletedTask;
        });
        _logsPill = Pill("Server logs", () => {
            Directory.CreateDirectory(_manager.LogFolder);
            ServerManager.Open(_manager.LogFolder);

            return Task.CompletedTask;
        });
        _setupLogPill = Pill("Open the setup log", () => {
            if (_setupLogPath is not null && File.Exists(_setupLogPath)) {
                ServerManager.Open(_setupLogPath);
            }

            return Task.CompletedTask;
        });
        _gameFolderPill = Pill("Choose my Wizard101 folder", ChooseGameFolderAsync);
        _rulesPill = Pill("★ Rules", () => {
            OpenBook("How a run works", "rules.txt");

            return Task.CompletedTask;
        });
        _noticePill = Pill("AI notice", () => {
            OpenBook("Please read", "notice.txt");

            return Task.CompletedTask;
        });
        _faqPill = Pill("? FAQ", () => {
            OpenBook("Questions & answers", "faq.txt");

            return Task.CompletedTask;
        });
        _creditsPill = Pill("♥ Credits", () => {
            OpenBook("Credits", "credits.txt");

            return Task.CompletedTask;
        });
        _pills.Children.AddRange([_updatePill, _sleepPill, _logsPill, _setupLogPill, _gameFolderPill, _rulesPill, _faqPill, _creditsPill, _noticePill]);
        _book = Book();
        foreach (var pill in _pills.Children) {
            pill.Margin = new Thickness(5, 4);
        }

        _setupProgress.Foreground = new LinearGradientBrush {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#B08AFF"), 0), new GradientStop(Color.Parse("#FFD45C"), 1) },
        };
        _setupProgress.Background = new SolidColorBrush(Color.Parse("#33FFFFFF"));
        _setupStep.Foreground = TEXT;
        _setupTip.Foreground = SOFT;
        _setupDetail.Foreground = FAINT;
        _setupBox.Children.AddRange([_setupStep, WithCenter(_setupProgress), _setupTip, _setupDetail]);

        _wizardPanel = WizardPanel();
        _magicPanel = MagicPanel();

        Content = new Grid {
            Children = {
                _sky,
                BuildLayout(),
                _book,
            },
        };

        BuildSky();
        _skyTimer.Tick += (_, _) => AnimateSky();
        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
        _tipTimer.Tick += (_, _) => _setupTip.Text = SETUP_TIPS[_random.Next(SETUP_TIPS.Length)];

        Opened += async (_, _) => {
            _skyTimer.Start();
            ShowInstalledState();
            if (!NoticeSeen) {
                OpenBook("Please read", "notice.txt", mustAnswer: true);
            }

            if (_manager.IsInstalled) {
                await RefreshStatusAsync();
                _statusTimer.Start();
                await AutoUpdateAsync();
            }
        };
        Closing += (_, e) => {
            // Closing puts the server to sleep, which would end a game that is still open.
            if (!_closeAnyway && !_busy && IsGameRunning()) {
                e.Cancel = true;
                _closeAnyway = true;
                Hint("Wizard101 is still open, so the server is staying awake. Close the game first, or close this window again to stop everything.", isError: true);
            }
            else if (_busy) {
                e.Cancel = true;
                Hint("Hold on, the setup is still busy. It will be done soon.", isError: true);
            }
        };
        Closed += (_, _) => {
            _skyTimer.Stop();
            _statusTimer.Stop();
            _tipTimer.Stop();
        };
    }

    // ---------------------------------------------------------------------
    // Layout
    // ---------------------------------------------------------------------

    private Control BuildLayout() {
        var title = new TextBlock {
            Text = Edition.NAME,
            FontFamily = TITLE_FONT,
            FontSize = 64,
            Foreground = GOLD,
            HorizontalAlignment = HorizontalAlignment.Center,
            Effect = new DropShadowEffect { OffsetX = 0, OffsetY = 5, BlurRadius = 0, Color = Color.Parse("#8A3FD1"), Opacity = 1 },
        };
        var tagline = new TextBlock {
            Text = "✦  Every run a new adventure: spells, pets and bosses reshuffled  ✦",
            FontSize = 18,
            Foreground = SOFT,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };

        _statusText.Foreground = TEXT;
        _hint.Foreground = SOFT;
        var status = new Border {
            Background = new SolidColorBrush(Color.Parse("#14FFFFFF")),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(14, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _orb, _statusText } },
        };

        var card = new Border {
            Background = CARD,
            BorderBrush = new SolidColorBrush(Color.Parse("#59FFD45C")),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(26),
            Padding = new Thickness(28, 22),
            Width = 560,
            HorizontalAlignment = HorizontalAlignment.Center,
            BoxShadow = BoxShadows.Parse("0 12 40 0 #59000000"),
            Child = new StackPanel {
                Spacing = 14,
                Children = { status, _playButton, _setupBox, _hint, _pills },
            },
        };

        var version = new TextBlock {
            Text = $"v{SelfUpdate.CurrentVersion}  ·  plays on your own Wizard101, never the live game",
            FontSize = 11,
            Foreground = FAINT,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var bottom = new Grid {
            ColumnDefinitions = new ColumnDefinitions("*,24,*"),
            Margin = new Thickness(24, 0),
        };
        Grid.SetColumn(_wizardPanel, 0);
        Grid.SetColumn(_magicPanel, 2);
        bottom.Children.Add(_wizardPanel);
        bottom.Children.Add(_magicPanel);

        var root = new Grid {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"),
            Margin = new Thickness(0, 30, 0, 8),
        };
        Place(root, title, 0);
        Place(root, tagline, 1);
        Place(root, WithCenter(card), 2);
        Place(root, bottom, 3);
        Place(root, WithMargin(version, new Thickness(0, 10, 0, 0)), 4);

        return root;
    }

    private Border PlayButton() {
        _playLabel.FontFamily = TITLE_FONT;
        _playLabel.Foreground = new SolidColorBrush(Color.Parse("#4A1D00"));
        var button = new Border {
            Width = 320,
            Height = 86,
            CornerRadius = new CornerRadius(43),
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = new LinearGradientBrush {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#FFE58A"), 0), new GradientStop(Color.Parse("#FFB52E"), 1) },
            },
            BoxShadow = BoxShadows.Parse("0 6 0 0 #C06A00, 0 10 24 0 #73FFB42E"),
            Child = _playLabel,
            RenderTransform = TransformOperations.Parse("scale(1)"),
            Transitions = [
                new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(140) },
                new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(200) },
            ],
        };
        button.PointerEntered += (_, _) => {
            if (!_busy) {
                button.RenderTransform = TransformOperations.Parse("scale(1.05)");
            }
        };
        button.PointerExited += (_, _) => button.RenderTransform = TransformOperations.Parse("scale(1)");
        button.PointerPressed += (_, _) => button.RenderTransform = TransformOperations.Parse("scale(0.97)");
        button.PointerReleased += async (_, _) => {
            button.RenderTransform = TransformOperations.Parse("scale(1.05)");
            await OnPlayAsync();
        };

        return button;
    }

    private Border WizardPanel() {
        var save = Pill("Save", SavePasswordAsync, gold: true);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*,8,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetColumn(_username, 0);
        Grid.SetColumn(_password, 2);
        Grid.SetColumn(save, 4);
        row.Children.AddRange([_username, _password, save]);

        return InfoPanel("Your wizard's secret word", new StackPanel {
            Children = {
                Small("Log in with any name: a new account is made for it the first time. It lets any password in until you set one here " +
                      "(the server must be awake)."),
                row,
            },
        });
    }

    private Border MagicPanel() {
        var rows = new StackPanel { Spacing = 2 };
        foreach (var (section, key, name) in new[] {
                     ("Roguelike", "RandomMusic", "Random music"),
                     ("Roguelike", "RandomBattleMusic", "Random battle music"),
                     ("Die Delete", "Enabled", "Die = Delete (one life only!)"),
                 }) {
            var toggle = new ToggleSwitch { OnContent = null, OffContent = null, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Right };
            toggle.IsCheckedChanged += (_, _) => SaveSwitch(section, key, name, toggle.IsChecked == true);
            _switches.Add((section, key, toggle));
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var label = new TextBlock { Text = name, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Foreground = TEXT };
            Grid.SetColumn(toggle, 1);
            line.Children.AddRange([label, toggle]);
            rows.Children.Add(line);
        }

        return InfoPanel("Run magic", rows);
    }

    // ---------------------------------------------------------------------
    // Sky
    // ---------------------------------------------------------------------

    private static Control Moon() {
        var crescent = new CombinedGeometry(GeometryCombineMode.Exclude,
            new EllipseGeometry(new Rect(0, 0, 92, 92)),
            new EllipseGeometry(new Rect(26, -14, 92, 92)));

        return new Avalonia.Controls.Shapes.Path {
            Data = crescent,
            Fill = new SolidColorBrush(Color.Parse("#FFE9A3")),
            Effect = new DropShadowEffect { OffsetX = 0, OffsetY = 0, BlurRadius = 28, Color = Color.Parse("#FFD45C"), Opacity = 0.55 },
        };
    }

    private void BuildSky() {
        for (var i = 0; i < 110; i++) {
            var size = 0.8 + _random.NextDouble() * 2.4;
            var star = new Ellipse {
                Width = size,
                Height = size,
                Fill = Brushes.White,
                Opacity = 0.25 + _random.NextDouble() * 0.75,
                Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(900) }],
            };
            Canvas.SetLeft(star, _random.NextDouble() * Width);
            Canvas.SetTop(star, _random.NextDouble() * (Height - 120));
            _stars.Add(star);
            _sky.Children.Add(star);
        }

        // A few sparkles that float up past the card and wrap round.
        var colors = new[] { "#FFE9A3", "#D9C8FF", "#FFFFFF", "#B3E5FF" };
        for (var i = 0; i < 12; i++) {
            var glyph = new TextBlock {
                Text = "✦",
                FontSize = 8 + _random.NextDouble() * 12,
                Foreground = new SolidColorBrush(Color.Parse(colors[i % colors.Length])),
                Opacity = 0.35 + _random.NextDouble() * 0.4,
            };
            Canvas.SetLeft(glyph, _random.NextDouble() * Width);
            Canvas.SetTop(glyph, _random.NextDouble() * Height);
            _sparkles.Add((glyph, 0.25 + _random.NextDouble() * 0.6, _random.NextDouble() * Math.PI * 2));
            _sky.Children.Add(glyph);
        }

        Canvas.SetLeft(_moon, Width - 170);
        Canvas.SetTop(_moon, 34);
        _sky.Children.Add(_moon);
    }

    private void AnimateSky() {
        _skyTime += 0.05;

        // Twinkle: a few stars pick a new brightness and fade to it.
        for (var i = 0; i < 4; i++) {
            _stars[_random.Next(_stars.Count)].Opacity = 0.15 + _random.NextDouble() * 0.85;
        }

        _moon.RenderTransform = new TranslateTransform(0, Math.Sin(_skyTime * 0.8) * 6);

        foreach (var (glyph, speed, sway) in _sparkles) {
            var top = Canvas.GetTop(glyph) - speed;
            if (top < -20) {
                top = Height + 10;
                Canvas.SetLeft(glyph, _random.NextDouble() * Width);
            }

            Canvas.SetTop(glyph, top);
            glyph.RenderTransform = new TranslateTransform(Math.Sin(_skyTime + sway) * 6, 0);
        }
    }

    // ---------------------------------------------------------------------
    // State
    // ---------------------------------------------------------------------

    /// <summary>Shows Play (installed) or Begin (not yet), and which pills and panels apply.</summary>
    private void ShowInstalledState() {
        var installed = _manager.IsInstalled;
        _playLabel.Text = installed ? "PLAY" : "BEGIN!";
        _playLabel.FontSize = installed ? 44 : 40;
        _updatePill.IsVisible = installed;
        _sleepPill.IsVisible = installed;
        _logsPill.IsVisible = installed;
        _setupLogPill.IsVisible = _setupLogPath is not null;
        _gameFolderPill.IsVisible = !installed && _setupLogPath is not null;
        _wizardPanel.IsVisible = installed;
        _magicPanel.IsVisible = installed;
        if (!installed) {
            SetOrb(ORB_ASLEEP, "A new adventure awaits!");
            Hint("Press Begin to set everything up. It builds your own roguelike server from your Wizard101 files, " +
                 "which takes a few minutes the first time.");
        }
        else {
            LoadSwitches();
        }
    }

    private async Task RefreshStatusAsync() {
        if (_busy || !_manager.IsInstalled) {
            return;
        }

        var status = await Task.Run(_manager.Status);
        if (!status.ClientFound) {
            SetOrb(ORB_PROBLEM, "Wizard101 can't be found");
        }
        else if (status.Running && status.Ready) {
            SetOrb(ORB_READY, "Ready for adventure!");
        }
        else if (status.Running) {
            SetOrb(ORB_WAKING, "The server is waking up…");
        }
        else {
            SetOrb(ORB_ASLEEP, "The server is asleep. Press Play to wake it!");
        }

        _sleepPill.IsVisible = status.Running;
    }

    private void SetOrb(Color color, string text) {
        _orb.Background = new SolidColorBrush(color);
        _orb.BoxShadow = BoxShadows.Parse($"0 0 12 2 {color}");
        _statusText.Text = text;
    }

    private void Hint(string text, bool isError = false) {
        _hint.Text = text;
        _hint.Foreground = isError ? ERROR : SOFT;
    }

    private void SetBusy(bool busy) {
        _busy = busy;
        _playButton.Opacity = busy ? 0.55 : 1;
        _playButton.Cursor = new Cursor(busy ? StandardCursorType.Wait : StandardCursorType.Hand);
        foreach (var pill in _pills.Children) {
            pill.IsEnabled = !busy;
        }
    }

    // ---------------------------------------------------------------------
    // Play
    // ---------------------------------------------------------------------

    private async Task OnPlayAsync() {
        if (_busy) {
            return;
        }

        if (!_manager.IsInstalled) {
            await RunSetupAsync(update: false);

            return;
        }

        try {
            if (GameClient.ClientExe(_manager.InstallFolder) is null) {
                Hint("Wizard101 couldn't be found. Check for updates once to find it again, then press Play.", isError: true);

                return;
            }

            var running = _manager.Status().Ready;
            await _manager.PlayHereAsync();
            Hint(running
                ? "Off you go! Wizard101 is opening. Log in with any name you like."
                : "Waking the server… Wizard101 pops open by itself when it's ready. Log in with any name you like.");
            await RefreshStatusAsync();
        }
        catch (Exception ex) when (ex is PanelException or AccountException) {
            Hint(ex.Message, isError: true);
        }
        catch (Exception ex) {
            Hint($"Something went wrong: {ex.Message}", isError: true);
        }
    }

    private static bool IsGameRunning() {
        try {
            var running = Process.GetProcessesByName("WizardGraphicalClient");
            var any = running.Length > 0;
            foreach (var process in running) {
                process.Dispose();
            }

            return any;
        }
        catch (Exception) {
            return false;
        }
    }

    // ---------------------------------------------------------------------
    // Setup and updates
    // ---------------------------------------------------------------------

    private async Task AutoUpdateAsync() {
        if (!_manager.AutoUpdate || await SelfUpdate.NewerReleaseAsync() is not { } version) {
            return;
        }

        _newerVersion = version;
        if (_manager.Status().Running) {
            SetPillText(_updatePill, $"✦ Update to v{version}");
            Hint($"A new version (v{version}) is out! Press \"Update to v{version}\" when you're done playing.");

            return;
        }

        await RunSetupAsync(update: true);
    }

    private async Task CheckForUpdatesAsync() {
        if (_newerVersion is null) {
            Hint("Looking for new magic…");
            _newerVersion = await SelfUpdate.NewerReleaseAsync();
        }

        if (_newerVersion is null) {
            Hint($"You have the newest version (v{SelfUpdate.CurrentVersion}). Updating the server anyway brings in its newest changes…");
        }

        if (IsGameRunning()) {
            Hint("Close Wizard101 first: updating puts the server to sleep while it is rebuilt.", isError: true);

            return;
        }

        await RunSetupAsync(update: true);
    }

    /// <summary>
    /// Runs this program's own setup (download, read the game files, build, configure) in the background and shows
    /// its progress. When it finishes, the installed launcher takes over.
    /// </summary>
    private async Task RunSetupAsync(bool update) {
        var self = Environment.ProcessPath;
        if (self is null) {
            Hint("The launcher cannot find its own program file.", isError: true);

            return;
        }

        SetBusy(true);
        _statusTimer.Stop();
        _manager.StopServer();
        _setupLogPath = null;
        _setupBox.IsVisible = true;
        _playButton.IsVisible = false;
        _pills.IsVisible = false;
        _setupProgress.Value = 0;
        _setupStep.Text = update ? "Fetching the newest magic…" : "Setting up your adventure…";
        _setupTip.Text = SETUP_TIPS[_random.Next(SETUP_TIPS.Length)];
        _setupDetail.Text = "";
        SetOrb(ORB_WAKING, update ? "Updating…" : "Getting everything ready…");
        Hint("This takes a few minutes. You can leave it running.");
        _tipTimer.Start();

        var lines = new List<string>();
        int exitCode;
        try {
            var startInfo = new ProcessStartInfo(self) {
                WorkingDirectory = Path.GetDirectoryName(self)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) {
                startInfo.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
            }

            var args = new List<string> { "--update", "--yes", "--dir", _manager.InstallFolder, "--admin-password", RandomPassword() };
            if (_gameFolder is not null) {
                args.AddRange(["--game", _gameFolder]);
            }

            foreach (var arg in args) {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = startInfo };
            void OnLine(string? line) {
                if (line is null) {
                    return;
                }

                Dispatcher.UIThread.Post(() => {
                    lines.Add(line);
                    ShowSetupLine(line);
                });
            }

            process.OutputDataReceived += (_, e) => OnLine(e.Data);
            process.ErrorDataReceived += (_, e) => OnLine(e.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            exitCode = process.ExitCode;
        }
        catch (Exception ex) {
            lines.Add(ex.Message);
            exitCode = -1;
        }

        // Let the last lines arrive.
        await Task.Delay(300);
        _tipTimer.Stop();

        if (exitCode == 0 && _manager.IsInstalled) {
            _setupProgress.Value = SETUP_STEPS;
            _setupStep.Text = "All done! ✦";
            SetOrb(ORB_READY, "Ready for adventure!");
            Hint("Opening your launcher…");
            await Task.Delay(1200);
            RestartIntoInstalledCopy();

            return;
        }

        // Show why it stopped: the line just before "The full log is at …".
        var logLine = lines.FindLastIndex(l => l.StartsWith("The full log is at ", StringComparison.Ordinal));
        if (logLine >= 0) {
            _setupLogPath = LogPathOf(lines[logLine]);
        }

        var reason = lines.Take(logLine >= 0 ? logLine : lines.Count)
            .LastOrDefault(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("Press any key", StringComparison.Ordinal))
            ?? "The setup stopped without saying why.";

        SetBusy(false);
        _setupBox.IsVisible = false;
        _playButton.IsVisible = true;
        _pills.IsVisible = true;
        ShowInstalledState();
        SetOrb(ORB_PROBLEM, "A spell fizzled");
        Hint(reason + (reason.Contains("Wizard101", StringComparison.OrdinalIgnoreCase) && !_manager.IsInstalled
            ? " You can point to your Wizard101 folder below, then press Begin again."
            : " Press the button to try again."), isError: true);
        if (_manager.IsInstalled) {
            _statusTimer.Start();
        }
    }

    private void ShowSetupLine(string line) {
        if (StepLine().Match(line) is { Success: true } step) {
            var number = int.Parse(step.Groups["n"].Value);
            _setupProgress.Value = Math.Max(0, number - 1);
            _setupStep.Text = $"Step {number} of {SETUP_STEPS}: {Friendly(step.Groups["text"].Value)}";

            return;
        }

        if (!string.IsNullOrWhiteSpace(line)) {
            _setupDetail.Text = line.Trim();
        }
    }

    // The setup's own step names, said more playfully.
    private static string Friendly(string step) => step switch {
        "Finding Wizard101" => "Finding your Wizard101",
        "Checking for the .NET SDK" => "Gathering the spellbooks (.NET)",
        "Downloading the latest Imlight" => "Downloading the newest roguelike",
        "Reading game files from your Wizard101 install" => "Reading your game's secrets",
        "Building the server" => "Building your server (the slow bit!)",
        "Setting up the server" => "Sprinkling on the settings",
        "Adding shortcuts" => "Adding a desktop shortcut",
        _ => step,
    };

    private static string? LogPathOf(string line) {
        var path = line["The full log is at ".Length..];
        var end = path.IndexOf(". Share", StringComparison.Ordinal);

        return end > 0 ? path[..end] : path.TrimEnd('.');
    }

    private async Task ChooseGameFolderAsync() {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {
            Title = "Where is Wizard101 installed?",
            AllowMultiple = false,
        });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path) {
            return;
        }

        _gameFolder = path;
        Hint($"Got it: {path}. Press Begin to try again.");
    }

    /// <summary>
    /// Opens the launcher setup just installed (the desktop shortcut's copy) and closes this one.
    /// </summary>
    private void RestartIntoInstalledCopy() {
        var installed = Path.Combine(_manager.InstallFolder, InstalledCommands.INSTALLED_EXE);
        try {
            if (File.Exists(installed)) {
                Process.Start(new ProcessStartInfo(installed) { WorkingDirectory = _manager.InstallFolder, UseShellExecute = true })?.Dispose();
                _closeAnyway = true;
                _busy = false;
                Close();

                return;
            }
        }
        catch (Exception ex) {
            Hint($"Everything is set up, but the new launcher did not open ({ex.Message}). Open \"{Edition.NAME}\" from your desktop.", isError: true);
        }

        SetBusy(false);
        _setupBox.IsVisible = false;
        _playButton.IsVisible = true;
        _pills.IsVisible = true;
        ShowInstalledState();
        _statusTimer.Start();
    }

    private static string RandomPassword() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));

    [GeneratedRegex(@"^\[(?<n>\d+)/\d+\]\s+(?<text>.+)$")]
    private static partial Regex StepLine();

    // ---------------------------------------------------------------------
    // Accounts and run options
    // ---------------------------------------------------------------------

    private async Task SavePasswordAsync() {
        var username = _username.Text?.Trim() ?? "";
        var password = _password.Text ?? "";
        if (username.Length == 0 || password.Length == 0) {
            Hint("Type your wizard's login name and a secret word first.", isError: true);

            return;
        }

        if (!_manager.Status().Running) {
            Hint("Wake the server first (press Play), then save your secret word.", isError: true);

            return;
        }

        try {
            using var accounts = _manager.Accounts();
            await accounts.SetPasswordAsync(username, password);
            _password.Text = "";
            Hint($"Saved! {username} now needs that secret word to log in.");
        }
        catch (Exception ex) when (ex is PanelException or AccountException) {
            Hint(ex.Message.Contains("No account", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                ? $"There's no wizard account named {username} yet. Log in with it once in the game, then save."
                : ex.Message, isError: true);
        }
        catch (Exception ex) {
            Hint($"The secret word couldn't be saved: {ex.Message}", isError: true);
        }
    }

    private void LoadSwitches() {
        _loadingSwitches = true;
        foreach (var (section, key, toggle) in _switches) {
            toggle.IsVisible = _manager.HasSetting(section, key);
            toggle.IsChecked = _manager.GetSwitch(section, key);
        }

        _loadingSwitches = false;
    }

    private void SaveSwitch(string section, string key, string name, bool on) {
        if (_loadingSwitches) {
            return;
        }

        try {
            var running = _manager.SaveSettings([new Setting(section, key, on ? "true" : "false", "")]);
            Hint(running
                ? $"{name} is now {(on ? "on" : "off")}. It takes effect the next time the server wakes up."
                : $"{name} is now {(on ? "on" : "off")}.");
        }
        catch (Exception ex) {
            Hint(ex.Message, isError: true);
        }
    }

    // ---------------------------------------------------------------------
    // Building blocks
    // ---------------------------------------------------------------------

    private Border Pill(string text, Func<Task> action, bool gold = false) {
        var label = new TextBlock {
            Text = text,
            FontSize = 14,
            Foreground = gold ? new SolidColorBrush(Color.Parse("#4A1D00")) : TEXT,
            FontWeight = gold ? FontWeight.Bold : FontWeight.Medium,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var normal = gold ? new SolidColorBrush(Color.Parse("#FFC847")) : PILL;
        var hover = gold ? new SolidColorBrush(Color.Parse("#FFD978")) : PILL_HOVER;
        var pill = new Border {
            Background = normal,
            BorderBrush = gold ? Brushes.Transparent : PILL_EDGE,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14, 7),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = label,
        };
        pill.PointerEntered += (_, _) => pill.Background = hover;
        pill.PointerExited += (_, _) => pill.Background = normal;
        pill.PointerReleased += async (_, _) => {
            if (!pill.IsEnabled || _busy) {
                return;
            }

            try {
                await action();
            }
            catch (Exception ex) {
                Hint(ex.Message, isError: true);
            }
        };

        return pill;
    }

    private static void SetPillText(Border pill, string text) {
        if (pill.Child is TextBlock label) {
            label.Text = text;
        }
    }

    private static Border InfoPanel(string heading, Control content) => new() {
        Background = PANEL,
        BorderBrush = new SolidColorBrush(Color.Parse("#40B496FF")),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(18),
        Padding = new Thickness(16, 12),
        VerticalAlignment = VerticalAlignment.Bottom,
        Child = new StackPanel {
            Spacing = 4,
            Children = {
                new TextBlock { Text = heading, FontSize = 16, FontWeight = FontWeight.Bold, Foreground = GOLD },
                content,
            },
        },
    };

    // ---------------------------------------------------------------------
    // FAQ and credits
    // ---------------------------------------------------------------------

    /// <summary>The panel the FAQ and credits open in: a scrolling page over the card, closed by its button or a click outside.</summary>
    private Border Book() {
        _bookTitle.FontFamily = TITLE_FONT;
        _bookTitle.Foreground = GOLD;
        _bookTitle.Effect = new DropShadowEffect { OffsetX = 0, OffsetY = 3, BlurRadius = 0, Color = Color.Parse("#8A3FD1"), Opacity = 1 };
        _bookScroll.Content = _bookPages;
        var close = Pill("Close", () => {
            CloseBook();

            return Task.CompletedTask;
        }, gold: true);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        _bookClose = close;
        var understood = Pill("I understand, let's play", () => {
            NoticeSeen = true;
            CloseBook();

            return Task.CompletedTask;
        }, gold: true);
        var leave = Pill("Close the launcher", () => {
            _closeAnyway = true;
            Close();

            return Task.CompletedTask;
        });
        _bookAnswer.Children.AddRange([leave, understood]);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 10) };
        Grid.SetColumn(close, 1);
        header.Children.AddRange([_bookTitle, close]);

        var page = new Border {
            Width = 680,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse("#F21A0F3D")),
            BorderBrush = new SolidColorBrush(Color.Parse("#80FFD45C")),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(26),
            Padding = new Thickness(28, 20, 20, 22),
            BoxShadow = BoxShadows.Parse("0 16 50 0 #80000000"),
            Child = new StackPanel { Children = { header, _bookScroll, _bookAnswer } },
        };
        // Clicks on the page stay on the page; a click on the dimmed sky around it closes it.
        page.PointerPressed += (_, e) => e.Handled = true;

        var shade = new Border {
            Background = new SolidColorBrush(Color.Parse("#990A0620")),
            IsVisible = false,
            Child = page,
        };
        shade.PointerPressed += (_, _) => {
            if (!_bookMustAnswer) {
                CloseBook();
            }
        };

        return shade;
    }

    /// <summary>
    /// Opens a page. With <paramref name="mustAnswer"/> (the AI notice the first time) it stays until the player picks
    /// "I understand" or "Close the launcher".
    /// </summary>
    private void OpenBook(string title, string file, bool mustAnswer = false) {
        _bookMustAnswer = mustAnswer;
        _bookAnswer.IsVisible = mustAnswer;
        if (_bookClose is not null) {
            _bookClose.IsVisible = !mustAnswer;
        }

        _bookTitle.Text = title;
        _bookPages.Children.Clear();
        foreach (var block in ReadBook(file)) {
            _bookPages.Children.Add(block);
        }

        _bookScroll.Offset = new Vector(0, 0);
        _book.IsVisible = true;
    }

    private void CloseBook() {
        _bookMustAnswer = false;
        _book.IsVisible = false;
    }

    // Whether this PC has answered the AI notice; kept next to the install, so it shows once.
    private static string NoticeFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Edition.INSTALL_FOLDER_NAME, "ai-notice-seen.txt");

    private static bool NoticeSeen {
        get {
            try {
                return File.Exists(NoticeFile);
            }
            catch (Exception) {
                return false;
            }
        }
        set {
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(NoticeFile)!);
                File.WriteAllText(NoticeFile, $"The AI notice was read on {DateTime.Now:yyyy-MM-dd HH:mm}.");
            }
            catch (Exception) {
                // Shown again next time; nothing else depends on it.
            }
        }
    }

    /// <summary>
    /// Turns faq.txt or credits.txt into text: "# " headings, "Q: " lines in gold, paragraphs (lines next to each other
    /// join up), and ";" comments left out.
    /// </summary>
    private static List<Control> ReadBook(string file) {
        string text;
        try {
            using var stream = AssetLoader.Open(new Uri($"{ASSETS}/{file}"));
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (Exception) {
            return [Small($"{file} is missing from this build.")];
        }

        var blocks = new List<Control>();
        var paragraph = new List<string>();
        void EndParagraph() {
            if (paragraph.Count > 0) {
                blocks.Add(new TextBlock {
                    Text = string.Join(" ", paragraph),
                    FontSize = 14,
                    Foreground = TEXT,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 20,
                    Margin = new Thickness(0, 0, 8, 6),
                });
                paragraph.Clear();
            }
        }

        foreach (var raw in text.Split('\n')) {
            var line = raw.TrimEnd('\r').Trim();
            if (line.StartsWith(';')) {
                continue;
            }

            if (line.Length == 0) {
                EndParagraph();

                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal)) {
                EndParagraph();
                blocks.Add(new TextBlock {
                    Text = "✦ " + line[2..].Trim(),
                    FontSize = 19,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#B9A2FF")),
                    Margin = new Thickness(0, blocks.Count == 0 ? 0 : 14, 0, 2),
                });

                continue;
            }

            if (line.StartsWith("Q:", StringComparison.Ordinal)) {
                EndParagraph();
                blocks.Add(new TextBlock {
                    Text = line[2..].Trim(),
                    FontSize = 15,
                    FontWeight = FontWeight.Bold,
                    Foreground = GOLD,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 8, 0),
                });

                continue;
            }

            paragraph.Add(line);
        }

        EndParagraph();

        return blocks;
    }

    private static TextBlock Small(string text) => new() { Text = text, FontSize = 13, Foreground = SOFT, TextWrapping = TextWrapping.Wrap };

    private static void Place(Grid grid, Control control, int row) {
        Grid.SetRow(control, row);
        grid.Children.Add(control);
    }

    private static T WithCenter<T>(T control) where T : Control {
        control.HorizontalAlignment = HorizontalAlignment.Center;
        control.VerticalAlignment = VerticalAlignment.Center;

        return control;
    }

    private static T WithMargin<T>(T control, Thickness margin) where T : Control {
        control.Margin = margin;

        return control;
    }

}
