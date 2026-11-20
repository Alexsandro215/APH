using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Hud.App.Services;

namespace Hud.App.Views
{
    /// <summary>
    /// Indicador de carga de APH: la pica del logo latiendo dentro de un arco que gira, con el color
    /// del tema. Con las animaciones apagadas el arco sigue girando (indica que APH trabaja), pero la
    /// pica queda quieta.
    /// </summary>
    public sealed class SpadeLoader : Grid
    {
        /// <summary>La misma pica del logo de la barra lateral.</summary>
        public const string SpadeData =
            "M12,1 C12,1 2.5,8.2 2.5,13.6 C2.5,16.9 4.8,19.1 7.8,19.1 C9.4,19.1 10.7,18.4 11.3,17.3 " +
            "C11.1,19.6 10,21.4 8.3,23 L15.7,23 C14,21.4 12.9,19.6 12.7,17.3 C13.3,18.4 14.6,19.1 16.2,19.1 " +
            "C19.2,19.1 21.5,16.9 21.5,13.6 C21.5,8.2 12,1 12,1 Z";

        public SpadeLoader() : this(84)
        {
        }

        public SpadeLoader(double size)
        {
            Width = size;
            Height = size;

            var track = new Ellipse { StrokeThickness = 3, Opacity = 0.25 };
            track.SetResourceReference(Shape.StrokeProperty, "Brush.Border");
            Children.Add(track);

            // Arco de un cuarto de vuelta que gira alrededor de la pica.
            var radius = size / 2 - 2;
            var arc = new Path
            {
                StrokeThickness = 3,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = new PathGeometry(new[]
                {
                    new PathFigure(new Point(size / 2, 2), new PathSegment[]
                    {
                        new ArcSegment(new Point(size - 2, size / 2), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true)
                    }, false)
                }),
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            arc.SetResourceReference(Shape.StrokeProperty, "Brush.Accent");
            var spin = new RotateTransform();
            arc.RenderTransform = spin;
            Children.Add(arc);

            var spade = new Path
            {
                Data = Geometry.Parse(SpadeData),
                Stretch = Stretch.Uniform,
                Width = size * 0.38,
                Height = size * 0.42,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.55)
            };
            spade.SetResourceReference(Shape.FillProperty, "Brush.Accent");
            var beat = new ScaleTransform(1, 1);
            spade.RenderTransform = beat;
            Children.Add(spade);

            // Solo gira mientras se ve: oculta (capa de carga plegada) no gasta procesador.
            void Start()
            {
                spin.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
                if (!UiMotion.Enabled)
                    return;
                var pulse = new DoubleAnimation(0.9, 1.06, TimeSpan.FromMilliseconds(620))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                beat.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
                beat.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
            }

            void Stop()
            {
                spin.BeginAnimation(RotateTransform.AngleProperty, null);
                beat.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                beat.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }

            IsVisibleChanged += (_, e) =>
            {
                if ((bool)e.NewValue)
                    Start();
                else
                    Stop();
            };
            Unloaded += (_, _) => Stop();
        }
    }
}
