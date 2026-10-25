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
/// The roguelike launcher's window (Spiral Gauntlet): a torchlit dungeon with the run's tiers mapped across it and a big
/// Begin Run button. The first time, the same button sets the
/// server up (the setup steps run in the background and show here); after that it wakes the server and opens
/// Wizard101 on it. It also updates, sets a password for a wizard's account, and flips a few run options.
/// </summary>
internal sealed partial class LauncherWindow : Window {

    // Colors: torchlit stone, old gold and ember red.
    private static readonly Color GOLD_COLOR = Color.Parse("#E9C46A");
    private static readonly IBrush GOLD = new SolidColorBrush(GOLD_COLOR);
    private static readonly IBrush GOLD_DIM = new SolidColorBrush(Color.Parse("#C99A3E"));
    private static readonly IBrush BRASS = new SolidColorBrush(Color.Parse("#6E5428"));
    private static readonly IBrush TEXT = new SolidColorBrush(Color.Parse("#EFE6D6"));
    private static readonly IBrush SOFT = new SolidColorBrush(Color.Parse("#B9A68A"));
    private static readonly IBrush FAINT = new SolidColorBrush(Color.Parse("#7A6A52"));
    private static readonly IBrush PANEL = new SolidColorBrush(Color.Parse("#E0150F0F"));
    private static readonly IBrush PILL = new SolidColorBrush(Color.Parse("#0AFFFFFF"));
    private static readonly IBrush PILL_HOVER = new SolidColorBrush(Color.Parse("#1FE9C46A"));
    private static readonly IBrush PILL_EDGE = BRASS;
    private static readonly IBrush ERROR = new SolidColorBrush(Color.Parse("#FF8A72"));
    private static readonly Color ORB_READY = Color.Parse("#7BD389");
    private static readonly Color ORB_WAKING = Color.Parse("#FFB04A");
    private static readonly Color ORB_ASLEEP = Color.Parse("#8C7E6A");
    private static readonly Color ORB_PROBLEM = Color.Parse("#FF6A4D");

    private static readonly string ASSETS = $"avares://{typeof(LauncherWindow).Assembly.GetName().Name}/Assets";
    private static readonly FontFamily TITLE_FONT = new($"{ASSETS}/Fonts#Cinzel Decorative");
    private static readonly FontFamily HEADING_FONT = new($"{ASSETS}/Fonts#Cinzel");
    private static readonly FontFamily BODY_FONT = new("fonts:Inter#Inter");

    private const int SETUP_STEPS = 7;

    // Little lines shown while the server is being set up.
    private static readonly string[] SETUP_TIPS = [
        "Lighting the torches…",
        "Shuffling the creatures' decks…",
        "Sharpening the bosses…",
        "Hiding jewels in the chests…",
        "Stocking the shops with random gear…",
        "Rolling the dice for your first run…",
        "Polishing the tiers…",
        "Feeding the pets. They are always hungry…",
        "Reading your game's old scrolls…",
    ];

    private readonly ServerManager _manager;
    private readonly Random _random = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _skyTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _tipTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    // Embers drifting up from the torches.
    private readonly Canvas _sky = new() { IsHitTestVisible = false };
    private readonly List<(Ellipse Ember, double Speed, double Sway)> _embers = [];
    private double _skyTime;

    // The card in the middle.
    private readonly Border _orb = new() { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _statusText = new() { FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _playButton;
    private readonly TextBlock _playLabel = new() { Text = "BEGIN RUN", FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, LetterSpacing = 4 };
    private readonly TextBlock _hint = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 490 };
    private readonly StackPanel _setupBox = new() { Spacing = 8, IsVisible = false };
    private readonly TextBlock _setupStep = new() { FontSize = 15, TextAlignment = TextAlignment.Center };
    private readonly ProgressBar _setupProgress = new() { Minimum = 0, Maximum = SETUP_STEPS, Height = 10, MinHeight = 10, CornerRadius = new CornerRadius(0), Width = 420 };
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
    private readonly Border _patchNotesPill;

    // The FAQ and credits, shown over the card.
    private readonly Border _book;
    private readonly TextBlock _bookTitle = new() { FontSize = 28 };
    private readonly StackPanel _bookPages = new() { Spacing = 6 };
    private readonly ScrollViewer _bookScroll = new() { MaxHeight = 430 };
    private readonly StackPanel _bookAnswer = new() { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 8, 0) };
    private Border? _bookClose;
    private bool _bookMustAnswer;
    private bool _updateAfterNotice;

