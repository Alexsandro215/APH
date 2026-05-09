using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Hud.App.Services;

namespace Hud.App.Views
{
    public partial class SessionsWindow : Window
    {
        private readonly ObservableCollection<SessionRow> _rows = new();

        public SessionsWindow()
        {
            InitializeComponent();
            SessionsGrid.ItemsSource = _rows;
            LoadSessions();
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => LoadSessions();

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
            FolderText.Text = $"Carpeta: {folder}";
            var sessions = ReportSessionIndexService.LoadSessions();
            foreach (var session in sessions)
                _rows.Add(SessionRow.FromRecord(session));

            StatusText.Text = sessions.Count == 0
                ? "No hay informes guardados todavia."
                : $"{sessions.Count} informes encontrados. Doble click para abrir un PDF.";
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
