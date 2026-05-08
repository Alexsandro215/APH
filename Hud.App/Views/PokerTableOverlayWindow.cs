using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Documents;
using System.Windows.Threading;
using HandReader.Core.Models;
using Hud.App.Services;

namespace Hud.App.Views
{
    public sealed class PokerTableOverlayWindow : Window
    {
        private static readonly (double X, double Y)[] SeatAnchors =
        {
            (0.50, 0.06),
            (0.82, 0.18),
            (0.84, 0.53),
            (0.50, 0.79),
            (0.16, 0.53),
            (0.18, 0.18)
        };

        private readonly IntPtr _targetHandle;
        private readonly Func<IReadOnlyList<PlayerStats>> _playersProvider;
        private readonly string? _heroName;
        private readonly Canvas _canvas = new();
        private readonly List<Border> _hudBoxes = new();
        private readonly DispatcherTimer _timer;
        private readonly StatToBrushConverter _statBrushConverter = new();

        public PokerTableOverlayWindow(
            IntPtr targetHandle,
            Func<IReadOnlyList<PlayerStats>> playersProvider,
            string? heroName)
        {
            _targetHandle = targetHandle;
            _playersProvider = playersProvider;
            _heroName = string.IsNullOrWhiteSpace(heroName) ? null : heroName.Trim().TrimEnd(':').Trim();

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Content = _canvas;

            for (var i = 0; i < SeatAnchors.Length; i++)
            {
                var text = new TextBlock
                {
                    Foreground = Brushes.White,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontFamily = new FontFamily("Consolas"),
                    FontWeight = FontWeights.SemiBold
                };

                var border = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(226, 2, 5, 8)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(190, 64, 77, 91)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 2, 4, 2),
                    Child = text,
                    Visibility = Visibility.Collapsed
                };

                _hudBoxes.Add(border);
                _canvas.Children.Add(border);
            }

