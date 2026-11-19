using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Hud.App.Services.Trainer
{
    /// <summary>Linea de texto leida, con su caja en pixeles de la captura original.</summary>
    public sealed record OcrLineBox(string Text, double X, double Y, double W, double H, IReadOnlyList<OcrWordBox> Words)
    {
        public double CenterX => X + W / 2;
        public double CenterY => Y + H / 2;
    }

    public sealed record OcrWordBox(string Text, double X, double Y, double W, double H)
    {
        public double CenterX => X + W / 2;
        public double CenterY => Y + H / 2;
    }

    /// <summary>
    /// OCR integrado de Windows (Windows.Media.Ocr) sobre recortes de la mesa. Antes de reconocer,
    /// el recorte se amplia y se pasa a gris invertido (255 - min(R,G,B)): el texto claro de
    /// PokerStars (blanco sobre rojo en los botones, blanco o azul sobre las placas oscuras) queda
    /// negro sobre fondo claro, que es lo que mejor lee el motor.
    /// </summary>
    public sealed class TableTextReader
    {
        private readonly object _sync = new();
        private readonly OcrEngine? _engine;

        public TableTextReader()
        {
            _engine = TryCreateEngine();
        }

        public bool IsAvailable => _engine is not null;

        public string Language => _engine?.RecognizerLanguage?.LanguageTag ?? "-";

        /// <summary>
        /// Ampliacion efectiva: los recortes se pensaron para una mesa de 692 px de ancho; en una mas
        /// chica el texto mide menos y se amplia en proporcion, para que le llegue al OCR del mismo
        /// alto (a 624 px queda igual: 3 y 4; a 500 px, 4 y 6). Como mucho 8.
        /// </summary>
        public static int EffectiveScale(int scale, int frameWidth) =>
            frameWidth >= 660 || frameWidth <= 0 ? scale : Math.Clamp((int)Math.Round(scale * 692.0 / frameWidth), scale, 8);

        public IReadOnlyList<OcrLineBox> Read(CapturedFrame frame, double left, double top, double width, double height, int scale, bool binarize = false)
        {
            if (_engine is null)
                return Array.Empty<OcrLineBox>();
            scale = EffectiveScale(scale, frame.Width);

            var x0 = Math.Clamp((int)Math.Round(left * frame.Width), 0, frame.Width - 1);
            var y0 = Math.Clamp((int)Math.Round(top * frame.Height), 0, frame.Height - 1);
            var w = Math.Clamp((int)Math.Round(width * frame.Width), 1, frame.Width - x0);
            var h = Math.Clamp((int)Math.Round(height * frame.Height), 1, frame.Height - y0);
            if (w < 8 || h < 8)
                return Array.Empty<OcrLineBox>();

            var scaledW = w * scale;
            var scaledH = h * scale;
            var pixels = new byte[scaledW * scaledH * 4];
            for (var y = 0; y < scaledH; y++)
            {
                // Interpolacion bilineal: los bordes suaves se leen mejor que los "escalones".
                var sy = Math.Min(h - 1.0001, Math.Max(0, (y + 0.5) / scale - 0.5));
                var iy = (int)sy;
                var fy = sy - iy;
                for (var x = 0; x < scaledW; x++)
                {
                    var sx = Math.Min(w - 1.0001, Math.Max(0, (x + 0.5) / scale - 0.5));
                    var ix = (int)sx;
                    var fx = sx - ix;
                    var top00 = Ink(frame, x0 + ix, y0 + iy);
                    var top01 = Ink(frame, x0 + ix + 1, y0 + iy);
                    var bottom10 = Ink(frame, x0 + ix, y0 + iy + 1);
                    var bottom11 = Ink(frame, x0 + ix + 1, y0 + iy + 1);
                    var value = (byte)Math.Round(
                        top00 * (1 - fx) * (1 - fy) + top01 * fx * (1 - fy) +
                        bottom10 * (1 - fx) * fy + bottom11 * fx * fy);
                    var target = (y * scaledW + x) * 4;
                    pixels[target] = value;
                    pixels[target + 1] = value;
                    pixels[target + 2] = value;
                    pixels[target + 3] = 255;
                }
            }

            if (binarize)
                Binarize(pixels);

            OcrResult result;
            lock (_sync)
            {
                using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, scaledW, scaledH, BitmapAlphaMode.Premultiplied);
                result = _engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
            }

            var lines = new List<OcrLineBox>();
            foreach (var line in result.Lines)
            {
                var words = line.Words
                    .Select(word => new OcrWordBox(
                        word.Text,
                        x0 + word.BoundingRect.X / scale,
                        y0 + word.BoundingRect.Y / scale,
                        word.BoundingRect.Width / scale,
                        word.BoundingRect.Height / scale))
                    .ToList();
                if (words.Count == 0)
                    continue;

                var left0 = words.Min(word => word.X);
                var top0 = words.Min(word => word.Y);
                var right0 = words.Max(word => word.X + word.W);
                var bottom0 = words.Max(word => word.Y + word.H);
                lines.Add(new OcrLineBox(line.Text, left0, top0, right0 - left0, bottom0 - top0, words));
            }

            return lines;
        }

        /// <summary>
        /// Lee varios trozos de la captura puestos uno al lado del otro en una sola linea. El OCR de
        /// Windows descarta las lineas muy cortas ("1 BB" debajo de "Igualar"); recolocando las dos
        /// lineas de un boton como "Igualar 1 BB" las lee sin problema.
        /// </summary>
        public string ReadStitched(CapturedFrame frame, IReadOnlyList<(int X, int Y, int W, int H)> parts, int scale, bool binarize, int gapPixels = 4)
        {
            if (_engine is null || parts.Count == 0)
                return "";
            scale = EffectiveScale(scale, frame.Width);

            const int padding = 6;
            var gap = gapPixels * scale;
            var height = parts.Max(part => part.H) * scale + 2 * padding * scale;
            var width = parts.Sum(part => part.W * scale) + gap * (parts.Count + 1);
            var pixels = new byte[width * height * 4];
            Array.Fill(pixels, (byte)255);

            var offsetX = gap;
            foreach (var part in parts)
            {
                var offsetY = padding * scale + (parts.Max(p => p.H) - part.H) * scale / 2;
                for (var y = 0; y < part.H * scale; y++)
                {
                    var sy = Math.Min(part.H - 1.0001, Math.Max(0, (y + 0.5) / scale - 0.5));
                    var iy = (int)sy;
                    var fy = sy - iy;
                    for (var x = 0; x < part.W * scale; x++)
                    {
                        var sx = Math.Min(part.W - 1.0001, Math.Max(0, (x + 0.5) / scale - 0.5));
                        var ix = (int)sx;
                        var fx = sx - ix;
                        var value = (byte)Math.Round(
                            Ink(frame, part.X + ix, part.Y + iy) * (1 - fx) * (1 - fy) +
                            Ink(frame, part.X + ix + 1, part.Y + iy) * fx * (1 - fy) +
                            Ink(frame, part.X + ix, part.Y + iy + 1) * (1 - fx) * fy +
                            Ink(frame, part.X + ix + 1, part.Y + iy + 1) * fx * fy);
                        var target = ((offsetY + y) * width + offsetX + x) * 4;
                        pixels[target] = value;
                        pixels[target + 1] = value;
                        pixels[target + 2] = value;
                    }
                }

                offsetX += part.W * scale + gap;
            }

            if (binarize)
                Binarize(pixels);

            OcrResult result;
            lock (_sync)
            {
                using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
                result = _engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
            }

            return string.Join(" ", result.Lines.Select(line => line.Text));
        }

        /// <summary>
        /// Blanco y negro con el umbral de Otsu del propio recorte: los importes pequenos con coma
        /// ("8,5 BB") se le escapan al OCR en gris y los lee bien con el contraste maximo.
        /// </summary>
        private static void Binarize(byte[] pixels)
        {
            var histogram = new int[256];
            for (var i = 0; i < pixels.Length; i += 4)
                histogram[pixels[i]]++;

            var total = pixels.Length / 4;
            double sum = 0;
            for (var v = 0; v < 256; v++) sum += v * (double)histogram[v];

            double sumBackground = 0, bestVariance = -1;
            int weightBackground = 0, threshold = 128;
            for (var v = 0; v < 256; v++)
            {
                weightBackground += histogram[v];
                if (weightBackground == 0) continue;
                var weightForeground = total - weightBackground;
                if (weightForeground == 0) break;
                sumBackground += v * (double)histogram[v];
                var meanBackground = sumBackground / weightBackground;
                var meanForeground = (sum - sumBackground) / weightForeground;
                var variance = (double)weightBackground * weightForeground * (meanBackground - meanForeground) * (meanBackground - meanForeground);
                if (variance > bestVariance)
                {
                    bestVariance = variance;
                    threshold = v;
                }
            }

            for (var i = 0; i < pixels.Length; i += 4)
            {
                var value = pixels[i] <= threshold ? (byte)0 : (byte)255;
                pixels[i] = value;
                pixels[i + 1] = value;
                pixels[i + 2] = value;
            }
        }

        /// <summary>Gris invertido: el texto claro sale oscuro y los fondos (rojo, oscuro, verde) claros.</summary>
        private static double Ink(CapturedFrame frame, int x, int y)
        {
            x = Math.Min(x, frame.Width - 1);
            y = Math.Min(y, frame.Height - 1);
            var i = (y * frame.Width + x) * 4;
            var min = Math.Min(frame.Pixels[i], Math.Min(frame.Pixels[i + 1], frame.Pixels[i + 2]));
            return 255 - min;
        }

        private static OcrEngine? TryCreateEngine()
        {
            try
            {
                foreach (var tag in new[] { "es-MX", "es-ES", "es-419", "es" })
                {
                    var language = new Windows.Globalization.Language(tag);
                    if (OcrEngine.IsLanguageSupported(language))
                        return OcrEngine.TryCreateFromLanguage(language);
                }

                return OcrEngine.TryCreateFromUserProfileLanguages();
            }
            catch
            {
                // Windows sin OCR (versiones antiguas o recortadas): el entrenador sigue sin decision en vivo.
                return null;
            }
        }
    }
}
