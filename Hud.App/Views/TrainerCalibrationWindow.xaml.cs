using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Hud.App.Services.Trainer;

namespace Hud.App.Views
{
    /// <summary>
    /// Calibracion del lector: sobre una captura de la mesa el usuario marca las 2 cartas del heroe
    /// y las 5 del board. Las zonas se guardan en fracciones de la mesa.
    /// </summary>
    public partial class TrainerCalibrationWindow : Window
    {
        private static readonly Brush HeroBrush = Frozen(Color.FromRgb(48, 217, 139));
        private static readonly Brush BoardBrush = Frozen(Color.FromRgb(227, 179, 65));

        private readonly IntPtr _table;
        private readonly TrainerCardReader _reader;
        private readonly TrainerCalibration _working;
        private readonly List<SlotItem> _slots;
        private CapturedFrame? _frame;
        private Point? _dragStart;
        private Rectangle? _dragShape;

        public TrainerCalibrationWindow(IntPtr table, TrainerCalibration current, TrainerCardReader reader)
        {
            InitializeComponent();
            _table = table;
            _reader = reader;
            _working = current.Clone();
            _slots = Enumerable.Range(0, TrainerCalibration.SlotCount).Select(index => new SlotItem(index, _working)).ToList();
            SlotsList.ItemsSource = _slots;
            SlotsList.SelectedIndex = 0;
            SlotsList.SelectionChanged += (_, _) => Redraw();
            Loaded += (_, _) =>
            {
                Capture();
                if (!_working.HasHero)
                    AutoCalibrate();
            };
        }

        private void BtnAuto_Click(object sender, RoutedEventArgs e) => AutoCalibrate();

        private void AutoCalibrate()
        {
            if (_frame is null)
                return;

            var located = CardLocator.Locate(_frame);
            AutoResult.Text = located.Report;
            if (located.Calibration is null)
                return;

            // Sin las cartas del heroe a la vista se conserva lo que hubiera marcado para ellas.
            for (var slot = 0; slot < TrainerCalibration.SlotCount; slot++)
            {
                if (located.Calibration.Slots[slot].IsValid)
                    _working.Slots[slot] = located.Calibration.Slots[slot];
            }

            RefreshSlotLabels();
            Redraw();
        }

        public TrainerCalibration? Result { get; private set; }

        private void BtnCapture_Click(object sender, RoutedEventArgs e) => Capture();

        private void Capture()
        {
            _frame = TableCapture.CaptureClient(_table);
            if (_frame is null)
            {
                TestResult.Text = "No se pudo capturar la mesa. Comprueba que no este minimizada.";
                return;
            }

            TableImage.Source = _frame.ToBitmapSource();
            Surface.Width = _frame.Width;
            Surface.Height = _frame.Height;
            Overlay.Width = _frame.Width;
            Overlay.Height = _frame.Height;
            Redraw();
        }

        // ------------------------------------------------------------------ dibujo de zonas

        private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_frame is null || SlotsList.SelectedIndex < 0)
                return;

