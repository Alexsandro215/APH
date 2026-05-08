using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Hud.App.Views
{
    public partial class HeroProfileDictionaryWindow : Window
    {
        public HeroProfileDictionaryWindow()
        {
            InitializeComponent();
            DataContext = HeroProfileDictionaryViewModel.Build();
        }

        private sealed class HeroProfileDictionaryViewModel
        {
            public IReadOnlyList<DictionaryTagRow> Tags { get; private init; } = Array.Empty<DictionaryTagRow>();
            public IReadOnlyList<ColorLegendRow> ColorRows { get; private init; } = Array.Empty<ColorLegendRow>();
            public IReadOnlyList<MetricMeaningRow> MetricRows { get; private init; } = Array.Empty<MetricMeaningRow>();

            public static HeroProfileDictionaryViewModel Build() =>
                new()
                {
                    Tags = BuildTags().ToList(),
                    ColorRows = BuildColorRows().ToList(),
                    MetricRows = BuildMetricRows().ToList()
                };

            private static IEnumerable<DictionaryTagRow> BuildTags()
            {
                yield return Neutral("Fish", "Perfil base: VPIP muy alto, PFR bajo y agresion baja.");
                yield return Neutral("Maniac", "Perfil base: juega/sube demasiado o tiene agresion/3Bet muy altos.");
                yield return Neutral("Loose pasivo", "Perfil base: juega muchas manos, pero no sube lo suficiente.");
                yield return Neutral("LAG", "Perfil base: loose agresivo, rango amplio con mucha iniciativa.");
                yield return Neutral("TAG", "Perfil base: tight agresivo, rango ordenado y agresion sana.");
                yield return Neutral("Nit", "Perfil base: rango demasiado cerrado y PFR bajo.");
                yield return Neutral("Tight", "Perfil base: juega pocas manos y no abre demasiado.");
                yield return Neutral("Pasivo", "Perfil base: agresion postflop baja.");
                yield return Neutral("Regular", "Perfil base sin extremos claros.");
                yield return Neutral("Sin muestra", "Menos de 30 manos: los colores y lecturas todavia son poco confiables.");

                yield return Negative("Juega muchas manos", "VPIP alto: entra voluntariamente en demasiadas manos.");
                yield return Negative("Agresor", "AF o AFq altos: apuesta/sube mucho postflop.");
                yield return Negative("3Bet alto", "3Bet por encima del umbral: resube preflop con mucha frecuencia.");
                yield return Positive("Foldea mucho a CBet", "FvCB alto: abandona demasiados flops ante continuation bet.");
                yield return Negative("No foldea CBet", "FvCB bajo: paga/continua demasiado contra continuation bet.");
                yield return Negative("Va mucho a showdown", "WTSD alto: llega mucho a mostrar cartas; revisar calls tardios si W$SD es bajo.");
                yield return Positive("Calling station", "VPIP alto, PFR bajo y agresion baja: paga mucho y presiona poco.");
                yield return Neutral("Roca", "Rango muy cerrado con muestra suficiente.");

                yield return Positive("Loose", "VPIP alto: juega muchas manos voluntariamente.");
                yield return Neutral("Tight", "VPIP bajo: selecciona mucho sus manos.");
                yield return Neutral("Rango medio", "VPIP sin extremo claro.");
                yield return Neutral("Agresivo preflop", "PFR alto: toma mucha iniciativa antes del flop.");
                yield return Neutral("PFR bajo", "PFR bajo: sube poco en comparacion con la muestra.");
                yield return Neutral("PFR estable", "PFR dentro de un rango medio.");
                yield return Negative("Overfold vs CBet", "Fold vs CBet alto: abandona demasiados flops ante apuesta de continuacion.");
                yield return Neutral("Defensa vs CBet ok", "Fold vs CBet sin alerta fuerte.");
                yield return Neutral("CBet frecuente", "CBet flop alta: apuesta mucho cuando fue agresor preflop.");
                yield return Neutral("CBet selectiva", "CBet flop moderada/baja: elige mas sus continuation bets.");
                yield return Positive("Showdown fuerte", "W$SD alto: gana bastante cuando llega a showdown.");
                yield return Negative("Showdown a revisar", "W$SD bajo o medio: revisar manos que llegan a showdown.");

                yield return Negative("Leak <posicion>", "Marca la posicion con peor resultado total en bb.");
                yield return CardNeutral("AKs", "lover", "El combo voluntario mas frecuente se muestra como cartas + lover. La descripcion indica frecuencia y uso.");
                yield return Neutral("Amante premium", "Muchos combos frecuentes pertenecen al rango premium.");
                yield return Neutral("Manos bajas", "Muchas manos frecuentes tienen dos rangos bajos.");
                yield return Neutral("Suited connectors", "Juega conectores suited con frecuencia.");
                yield return Neutral("Mixto", "Los combos frecuentes cubren varias categorias: premium, bajas, suited, offsuit, suited.");
            }

            private static IEnumerable<ColorLegendRow> BuildColorRows()
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

            private static IEnumerable<MetricMeaningRow> BuildMetricRows()
            {
                yield return new MetricMeaningRow("VPIP%", "Porcentaje de manos que juegas voluntariamente preflop.");
                yield return new MetricMeaningRow("PFR%", "Porcentaje de manos que subes preflop.");
                yield return new MetricMeaningRow("3Bet%", "Frecuencia con la que resubes preflop ante una subida.");
                yield return new MetricMeaningRow("AF", "Aggression Factor: relacion entre apuestas/subidas y calls postflop.");
                yield return new MetricMeaningRow("AFq%", "Aggression Frequency: porcentaje de oportunidades postflop en que eliges una accion agresiva.");
                yield return new MetricMeaningRow("CBet%", "Continuation bet flop: apuestas flop despues de ser agresor preflop.");
                yield return new MetricMeaningRow("FvCBet%", "Fold vs CBet flop. Esta metrica es invertida: demasiado alto se pinta como alerta.");
                yield return new MetricMeaningRow("WTSD%", "Went to Showdown: frecuencia con la que llegas a showdown tras ver flop.");
                yield return new MetricMeaningRow("W$SD%", "Won Money at Showdown. Esta metrica es invertida en color: bajo es alerta, alto es mejor.");
                yield return new MetricMeaningRow("WWSF%", "Won When Saw Flop: frecuencia con la que ganas la mano cuando viste flop.");
            }

            private static DictionaryTagRow Positive(string text, string description) =>
                new(text, description, BrushFrom(16, 76, 52), BrushFrom(33, 192, 122), Brushes.White, Array.Empty<CardChipViewModel>());

            private static DictionaryTagRow Negative(string text, string description) =>
                new(text, description, BrushFrom(98, 21, 32), BrushFrom(226, 78, 91), Brushes.White, Array.Empty<CardChipViewModel>());

            private static DictionaryTagRow Neutral(string text, string description) =>
                new(text, description, BrushFrom(28, 40, 54), BrushFrom(86, 108, 132), Brushes.White, Array.Empty<CardChipViewModel>());

            private static DictionaryTagRow CardNeutral(string combo, string text, string description) =>
                new(text, description, BrushFrom(28, 40, 54), BrushFrom(86, 108, 132), Brushes.White, CardChipViewModel.FromCards(ComboToCards(combo)));

            private static string ComboToCards(string combo)
            {
                if (string.IsNullOrWhiteSpace(combo) || combo.Length < 2)
                    return "";

                var first = combo[0];
                var second = combo[1];
                if (first == second)
                    return $"{first}h {second}d";

                return combo.EndsWith("s", StringComparison.Ordinal)
                    ? $"{first}h {second}h"
                    : $"{first}h {second}d";
            }

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
            Brush Foreground,
            IReadOnlyList<CardChipViewModel> CardChips);

        private sealed record ColorLegendRow(Brush Brush, string Label, string Description);
        private sealed record MetricMeaningRow(string Name, string Description);
    }
}