    // The panels at the bottom.
    private readonly Border _wizardPanel;
    private readonly Border _magicPanel;
    private readonly TextBox _username = new() { Watermark = "Login name", CornerRadius = new CornerRadius(0) };
    private readonly TextBox _password = new() { Watermark = "Secret word", PasswordChar = '•', CornerRadius = new CornerRadius(0) };
    private readonly List<(string Section, string Key, ToggleSwitch Switch)> _switches = [];
    private bool _loadingSwitches;

    private readonly (List<string> Worlds, HashSet<int> Bosses) _tiers;
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
        // Torchlight on stone: warm in the middle, dark at the edges.
        Background = new RadialGradientBrush {
            Center = new RelativePoint(0.5, 0.55, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.55, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.65, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.6, RelativeUnit.Relative),
            GradientStops = {
                new GradientStop(Color.Parse("#3A2216"), 0),
                new GradientStop(Color.Parse("#1D1414"), 0.55),
                new GradientStop(Color.Parse("#0C090C"), 1),
            },
        };
        try {
            Icon = new WindowIcon(AssetLoader.Open(new Uri($"{ASSETS}/launcher.ico")));
        }
        catch (Exception) {
            // Only the editions built with the icon have it.
        }

        _playButton = PlayButton();
        _updatePill = Pill("Updates", CheckForUpdatesAsync);
        _sleepPill = Pill("Rest the server", () => {
            Hint(_manager.StopServer() == "The server was stopped." ? "The server rests. Begin a run to wake it." : "The server was already resting.");

            return Task.CompletedTask;
        });
        _logsPill = Pill("Logs", () => {
            Directory.CreateDirectory(_manager.LogFolder);
            ServerManager.Open(_manager.LogFolder);

            return Task.CompletedTask;
        });
        _setupLogPill = Pill("Setup log", () => {
            if (_setupLogPath is not null && File.Exists(_setupLogPath)) {
                ServerManager.Open(_setupLogPath);
            }

            return Task.CompletedTask;
        });
        _gameFolderPill = Pill("Find Wizard101", ChooseGameFolderAsync);
        _rulesPill = Pill("Rules", () => {
            OpenBook("How a run works", "rules.txt");

            return Task.CompletedTask;
        });
        _noticePill = Pill("AI Notice", () => {
            OpenBook("Please read", "notice.txt");

            return Task.CompletedTask;
        });
        _faqPill = Pill("FAQ", () => {
            OpenBook("Questions & answers", "faq.txt");

            return Task.CompletedTask;
        });
        _creditsPill = Pill("Credits", () => {
            OpenBook("Credits", "credits.txt");

            return Task.CompletedTask;
        });
        _patchNotesPill = Pill("Patch notes", () => {
            OpenBook("Patch notes", "patchnotes.txt");

            return Task.CompletedTask;
        });
        _settingsPill = Pill("Settings", () => {
            OpenSettings();

            return Task.CompletedTask;
        });
        _pills.Children.AddRange([_rulesPill, _faqPill, _patchNotesPill, _creditsPill, _noticePill, _settingsPill, _updatePill, _sleepPill, _logsPill, _setupLogPill, _gameFolderPill]);
        BuildSettingsActions();
        _book = Book();
        foreach (var pill in _pills.Children) {
            pill.Margin = new Thickness(4, 4);
        }

        _setupProgress.Foreground = new LinearGradientBrush {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#9B2A18"), 0), new GradientStop(Color.Parse("#E9C46A"), 1) },
        };
        _setupProgress.Background = new SolidColorBrush(Color.Parse("#2A1E18"));
        _setupStep.Foreground = TEXT;
        _setupTip.Foreground = SOFT;
        _setupDetail.Foreground = FAINT;
        _setupBox.Children.AddRange([_setupStep, WithCenter(_setupProgress), _setupTip, _setupDetail]);

        _wizardPanel = WizardPanel();
        _magicPanel = MagicPanel();
        _tiers = ReadTiers();