            _dragStart = e.GetPosition(Overlay);
            _dragShape = new Rectangle
            {
                Stroke = Brushes.White,
                StrokeThickness = Math.Max(1, _frame.Width / 500.0),
                StrokeDashArray = new DoubleCollection { 3, 2 }
            };
            Overlay.Children.Add(_dragShape);
            Overlay.CaptureMouse();
            UpdateDragShape(_dragStart.Value);
        }

        private void Overlay_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragStart is not null)
                UpdateDragShape(e.GetPosition(Overlay));
        }

        private void Overlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragStart is not { } start || _frame is null)
                return;

            Overlay.ReleaseMouseCapture();
            var end = e.GetPosition(Overlay);
            _dragStart = null;
            _dragShape = null;

            var left = Math.Clamp(Math.Min(start.X, end.X), 0, _frame.Width);
            var top = Math.Clamp(Math.Min(start.Y, end.Y), 0, _frame.Height);
            var right = Math.Clamp(Math.Max(start.X, end.X), 0, _frame.Width);
            var bottom = Math.Clamp(Math.Max(start.Y, end.Y), 0, _frame.Height);
            if (right - left < 4 || bottom - top < 4)
            {
                Redraw();
                return;
            }

            var index = SlotsList.SelectedIndex;
            _working.Slots[index] = new NormalizedRect(
                left / _frame.Width,
                top / _frame.Height,
                (right - left) / _frame.Width,
                (bottom - top) / _frame.Height);

            RefreshSlotLabels();
            // Pasa sola a la siguiente zona para marcar las 7 seguidas.
            SlotsList.SelectedIndex = Math.Min(index + 1, TrainerCalibration.SlotCount - 1);
            Redraw();
        }

        private void UpdateDragShape(Point current)
        {
            if (_dragStart is not { } start || _dragShape is null)
                return;

            Canvas.SetLeft(_dragShape, Math.Min(start.X, current.X));
            Canvas.SetTop(_dragShape, Math.Min(start.Y, current.Y));
            _dragShape.Width = Math.Abs(current.X - start.X);
            _dragShape.Height = Math.Abs(current.Y - start.Y);
        }

        private void Redraw()
        {
            Overlay.Children.Clear();
            if (_frame is null)
                return;

            var thickness = Math.Max(1.5, _frame.Width / 420.0);
            var fontSize = Math.Max(10, _frame.Width / 70.0);
            for (var i = 0; i < TrainerCalibration.SlotCount; i++)
            {
                var rect = _working.Slots[i];
                if (!rect.IsValid)
                    continue;

                var brush = i < 2 ? HeroBrush : BoardBrush;
                var shape = new Rectangle
                {
                    Width = rect.W * _frame.Width,
                    Height = rect.H * _frame.Height,
                    Stroke = brush,
                    StrokeThickness = i == SlotsList.SelectedIndex ? thickness * 2 : thickness
                };
                Canvas.SetLeft(shape, rect.X * _frame.Width);
                Canvas.SetTop(shape, rect.Y * _frame.Height);
                Overlay.Children.Add(shape);

                var label = new TextBlock
                {
                    Text = i < 2 ? $"H{i + 1}" : $"B{i - 1}",
                    Foreground = brush,
                    FontSize = fontSize,
                    FontWeight = FontWeights.Bold,
                    TextWrapping = TextWrapping.NoWrap
                };
                Canvas.SetLeft(label, rect.X * _frame.Width);
                Canvas.SetTop(label, Math.Max(0, rect.Y * _frame.Height - fontSize * 1.4));
                Overlay.Children.Add(label);
            }
        }

        private void BtnClearSlot_Click(object sender, RoutedEventArgs e)
        {
            if (SlotsList.SelectedIndex < 0)
                return;

            _working.Slots[SlotsList.SelectedIndex] = default;
            RefreshSlotLabels();
            Redraw();
        }

        private void RefreshSlotLabels()
        {
            var selected = SlotsList.SelectedIndex;
            SlotsList.ItemsSource = null;
            SlotsList.ItemsSource = _slots;
            SlotsList.SelectedIndex = selected;
        }

        // ------------------------------------------------------------------ prueba y ensenanza

        private void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            if (_frame is null)
                return;

            // Lector temporal con la calibracion en edicion y las plantillas reales. Dos lecturas
            // porque el lector exige dos iguales seguidas antes de dar una carta por buena.
            var probe = new TrainerCardReader(_working, _reader.Store);
            probe.Observe(_frame);
            var reading = probe.Observe(_frame);

            var text = new StringBuilder();
            for (var i = 0; i < TrainerCalibration.SlotCount; i++)
            {
                var name = TrainerCalibration.SlotNames[i].PadRight(14);
                if (!_working.Slots[i].IsValid)
                {
                    text.AppendLine($"{name} sin zona");
                    continue;
                }

                var slot = reading.Slots[i];
                var match = reading.Matches[i];
                var state = slot is null
                    ? "vacia"
                    : slot == "?"
                        ? $"carta sin reconocer{(match.Card is null ? "" : $" (parece {match.Card})")}"
                        : $"{slot} ({match.Method}, d={match.Distance.ToString("0.00", CultureInfo.InvariantCulture)})";
                text.AppendLine($"{name} {state}  blanco {reading.WhiteFractions[i]:P0}");
            }

            text.AppendLine();
            text.Append($"Plantillas: {_reader.Store.LearnedCards}/52 cartas, {_reader.Store.LearnedRanks}/13 rangos, {_reader.Store.LearnedSuits}/4 palos.");
            TestResult.Text = text.ToString();
        }

        private void BtnTeach_Click(object sender, RoutedEventArgs e)
        {
            if (_frame is null)
                return;

            var hero = TrainerCards.ParseMany(TeachHero.Text);
            var board = TrainerCards.ParseMany(TeachBoard.Text);
            if (hero.Count == 0 && board.Count == 0)
            {
                TeachResult.Text = "Escribe las cartas como 'Ah Kd' (rango: 2-9, T, J, Q, K, A; palo: h, d, c, s).";
                return;
            }

            var probe = new TrainerCardReader(_working, _reader.Store);
            var (taught, skipped) = probe.Teach(_frame, TeachHero.Text, TeachBoard.Text);
            var skippedText = skipped.Count == 0
                ? ""
                : $" {string.Join(", ", skipped)}: no hay carta en la captura, no se aprendieron.";
            TeachResult.Text = taught == 0
                ? "No se aprendio nada: revisa que las zonas esten marcadas y que las cartas se vean en la captura." + skippedText
                : $"Aprendidas {taught} carta(s). Ahora: {_reader.Store.LearnedCards}/52 cartas." + skippedText;
        }

        private void BtnForget_Click(object sender, RoutedEventArgs e)
        {
            var answer = MessageBox.Show(
                this,
                "Se borraran todas las cartas aprendidas y el lector volvera a aprender desde cero. Continuar?",
                "Borrar lo aprendido",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
                return;

            _reader.Store.Clear();
            TeachResult.Text = "Plantillas borradas.";
        }

        // ------------------------------------------------------------------ cierre

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (!_working.HasHero)
            {
                MessageBox.Show(this, "Marca al menos las dos cartas del heroe.", "Calibracion", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_frame is not null)
            {
                _working.ClientWidth = _frame.Width;
                _working.ClientHeight = _frame.Height;
            }

            Result = _working;
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private sealed class SlotItem
        {
            private readonly TrainerCalibration _calibration;

            public SlotItem(int index, TrainerCalibration calibration)
            {
                Index = index;
                _calibration = calibration;
            }

            public int Index { get; }

            public string Label =>
                $"{TrainerCalibration.SlotNames[Index]}  -  {(_calibration.Slots[Index].IsValid ? "marcada" : "sin marcar")}";
        }
    }
}
