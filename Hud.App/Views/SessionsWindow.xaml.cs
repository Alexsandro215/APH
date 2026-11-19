using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Hud.App.Services;

namespace Hud.App.Views
{
    public partial class SessionsWindow : Window
    {
        private readonly ObservableCollection<SessionRow> _rows = new();
        private IReadOnlyList<MainWindow.TableSessionStats> _tables = Array.Empty<MainWindow.TableSessionStats>();

        public SessionsWindow(IReadOnlyList<MainWindow.TableSessionStats>? tables = null)
        {
            InitializeComponent();
            SessionsGrid.ItemsSource = _rows;
            LoadSessions();
            LoadPlayedSessions(tables ?? Array.Empty<MainWindow.TableSessionStats>());
        }

        /// <summary>Sesiones calculadas de los historiales (en segundo plano: lee cada mano).</summary>
        private void LoadPlayedSessions(IReadOnlyList<MainWindow.TableSessionStats> tables)
        {
            _tables = tables;
            PlayedStatusText.Text = LocalizationManager.Text("Sessions.Loading");
            BackgroundLoad.Run(this, () => SessionSummaryService.Group(SessionSummaryService.LoadHands(tables)), sessions =>
            {
                PlayedGrid.ItemsSource = sessions;
                PlayedChart.Items = sessions
                    .Select(session => new Charts.BarDatum(
                        $"{session.Start:MM-dd HH:mm}",
                        session.NetBb,
                        session.NetLabel,
                        string.Format(CultureInfo.CurrentCulture, LocalizationManager.Text("HandTypes.Hands"), session.Hands) + " · " + session.DurationLabel,
                        string.Format(CultureInfo.CurrentCulture, LocalizationManager.Text("Sessions.BarDetail"), session.DateLabel, session.TimeLabel,
                            session.Hands, session.Tables, session.NetLabel, session.AdjustedLabel, session.BestLabel, session.WorstLabel)))
                    .ToList();
                PlayedStatusText.Text = sessions.Count == 0 ? LocalizationManager.Text("Sessions.NoPlayed") : "";
            });
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => LoadSessions();

        /// <summary>Informe PDF de la sesion elegida (o la mas reciente) con las manos de los historiales.</summary>
        private void BtnGenerateReport_Click(object sender, RoutedEventArgs e)
        {
            var session = PlayedGrid.SelectedItem as SessionSummary ??
                          (PlayedGrid.ItemsSource as IEnumerable<SessionSummary>)?.FirstOrDefault();
            if (session is null || _tables.Count == 0)
                return;

            GenerateReportButton.IsEnabled = false;
            PlayedStatusText.Text = LocalizationManager.Text("Sessions.Generating");
            var tables = _tables;
            BackgroundLoad.Run(this, () =>
            {
                try
                {
                    return (Path: RealTimeSessionReportService.SavePdfForSession(tables, session.Start, session.End), Error: (string?)null);
                }
                catch (Exception ex)
                {
                    return (Path: (string?)null, Error: ex.Message);
                }
            }, result =>
            {
                GenerateReportButton.IsEnabled = true;
                if (result.Path is null)
                {
                    PlayedStatusText.Text = string.Format(LocalizationManager.Text("Sessions.GenerateFailed"), result.Error);
                    return;
                }

                PlayedStatusText.Text = string.Format(LocalizationManager.Text("Sessions.Generated"), Path.GetFileName(result.Path));
                LoadSessions();
                Process.Start(new ProcessStartInfo(result.Path) { UseShellExecute = true });
            });
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var folder = ReportSessionIndexService.GetReportsFolder();
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }

        private void SessionsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SessionsGrid.SelectedItem is not SessionRow row || !File.Exists(row.PdfPath))
                return;

            Process.Start(new ProcessStartInfo(row.PdfPath) { UseShellExecute = true });
        }

        private void LoadSessions()
        {
            _rows.Clear();
            var folder = ReportSessionIndexService.GetReportsFolder();
            FolderText.Text = $"{LocalizationManager.Text("Common.Folder")}: {folder}";
            var sessions = ReportSessionIndexService.LoadSessions();
            foreach (var session in sessions)
                _rows.Add(SessionRow.FromRecord(session));

            StatusText.Text = sessions.Count == 0
                ? LocalizationManager.Text("Sessions.NoReports")
                : string.Format(LocalizationManager.Text("Sessions.ReportsFound"), sessions.Count);
        }

        private sealed class SessionRow
        {
            public int SessionNumber { get; init; }
            public string StartLabel { get; init; } = "-";
            public string CloseLabel { get; init; } = "-";
            public int TableCount { get; init; }
            public string DurationLabel { get; init; } = "-";
            public string Hero { get; init; } = "-";
            public string FileName { get; init; } = "-";
            public string PdfPath { get; init; } = "";

            public static SessionRow FromRecord(ReportSessionRecord record) =>
                new()
                {
                    SessionNumber = record.SessionNumber,
                    StartLabel = FormatDate(record.StartedAt),
                    CloseLabel = FormatDate(record.ClosedAt),
                    TableCount = record.TableCount,
                    DurationLabel = FormatDuration(record.Duration),
                    Hero = record.Hero,
                    FileName = Path.GetFileName(record.PdfPath),
                    PdfPath = record.PdfPath
                };

            private static string FormatDate(DateTime? value) =>
                value.HasValue ? value.Value.ToString("yyyy-MM-dd HH:mm:ss") : "-";

            private static string FormatDuration(TimeSpan duration)
            {
                if (duration <= TimeSpan.Zero)
                    return "-";

                return duration.TotalHours >= 1
                    ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
                    : $"{duration.Minutes}m";
            }
        }
    }
}
