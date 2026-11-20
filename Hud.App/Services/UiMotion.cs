using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Hud.App.Services
{
    /// <summary>Cuanto se mueve la interfaz (Configuración > Apariencia).</summary>
    public enum MotionLevel
    {
        /// <summary>Transiciones, cifras que suben contando y curvas que se dibujan.</summary>
        Normal,
        /// <summary>Lo mismo, mas corto y sin desplazamientos: solo fundidos.</summary>
        Reduced,
        /// <summary>Todo aparece al instante.</summary>
        Off
    }

    /// <summary>
    /// Animaciones de la interfaz en un solo sitio, para que todas respeten el ajuste de movimiento:
    /// entrada de paginas, aparicion escalonada, barras que crecen, curvas que se dibujan y cifras que
    /// suben contando. Todas son cortas (menos de un segundo) y nunca bloquean: si algo se vuelve a
    /// dibujar a media animacion, la nueva sustituye a la anterior.
    /// </summary>
    public static class UiMotion
    {
        public static MotionLevel Level { get; set; } = MotionLevel.Normal;

        public static bool Enabled => Level != MotionLevel.Off;

        public static MotionLevel Parse(string? value) => value?.Trim().ToLowerInvariant() switch
        {
            "reduced" or "reducidas" => MotionLevel.Reduced,
            "off" or "apagadas" or "none" => MotionLevel.Off,
            _ => MotionLevel.Normal
        };

        private static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });

        private static IEasingFunction Freeze(EasingFunctionBase ease)
        {
            ease.Freeze();
            return ease;
        }

        /// <summary>Duracion ajustada al nivel: las reducidas duran algo mas de la mitad.</summary>
        private static Duration Ms(double ms) =>
            new(TimeSpan.FromMilliseconds(Level == MotionLevel.Reduced ? ms * 0.6 : ms));

        private static TimeSpan Delay(double ms) =>
            TimeSpan.FromMilliseconds(Level == MotionLevel.Reduced ? ms * 0.5 : ms);

        // ------------------------------------------------------------------ entradas

        /// <summary>Una pagina nueva: aparece con un fundido y sube unos pixeles.</summary>
        public static void EnterPage(FrameworkElement host)
        {
            if (!Enabled)
            {
                Stop(host);
                return;
            }

            FadeIn(host, 240);
            if (Level == MotionLevel.Normal)
                Slide(host, 0, 16, 300, 0);
        }

        /// <summary>Aparece con un fundido, opcionalmente despues de una espera.</summary>
        public static void FadeIn(UIElement element, double ms = 200, double delayMs = 0)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            if (!Enabled)
            {
                element.Opacity = 1;
                return;
            }

            element.Opacity = 0;
            var fade = new DoubleAnimation(0, 1, Ms(ms)) { EasingFunction = EaseOut, BeginTime = Delay(delayMs) };
            fade.Completed += (_, _) =>
            {
                element.Opacity = 1;
                element.BeginAnimation(UIElement.OpacityProperty, null);
            };
            element.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        /// <summary>Desaparece con un fundido y luego llama a <paramref name="done"/>.</summary>
        public static void FadeOut(UIElement element, double ms, Action done)
        {
            if (!Enabled)
            {
                done();
                return;
            }

            var fade = new DoubleAnimation(element.Opacity, 0, Ms(ms)) { EasingFunction = EaseOut };
            fade.Completed += (_, _) => done();
            element.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        /// <summary>Los hijos de un panel aparecen uno tras otro (como cartas repartidas).</summary>
        public static void Stagger(Panel panel, double stepMs = 40, int maxItems = 14)
        {
            if (!Enabled)
                return;

            var index = 0;
            foreach (UIElement child in panel.Children)
            {
                var delay = Math.Min(index, maxItems) * stepMs;
                FadeIn(child, 260, delay);
                if (Level == MotionLevel.Normal && child is FrameworkElement element)
                    Slide(element, 0, 10, 300, delay);
                index++;
            }
        }

        /// <summary>Entrada con un pequeño rebote de tamaño (avisos, insignias).</summary>
        public static void Pop(FrameworkElement element, double delayMs = 0)
        {
            FadeIn(element, 180, delayMs);
            if (Level != MotionLevel.Normal)
                return;

            var scale = OwnTransform<ScaleTransform>(element);
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            var grow = new DoubleAnimation(0.92, 1, Ms(260))
            {
                EasingFunction = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }),
                BeginTime = Delay(delayMs)
            };
            scale.ScaleX = scale.ScaleY = 0.92;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }

        /// <summary>Desplaza el elemento desde (x, y) hasta su sitio.</summary>
        public static void Slide(FrameworkElement element, double fromX, double fromY, double ms, double delayMs)
        {
            var move = OwnTransform<TranslateTransform>(element);
            move.BeginAnimation(TranslateTransform.XProperty, null);
            move.BeginAnimation(TranslateTransform.YProperty, null);
            move.X = 0;
            move.Y = 0;
            if (!Enabled)
                return;

            if (fromX != 0)
                move.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(fromX, 0, Ms(ms)) { EasingFunction = EaseOut, BeginTime = Delay(delayMs) });
            if (fromY != 0)
                move.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(fromY, 0, Ms(ms)) { EasingFunction = EaseOut, BeginTime = Delay(delayMs) });
            // Durante la espera el elemento ya esta en su sitio: se le pone el desplazamiento inicial.
            move.X = fromX;
            move.Y = fromY;
        }

        /// <summary>Una barra crece desde su base (la izquierda, o la derecha si es negativa).</summary>
        public static void GrowBar(FrameworkElement bar, bool fromRight, int index)
        {
            if (!Enabled)
                return;

            bar.RenderTransformOrigin = new Point(fromRight ? 1 : 0, 0.5);
            var scale = OwnTransform<ScaleTransform>(bar);
            scale.ScaleX = 0;
            var grow = new DoubleAnimation(0, 1, Ms(520))
            {
                EasingFunction = EaseOut,
                BeginTime = Delay(Math.Min(index, 16) * 30)
            };
            grow.Completed += (_, _) =>
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.ScaleX = 1;
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        }

        /// <summary>
        /// Una curva se dibuja de izquierda a derecha: se recorta y el recorte se abre hasta el ancho
        /// completo. <paramref name="startX"/> es donde empieza la zona de la grafica (tras la escala).
        /// </summary>
        public static void Reveal(UIElement element, double startX, double width, double height, double ms = 750, double delayMs = 0)
        {
            if (!Enabled || width <= startX || height <= 0)
                return;

            var clip = new RectangleGeometry(new Rect(0, -20, startX, height + 40));
            element.Clip = clip;
            var open = new RectAnimation(new Rect(0, -20, startX, height + 40), new Rect(0, -20, width + 20, height + 40), Ms(ms))
            {
                EasingFunction = EaseOut,
                BeginTime = Delay(delayMs)
            };
            open.Completed += (_, _) => element.Clip = null;
            clip.BeginAnimation(RectangleGeometry.RectProperty, open);
        }

        private static void Stop(UIElement element)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            if (element is FrameworkElement fe && fe.RenderTransform is TransformGroup group)
            {
                foreach (var transform in group.Children)
                {
                    transform.BeginAnimation(TranslateTransform.XProperty, null);
                    transform.BeginAnimation(TranslateTransform.YProperty, null);
                    if (transform is TranslateTransform t)
                        t.X = t.Y = 0;
                }
            }
        }

        /// <summary>
        /// Transformacion propia de las animaciones dentro de un grupo, para no pisar la que tuviera el
        /// elemento (una casilla girada, por ejemplo).
        /// </summary>
        private static T OwnTransform<T>(FrameworkElement element) where T : Transform, new()
        {
            if (element.RenderTransform is TransformGroup { IsFrozen: false } group && Owned.TryGetValue(group, out _))
            {
                if (group.Children.OfType<T>().FirstOrDefault() is { } existing)
                    return existing;
                var added = new T();
                group.Children.Add(added);
                return added;
            }

            var created = new T();
            var mine = new TransformGroup();
            Owned.AddOrUpdate(mine, MotionTag);
            var original = element.RenderTransform;
            if (original is not null && !ReferenceEquals(original, Transform.Identity))
                mine.Children.Add(original);
            mine.Children.Add(created);
            element.RenderTransform = mine;
            return created;
        }

        private static readonly object MotionTag = new();
        private static readonly ConditionalWeakTable<TransformGroup, object> Owned = new();

        // ------------------------------------------------------------------ cifras que suben contando

        /// <summary>
        /// En un TextBlock con su texto enlazado: cuando el texto cambia a otra cifra, la cifra sube (o
        /// baja) contando desde la anterior. El enlace se conserva: los pasos intermedios se ponen con
        /// SetCurrentValue y el ultimo es el texto real.
        /// </summary>
        public static readonly DependencyProperty CountUpProperty = DependencyProperty.RegisterAttached(
            "CountUp", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, OnCountUpChanged));

        public static bool GetCountUp(DependencyObject element) => (bool)element.GetValue(CountUpProperty);

        public static void SetCountUp(DependencyObject element, bool value) => element.SetValue(CountUpProperty, value);

        private sealed class CountState
        {
            public bool Writing;
            public double? Shown;
            public double Start;
            public double Target;
            public string Final = "";
            public DateTime Began;
            public double DurationMs;
        }

        private static readonly ConditionalWeakTable<TextBlock, CountState> CountStates = new();
        private static readonly List<WeakReference<TextBlock>> Counting = new();
        private static bool _renderingHooked;

        private static readonly System.ComponentModel.DependencyPropertyDescriptor TextDescriptor =
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

        private static void OnCountUpChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextBlock text)
                return;

            if ((bool)e.NewValue)
            {
                TextDescriptor.AddValueChanged(text, OnCountedTextChanged);
                CountStates.GetValue(text, _ => new CountState()).Shown = CountUpText.TryParse(text.Text, CultureInfo.CurrentCulture, out var v) ? v : null;
            }
            else
            {
                TextDescriptor.RemoveValueChanged(text, OnCountedTextChanged);
            }
        }

        private static void OnCountedTextChanged(object? sender, EventArgs e)
        {
            if (sender is not TextBlock text)
                return;

            var state = CountStates.GetValue(text, _ => new CountState());
            if (state.Writing)
                return;

            var final = text.Text ?? "";
            if (!CountUpText.TryParse(final, CultureInfo.CurrentCulture, out var target))
            {
                state.Shown = null;
                return;
            }

            var start = state.Shown ?? 0;
            state.Final = final;
            state.Target = target;
            if (!Enabled || !text.IsVisible || Math.Abs(target - start) < 1e-9)
            {
                state.Shown = target;
                return;
            }

            state.Start = start;
            state.Began = DateTime.UtcNow;
            state.DurationMs = Level == MotionLevel.Reduced ? 450 : 850;
            Show(text, state, start);

            if (!Counting.Any(w => w.TryGetTarget(out var t) && ReferenceEquals(t, text)))
                Counting.Add(new WeakReference<TextBlock>(text));
            if (!_renderingHooked)
            {
                _renderingHooked = true;
                CompositionTarget.Rendering += OnRendering;
            }
        }

        private static void OnRendering(object? sender, EventArgs e)
        {
            var now = DateTime.UtcNow;
            for (var i = Counting.Count - 1; i >= 0; i--)
            {
                if (!Counting[i].TryGetTarget(out var text) || !CountStates.TryGetValue(text, out var state))
                {
                    Counting.RemoveAt(i);
                    continue;
                }

                var t = Math.Clamp((now - state.Began).TotalMilliseconds / state.DurationMs, 0, 1);
                var eased = 1 - Math.Pow(1 - t, 3);
                if (t >= 1)
                {
                    state.Writing = true;
                    text.SetCurrentValue(TextBlock.TextProperty, state.Final);
                    state.Writing = false;
                    state.Shown = state.Target;
                    Counting.RemoveAt(i);
                    continue;
                }

                Show(text, state, state.Start + (state.Target - state.Start) * eased);
            }

            if (Counting.Count == 0)
            {
                CompositionTarget.Rendering -= OnRendering;
                _renderingHooked = false;
            }
        }

        private static void Show(TextBlock text, CountState state, double value)
        {
            state.Writing = true;
            text.SetCurrentValue(TextBlock.TextProperty, CountUpText.Format(state.Final, value, CultureInfo.CurrentCulture));
            state.Writing = false;
            state.Shown = value;
        }

        /// <summary>Para cifras que se ponen desde codigo (sin enlace): sube contando desde <paramref name="from"/>.</summary>
        public static void CountTo(TextBlock text, string final, double? from)
        {
            text.Text = final;
            if (!Enabled || from is null || !CountUpText.TryParse(final, CultureInfo.CurrentCulture, out var target) ||
                Math.Abs(target - from.Value) < 1e-9)
                return;

            var state = CountStates.GetValue(text, _ => new CountState());
            state.Final = final;
            state.Target = target;
            state.Start = from.Value;
            state.Began = DateTime.UtcNow;
            state.DurationMs = Level == MotionLevel.Reduced ? 450 : 850;
            Show(text, state, from.Value);
            Counting.Add(new WeakReference<TextBlock>(text));
            if (!_renderingHooked)
            {
                _renderingHooked = true;
                CompositionTarget.Rendering += OnRendering;
            }
        }
    }

    /// <summary>
    /// La cifra dentro de un texto ("+1,234.5 bb", "48.8%", "-3 bb/100") y como escribir otra con la
    /// misma forma: mismos decimales, separador de miles y signo.
    /// </summary>
    public static class CountUpText
    {
        private static Regex NumberPattern(CultureInfo culture)
        {
            var group = Regex.Escape(culture.NumberFormat.NumberGroupSeparator);
            var dec = Regex.Escape(culture.NumberFormat.NumberDecimalSeparator);
            return new Regex($@"(?<sign>[+\-−]?)(?<int>\d{{1,3}}(?:{group}\d{{3}})+|\d+)(?:{dec}(?<frac>\d+))?(?!\d)");
        }

        public static bool TryParse(string? text, CultureInfo culture, out double value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text))
                return false;

            var match = NumberPattern(culture).Match(text);
            if (!match.Success)
                return false;

            var digits = match.Groups["int"].Value.Replace(culture.NumberFormat.NumberGroupSeparator, "");
            var frac = match.Groups["frac"].Value;
            if (!double.TryParse(digits + (frac.Length > 0 ? "." + frac : ""), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return false;
            if (match.Groups["sign"].Value is "-" or "−")
                value = -value;
            return true;
        }

        /// <summary>El texto <paramref name="template"/> con su cifra cambiada por <paramref name="value"/>.</summary>
        public static string Format(string template, double value, CultureInfo culture)
        {
            var match = NumberPattern(culture).Match(template);
            if (!match.Success)
                return template;

            var decimals = match.Groups["frac"].Value.Length;
            // Con separador de miles en el texto final, los pasos intermedios tambien lo llevan.
            var grouped = match.Groups["int"].Value.Contains(culture.NumberFormat.NumberGroupSeparator, StringComparison.Ordinal);
            var rounded = Math.Round(Math.Abs(value), decimals, MidpointRounding.AwayFromZero);
            var body = rounded.ToString((grouped ? "N" : "F") + decimals, culture);
            var templateSign = match.Groups["sign"].Value;
            var sign = value < 0 && rounded > 0
                ? (templateSign == "−" ? "−" : "-")
                : templateSign == "+" ? "+" : "";
            return template[..match.Index] + sign + body + template[(match.Index + match.Length)..];
        }
    }
}
