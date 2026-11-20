using System.Windows;
using System.Windows.Threading;
using Hud.App.Services;
using Hud.App.Views;

namespace Hud.App
{
    /// <summary>
    /// Vigila cada pocos segundos si hay mesas de PokerStars abiertas. Si las hay y Tiempo real esta
    /// cerrado, pregunta (con un aviso que no se lleva el foco) si se arranca. Pregunta una vez por
    /// tanda de mesas: si dices "Ahora no", no vuelve a preguntar hasta que cierres todas las mesas y
    /// abras otras. Nunca arranca Tiempo real sin que lo pidas.
    /// </summary>
    public partial class MainWindow
    {
        private DispatcherTimer? _tableWatch;
        private bool _tableWatchBusy;
        private bool _askedForTheseTables;
        private TablesDetectedPrompt? _tablesPrompt;

        private void StartTableWatch()
        {
            _tableWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _tableWatch.Tick += async (_, _) => await CheckOpenTablesAsync();
            _tableWatch.Start();
            Closed += (_, _) =>
            {
                _tableWatch.Stop();
                _tablesPrompt?.Close();
            };
        }

        private static bool RealTimeOpen() =>
            Application.Current.Windows.OfType<RealTimeWindow>().Any(window => window.IsVisible);

        private async Task CheckOpenTablesAsync()
        {
            if (_tableWatchBusy)
                return;

            _tableWatchBusy = true;
            try
            {
                IReadOnlyList<PokerStarsTableWindow> tables;
                try
                {
                    tables = await Task.Run(PokerStarsWindowDetector.DetectOpenTables);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("No se pudieron buscar mesas abiertas de PokerStars", ex);
                    return;
                }

                if (tables.Count == 0)
                {
                    // Sin mesas: cuando se abran otras, se vuelve a preguntar.
                    _askedForTheseTables = false;
                    _tablesPrompt?.Close();
                    return;
                }

                if (RealTimeOpen())
                {
                    // Ya esta en marcha (o lo abriste a mano): no se pregunta por estas mesas.
                    _askedForTheseTables = true;
                    _tablesPrompt?.Close();
                    return;
                }

                if (_tablesPrompt is not null)
                {
                    _tablesPrompt.SetCount(tables.Count);
                    return;
                }

                if (_askedForTheseTables || _openDialogs > 0 || !AppSettingsService.Load().AskToStartRealTime)
                    return;

                _askedForTheseTables = true;
                var prompt = new TablesDetectedPrompt(tables.Count, tables.Select(table => table.Handle).ToList());
                prompt.Answered += (_, answer) => OnTablesPromptAnswer(answer);
                prompt.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_tablesPrompt, prompt))
                        _tablesPrompt = null;
                };
                _tablesPrompt = prompt;
                prompt.Show();
                AppLog.Info($"Mesas detectadas ({tables.Count}): se pregunta si arrancar Tiempo real");
            }
            finally
            {
                _tableWatchBusy = false;
            }
        }

        private void OnTablesPromptAnswer(TablesPromptAnswer answer)
        {
            switch (answer)
            {
                case TablesPromptAnswer.Start:
                    if (!RealTimeOpen())
                        BtnRT_Click(this, new RoutedEventArgs());
                    break;

                case TablesPromptAnswer.Never:
                    var settings = AppSettingsService.Load();
                    settings.AskToStartRealTime = false;
                    AppSettingsService.Save(settings);
                    Toast.Show(LocalizationManager.Text("Rt.Prompt.NeverToast"), ToastKind.Info);
                    break;
            }
        }
    }
}
