using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Hud.App.Services.Trainer
{
    public enum LiveActionKind
    {
        Fold,
        Check,
        Call,
        Bet,
        Raise,
        AllIn
    }

    /// <summary>Un boton de accion del turno del heroe: "Igualar 4,25 BB" -> Call 4,25 bb.</summary>
    public sealed record LiveActionOption(LiveActionKind Kind, double? AmountBb, string Text);

    /// <summary>
    /// Lo que se ve en la placa de un asiento. Indice 0 = heroe (abajo) y 1..5 en el sentido de
    /// las agujas del reloj, que es el orden de asientos de PokerStars.
    /// </summary>
    public sealed record LivePlate(int Index, string? Name, string? Action, double? StackBb, bool HasCards, bool SittingOut, bool EmptySeat);

    public sealed class LiveTurnState
    {
        public LiveTurnState(IReadOnlyList<LiveActionOption> options, double? potBb, IReadOnlyList<LivePlate> plates, int buttonCount = 0)
        {
            Options = options;
            PotBb = potBb;
            Plates = plates;
            ButtonCount = buttonCount;
        }

        public IReadOnlyList<LiveActionOption> Options { get; }
        public double? PotBb { get; }
        public IReadOnlyList<LivePlate> Plates { get; }

        /// <summary>Botones rojos en la barra (leidos o no): sirve para saber si falta alguno.</summary>
        public int ButtonCount { get; }

        public bool CanCheck => Options.Any(option => option.Kind == LiveActionKind.Check);

        /// <summary>
        /// Queda subida posible: hay boton de apostar o subir (o all-in ademas de igualar). False
        /// solo si se leyeron todos los botones y ninguno sube (rival all-in o no te llega para
        /// mas que igualar); null si falta algun boton por leer.
        /// </summary>
        public bool? CanRaise
        {
            get
            {
                if (Options.Any(option => option.Kind is LiveActionKind.Raise or LiveActionKind.Bet) ||
                    Options.Any(option => option.Kind == LiveActionKind.AllIn) &&
                    Options.Any(option => option.Kind is LiveActionKind.Call or LiveActionKind.Check))
                    return true;
                return ButtonCount > 0 && Options.Count >= ButtonCount ? false : null;
            }
        }

        public bool FacingBet => !CanCheck && Options.Any(option => option.Kind is LiveActionKind.Call or LiveActionKind.Fold);

        /// <summary>
        /// Lo que cuesta igualar. Solo si no hay boton de igualar (pagar es quedarse all-in) se toma
        /// el importe del boton all-in: con los dos, el all-in es una subida y su cifra no es el pago.
        /// </summary>
        public double? ToCallBb => Options.FirstOrDefault(option => option.Kind == LiveActionKind.Call) is { } call
            ? call.AmountBb
            : FacingBet ? Options.FirstOrDefault(option => option.Kind == LiveActionKind.AllIn)?.AmountBb : null;

        /// <summary>
        /// Boton de apostar o "subir a". Ojo: su importe es el que marca el slider en ese momento
        /// (empieza en el minimo, pero el alumno lo mueve), asi que no sirve como minimo fiable.
        /// </summary>
        public LiveActionOption? Aggressive => Options.FirstOrDefault(option => option.Kind is LiveActionKind.Raise or LiveActionKind.Bet)
                                               ?? Options.FirstOrDefault(option => option.Kind == LiveActionKind.AllIn && !FacingBet);

        /// <summary>Stack del heroe leido en su placa.</summary>
        public double? HeroStackBb => Plates.Count > 0 ? Plates[0].StackBb : null;

        public string Signature => string.Join("|", Options.Select(option => $"{option.Kind}:{option.AmountBb?.ToString("0.##", CultureInfo.InvariantCulture)}")) +
                                   $"#{PotBb?.ToString("0.##", CultureInfo.InvariantCulture)}";

        public string Describe() =>
            string.Join(" | ", Options.Select(option => option.AmountBb is { } amount
                ? $"{option.Kind} {amount.ToString("0.##", CultureInfo.InvariantCulture)} bb"
                : option.Kind.ToString())) +
            (PotBb is { } pot ? $" | bote {pot.ToString("0.##", CultureInfo.InvariantCulture)} bb" : " | bote ?");
    }

    /// <summary>
    /// Lee el estado de la mesa de PokerStars en el turno del heroe: los botones de accion (que
    /// dicen exactamente cuanto hay que pagar), el bote total y las placas de los jugadores
    /// (ultima accion de la calle, stack y si siguen con cartas). Medido sobre la mesa real 6-max
    /// del cliente nuevo; las zonas van en fracciones del area cliente, valen a cualquier tamano.
    /// </summary>
    public sealed class LiveTableReader
    {
        // Centro de cada placa: heroe abajo y despues en el sentido de las agujas del reloj.
        private static readonly (double X, double Y)[] PlateAnchors =
        {
            (0.500, 0.728),
            (0.121, 0.548),
            (0.153, 0.233),
            (0.500, 0.137),
            (0.846, 0.233),
            (0.877, 0.548)
        };

        /// <summary>
        /// Placa junto a la que esta el boton del dealer (0 = heroe, en el sentido de las agujas del
        /// reloj), o null si no se ve. El boton es un disco claro de ~20 px con la "D" roja: se buscan
        /// manchas rojas de su tamano rodeadas de gris claro y con el tapete (no una carta blanca)
        /// un poco mas afuera; las cartas de corazones y los avatares se descartan asi. Con el boton
        /// visto, la posicion ya no depende de adivinar a donde se movio desde la mano anterior
        /// (PokerStars lo mueve raro cuando alguien entra o sale: la SB dos manos seguidas).
        /// </summary>
        public static int? DealerPlate(CapturedFrame frame)
        {
            int w = frame.Width, h = frame.Height;
            if (w < 200 || h < 150)
                return null;
            var scale = Math.Min(w / 692.0, h / 477.0);
            bool Red(int x, int y)
            {
                var i = (y * w + x) * 4;
                int b = frame.Pixels[i], g = frame.Pixels[i + 1], r = frame.Pixels[i + 2];
                return r >= 170 && g <= 95 && b <= 95 && r - g >= 90;
            }
            bool Light(int x, int y)
            {
                if (x < 0 || y < 0 || x >= w || y >= h)
                    return false;
                var i = (y * w + x) * 4;
                int b = frame.Pixels[i], g = frame.Pixels[i + 1], r = frame.Pixels[i + 2];
                var min = Math.Min(r, Math.Min(g, b));
                return min >= 150 && Math.Max(r, Math.Max(g, b)) - min <= 70;
            }
            int Ring(double cx, double cy, double radius)
            {
                var count = 0;
                for (var k = 0; k < 24; k++)
                {
                    var angle = 2 * Math.PI * k / 24;
                    if (Light((int)Math.Round(cx + radius * Math.Cos(angle)), (int)Math.Round(cy + radius * Math.Sin(angle))))
                        count++;
                }
                return count;
            }

            var seen = new bool[w * h];
            var stack = new Stack<(int X, int Y)>();
            (double X, double Y, int Score)? best = null;
            int x0 = (int)(w * 0.05), x1 = (int)(w * 0.95), y0 = (int)(h * 0.08), y1 = (int)(h * 0.82);
            for (var y = y0; y < y1; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    if (seen[y * w + x] || !Red(x, y))
                        continue;
                    int n = 0, minX = x, maxX = x, minY = y, maxY = y;
                    double sumX = 0, sumY = 0;
                    stack.Push((x, y));
                    seen[y * w + x] = true;
                    while (stack.Count > 0)
                    {
                        var (px, py) = stack.Pop();
                        n++;
                        sumX += px;
                        sumY += py;
                        minX = Math.Min(minX, px);
                        maxX = Math.Max(maxX, px);
                        minY = Math.Min(minY, py);
                        maxY = Math.Max(maxY, py);
                        foreach (var (nx, ny) in new[] { (px + 1, py), (px - 1, py), (px, py + 1), (px, py - 1) })
                        {
                            if (nx < x0 || ny < y0 || nx >= x1 || ny >= y1 || seen[ny * w + nx] || !Red(nx, ny))
                                continue;
                            seen[ny * w + nx] = true;
                            stack.Push((nx, ny));
                        }
                    }

                    var bw = (maxX - minX + 1) / scale;
                    var bh = (maxY - minY + 1) / scale;
                    var size = n / (scale * scale);
                    if (size < 45 || size > 120 || bw < 10 || bw > 18 || bh < 7 || bh > 14)
                        continue;
                    double cx = sumX / n, cy = sumY / n;
                    var inner = Ring(cx, cy, 8 * scale);
                    var outer = Ring(cx, cy, 14 * scale);
                    if (inner < 13 || outer > 5)
                        continue;
                    var score = inner - outer;
                    if (best is null || score > best.Value.Score)
                        best = (cx, cy, score);
                }
            }

            if (best is not { } button)
                return null;
            var bx = button.X / w;
            var by = button.Y / h;
            return Enumerable.Range(0, PlateAnchors.Length)
                .OrderBy(index => Math.Pow(bx - PlateAnchors[index].X, 2) + Math.Pow(by - PlateAnchors[index].Y, 2))
                .First();
        }

        // Barra de los botones grandes (No ir / Igualar / Subir, o Pasar / Apostar).
        private const double BarLeft = 0.48;
        private const double BarTop = 0.885;
        private const double BarWidth = 0.515;
        private const double BarHeight = 0.11;

        private static readonly Regex AmountRx = new(@"(?<number>\d+(?:[.,]\d+)*)\s*(?<bb>bb)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly TableTextReader _ocr;

        public LiveTableReader(TableTextReader ocr) => _ocr = ocr;

        public bool OcrAvailable => _ocr.IsAvailable;

        /// <summary>
        /// Turno del heroe: la barra de botones grandes es roja (83% de sus pixeles en la mesa real con
        /// el tema de siempre; 44-67% con el tema de rojo oscuro, dos o tres botones) y fuera de turno,
        /// con las casillas de accion anticipada, casi negra (0%).
        /// </summary>
        public static bool IsHeroTurn(CapturedFrame frame) =>
            Fraction(frame, BarLeft, BarTop, BarWidth, BarHeight, IsButtonRed) >= 0.35;

        /// <summary>
        /// Huella barata de la barra de botones para no repetir el OCR si nada cambia. Se deja
        /// fuera el boton de la derecha ("Apostar" / "Subir a"): su importe sigue al slider y
        /// cambiaria cada vez que el alumno lo mueve.
        /// </summary>
        public static string BarSignature(CapturedFrame frame)
        {
            const double signatureWidth = 0.81 - BarLeft;
            var builder = new StringBuilder();
            for (var gy = 0; gy < 4; gy++)
            for (var gx = 0; gx < 24; gx++)
            {
                var x = (int)((BarLeft + signatureWidth * (gx + 0.5) / 24) * frame.Width);
                var y = (int)((BarTop + BarHeight * (gy + 0.5) / 4) * frame.Height);
                var i = (Math.Min(y, frame.Height - 1) * frame.Width + Math.Min(x, frame.Width - 1)) * 4;
                builder.Append((char)('a' + Math.Min(frame.Pixels[i], Math.Min(frame.Pixels[i + 1], frame.Pixels[i + 2])) / 32));
            }

            return builder.ToString();
        }

        public LiveTurnState ReadTurn(CapturedFrame frame, double bigBlind, bool heroHasCards)
        {
            var (pot, amountsInBb) = ReadPotAndUnit(frame, bigBlind);
            var options = ReadButtons(frame, bigBlind, amountsInBb);
            var plates = ReadPlates(frame, bigBlind, heroHasCards);
            return new LiveTurnState(options, pot, plates, ButtonRanges(frame).Count);
        }

        // ------------------------------------------------------------------ botones

        /// <summary>
        /// Botones del turno. El OCR de Windows descarta la linea del importe cuando lleva "BB"
        /// ("1 BB" no lo lee nunca); quitando las dos "B" y poniendo las cifras detras de la palabra
        /// del boton lee "Igualar 1", "Subir a 2" o "Igualar 4,25" sin fallos en la mesa real.
        /// </summary>
        public IReadOnlyList<LiveActionOption> ReadButtons(CapturedFrame frame, double bigBlind, bool amountsInBb)
        {
            var options = new List<LiveActionOption>();
            foreach (var (left, right) in ButtonRanges(frame))
            {
                var bands = TextBands(frame, left, right);
                if (bands.Count == 0)
                    continue;

                var parts = bands.Take(bands.Count - 1).ToList();
                var digits = bands.Count > 1 && amountsInBb ? DigitsBeforeBb(frame, bands[^1]) : bands[^1];
                if (bands.Count == 1)
                    parts = bands;
                else
                    parts.Add(digits);

                LiveActionOption? option = null;
                foreach (var (scale, binarize) in new[] { (3, true), (4, true), (4, false) })
                {
                    var candidate = ParseButton(_ocr.ReadStitched(frame, parts, scale, binarize), bigBlind, amountsInBb);
                    if (candidate is null)
                        continue;
                    option ??= candidate;
                    if (candidate.AmountBb is not null || bands.Count == 1)
                    {
                        option = candidate;
                        break;
                    }
                }

                // Las cifras solas a veces se leen mejor que pegadas a la palabra ("0,5" desde la SB).
                if (option is not null && option.AmountBb is null && bands.Count > 1)
                {
                    foreach (var (scale, binarize) in new[] { (3, true), (4, false), (4, true) })
                    {
                        var number = ParseButton("igualar " + _ocr.ReadStitched(frame, new[] { digits }, scale, binarize), bigBlind, amountsInBb);
                        if (number?.AmountBb is { } value)
                        {
                            option = option with { AmountBb = value };
                            break;
                        }
                    }
                }

                if (option is not null)
                    options.Add(option);
            }

            return Plausible(options);
        }

        /// <summary>
        /// Descarta importes imposibles (un recuadro de fichas tapando la cifra hace leer "18.600"):
        /// igualar nunca cuesta mas que el "subir a" minimo y ningun importe pasa de 5000 bb.
        /// </summary>
        private static IReadOnlyList<LiveActionOption> Plausible(List<LiveActionOption> options)
        {
            var raiseTo = options.FirstOrDefault(option => option.Kind == LiveActionKind.Raise)?.AmountBb;
            return options
                .Select(option =>
                {
                    if (option.AmountBb is not { } amount)
                        return option;
                    var impossible = amount <= 0 || amount > 5000 ||
                                     (option.Kind == LiveActionKind.Call && raiseTo is { } raise && amount > raise);
                    return impossible ? option with { AmountBb = null } : option;
                })
                .ToList();
        }

        /// <summary>
        /// Parte de cifras de la linea del importe: las columnas con tinta blanca forman glifos y
        /// los dos ultimos son "BB" (mismo ancho, pegados entre si y separados de las cifras).
        /// </summary>
        private static (int X, int Y, int W, int H) DigitsBeforeBb(CapturedFrame frame, (int X, int Y, int W, int H) band)
        {
            var runs = new List<(int A, int B)>();
            for (var x = band.X; x < band.X + band.W; x++)
            {
                var white = false;
                var dark = 0;
                for (var y = band.Y; y < band.Y + band.H; y++)
                {
                    var i = (y * frame.Width + x) * 4;
                    var max = Math.Max(frame.Pixels[i], Math.Max(frame.Pixels[i + 1], frame.Pixels[i + 2]));
                    white |= Math.Min(frame.Pixels[i], Math.Min(frame.Pixels[i + 1], frame.Pixels[i + 2])) >= 190;
                    if (max < 70)
                        dark++;
                }

                // Columna del recuadro oscuro de fichas que el cliente superpone al importe: fuera.
                if (dark >= band.H * 0.25 || !white)
                    continue;
                if (runs.Count > 0 && x - runs[^1].B <= 1)
                    runs[^1] = (runs[^1].A, x);
                else
                    runs.Add((x, x));
            }

            if (runs.Count < 3)
                return band;

            var firstB = runs[^2];
            var secondB = runs[^1];
            var sameWidth = Math.Abs((firstB.B - firstB.A) - (secondB.B - secondB.A)) <= 2;
            var spaceBefore = firstB.A - runs[^3].B;
            var gapBetween = secondB.A - firstB.B;
            if (!sameWidth || spaceBefore <= gapBetween)
                return band;

            // Desde la primera cifra (asi queda fuera el recuadro de fichas) hasta antes de "BB".
            var start = Math.Max(band.X, runs[0].A - 2);
            var end = runs[^3].B + 3;
            return (start, band.Y, Math.Max(4, end - start), band.H);
        }

        /// <summary>Para diagnostico: tramos de botones y lineas de texto detectadas en cada uno.</summary>
        public static IReadOnlyList<((int Left, int Right) Range, List<(int X, int Y, int W, int H)> Bands)> DebugButtonLayout(CapturedFrame frame) =>
            ButtonRanges(frame).Select(range => (range, TextBands(frame, range.Left, range.Right))).ToList();

        /// <summary>
        /// Tramos horizontales de la barra ocupados por botones rojos. Una columna es de boton si al
        /// menos el 20% de la barra es rojo (el texto blanco ocupa como mucho la mitad de un boton);
        /// los huecos entre botones son oscuros.
        /// </summary>
        private static List<(int Left, int Right)> ButtonRanges(CapturedFrame frame)
        {
            var x0 = (int)(BarLeft * frame.Width);
            var x1 = Math.Min(frame.Width - 1, (int)((BarLeft + BarWidth) * frame.Width));
            var y0 = (int)(BarTop * frame.Height);
            var y1 = Math.Min(frame.Height - 1, (int)((BarTop + BarHeight) * frame.Height));
            var ranges = new List<(int, int)>();
            var start = -1;
            for (var x = x0; x <= x1 + 1; x++)
            {
                var red = false;
                if (x <= x1)
                {
                    var hits = 0;
                    var total = 0;
                    for (var y = y0; y <= y1; y++)
                    {
                        total++;
                        var i = (y * frame.Width + x) * 4;
                        if (IsButtonRed(frame.Pixels[i + 2], frame.Pixels[i + 1], frame.Pixels[i]))
                            hits++;
                    }

                    red = total > 0 && hits >= total * 0.2;
                }

                if (red && start < 0)
                {
                    start = x;
                }
                else if (!red && start >= 0)
                {
                    if (x - start >= frame.Width * 0.06)
                        ranges.Add((start, x - 1));
                    start = -1;
                }
            }

            return ranges;
        }

        /// <summary>Lineas de texto blanco dentro de un boton (una para "No ir", dos para "Igualar / 1 BB").</summary>
        private static List<(int X, int Y, int W, int H)> TextBands(CapturedFrame frame, int left, int right)
        {
            var y0 = (int)(BarTop * frame.Height);
            var y1 = Math.Min(frame.Height - 1, (int)((BarTop + BarHeight) * frame.Height));
            var bands = new List<(int X, int Y, int W, int H)>();
            var start = -1;
            for (var y = y0; y <= y1 + 1; y++)
            {
                var text = false;
                if (y <= y1)
                {
                    var white = 0;
                    for (var x = left + 2; x <= right - 2; x++)
                    {
                        var i = (y * frame.Width + x) * 4;
                        if (Math.Min(frame.Pixels[i], Math.Min(frame.Pixels[i + 1], frame.Pixels[i + 2])) >= 190)
                            white++;
                    }

                    text = white >= 2;
                }

                if (text && start < 0)
                {
                    start = y;
                }
                else if (!text && start >= 0)
                {
                    if (y - start >= 4)
                    {
                        var top = Math.Max(0, start - 2);
                        var bottom = Math.Min(frame.Height - 1, y + 1);
                        bands.Add((left + 2, top, Math.Max(1, right - left - 4), bottom - top + 1));
                    }

                    start = -1;
                }
            }

            return bands;
        }

        public static LiveActionOption? ParseButton(string raw, double bigBlind, bool amountsInBb = true)
        {
            var text = NormalizeOcr(raw);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            LiveActionKind kind;
            if (text.Contains("no ir") || text.StartsWith("retir", StringComparison.Ordinal) || text.StartsWith("noir", StringComparison.Ordinal))
                kind = LiveActionKind.Fold;
            else if (text.Contains("all-in") || text.Contains("all in") || text.Contains("allin"))
                kind = LiveActionKind.AllIn;
            else if (text.StartsWith("pasar", StringComparison.Ordinal))
                kind = LiveActionKind.Check;
            else if (text.StartsWith("igualar", StringComparison.Ordinal) || text.StartsWith("pagar", StringComparison.Ordinal))
                kind = LiveActionKind.Call;
            else if (text.StartsWith("apostar", StringComparison.Ordinal))
                kind = LiveActionKind.Bet;
            else if (text.StartsWith("subir", StringComparison.Ordinal))
                kind = LiveActionKind.Raise;
            else
                return null;

            // En modo BB las "B" se quitaron antes del OCR: la primera cifra ya son ciegas grandes.
            double? amount;
            if (amountsInBb)
            {
                var first = AmountRx.Match(text);
                amount = first.Success ? ParseNumber(first.Groups["number"].Value) : null;
            }
            else
            {
                amount = ParseAmount(text, bigBlind);
            }

            return new LiveActionOption(kind, amount, raw.Trim());
        }

        // ------------------------------------------------------------------ bote

        /// <summary>
        /// "Bote: 8,5 BB" encima del board: bote total con las apuestas de la calle y sin el rake
        /// ya descontado (es lo que se puede ganar). Recorte ajustado a la pildora, x3 y en blanco y
        /// negro, que es la variante que leyo bien todos los importes en la mesa real.
        /// </summary>
        public double? ReadPot(CapturedFrame frame, double bigBlind) => ReadPotAndUnit(frame, bigBlind).Pot;

        /// <summary>Bote y si la mesa muestra los importes en ciegas grandes ("BB") o en fichas.</summary>
        public (double? Pot, bool AmountsInBb) ReadPotAndUnit(CapturedFrame frame, double bigBlind)
        {
            // Se lee con las tres variantes: a veces una se come la coma ("87,9" -> 879) y la mesa
            // recomendaba ir all-in con carta alta por un bote diez veces mayor.
            var readings = new List<(double Pot, bool InBb)>();
            foreach (var (scale, binarize) in new[] { (3, true), (4, true), (3, false) })
            {
                foreach (var line in _ocr.Read(frame, 0.36, 0.283, 0.28, 0.040, scale, binarize))
                {
                    var text = NormalizeOcr(line.Text);
                    var index = text.IndexOf("bote", StringComparison.Ordinal);
                    if (index < 0)
                        continue;

                    var rest = text[(index + 4)..];
                    if (ParseAmount(rest, bigBlind) is { } amount)
                    {
                        readings.Add((amount, rest.Contains("bb")));
                        break;
                    }
                }
            }

            if (readings.Count == 0)
                return (null, true); // sin cifra legible se supone el modo BB, como viene el cliente

            return (PickPot(readings.Select(reading => reading.Pot).ToList()), readings[0].InBb);
        }

        /// <summary>
        /// Entre las lecturas del bote: la primera, salvo que otra sea unas diez veces menor (la coma
        /// perdida solo infla la cifra, nunca la reduce).
        /// </summary>
        public static double PickPot(IReadOnlyList<double> readings)
        {
            var first = readings[0];
            foreach (var other in readings.Skip(1))
            {
                var ratio = first / Math.Max(other, 1e-9);
                if (ratio is > 8.5 and < 11.5 || ratio is > 85 and < 115)
                    return other;
            }

            return first;
        }

        // ------------------------------------------------------------------ placas

        public IReadOnlyList<LivePlate> ReadPlates(CapturedFrame frame, double bigBlind, bool heroHasCards)
        {
            var plates = new List<LivePlate>();
            for (var index = 0; index < PlateAnchors.Length; index++)
            {
                var (ax, ay) = PlateAnchors[index];
                var lines = _ocr.Read(frame, ax - 0.105, ay - 0.05, 0.21, 0.10, 3)
                    .OrderBy(line => line.Y)
                    .ToList();
                var texts = lines.Select(line => NormalizeOcr(line.Text)).ToList();

                var sittingOut = texts.Any(text => text.Contains("ausente"));
                var empty = texts.Count == 0 || texts.Any(text => text.Contains("asiento libre") || text == "libre");
                double? stack = null;
                foreach (var text in texts)
                {
                    if (StackFromLine(text, bigBlind) is { } value)
                        stack = value;
                }

                var first = lines.FirstOrDefault(line => !NormalizeOcr(line.Text).Contains("bb"));
                var firstText = first is null ? "" : NormalizeOcr(first.Text);
                var label = ActionLabel(firstText);
                // Quien esta all-in lleva "All-in" donde iria su stack, debajo del nombre (la etiqueta
                // de accion de arriba desaparece al rato): antes solo se miraba la primera linea y
                // ese all-in no se veia.
                var allInBelow = texts.Skip(1).Any(text => ActionLabel(text) == "AllIn");
                var action = label ?? (allInBelow ? "AllIn" : null);
                var name = label is null && first is not null && !sittingOut && !empty ? first.Text.Trim() : null;

                var hasCards = index == 0
                    ? heroHasCards
                    : Fraction(frame, ax - 0.065, ay - 0.095, 0.13, 0.065, IsCardBack) >= 0.2;

                plates.Add(new LivePlate(index, name, action, stack, hasCards && !sittingOut && !empty, sittingOut, empty));
            }

            return plates;
        }

        /// <summary>Ultima accion de la calle tal como la pinta la placa ("No ir", "Igualar"...).</summary>
        public static string? ActionLabel(string normalized)
        {
            normalized = normalized.TrimStart('{', '}', '(', ')', '[', ']', '|', '-', '.', ' ', '_');
            if (normalized.StartsWith("poner", StringComparison.Ordinal)) return "Post";
            if (normalized.StartsWith("no ir", StringComparison.Ordinal) || normalized == "noir") return "Fold";
            if (normalized.StartsWith("pasar", StringComparison.Ordinal)) return "Check";
            if (normalized.StartsWith("igualar", StringComparison.Ordinal)) return "Call";
            if (normalized.StartsWith("apostar", StringComparison.Ordinal)) return "Bet";
            if (normalized.StartsWith("subir", StringComparison.Ordinal)) return "Raise";
            if (normalized.Contains("all-in") || normalized.Contains("all in")) return "AllIn";
            return null;
        }

        // ------------------------------------------------------------------ utilidades

        /// <summary>
        /// Pila de una linea de la placa ("172,8 BB"). El OCR a veces parte la cifra con un espacio
        /// ("17 2,8 BB" o "13 2,6 BB"): se tomaba el primer trozo como fichas (13/500 = 0,03 bb) o el
        /// ultimo como la pila (2,8 bb), y la mesa pedia "IGUALAR ALL-IN (0,03 BB)" o "NO IR" con
        /// 172 bb. Los trozos separados se juntan.
        /// </summary>
        public static double? StackFromLine(string normalized, double bigBlind)
        {
            var index = normalized.IndexOf("bb", StringComparison.Ordinal);
            if (index < 0)
                return null;

            // Trozos de cifras justo antes de "bb", de derecha a izquierda. Se junta un trozo corto
            // (1-2 cifras, "13" de "13 2, 5") o uno pegado por la coma ("2," + "5"); un numero largo
            // (el del nombre, "jdow 88554 178,5") corta.
            // "Z" pegada a una cifra es un 2 ("20Z1 BB" de 202,1).
            var before = Regex.Replace(normalized[..index], @"(?<=\d)z|z(?=\s?\d)", "2");
            var chunks = before.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var number = "";
            for (var i = chunks.Length - 1; i >= 0; i--)
            {
                var chunk = chunks[i];
                if (!Regex.IsMatch(chunk, @"^[\d.,]+$"))
                    break;
                if (number.Length == 0)
                {
                    number = chunk;
                }
                else if (Regex.IsMatch(number, @"^\d{1,2}$") && Regex.IsMatch(chunk, @"^\d{3,}$"))
                {
                    // Coma perdida y un hueco en su lugar: "101 2 BB" es 101,2 (se leia 2 bb).
                    number = chunk + "," + number;
                }
                else if (Regex.IsMatch(chunk, @"^\d{1,2}[.,]?$") || number[0] is ',' or '.' || chunk[^1] is ',' or '.')
                {
                    number = chunk + number;
                }
                else
                {
                    break;
                }
            }

            number = number.Trim(',', '.');
            return number.Length > 0 && ParseNumber(number) is { } value ? value : ParseAmount(normalized, bigBlind);
        }

        /// <summary>
        /// Minusculas, sin tildes y con las confusiones tipicas del OCR en importes corregidas:
        /// "Igualarl BB" -> "igualar 1 bb", "l"/"I" sueltos -> "1", "O" entre cifras -> "0".
        /// </summary>
        public static string NormalizeOcr(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";

            var decomposed = raw.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder();
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    builder.Append(c);
            }

            var text = builder.ToString().Normalize(NormalizationForm.FormC);
            text = Regex.Replace(text, @"^(igualar|apostar|pagar)([li|])(?=\s|$)", "$1 1");
            text = Regex.Replace(text, @"(?<=\s|^)[li|](?=\s*bb\b)", "1");
            text = Regex.Replace(text, @"(?<=\d)[o](?=\d|\s|,|\.|$)", "0");
            text = Regex.Replace(text, @"(?<=[a-z])(?=\d)", " ");
            return Regex.Replace(text, @"\s+", " ").Trim();
        }

        /// <summary>
        /// Importe en bb. Con "BB" el cliente muestra ciegas grandes con coma decimal ("4,25 BB");
        /// sin "BB" son fichas ("1.795"), con punto de miles, y se dividen por la ciega grande.
        /// </summary>
        public static double? ParseAmount(string normalized, double bigBlind)
        {
            // Si alguna cifra lleva "BB", manda esa (un numero suelto delante no se toma como fichas).
            // Las pilas partidas por el OCR ("13 2,6 BB") se juntan antes, en StackFromLine.
            var matches = AmountRx.Matches(normalized).Cast<Match>().ToList();
            foreach (var match in matches.Where(match => match.Groups["bb"].Success))
            {
                if (ParseNumber(match.Groups["number"].Value) is { } value)
                    return value;
            }

            foreach (var match in matches)
            {
                if (ParseNumber(match.Groups["number"].Value) is { } value && bigBlind > 0)
                    return value / bigBlind;
            }

            return null;
        }

        /// <summary>
        /// Numero con separadores ambiguos: el OCR devuelve la coma decimal como coma o como punto
        /// ("4,25" o "4.25"). Un separador seguido de 1 o 2 cifras al final es decimal; seguido de 3
        /// es de miles ("1.795" fichas).
        /// </summary>
        public static double? ParseNumber(string number)
        {
            var last = number.LastIndexOfAny(new[] { ',', '.' });
            string clean;
            if (last < 0)
            {
                clean = number;
            }
            else
            {
                var decimals = number.Length - last - 1;
                var integerPart = number[..last].Replace(".", "").Replace(",", "");
                clean = decimals is 1 or 2
                    ? $"{integerPart}.{number[(last + 1)..]}"
                    : number.Replace(".", "").Replace(",", "");
            }

            return double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
        }

        /// <summary>
        /// Rojo de los botones grandes: el intenso del tema de siempre o el rojo oscuro en degradado de
        /// otros temas de mesa (20-11-2026: con el tapete verde sobre fondo negro los botones van de
        /// ~(80,0,0) a ~(160,20,0); con solo el rojo intenso la barra daba 14-34% y nunca se veia el
        /// turno). Los dorsos de las cartas (156,67,66) siguen fuera: tienen demasiado verde y azul.
        /// </summary>
        private static bool IsButtonRed(byte r, byte g, byte b) =>
            r >= 120 && g <= 60 && b <= 60 && r - g >= 80 ||
            r >= 70 && g <= 45 && b <= 45 && r - Math.Max(g, b) >= 55;

        /// <summary>Rojo apagado de los dorsos (media 156,67,66), distinto del rojo intenso de los botones.</summary>
        private static bool IsCardBack(byte r, byte g, byte b) => r > 120 && g >= 45 && g <= 115 && b >= 40 && b <= 115 && r - g > 50;

        private static double Fraction(CapturedFrame frame, double left, double top, double width, double height, Func<byte, byte, byte, bool> test)
        {
            var x0 = Math.Clamp((int)(left * frame.Width), 0, frame.Width - 1);
            var y0 = Math.Clamp((int)(top * frame.Height), 0, frame.Height - 1);
            var x1 = Math.Clamp((int)((left + width) * frame.Width), 0, frame.Width - 1);
            var y1 = Math.Clamp((int)((top + height) * frame.Height), 0, frame.Height - 1);
            var hits = 0;
            var total = 0;
            for (var y = y0; y <= y1; y += 2)
            for (var x = x0; x <= x1; x += 2)
            {
                var i = (y * frame.Width + x) * 4;
                total++;
                if (test(frame.Pixels[i + 2], frame.Pixels[i + 1], frame.Pixels[i]))
                    hits++;
            }

            return total == 0 ? 0 : hits / (double)total;
        }
    }
}