            SourceInitialized += (_, _) => MakeClickThrough();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            _timer.Tick += (_, _) => Refresh();
            Loaded += (_, _) =>
            {
                Refresh();
                _timer.Start();
            };
            Closed += (_, _) => _timer.Stop();
        }

        public void Refresh()
        {
            if (!PokerStarsWindowDetector.TryGetWindowRect(_targetHandle, out var bounds))
            {
                Hide();
                return;
            }

            if (!IsVisible)
                Show();

            Left = bounds.Left;
            Top = bounds.Top;
            Width = bounds.Width;
            Height = bounds.Height;
            _canvas.Width = Width;
            _canvas.Height = Height;

            var players = OrderPlayersForVisibleSeats(_playersProvider());
            var boxWidth = Math.Clamp(bounds.Width * 0.23, 132, 208);
            var fontSize = Math.Clamp(bounds.Width / 112.0, 7.6, 10.2);

            for (var i = 0; i < _hudBoxes.Count; i++)
            {
                var box = _hudBoxes[i];
                if (i >= players.Count)
                {
                    box.Visibility = Visibility.Collapsed;
                    continue;
                }

                var player = players[i];
                var text = (TextBlock)box.Child;
                text.FontSize = fontSize;
                text.LineHeight = fontSize + 1.1;
                ApplyHudText(text, player);

                box.Width = boxWidth;
                box.Visibility = Visibility.Visible;
                box.Background = IsHero(player)
                    ? new SolidColorBrush(Color.FromArgb(232, 13, 27, 44))
                    : new SolidColorBrush(Color.FromArgb(226, 2, 5, 8));

                var anchor = SeatAnchors[i];
                var x = bounds.Width * anchor.X - boxWidth / 2;
                var y = bounds.Height * anchor.Y;
                Canvas.SetLeft(box, Math.Clamp(x, 2, Math.Max(2, bounds.Width - boxWidth - 2)));
                Canvas.SetTop(box, Math.Clamp(y, 2, Math.Max(2, bounds.Height - 78)));
            }
        }

        private void ApplyHudText(TextBlock text, PlayerStats player)
        {
            var last = LastPlayedHand(player);
            var name = player.Name.Length > 13 ? player.Name[..13] : player.Name;
            text.Inlines.Clear();

            Add(text, IsHero(player) ? "Hero" : name, Brushes.White);
            Add(text, " (", DimBrush);
            Add(text, player.HandsReceived.ToString("0"), HandsBrush);
            Add(text, $") Ult:{last}\n", DimBrush);

            Add(text, "VP/PF/3B/AF/AFq: ", DimBrush);
            AddStat(text, player, player.VPIPPct, "VPIP");
            Add(text, "/", DimBrush);
            AddStat(text, player, player.PFRPct, "PFR");
            Add(text, "/", DimBrush);
            AddStat(text, player, player.ThreeBetPct, "THREEBET");
            Add(text, "/", DimBrush);
            AddStat(text, player, player.AF, "AF", "0.#");
            Add(text, "/", DimBrush);
            AddStat(text, player, player.AFqPct, "AFQ");
            Add(text, "\n", DimBrush);

            Add(text, "CB/Fv/WT/W$/WW: ", DimBrush);
            AddStat(text, player, player.CBetFlopPct, "CBF");
            Add(text, "/", DimBrush);
            AddStat(text, player, player.FoldVsCBetFlopPct, "FVCBF");
            Add(text, "/", DimBrush);
            AddStat(text, player, player.WTSDPct, "WTSD");
            Add(text, "/", DimBrush);
            AddStat(text, player, player.WSDPct, "WSD");
            Add(text, "/", DimBrush);
            AddStat(text, player, player.WWSFPct, "WWSF");
        }

        private static void Add(TextBlock text, string value, Brush brush) =>
            text.Inlines.Add(new Run(value) { Foreground = brush });

        private void AddStat(TextBlock text, PlayerStats player, double value, string key, string format = "0")
        {
            var brush = _statBrushConverter.Convert(
                new object[] { value, player.HandsReceived, StakeProfile.Low, key },
                typeof(Brush),
                Binding.DoNothing,
                System.Globalization.CultureInfo.InvariantCulture) as Brush ?? DimBrush;

            text.Inlines.Add(new Run(value.ToString(format, System.Globalization.CultureInfo.InvariantCulture))
            {
                Foreground = brush
            });
        }

        private static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(174, 184, 194));
        private static readonly Brush HandsBrush = new SolidColorBrush(Color.FromRgb(255, 82, 101));

        private static string LastPlayedHand(PlayerStats player)
        {
            var last = player.LastHands
                .Reverse()
                .FirstOrDefault(hand => !string.IsNullOrWhiteSpace(hand) && hand != "--");
            return string.IsNullOrWhiteSpace(last) ? "-" : last;
        }

        private bool IsHero(PlayerStats player) =>
            !string.IsNullOrWhiteSpace(_heroName) &&
            string.Equals(player.Name.Trim().TrimEnd(':').Trim(), _heroName, StringComparison.Ordinal);

        private IReadOnlyList<PlayerStats> OrderPlayersForVisibleSeats(IReadOnlyList<PlayerStats> players)
        {
            if (players.Count == 0 || string.IsNullOrWhiteSpace(_heroName))
                return players;

            var heroIndex = -1;
            for (var i = 0; i < players.Count; i++)
            {
                if (string.Equals(players[i].Name.Trim().TrimEnd(':').Trim(), _heroName, StringComparison.Ordinal))
                {
                    heroIndex = i;
                    break;
                }
            }

            if (heroIndex < 0)
                return players;

            const int heroAnchorIndex = 3;
            var ordered = new PlayerStats[players.Count];
            for (var i = 0; i < players.Count; i++)
            {
                var target = (heroAnchorIndex + i - heroIndex + players.Count) % players.Count;
                ordered[target] = players[i];
            }

            return ordered;
        }

        private void MakeClickThrough()
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
                return;

            var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
            style |= WsExTransparent | WsExToolWindow | WsExNoActivate;
            SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
        }

        private const int GwlExStyle = -20;
        private const long WsExTransparent = 0x00000020L;
        private const long WsExToolWindow = 0x00000080L;
        private const long WsExNoActivate = 0x08000000L;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    }
}
