using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hud.App.Services;

namespace Hud.App.Views
{
    /// <summary>
    /// Nota y color de un jugador ("farolea en el river", rojo). Se guarda en player-notes.json y se
    /// ve en su cuadro del HUD de la mesa.
    /// </summary>
    public sealed class PlayerNoteWindow : Window
    {
        private readonly string _player;
        private readonly TextBox _note;
        private string _color;

        public PlayerNoteWindow(string player)
        {
            _player = player;
            var current = PlayerNotes.Get(player);
            _color = current?.Color ?? "";

            Title = string.Format(LocalizationManager.Text("Notes.Title"), player);
            Width = 460;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "Brush.Card");
            SetResourceReference(ForegroundProperty, "Brush.Text");

            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock { Text = player, FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) });

            panel.Children.Add(Label(LocalizationManager.Text("Notes.Note")));
            _note = new TextBox
            {
                Text = current?.Note ?? "",
                AcceptsReturn = false,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 56,
                FontSize = 14,
                Padding = new Thickness(6, 4, 6, 4)
            };
            System.Windows.Automation.AutomationProperties.SetName(_note, LocalizationManager.Text("Notes.Note"));
            panel.Children.Add(_note);

            panel.Children.Add(Label(LocalizationManager.Text("Notes.Color"), top: 12));
            var colors = new WrapPanel();
            colors.Children.Add(ColorChip("", LocalizationManager.Text("Notes.NoColor"), null));
            foreach (var (key, labelKey, color) in PlayerNotes.Palette)
                colors.Children.Add(ColorChip(key, LocalizationManager.Text(labelKey), color));
            panel.Children.Add(colors);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var save = new Button { Content = LocalizationManager.Text("Notes.Save"), Padding = new Thickness(16, 6, 16, 6), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            save.SetResourceReference(StyleProperty, "Btn.Accent");
            save.Click += (_, _) =>
            {
                PlayerNotes.Set(_player, _note.Text, _color);
                DialogResult = true;
            };
            var cancel = new Button { Content = LocalizationManager.Text("Notes.Cancel"), Padding = new Thickness(16, 6, 16, 6), IsCancel = true };
            buttons.Children.Add(save);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            Content = panel;
            Loaded += (_, _) => _note.Focus();
        }

        private static TextBlock Label(string text, double top = 0)
        {
            var label = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 4) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
            return label;
        }

        private RadioButton ColorChip(string key, string label, Color? color)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (color is { } fill)
                content.Children.Add(new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(fill), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            var chip = new RadioButton
            {
                Content = content,
                GroupName = "note-color",
                IsChecked = _color == key,
                Margin = new Thickness(0, 0, 12, 6),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            chip.SetResourceReference(ForegroundProperty, "Brush.Text");
            System.Windows.Automation.AutomationProperties.SetName(chip, label);
            chip.Checked += (_, _) => _color = key;
            return chip;
        }
    }
}
