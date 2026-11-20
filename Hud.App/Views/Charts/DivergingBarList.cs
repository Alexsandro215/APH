using Hud.App.Services;
using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Hud.App.Views.Charts
{
    /// <summary>Una barra: nombre, valor con signo y detalle para el tooltip.</summary>
    public sealed record BarDatum(
        string Label,
        double Value,
        string ValueText,
        string? Sublabel = null,
        string? Detail = null,
        object? Payload = null);

    /// <summary>
    /// Lista de barras divergentes alrededor de cero: lo ganado sale a la derecha en verde y lo
    /// perdido a la izquierda en rojo, ordenado para que se lea de un vistazo donde se gana y
    /// donde se pierde. Si todos los valores son positivos se dibuja como barras normales.
    /// Pasa el raton por una barra para ver el detalle; clic para abrirla (si la pagina lo usa).
    /// </summary>
    public sealed class DivergingBarList : UserControl
    {
        public static readonly DependencyProperty ItemsProperty =
            DependencyProperty.Register(nameof(Items), typeof(IEnumerable), typeof(DivergingBarList),
                new PropertyMetadata(null, (d, _) => ((DivergingBarList)d).Rebuild()));

        public static readonly DependencyProperty LabelWidthProperty =
            DependencyProperty.Register(nameof(LabelWidth), typeof(double), typeof(DivergingBarList),
                new PropertyMetadata(150.0, (d, _) => ((DivergingBarList)d).Rebuild()));

        public static readonly DependencyProperty EmptyTextProperty =
            DependencyProperty.Register(nameof(EmptyText), typeof(string), typeof(DivergingBarList),
                new PropertyMetadata("Sin datos", (d, _) => ((DivergingBarList)d).Rebuild()));

        /// <summary>Cantidades (no ganar/perder): una sola tonalidad azul, sin verde ni rojo.</summary>
        public static readonly DependencyProperty IsMagnitudeProperty =
            DependencyProperty.Register(nameof(IsMagnitude), typeof(bool), typeof(DivergingBarList),
                new PropertyMetadata(false, (d, _) => ((DivergingBarList)d).Rebuild()));

        public static readonly RoutedEvent ItemClickedEvent =
            EventManager.RegisterRoutedEvent(nameof(ItemClicked), RoutingStrategy.Bubble,
                typeof(RoutedEventHandler), typeof(DivergingBarList));

        // Verde y rojo validados para fondo oscuro: se distinguen tambien con daltonismo
        // (deuteranopia, dE 18.7) porque difieren en luminosidad; ademas el lado de la barra y
        // el signo del valor repiten la informacion.
        public static readonly Color PositiveColor = Color.FromRgb(0x3C, 0xC4, 0x8A);
        public static readonly Color NegativeColor = Color.FromRgb(0xB8, 0x3A, 0x4B);

        // Azul para cantidades, validado contra el fondo oscuro (luminosidad, croma y contraste).
        public static readonly Color MagnitudeColor = Color.FromRgb(0x4F, 0x8F, 0xD6);

        private readonly StackPanel _rows = new();

        public DivergingBarList()
        {
            Content = _rows;
        }

        public IEnumerable? Items
        {
            get => (IEnumerable?)GetValue(ItemsProperty);
            set => SetValue(ItemsProperty, value);
        }

        public double LabelWidth
        {
            get => (double)GetValue(LabelWidthProperty);
            set => SetValue(LabelWidthProperty, value);
        }

        public bool IsMagnitude
        {
            get => (bool)GetValue(IsMagnitudeProperty);
            set => SetValue(IsMagnitudeProperty, value);
        }

        public string EmptyText
        {
            get => (string)GetValue(EmptyTextProperty);
            set => SetValue(EmptyTextProperty, value);
        }

        /// <summary>Clic en una barra; el BarDatum va en RoutedEventArgs.OriginalSource... ver <see cref="LastClicked"/>.</summary>
        public event RoutedEventHandler ItemClicked
        {
            add => AddHandler(ItemClickedEvent, value);
            remove => RemoveHandler(ItemClickedEvent, value);
        }

        public BarDatum? LastClicked { get; private set; }

        private void Rebuild()
        {
            _rows.Children.Clear();
            var items = Items?.OfType<BarDatum>().ToList() ?? new List<BarDatum>();

            if (items.Count == 0)
            {
                var empty = new TextBlock { Text = EmptyText, Margin = new Thickness(0, 6, 0, 6), FontSize = 12 };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
                _rows.Children.Add(empty);
                return;
            }

            var maxPositive = items.Max(item => Math.Max(0, item.Value));
            var maxNegative = items.Max(item => Math.Max(0, -item.Value));
            var scale = Math.Max(maxPositive, maxNegative);
            if (scale <= 0)
                scale = 1;

            // Mitad negativa solo si hay valores negativos (y viceversa): con todo positivo las
            // barras arrancan desde la izquierda y aprovechan todo el ancho.
            var negativeShare = maxNegative > 0 ? (maxPositive > 0 ? maxNegative : scale) : 0;
            var positiveShare = maxPositive > 0 ? (maxNegative > 0 ? maxPositive : scale) : 0;

            _rowIndex = 0;
            foreach (var item in items)
            {
                _rows.Children.Add(BuildRow(item, negativeShare, positiveShare));
                _rowIndex++;
            }
        }

        // Fila que se esta armando: las barras crecen una tras otra, de arriba abajo.
        private int _rowIndex;

        private FrameworkElement BuildRow(BarDatum item, double negativeShare, double positiveShare)
        {
            var negative = Math.Max(0, -item.Value);
            var positive = Math.Max(0, item.Value);

            var row = new Grid
            {
                Background = Brushes.Transparent,
                Margin = new Thickness(0, 0, 0, 4),
                MinHeight = 28,
                Cursor = Cursors.Hand
            };
            AutomationPropertiesHelper.SetName(row, $"{item.Label}: {item.ValueText}");

            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = Star(negativeShare - negative) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = Star(negative) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(negativeShare > 0 && positiveShare > 0 ? 1 : 0) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = Star(positive) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = Star(positiveShare - positive) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });

            var hover = new Border { CornerRadius = new CornerRadius(6), Opacity = 0 };
            hover.SetResourceReference(Border.BackgroundProperty, "Brush.CardHover");
            Grid.SetColumnSpan(hover, 7);
            row.Children.Add(hover);

            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 10, 0) };
            var name = new TextBlock
            {
                Text = item.Label,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
            label.Children.Add(name);
            if (!string.IsNullOrWhiteSpace(item.Sublabel))
            {
                var sub = new TextBlock
                {
                    Text = item.Sublabel,
                    FontSize = 10.5,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap
                };
                sub.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
                label.Children.Add(sub);
            }
            row.Children.Add(label);

            if (negative > 0)
            {
                var bar = Bar(NegativeColor, new CornerRadius(4, 0, 0, 4));
                Grid.SetColumn(bar, 2);
                row.Children.Add(bar);
                UiMotion.GrowBar(bar, fromRight: true, _rowIndex);
            }

            if (negativeShare > 0 && positiveShare > 0)
            {
                var axis = new Border { Width = 1, Margin = new Thickness(0, 3, 0, 3) };
                axis.SetResourceReference(Border.BackgroundProperty, "Brush.HeaderBorder");
                Grid.SetColumn(axis, 3);
                row.Children.Add(axis);
            }

            if (positive > 0)
            {
                var bar = Bar(IsMagnitude ? MagnitudeColor : PositiveColor, new CornerRadius(0, 4, 4, 0));
                Grid.SetColumn(bar, 4);
                row.Children.Add(bar);
                UiMotion.GrowBar(bar, fromRight: false, _rowIndex);
            }

            if (negative <= 0 && positive <= 0)
            {
                // Valor cero: una marca gris pequena en el eje, sin color de ganar ni perder.
                var tick = new Border { Width = 4, Height = 12, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
                tick.SetResourceReference(Border.BackgroundProperty, "Brush.TextDim");
                Grid.SetColumn(tick, positiveShare > 0 ? 4 : 2);
                row.Children.Add(tick);
            }

            var value = new TextBlock
            {
                Text = item.ValueText,
                FontSize = 12.5,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 6, 0),
                TextWrapping = TextWrapping.NoWrap
            };
            value.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
            Grid.SetColumn(value, 6);
            row.Children.Add(value);

            if (!string.IsNullOrWhiteSpace(item.Detail))
            {
                row.ToolTip = new TextBlock { Text = item.Detail, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 };
                ToolTipService.SetInitialShowDelay(row, 150);
                ToolTipService.SetPlacement(row, System.Windows.Controls.Primitives.PlacementMode.Mouse);
            }

            row.MouseEnter += (_, _) => hover.Opacity = 1;
            row.MouseLeave += (_, _) => hover.Opacity = 0;
            row.MouseLeftButtonUp += (_, _) =>
            {
                LastClicked = item;
                RaiseEvent(new RoutedEventArgs(ItemClickedEvent, this));
            };

            return row;
        }

        private static Border Bar(Color color, CornerRadius radius)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return new Border
            {
                Background = brush,
                Height = 14,
                CornerRadius = radius,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private static GridLength Star(double value) => new(Math.Max(0.0001, value), GridUnitType.Star);
    }

    internal static class AutomationPropertiesHelper
    {
        public static void SetName(DependencyObject element, string name) =>
            System.Windows.Automation.AutomationProperties.SetName(element, name);
    }
}
