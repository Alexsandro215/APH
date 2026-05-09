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

            Loaded += (_, _) => FitToWorkArea();
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
                DetectionStatusText.Text = LocalizationManager.Text("Common.RtNoWindows");
                return;
            }

            var matches = PokerStarsWindowDetector.MatchHandHistories(windows).Take(slots.Length).ToList();
            if (matches.Count == 0)
            {
                var root = PokerStarsWindowDetector.GetDefaultHandHistoryRoot() ?? "(sin carpeta)";
                var names = string.Join(", ", windows.Select(window => window.TableName).Take(4));
                DetectionStatusText.Text = string.Format(LocalizationManager.Text("Common.RtNoMatches"), names, root);
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
                ? string.Format(LocalizationManager.Text("Common.RtStartedPartial"), matches.Count, missing)
                : string.Format(LocalizationManager.Text("Common.RtStartedComplete"), matches.Count);
        }

        private void BtnFinishSession_Click(object sender, RoutedEventArgs e)
        {
            var slots = GetSlots();
            var snapshots = slots
                .Select(slot => slot.GetReportSnapshot())
                .Where(table => !string.IsNullOrWhiteSpace(table.SourcePath) || table.Players.Count > 0)
                .ToList();

            if (snapshots.Count == 0)
            {
                MessageBox.Show(
                    LocalizationManager.Text("RT.NoSessionToReport"),
                    LocalizationManager.Text("RT.FinishSession"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var active = snapshots.Where(table => table.IsRunning).ToList();
            if (active.Count > 0)
            {
                var details = string.Join(
                    Environment.NewLine,
                    active.Select(table => string.Format(
                        LocalizationManager.Text("RT.ActiveTableLine"),
                        table.TableName,
                        FormatDate(table.LastHandTime))));
                var message = string.Format(LocalizationManager.Text("RT.FinishWarning"), active.Count, details);
                var result = MessageBox.Show(
                    message,
                    LocalizationManager.Text("RT.FinishSession"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes)
                    return;
            }

            try
            {
                var path = RealTimeSessionReportService.SavePdf(snapshots);
                foreach (var slot in slots)
                    slot.StopSession();
                ClearOverlays();

                MessageBox.Show(
                    string.Format(LocalizationManager.Text("RT.ReportSaved"), path),
                    LocalizationManager.Text("RT.FinishSession"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format(LocalizationManager.Text("RT.ReportError"), ex.Message),
                    LocalizationManager.Text("RT.FinishSession"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
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

        private void FitToWorkArea()
        {
            var workArea = SystemParameters.WorkArea;
            MaxHeight = Math.Max(520, workArea.Height - 24);
            Height = Math.Min(MaxHeight, Math.Max(720, workArea.Height - 48));

            if (Top + Height > workArea.Bottom)
                Top = Math.Max(workArea.Top + 12, workArea.Bottom - Height - 12);
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

        private static string FormatDate(DateTime? value) =>
            value.HasValue ? value.Value.ToString("yyyy-MM-dd HH:mm:ss") : "-";
    }
}



