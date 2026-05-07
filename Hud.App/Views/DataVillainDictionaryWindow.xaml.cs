using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Hud.App.Views
{
    public partial class DataVillainDictionaryWindow : Window
    {
        public DataVillainDictionaryWindow()
        {
            InitializeComponent();
            DataContext = DataVillainDictionaryViewModel.Build();
        }

        private sealed class DataVillainDictionaryViewModel
        {
            public IReadOnlyList<DictionaryTagRow> Tags { get; private init; } = Array.Empty<DictionaryTagRow>();
            public IReadOnlyList<ColorLegendRow> MetricColorRows { get; private init; } = Array.Empty<ColorLegendRow>();
            public IReadOnlyList<ColorLegendRow> RangeColorRows { get; private init; } = Array.Empty<ColorLegendRow>();
            public IReadOnlyList<MetricMeaningRow> MetricRows { get; private init; } = Array.Empty<MetricMeaningRow>();

            public static DataVillainDictionaryViewModel Build() =>
                new()
                {
                    Tags = BuildTags().ToList(),
                    MetricColorRows = BuildMetricColorRows().ToList(),
                    RangeColorRows = BuildRangeColorRows().ToList(),
                    MetricRows = BuildMetricRows().ToList()
                };

            private static IEnumerable<DictionaryTagRow> BuildTags()
            {
                yield return Neutral("Sin muestra", "Menos de 30 manos totales: lectura inicial con baja confiabilidad.");
                yield return Neutral("Fish", "Perfil base: VPIP muy alto, PFR bajo y agresion baja.");
                yield return Neutral("Maniac", "Perfil base: juega/sube demasiado o tiene agresion/3Bet muy altos.");
                yield return Neutral("Loose pasivo", "Perfil base: juega muchas manos, pero no sube lo suficiente.");
                yield return Neutral("LAG", "Perfil base: loose agresivo, rango amplio con mucha iniciativa.");
                yield return Neutral("TAG", "Perfil base: tight agresivo, rango ordenado y agresion sana.");
                yield return Neutral("Nit", "Perfil base: rango demasiado cerrado y PFR bajo.");
                yield return Neutral("Tight", "Perfil base: juega pocas manos y no abre demasiado.");
                yield return Neutral("Pasivo", "Perfil base: agresion postflop baja.");
                yield return Neutral("Regular", "Perfil base sin extremos claros.");

                yield return Neutral("Rival frecuente", "Tiene muchas manos contra el heroe; la lectura pesa mas que una muestra pequeña.");
                yield return Positive("Pierde vs Hero", "El resultado compartido favorece al heroe.");
                yield return Negative("Gana vs Hero", "El resultado compartido favorece al villano.");
                yield return Negative("Juega muchas manos", "VPIP alto: entra voluntariamente en demasiadas manos.");
                yield return Negative("Agresor", "AF o AFq altos: apuesta/sube mucho postflop.");
                yield return Negative("3Bet alto", "3Bet por encima del umbral: resube preflop con mucha frecuencia.");
                yield return Positive("Foldea mucho a CBet", "FvCB alto: abandona demasiados flops ante continuation bet.");
                yield return Negative("No foldea CBet", "FvCB bajo: paga/continua demasiado contra continuation bet.");
                yield return Negative("Va mucho a showdown", "WTSD alto: llega mucho a mostrar cartas; revisar si gana o pierde al showdown.");
                yield return Positive("Calling station", "VPIP alto, PFR bajo y agresion baja: paga mucho y presiona poco.");
                yield return Neutral("Roca", "Rango muy cerrado con muestra suficiente.");

                yield return Neutral("Amante premium", "Muchas cartas conocidas pertenecen al rango premium.");
                yield return Neutral("Manos bajas", "Muestra tendencia a mostrar o jugar manos bajas.");
                yield return Neutral("Suited connectors", "Muestra conectores suited con frecuencia.");
                yield return Neutral("Mixto", "Las cartas conocidas cubren varias categorias de rango.");
                yield return Negative("Trampero", "Manos fuertes conocidas con agresion tardia en turn o river.");
                yield return Negative("All-in equity", "All-ins conocidos con rangos fuertes o conectados.");
                yield return Negative("Color lover", "Gana muchas bb conectando color.");
                yield return Negative("Set lover", "Gana muchas bb ligando set con par en mano.");
                yield return Negative("Trips lover", "Gana muchas bb conectando trips con una carta en mano y par en mesa.");
                yield return Negative("Double par", "Gana muchas bb conectando doble par.");
                yield return Negative("Par alto", "Gana muchas bb con par alto.");
                yield return Negative("Escalera lover", "Gana muchas bb conectando escalera.");
            }

            private static IEnumerable<ColorLegendRow> BuildMetricColorRows()
            {
                yield return new ColorLegendRow(FindBrush("BgDark"), "Neutro", "Menos de 30 manos o dato insuficiente.");
                yield return new ColorLegendRow(FindBrush("HudBlueDark"), "Azul oscuro", "Valor muy bajo para la metrica.");
                yield return new ColorLegendRow(FindBrush("HudBlue"), "Azul", "Valor bajo.");
                yield return new ColorLegendRow(FindBrush("HudGreenSoft"), "Verde", "Zona sana/baja segun la metrica.");
                yield return new ColorLegendRow(FindBrush("HudYellow"), "Amarillo", "Zona media o de atencion.");
                yield return new ColorLegendRow(FindBrush("HudOrange"), "Naranja", "Valor alto.");
                yield return new ColorLegendRow(FindBrush("HudRedSoft"), "Rojo suave", "Valor muy alto o alerta.");
                yield return new ColorLegendRow(FindBrush("HudRed"), "Rojo", "Extremo. En FvCBet y W$SD la escala se invierte.");
            }

            private static IEnumerable<ColorLegendRow> BuildRangeColorRows()
            {
                yield return new ColorLegendRow(BrushFrom(80, 86, 96), "Fold", "El villano foldeo con esa combinacion.");
                yield return new ColorLegendRow(BrushFrom(75, 102, 122), "Check", "El villano paso accion.");
                yield return new ColorLegendRow(BrushFrom(0, 148, 198), "Call", "El villano pago.");
                yield return new ColorLegendRow(BrushFrom(64, 184, 4), "Bet", "El villano aposto.");
                yield return new ColorLegendRow(BrushFrom(184, 181, 4), "Raise", "El villano subio.");
                yield return new ColorLegendRow(BrushFrom(226, 137, 0), "3Bet", "El villano hizo una resubida preflop.");
                yield return new ColorLegendRow(BrushFrom(255, 115, 115), "4Bet+", "El villano hizo 4Bet o mas.");
                yield return new ColorLegendRow(BrushFrom(156, 0, 0), "All-in", "El villano termino all-in.");
                yield return new ColorLegendRow(BrushFrom(33, 192, 122), "Ganancia bb", "Con Color por ganancia bb activo, verde indica resultado favorable para el villano en esa celda.");
                yield return new ColorLegendRow(BrushFrom(226, 78, 91), "Perdida bb", "Con Color por ganancia bb activo, rojo indica resultado negativo para el villano en esa celda.");
            }

            private static IEnumerable<MetricMeaningRow> BuildMetricRows()
            {
                yield return new MetricMeaningRow("VPIP%", "Porcentaje de manos que el jugador entra voluntariamente preflop.");
                yield return new MetricMeaningRow("PFR%", "Porcentaje de manos que el jugador sube preflop.");
                yield return new MetricMeaningRow("3Bet%", "Frecuencia con la que resube preflop ante una subida.");
                yield return new MetricMeaningRow("AF", "Aggression Factor: relacion entre apuestas/subidas y calls postflop.");
                yield return new MetricMeaningRow("AFq%", "Aggression Frequency: porcentaje de oportunidades postflop en que elige una accion agresiva.");
                yield return new MetricMeaningRow("CBet%", "Continuation bet flop: apuesta flop despues de ser agresor preflop.");
                yield return new MetricMeaningRow("FvCBet%", "Fold vs CBet flop. Esta metrica es invertida: demasiado alto se pinta como alerta/explotable.");
                yield return new MetricMeaningRow("WTSD%", "Went to Showdown: frecuencia con la que llega a showdown tras ver flop.");
                yield return new MetricMeaningRow("W$SD%", "Won Money at Showdown. Esta metrica es invertida en color: bajo es alerta, alto es mejor.");
                yield return new MetricMeaningRow("WWSF%", "Won When Saw Flop: frecuencia con la que gana la mano cuando vio flop.");
                yield return new MetricMeaningRow("Tablas del villano", "Cada celda muestra combo y veces vistas. Las pestanas separan PRE-FLOP, FLOP, TURN y RIVER; al seleccionar una celda aparecen acciones y manos exactas.");
            }

            private static DictionaryTagRow Positive(string text, string description) =>
                new(text, description, BrushFrom(16, 76, 52), BrushFrom(33, 192, 122), Brushes.White);

            private static DictionaryTagRow Negative(string text, string description) =>
                new(text, description, BrushFrom(98, 21, 32), BrushFrom(226, 78, 91), Brushes.White);

            private static DictionaryTagRow Neutral(string text, string description) =>
                new(text, description, BrushFrom(28, 40, 54), BrushFrom(86, 108, 132), Brushes.White);

            private static SolidColorBrush FindBrush(string key)
            {
                var obj = Application.Current.TryFindResource(key) as SolidColorBrush;
                return obj ?? new SolidColorBrush(Colors.Transparent);
            }

            private static Brush BrushFrom(byte r, byte g, byte b) =>
                new SolidColorBrush(Color.FromRgb(r, g, b));
        }

        private sealed record DictionaryTagRow(
            string Text,
            string Description,
            Brush Background,
            Brush Border,
            Brush Foreground);

        private sealed record ColorLegendRow(Brush Brush, string Label, string Description);
        private sealed record MetricMeaningRow(string Name, string Description);
    }
}
