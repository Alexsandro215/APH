using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Hud.App.Services;

namespace Hud.App.Views
{
    public partial class RealTimeWindow : Window
    {
        private readonly List<PokerTableOverlayWindow> _overlays = new();

        public RealTimeWindow()
        {
            InitializeComponent();

            // aplica escala inicial segun el checkbox (compacto por defecto)
            ApplyScale(ChkCompact?.IsChecked == true);
            if (ChkCompact != null)
            {
                ChkCompact.Checked += (_, __) => ApplyScale(true);
                ChkCompact.Unchecked += (_, __) => ApplyScale(false);
            }

            Closed += (_, _) => ClearOverlays();
        }

        private void BtnDetectTables_Click(object sender, RoutedEventArgs e)
        {
            ClearOverlays();
            var slots = GetSlots();
            foreach (var slot in slots)
                slot.ClearSlot();

            var windows = PokerStarsWindowDetector.DetectOpenTables();
            if (windows.Count == 0)
            {
                DetectionStatusText.Text = "No encontre ventanas activas de PokerStars con mesas Hold'em.";
                return;
            }

            var matches = PokerStarsWindowDetector.MatchHandHistories(windows).Take(slots.Length).ToList();
            if (matches.Count == 0)
            {
                var root = PokerStarsWindowDetector.GetDefaultHandHistoryRoot() ?? "(sin carpeta)";
                var names = string.Join(", ", windows.Select(window => window.TableName).Take(4));
                DetectionStatusText.Text = $"Detecte mesa(s): {names}. No encontre HH recientes coincidentes en {root}.";
                return;
            }

            for (var i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                slots[i].StartAuto(match.HandHistoryPath, match.Window.TableName, match.Window.HeroName);

                var slot = slots[i];
                var overlay = new PokerTableOverlayWindow(
                    match.Window.Handle,
                    slot.GetPlayersSnapshot,
                    match.Window.HeroName);
                overlay.Show();
                _overlays.Add(overlay);
            }

            var missing = windows.Count - matches.Count;
            DetectionStatusText.Text = missing > 0
                ? $"RT iniciado en {matches.Count} mesa(s). {missing} ventana(s) no tuvieron HH reciente coincidente."
                : $"RT iniciado en {matches.Count} mesa(s) detectada(s).";
        }

        private SlotTail[] GetSlots() =>
            new[] { Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8 };

        private void ClearOverlays()
        {
            foreach (var overlay in _overlays.ToList())
            {
                try { overlay.Close(); } catch { }
            }
            _overlays.Clear();
        }

        private void ApplyScale(bool compact)
        {
            // 0.80 en compacto, 1.00 en normal
            if (ScaleProxy != null)
                ScaleProxy.Tag = compact ? 0.80 : 1.00;
        }

        private void BtnDictionary_Click(object sender, RoutedEventArgs e)
        {
            var window = new RealTimeDictionaryWindow
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };

            window.Show();
        }
    }
}
