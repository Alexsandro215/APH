using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Hud.App.Views
{
    public partial class RealTimeDictionaryWindow : Window
    {
        public RealTimeDictionaryWindow()
        {
            InitializeComponent();
            DataContext = RealTimeDictionaryViewModel.Build();
        }

        private sealed class RealTimeDictionaryViewModel
        {
            public IReadOnlyList<HelpRow> ControlRows { get; private init; } = Array.Empty<HelpRow>();
            public IReadOnlyList<HelpRow> MetricRows { get; private init; } = Array.Empty<HelpRow>();
            public IReadOnlyList<ColorLegendRow> ColorRows { get; private init; } = Array.Empty<ColorLegendRow>();
            public IReadOnlyList<string> NoteRows { get; private init; } = Array.Empty<string>();

            public static RealTimeDictionaryViewModel Build() =>
                new()
                {
                    ControlRows = BuildControls().ToList(),
                    MetricRows = BuildMetrics().ToList(),
                    ColorRows = BuildColors().ToList(),
                    NoteRows = BuildNotes().ToList()
                };

            private static IEnumerable<HelpRow> BuildControls()
            {
                yield return new HelpRow("+", "Selecciona el archivo .txt de la mesa para empezar a leerla.");
                yield return new HelpRow("Play", "Inicia o reanuda el seguimiento del archivo seleccionado.");
                yield return new HelpRow("Stop", "Detiene el seguimiento de esa mesa sin cerrar la ventana.");
                yield return new HelpRow("Heroe", "Nombre usado para comparar tu fila contra los rivales de la mesa.");
                yield return new HelpRow("Lineas", "Cantidad de lineas leidas del historial de la mesa.");
                yield return new HelpRow("Modo compacto", "Reduce la escala para ver las 8 mesas sin desplazamiento.");
            }

            private static IEnumerable<HelpRow> BuildMetrics()
            {
                yield return new HelpRow("#Manos", "Manos recibidas por el jugador en esa mesa.");
                yield return new HelpRow("VPIP%", "Porcentaje de manos que juega voluntariamente preflop.");
                yield return new HelpRow("PFR%", "Porcentaje de manos que sube preflop.");
                yield return new HelpRow("3Bet%", "Frecuencia con la que resube preflop ante una subida.");
                yield return new HelpRow("AF", "Aggression Factor: apuestas/subidas dividido entre calls postflop.");
                yield return new HelpRow("AFq%", "Frecuencia de agresion postflop cuando tuvo oportunidad.");
                yield return new HelpRow("CBet%", "Continuation bet flop despues de ser agresor preflop.");
                yield return new HelpRow("FvCBet%", "Fold vs CBet flop. Alto puede ser explotable.");
                yield return new HelpRow("WTSD%", "Frecuencia con la que llega a showdown tras ver flop.");
                yield return new HelpRow("W$SD%", "Porcentaje de showdowns que gana.");
                yield return new HelpRow("WWSF%", "Frecuencia con la que gana cuando vio flop.");
            }

            private static IEnumerable<ColorLegendRow> BuildColors()
            {
                yield return new ColorLegendRow(FindBrush("BgDark"), "Neutro", "Muestra insuficiente o dato sin lectura clara.");
                yield return new ColorLegendRow(FindBrush("HudBlueDark"), "Azul oscuro", "Valor muy bajo para la metrica.");
                yield return new ColorLegendRow(FindBrush("HudBlue"), "Azul", "Valor bajo.");
                yield return new ColorLegendRow(FindBrush("HudGreenSoft"), "Verde", "Zona sana/baja segun la metrica.");
                yield return new ColorLegendRow(FindBrush("HudYellow"), "Amarillo", "Zona media o de atencion.");
                yield return new ColorLegendRow(FindBrush("HudOrange"), "Naranja", "Valor alto.");
                yield return new ColorLegendRow(FindBrush("HudRedSoft"), "Rojo suave", "Valor muy alto o alerta.");
                yield return new ColorLegendRow(FindBrush("HudRed"), "Rojo", "Extremo. En FvCBet y W$SD la escala se interpreta con cuidado.");
            }

            private static IEnumerable<string> BuildNotes()
            {
                yield return "Las lecturas en tiempo real son mas utiles cuando cada mesa tiene suficientes manos; con muestras pequenas los colores pueden cambiar rapido.";
                yield return "La fila del heroe permite comparar tu ritmo con el de los villanos activos en esa mesa.";
                yield return "Si una mesa no actualiza, revisa que el archivo .txt sea el correcto y que la sala este escribiendo historial.";
                yield return "Los porcentajes se recalculan mientras entran nuevas manos al archivo.";
            }

            private static SolidColorBrush FindBrush(string key)
            {
                var obj = Application.Current.TryFindResource(key) as SolidColorBrush;
                return obj ?? new SolidColorBrush(Colors.Transparent);
            }
        }

        private sealed record HelpRow(string Name, string Description);
        private sealed record ColorLegendRow(Brush Brush, string Label, string Description);
    }
}