        Content = new Grid {
            Children = {
                _sky,
                Vignette(),
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

                // An update waits until the notice is answered, so it never starts behind it.
                if (NoticeSeen) {
                    await AutoUpdateAsync();
                }
                else {
                    _updateAfterNotice = true;
                }
            }
        };
        Closing += (_, e) => {
            // Closing puts the server to sleep, which would end a game that is still open.
            if (!_closeAnyway && !_busy && IsGameRunning()) {
                e.Cancel = true;
                _closeAnyway = true;
                Hint("Wizard101 is still open, so the server is staying awake. Close the game first, or close this window again to stop everything.", isError: true);
            }
            else if (_busy && !_closeAnyway) {
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
            Text = Edition.NAME.ToUpperInvariant(),
            FontFamily = TITLE_FONT,
            FontSize = 56,
            LetterSpacing = 3,
            Foreground = GOLD,
            HorizontalAlignment = HorizontalAlignment.Center,
            Effect = new DropShadowEffect { OffsetX = 0, OffsetY = 0, BlurRadius = 22, Color = GOLD_COLOR, Opacity = 0.35 },
        };
        var rule = new Border {
            Width = 420,
            Height = 1,
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = new LinearGradientBrush {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = {
                    new GradientStop(Colors.Transparent, 0), new GradientStop(Color.Parse("#C99A3E"), 0.5), new GradientStop(Colors.Transparent, 1),
                },
            },
        };
        var tagline = new TextBlock {
            Text = $"{CountWord(_tiers.Worlds.Count)} TIERS  ·  ONE LIFE  ·  EVERY RUN RESHUFFLED",
            FontFamily = HEADING_FONT,
            FontWeight = FontWeight.SemiBold,
            FontSize = 14,
            LetterSpacing = 4,
            Foreground = SOFT,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };

        _statusText.Foreground = TEXT;
        _hint.Foreground = SOFT;
        var status = new StackPanel {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { _orb, _statusText },
        };

        var card = new Border {
            Background = new LinearGradientBrush {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#EB261C1A"), 0), new GradientStop(Color.Parse("#EB161010"), 1) },
            },
            BorderBrush = BRASS,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(28, 20),
            Width = 580,
            BoxShadow = BoxShadows.Parse("0 18 40 0 #80000000"),
            Child = new StackPanel {
                Spacing = 12,
                Children = { status, _playButton, _setupBox, _hint, _pills },
            },
        };

        var version = new TextBlock {
            Text = $"v{SelfUpdate.CurrentVersion}  ·  {Edition.NAME} plays on your own Wizard101, never the live game",
            FontSize = 11,
            Foreground = FAINT,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var bottom = new Grid {
            ColumnDefinitions = new ColumnDefinitions("*,24,*"),
            Margin = new Thickness(36, 0),
        };
        Grid.SetColumn(_wizardPanel, 0);
        Grid.SetColumn(_magicPanel, 2);
        bottom.Children.Add(_wizardPanel);
        bottom.Children.Add(_magicPanel);

        var root = new Grid {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto,Auto"),
            Margin = new Thickness(0, 24, 0, 8),
        };
        Place(root, title, 0);
        Place(root, rule, 1);
        Place(root, tagline, 2);
        Place(root, WithMargin(TierMap(), new Thickness(0, 18, 0, 0)), 3);
        Place(root, WithCenter(Bracketed(card)), 4);
        Place(root, bottom, 5);
        Place(root, WithMargin(version, new Thickness(0, 10, 0, 0)), 6);

        return root;
    }

    /// <summary>Gold corner brackets on the card, like a framed plate.</summary>
    private static Control Bracketed(Control card) {
        Border Corner(HorizontalAlignment h, VerticalAlignment v) => new() {
            Width = 16,
            Height = 16,
            HorizontalAlignment = h,
            VerticalAlignment = v,
            Margin = new Thickness(-3),
            BorderBrush = GOLD,
            BorderThickness = new Thickness(
                h == HorizontalAlignment.Left ? 2 : 0, v == VerticalAlignment.Top ? 2 : 0,
                h == HorizontalAlignment.Right ? 2 : 0, v == VerticalAlignment.Bottom ? 2 : 0),
            IsHitTestVisible = false,
        };

        return new Grid {
            Children = {
                card,
                Corner(HorizontalAlignment.Left, VerticalAlignment.Top),
                Corner(HorizontalAlignment.Right, VerticalAlignment.Top),
                Corner(HorizontalAlignment.Left, VerticalAlignment.Bottom),
                Corner(HorizontalAlignment.Right, VerticalAlignment.Bottom),
            },
        };
    }

    /// <summary>Dark edges over the background, so the light falls in the middle.</summary>
    private static Control Vignette() => new Border {
        IsHitTestVisible = false,
        Background = new RadialGradientBrush {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Colors.Transparent, 0.55), new GradientStop(Color.Parse("#A6000000"), 1) },
        },
    };

    // ---------------------------------------------------------------------
    // The tier map
    // ---------------------------------------------------------------------

    private static readonly string[] ROMAN = ["I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X", "XI", "XII", "XIII", "XIV", "XV"];

    // The default tiers, for before the server is installed: arc 1's streets, then arc 2's boss rush.
    private static readonly string[] DEFAULT_WORLDS = [
        "Wizard City", "Krokotopia", "Marleybone", "MooShu", "Dragonspyre",
        "Celestia", "Grizzleheim", "Avalon", "Azteca", "Wintertusk",
    ];

    /// <summary>
    /// The run's tiers as a row of waypoints, named after each tier's world (read from the server's TierN lines),
    /// with the arcs' last bosses (ArcEndTiers, and the last tier) larger and lit. Boss-only tiers (arc 2's boss rush,
    /// "none" for the street) are shown too.
    /// </summary>
    private Control TierMap() {
        var (worlds, bosses) = _tiers;
        // Fewer tiers sit closer together, centred.
        var width = Math.Min(860, 60 + 150.0 * Math.Max(1, worlds.Count - 1));
        var canvas = new Canvas { Width = width, Height = 84, HorizontalAlignment = HorizontalAlignment.Center };
        var step = worlds.Count > 1 ? (width - 60) / (worlds.Count - 1) : 0;
        var line = new Border {
            Width = width - 60,
            Height = 2,
            Background = new LinearGradientBrush {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = {
                    new GradientStop(Color.Parse("#7A5A24"), 0), new GradientStop(Color.Parse("#C99A3E"), 0.5), new GradientStop(Color.Parse("#7A5A24"), 1),
                },
            },
        };
        Canvas.SetLeft(line, 30);
        Canvas.SetTop(line, 34);
        canvas.Children.Add(line);

        for (var i = 0; i < worlds.Count; i++) {
            var x = 30 + i * step;
            var boss = bosses.Contains(i + 1);
            var size = boss ? 24.0 : 18.0;
            var node = new Border {
                Width = size,
                Height = size,
                Background = new SolidColorBrush(Color.Parse(boss ? "#5A1D14" : "#1B1412")),
                BorderBrush = boss ? GOLD : GOLD_DIM,
                BorderThickness = new Thickness(2),
                RenderTransform = TransformOperations.Parse("rotate(45deg)"),
                BoxShadow = boss ? BoxShadows.Parse("0 0 14 2 #99E9783C") : default,
            };
            Canvas.SetLeft(node, x - size / 2);
            Canvas.SetTop(node, 35 - size / 2);
            canvas.Children.Add(node);

            var numeral = new TextBlock {
                Text = i < ROMAN.Length ? ROMAN[i] : (i + 1).ToString(),
                FontFamily = HEADING_FONT,
                FontWeight = FontWeight.Bold,
                FontSize = 11,
                Foreground = GOLD_DIM,
                Width = 40,
                TextAlignment = TextAlignment.Center,
            };
            Canvas.SetLeft(numeral, x - 20);
            Canvas.SetTop(numeral, 2);
            canvas.Children.Add(numeral);

            var labelWidth = Math.Min(96, Math.Max(60, step - 2));
            var label = new TextBlock {
                Text = worlds[i],
                FontSize = worlds.Count > 7 ? 10 : 11,
                Foreground = SOFT,
                Width = labelWidth,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Canvas.SetLeft(label, x - labelWidth / 2);
            Canvas.SetTop(label, 56);
            canvas.Children.Add(label);
        }

        return canvas;
    }

    private static string CountWord(int n) => n switch {
        1 => "ONE", 2 => "TWO", 3 => "THREE", 4 => "FOUR", 5 => "FIVE", 6 => "SIX", 7 => "SEVEN", 8 => "EIGHT", 9 => "NINE",
        10 => "TEN", 11 => "ELEVEN", 12 => "TWELVE", _ => n.ToString(),
    };

    private (List<string> Worlds, HashSet<int> Bosses) ReadTiers() {
        var worlds = new List<string>();
        var bosses = new HashSet<int>();
        try {
            if (_manager.IsInstalled) {
                for (var tier = 1; tier <= ROMAN.Length; tier++) {
                    var line = _manager.GetSetting("Roguelike", $"Tier{tier}");
                    if (string.IsNullOrWhiteSpace(line)) {
                        break;
                    }

                    var parts = line.Split(',', StringSplitOptions.TrimEntries);
                    var zone = parts.Length > 1 ? parts[1] : parts[0];
                    worlds.Add(WorldName(zone));
                }

                foreach (var end in (_manager.GetSetting("Roguelike", "ArcEndTiers") ?? "5").Split(',', StringSplitOptions.RemoveEmptyEntries)) {
                    if (int.TryParse(end.Trim(), out var n)) {
                        bosses.Add(n);
                    }
                }
            }
        }
        catch (Exception) {
            worlds.Clear();
        }

        if (worlds.Count == 0) {
            worlds.AddRange(DEFAULT_WORLDS);
        }

        bosses.RemoveWhere(b => b > worlds.Count);
        bosses.Add(worlds.Count);

        return (worlds, bosses);
    }

    /// <summary>The world a zone path is in, as players know it ("DragonSpire/DS_..." is Dragonspyre).</summary>
    private static string WorldName(string zone) {
        if (zone.Contains("GH_HFjord", StringComparison.OrdinalIgnoreCase) || zone.Contains("Wintertusk", StringComparison.OrdinalIgnoreCase)) {
            return "Wintertusk";
        }

        var folder = zone.Split('/')[0];

        return folder switch {
            "WizardCity" => "Wizard City",
            "DragonSpire" => "Dragonspyre",
            "MooShu" => "MooShu",
            "" => "?",
            _ => Regex.Replace(folder, "(?<=[a-z])(?=[A-Z])", " "),
        };
    }

    private Border PlayButton() {
        _playLabel.FontFamily = HEADING_FONT;
        _playLabel.FontWeight = FontWeight.Bold;
        _playLabel.Foreground = new SolidColorBrush(Color.Parse("#FFF3DC"));
        _playLabel.Effect = new DropShadowEffect { OffsetX = 0, OffsetY = 2, BlurRadius = 2, Color = Colors.Black, Opacity = 0.5 };
        var normal = new LinearGradientBrush {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#B2361F"), 0), new GradientStop(Color.Parse("#6E1610"), 1) },
        };
        var lit = new LinearGradientBrush {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#CC4426"), 0), new GradientStop(Color.Parse("#841C13"), 1) },
        };
        var button = new Border {
            Width = 340,
            Height = 66,
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = normal,
            BorderBrush = GOLD,
            BorderThickness = new Thickness(2),
            BoxShadow = BoxShadows.Parse("0 0 22 0 #73DC5A28, 0 4 0 0 #3A0C08"),
            Child = _playLabel,
            RenderTransform = TransformOperations.Parse("scale(1)"),
            Transitions = [
                new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(140) },
                new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(200) },
            ],
        };
        button.PointerEntered += (_, _) => {
            if (!_busy) {
                button.Background = lit;
                button.RenderTransform = TransformOperations.Parse("scale(1.03)");
            }
        };
        button.PointerExited += (_, _) => {
            button.Background = normal;
            button.RenderTransform = TransformOperations.Parse("scale(1)");
        };
        button.PointerPressed += (_, _) => button.RenderTransform = TransformOperations.Parse("scale(0.98)");
        button.PointerReleased += async (_, _) => {
            button.RenderTransform = TransformOperations.Parse("scale(1.03)");
            await OnPlayAsync();
        };

        return button;
    }

    private Border WizardPanel() {
        var save = Pill("Seal", SavePasswordAsync, gold: true);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*,8,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetColumn(_username, 0);
        Grid.SetColumn(_password, 2);
        Grid.SetColumn(save, 4);
        row.Children.AddRange([_username, _password, save]);

        return InfoPanel("Secret word", new StackPanel {
            Children = {
                Small("Log in with any name: an account is made the first time. It lets any password in until you seal one here " +
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
                     ("Die Delete", "Enabled", "Die = Delete (one life)"),
                 }) {
            var toggle = new ToggleSwitch { OnContent = null, OffContent = null, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Right };
            toggle.IsCheckedChanged += (_, _) => SaveSwitch(section, key, name, toggle.IsChecked == true);
            _switches.Add((section, key, toggle));
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var label = new TextBlock { Text = name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Foreground = TEXT };
            Grid.SetColumn(toggle, 1);
            line.Children.AddRange([label, toggle]);
            rows.Children.Add(line);
        }

        return InfoPanel("Run options", rows);
    }

    // ---------------------------------------------------------------------
    // Embers
    // ---------------------------------------------------------------------

    private void BuildSky() {
        for (var i = 0; i < 46; i++) {
            var size = 1.2 + _random.NextDouble() * 2.6;
            var ember = new Ellipse {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(Color.Parse(_random.Next(3) == 0 ? "#FFD27A" : "#FF9A3C")),
                Opacity = 0.25 + _random.NextDouble() * 0.7,
                Effect = new DropShadowEffect { OffsetX = 0, OffsetY = 0, BlurRadius = 6, Color = Color.Parse("#FF8C28"), Opacity = 0.8 },
                Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(500) }],
            };
            Canvas.SetLeft(ember, _random.NextDouble() * Width);
            Canvas.SetTop(ember, _random.NextDouble() * Height);
            _embers.Add((ember, 0.2 + _random.NextDouble() * 0.7, _random.NextDouble() * Math.PI * 2));
            _sky.Children.Add(ember);
        }
    }

    private void AnimateSky() {
        _skyTime += 0.05;

        // A few embers flicker each tick.
        for (var i = 0; i < 3; i++) {
            _embers[_random.Next(_embers.Count)].Ember.Opacity = 0.2 + _random.NextDouble() * 0.8;
        }

        foreach (var (ember, speed, sway) in _embers) {
            var top = Canvas.GetTop(ember) - speed;
            if (top < -10) {
                top = Height + 10;
                Canvas.SetLeft(ember, _random.NextDouble() * Width);
            }

            Canvas.SetTop(ember, top);
            ember.RenderTransform = new TranslateTransform(Math.Sin(_skyTime * 0.9 + sway) * 8, 0);
        }
    }

    // ---------------------------------------------------------------------
    // State
    // ---------------------------------------------------------------------

    /// <summary>Shows Play (installed) or Begin (not yet), and which pills and panels apply.</summary>
    private void ShowInstalledState() {
        var installed = _manager.IsInstalled;
        _playLabel.Text = installed ? "BEGIN RUN" : "FORGE THE GAUNTLET";
        _playLabel.FontSize = installed ? 26 : 20;
        _updatePill.IsVisible = installed;
        _settingsPill.IsVisible = installed;
        _sleepPill.IsVisible = installed;
        _logsPill.IsVisible = installed;
        _setupLogPill.IsVisible = _setupLogPath is not null;
        _gameFolderPill.IsVisible = !installed && _setupLogPath is not null;
        _wizardPanel.IsVisible = installed;
        _magicPanel.IsVisible = installed;
        if (!installed) {
            SetOrb(ORB_ASLEEP, "The gauntlet is not yet built");
            Hint("Press the button to set everything up. It builds your own server from your Wizard101 files, " +
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
            SetOrb(ORB_READY, "The gates are open");
        }
        else if (status.Running) {
            SetOrb(ORB_WAKING, "The torches are being lit…");
        }
        else {
            SetOrb(ORB_ASLEEP, "The gauntlet rests. Begin a run to wake it.");
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
            // Never run the update from the installed exe: the update replaces that very file. Run the newest release
            // from the temp folder (or, offline, a temp copy of this launcher).
            _setupDetail.Text = "Fetching the newest launcher…";
            var runner = await SelfUpdate.DownloadLatestAsync() ?? CopyToTemp(self);
            var startInfo = new ProcessStartInfo(runner) {
                WorkingDirectory = Path.GetDirectoryName(runner)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (Path.GetFileNameWithoutExtension(runner).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) {
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
            SetOrb(ORB_READY, "The gates are open");
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
        SetOrb(ORB_PROBLEM, "Something went wrong");
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

    /// <summary>A copy of this launcher in the temp folder to run the setup from, or the launcher itself if it cannot be copied.</summary>
    private static string CopyToTemp(string self) {
        if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) {
            return self;
        }

        try {
            var copy = Path.Combine(Path.GetTempPath(), $"{Path.GetFileNameWithoutExtension(Edition.RELEASE_ASSET)}-setup-{SelfUpdate.CurrentVersion}.exe");
            File.Copy(self, copy, overwrite: true);

            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return self;
        }
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

    private Border Pill(string text, Func<Task> action, bool gold = false, bool evenWhenBusy = false) {
        // Small engraved plates; "gold" ones (Seal, Close, I understand) are ember red with a gold edge.
        var label = new TextBlock {
            Text = text.ToUpperInvariant(),
            FontFamily = HEADING_FONT,
            FontSize = 12,
            LetterSpacing = 1,
            Foreground = gold ? new SolidColorBrush(Color.Parse("#FFF3DC")) : new SolidColorBrush(Color.Parse("#D9C9A8")),
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var normal = gold ? new SolidColorBrush(Color.Parse("#7A1A12")) : PILL;
        var hover = gold ? new SolidColorBrush(Color.Parse("#9A2618")) : PILL_HOVER;
        var pill = new Border {
            Background = normal,
            BorderBrush = gold ? GOLD : PILL_EDGE,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(12, 6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = label,
        };
        pill.PointerEntered += (_, _) => pill.Background = hover;
        pill.PointerExited += (_, _) => pill.Background = normal;
        pill.PointerReleased += async (_, _) => {
            if (!evenWhenBusy && (!pill.IsEnabled || _busy)) {
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
        BorderBrush = new SolidColorBrush(Color.Parse("#4D3A20")),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(0),
        Padding = new Thickness(16, 12),
        VerticalAlignment = VerticalAlignment.Bottom,
        Child = new StackPanel {
            Spacing = 4,
            Children = {
                new TextBlock {
                    Text = heading.ToUpperInvariant(), FontFamily = HEADING_FONT, FontSize = 14, LetterSpacing = 2,
                    FontWeight = FontWeight.Bold, Foreground = GOLD,
                },
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
        _bookTitle.Effect = new DropShadowEffect { OffsetX = 0, OffsetY = 0, BlurRadius = 16, Color = GOLD_COLOR, Opacity = 0.3 };
        _bookScroll.Content = _bookPages;
        var close = Pill("Close", () => {
            CloseBook();

            return Task.CompletedTask;
        }, gold: true, evenWhenBusy: true);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        _bookClose = close;
        var understood = Pill("I understand", async () => {
            NoticeSeen = true;
            CloseBook();
            if (_updateAfterNotice) {
                _updateAfterNotice = false;
                await AutoUpdateAsync();
            }
        }, gold: true, evenWhenBusy: true);
        var leave = Pill("Close the launcher", () => {
            _closeAnyway = true;
            Close();

            return Task.CompletedTask;
        }, evenWhenBusy: true);
        _bookAnswer.Children.AddRange([leave, understood]);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 10) };
        Grid.SetColumn(close, 1);
        header.Children.AddRange([_bookTitle, close]);

        var page = new Border {
            Width = 680,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse("#F71A1312")),
            BorderBrush = BRASS,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(28, 20, 20, 22),
            BoxShadow = BoxShadows.Parse("0 16 50 0 #80000000"),
            Child = new StackPanel { Children = { header, _bookScroll, _settingsStatus, _bookAnswer, _settingsActions } },
        };
        // Clicks on the page stay on the page; a click on the dimmed sky around it closes it.
        page.PointerPressed += (_, e) => e.Handled = true;

        var shade = new Border {
            Background = new SolidColorBrush(Color.Parse("#B3050304")),
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
        _settingsActions.IsVisible = false;
        _settingsStatus.IsVisible = false;
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
                    Text = line[2..].Trim().ToUpperInvariant(),
                    FontFamily = HEADING_FONT,
                    LetterSpacing = 2,
                    FontSize = 15,
                    FontWeight = FontWeight.Bold,
                    Foreground = GOLD_DIM,
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
