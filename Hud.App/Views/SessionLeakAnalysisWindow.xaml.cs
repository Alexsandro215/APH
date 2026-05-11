using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Hud.App.Services;
using Hud.App;

namespace Hud.App.Views
{
    public partial class SessionLeakAnalysisWindow : Window
    {
        private readonly ObservableCollection<SessionAnalysisRow> _sessions = new();

        public SessionLeakAnalysisWindow()
        {
            InitializeComponent();
            SessionsGrid.ItemsSource = _sessions;
            LoadSessions();
        }

        private void LoadSessions()
        {
            _sessions.Clear();
            var sessions = ReportSessionIndexService.LoadSessions();
            foreach (var session in sessions)
            {
                _sessions.Add(new SessionAnalysisRow(session));
            }
            StatusText.Text = $"{_sessions.Count} sesiones encontradas. Listo para analizar.";
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => LoadSessions();

        private void SessionsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (SessionsGrid.SelectedItem is SessionAnalysisRow row)
                SafeAnalyzeSession(row);
        }

        private void BtnAnalyze_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is SessionAnalysisRow row)
                SafeAnalyzeSession(row);
        }

        private void SafeAnalyzeSession(SessionAnalysisRow row)
        {
            try
            {
                AnalyzeSession(row);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"No se pudo abrir el analisis: {ex.Message}";
                MessageBox.Show(
                    $"No se pudo abrir el analisis de esta sesion.\n\nDetalle: {ex.Message}",
                    "APH - Analisis de leaks",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void AnalyzeSession(SessionAnalysisRow row)
        {
            StatusText.Text = $"Analizando {row.Name}... por favor espera.";
            
            // Try to find history files
            var paths = row.Record.TableSourcePaths;
            if (paths == null || paths.Count == 0)
            {
                paths = TryFindHistoryFiles(row.Record);
            }

            if (paths == null || paths.Count == 0)
            {
                StatusText.Text = "No se encontraron archivos de historial para esta sesión.";
                MessageBox.Show("No hay archivos de historial disponibles para esta sesión histórica.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var handsToReview = new List<LeakSpotRow>();
            var hero = row.Record.Hero;

            foreach (var path in paths)
            {
                if (!File.Exists(path)) continue;
                handsToReview.AddRange(ExtractFilteredHands(path, hero));
            }

            if (handsToReview.Count == 0)
            {
                StatusText.Text = "Análisis completado: No se detectaron fugas críticas bajo los filtros actuales.";
                MessageBox.Show("No se encontraron fugas críticas (excluyendo ciegas) en esta sesión.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            StatusText.Text = $"Análisis completado: {handsToReview.Count} fugas detectadas.";
            
            var reviewWindow = new LeakReviewWindow(handsToReview, $"Análisis de Sesión: {row.Name}")
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };
            reviewWindow.Show();
        }

        private List<string> TryFindHistoryFiles(ReportSessionRecord record)
        {
            var results = new List<string>();
            var folder = Path.GetDirectoryName(record.PdfPath);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return results;

            // Search for .txt files in the same folder as the PDF
            var txtFiles = Directory.GetFiles(folder, "*.txt");
            if (txtFiles.Length > 0) return txtFiles.ToList();

            // Try to look into the selected poker-room history folder if defined.
            var settings = AppSettingsService.Load();
            var historyFolder = ResolveSelectedHistoryFolder(settings);
            if (!string.IsNullOrEmpty(historyFolder) && Directory.Exists(historyFolder))
            {
                // Search for files modified on the same day as the session
                if (record.StartedAt.HasValue)
                {
                    var dateStr = record.StartedAt.Value.ToString("yyyyMMdd");
                    results.AddRange(Directory.EnumerateFiles(historyFolder, $"*{dateStr}*", SearchOption.AllDirectories));
                }
            }

            return results.Distinct().ToList();
        }

        private static string? ResolveSelectedHistoryFolder(AppSettings settings)
        {
            if (settings.PokerRoomFolders.TryGetValue(settings.SelectedPokerRoom, out var selectedFolder) &&
                !string.IsNullOrWhiteSpace(selectedFolder))
            {
                return selectedFolder;
            }

            return string.Equals(settings.SelectedPokerRoom, "PokerStars", StringComparison.OrdinalIgnoreCase)
                ? settings.PokerStarsHandHistoryFolder
                : null;
        }

        private List<LeakSpotRow> ExtractFilteredHands(string path, string hero)
        {
            var results = new List<LeakSpotRow>();
            try 
            {
                var lines = File.ReadLines(path).ToList();
                var hands = PokerStarsHandHistory.SplitHands(lines).ToList();
                
                double bigBlind = DetectBigBlind(lines);

                for (int i = 0; i < hands.Count; i++)
                {
                    var hand = hands[i];
                    if (!PokerStarsHandHistory.HandHasPlayerActivity(hand, hero)) continue;

                    var netAmount = PokerStarsHandHistory.EstimateNetForPlayer(hand, hero);
                    var netBb = bigBlind > 0 ? netAmount / bigBlind : 0;

                    // FILTER: Only losses worse than 1bb
                    if (netBb >= -1.0) continue;

                    var action = DetectHeroAction(hand, hero);
                    
                    // USER RULE: "sin contar 0.5bb en SB y foldeo. o foldeo con 1bb en BB puesta."
                    bool isBlindFold = action == "Fold" && (Math.Abs(netBb + 0.5) < 0.05 || Math.Abs(netBb + 1.0) < 0.05);
                    
                    if (!isBlindFold)
                    {
                        results.Add(BuildDetailedLeakRow(hand, path, i + 1, hero, netBb, bigBlind));
                    }
                }
            }
            catch { /* Ignore corrupted files */ }

            return results;
        }

        private LeakSpotRow BuildDetailedLeakRow(IReadOnlyList<string> hand, string path, int index, string hero, double netBb, double bigBlind)
        {
            // Reuse parsing logic to make it high quality
            string cards = "";
            PokerStarsHandHistory.TryGetDealtCards(hand, hero, out cards);
            
            var posMap = PokerStarsHandHistory.BuildPositionMap(hand);
            var pos = posMap.TryGetValue(hero, out var p) ? p : "?";
            var action = DetectHeroAction(hand, hero);
            var stamp = PokerStarsHandHistory.ExtractTimestamp(hand) ?? DateTime.Now;
            
            // Street logic for the 4-street coach
            var board = PokerStarsHandHistory.ExtractBoardStreets(hand);
            var flopCards = board.Flop;
            var turnCard = board.Turn;
            var riverCard = board.River;

            // CLEAN DISPLAY DATA
            string rawName = Path.GetFileNameWithoutExtension(path);
            string cleanTable = "Mesa";
            string blinds = "";
            
            // Match PokerStars filename pattern: HH20260205 Apisaon - 100-200 - Dinero ficticio
            var nameMatch = Regex.Match(rawName, @"HH\d+\s+(?<name>.+?)\s+-");
            if (nameMatch.Success) cleanTable = nameMatch.Groups["name"].Value;

            var blindsMatch = Regex.Match(rawName, @"-\s+(?<blinds>\d+-\d+)");
            if (blindsMatch.Success) blinds = blindsMatch.Groups["blinds"].Value.Replace("-", "/");
            else blinds = bigBlind > 0 ? $"{bigBlind/2}/{bigBlind}" : "";

            string displayTable = $"{cleanTable} - {blinds}";
            string handLabel = $"Mano #{index}";

            // EXTRACT HAND SUMMARY INFO (Result and Combination)
            var summaryInfo = PokerStarsHandHistory.ExtractHandSummaryInfo(hand, hero);

            var dummyTable = new MainWindow.TableSessionStats(
                displayTable, "Hold'em", stamp.ToString("yyyy-MM-dd"), stamp, path, "PokerStars", hero, bigBlind, 
                Hud.App.Services.StakeProfile.Low, 0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, netBb, 0.0, true, "");

            return new LeakSpotRow(
                dummyTable, displayTable, stamp, index,
                cards, handLabel, pos, action, "Pot", "Texture",
                flopCards, turnCard, riverCard,
                "", "", "", "", 
                summaryInfo.Combination, summaryInfo.Result, 
                summaryInfo.VillainName, summaryInfo.VillainCards, summaryInfo.VillainCombination,
                "", netBb, 0,
                $"Fuga detectada en {pos}: {netBb:F1} bb");
        }

        private string ExtractCards(string line)
        {
            var match = Regex.Match(line, @"\[(?<cards>[^\]]+)\]");
            return match.Success ? match.Groups["cards"].Value : "";
        }

        private double DetectBigBlind(IEnumerable<string> lines)
        {
            foreach (var line in lines.Take(100))
            {
                var match = Regex.Match(line, @"\((?:\$)?[\d,.]+\s*/\s*(?:\$)?(?<big>[\d,.]+)\)");
                if (match.Success && double.TryParse(match.Groups["big"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var bb))
                    return bb;
            }
            return 0;
        }

        private string DetectHeroAction(IReadOnlyList<string> hand, string heroName)
        {
            var actions = hand
                .Where(line => {
                    var actor = PokerStarsHandHistory.ActorRx.Match(line);
                    return actor.Success && PokerStarsHandHistory.SamePlayer(actor.Groups["actor"].Value, heroName);
                })
                .Select(l => PokerStarsHandHistory.NormalizeAction(PokerStarsHandHistory.ActorRx.Match(l).Groups["action"].Value))
                .ToList();

            if (actions.Contains("folds")) return "Fold";
            if (actions.Contains("raises")) return "Raise";
            if (actions.Contains("calls")) return "Call";
            if (actions.Contains("bets")) return "Bet";
            return "Check";
        }

        public class SessionAnalysisRow
        {
            public ReportSessionRecord Record { get; }
            public string Name => Record.StartedAt?.ToString("Session yyyyMMdd_HHmm") ?? "Sesión Desconocida";
            public string Date => Record.StartedAt?.ToString("yyyy-MM-dd") ?? "-";
            public string Time => Record.StartedAt?.ToString("HH:mm") ?? "-";
            public int TableCount => Record.TableCount;
            public int HandCount => Record.HandCount;
            public string Result => Record.ResultLabel;
            public string Bb100 { 
                get {
                    if (HandCount < 50) return "N/A";
                    // Attempt to parse BB from ResultLabel
                    var match = Regex.Match(Result, @"(?<bb>[+-]?[\d,.]+)\s*bb");
                    if (match.Success && double.TryParse(match.Groups["bb"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var bb))
                    {
                        var val = (bb / HandCount) * 100;
                        return $"{val:+0.#;-0.#;0}";
                    }
                    return "N/A"; 
                }
            }
            public string AvgStats => Record.HeroStatsLabel;
            public Brush ResultBrush => Result.Contains("+") ? new SolidColorBrush(Color.FromRgb(33, 192, 122)) : new SolidColorBrush(Color.FromRgb(226, 78, 91));

            public SessionAnalysisRow(ReportSessionRecord record)
            {
                Record = record;
            }
        }
    }
}
