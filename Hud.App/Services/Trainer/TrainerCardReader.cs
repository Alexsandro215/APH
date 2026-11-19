using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Hud.App.Services.Trainer
{
    /// <summary>Rectangulo en fracciones del area cliente de la mesa (0..1), para que valga a cualquier tamano.</summary>
    public readonly record struct NormalizedRect(double X, double Y, double W, double H)
    {
        public bool IsValid => W > 0.004 && H > 0.004 && X >= 0 && Y >= 0 && X + W <= 1.001 && Y + H <= 1.001;
    }

    /// <summary>Donde estan en pantalla las 2 cartas del heroe y las 5 del board.</summary>
    public sealed class TrainerCalibration
    {
        public const int SlotCount = 7;

        public static readonly string[] SlotNames =
        {
            "Heroe carta 1", "Heroe carta 2", "Flop 1", "Flop 2", "Flop 3", "Turn", "River"
        };

        public List<NormalizedRect> Slots { get; set; } = Enumerable.Repeat(default(NormalizedRect), SlotCount).ToList();

        /// <summary>Tamano del area cliente de la mesa al calibrar (0 si no se sabe).</summary>
        public int ClientWidth { get; set; }
        public int ClientHeight { get; set; }

        public bool MatchesSize(CapturedFrame frame) =>
            ClientWidth == 0 || (Math.Abs(ClientWidth - frame.Width) <= 2 && Math.Abs(ClientHeight - frame.Height) <= 2);

        public bool HasHero => Slots.Count == SlotCount && Slots[0].IsValid && Slots[1].IsValid;
        public bool HasBoard => Slots.Count == SlotCount && Slots.Skip(2).All(slot => slot.IsValid);
        public bool IsComplete => HasHero && HasBoard;

        public static string FolderPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "APH", "trainer");

        private static string FilePath => Path.Combine(FolderPath, "calibration.json");

        public static TrainerCalibration Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var loaded = JsonSerializer.Deserialize<TrainerCalibration>(File.ReadAllText(FilePath));
                    if (loaded is not null && loaded.Slots.Count == SlotCount)
                        return loaded;
                }
            }
            catch
            {
                // Calibracion corrupta: se vuelve a calibrar, no debe impedir abrir el entrenador.
            }

            return new TrainerCalibration();
        }

        private static readonly object FileLock = new();

        /// <summary>La ventana y la sesion pueden guardar a la vez: se serializa la escritura.</summary>
        public void Save()
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(FolderPath);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        public TrainerCalibration Clone() => new() { Slots = Slots.ToList(), ClientWidth = ClientWidth, ClientHeight = ClientHeight };

        /// <summary>Misma colocacion de zonas (centros a menos de ~3 px en una mesa de 950 px).</summary>
        public bool SameLayout(TrainerCalibration other)
        {
            for (var slot = 0; slot < SlotCount; slot++)
            {
                var a = Slots[slot];
                var b = other.Slots[slot];
                if (a.IsValid != b.IsValid)
                    return false;
                if (!a.IsValid)
                    continue;
                if (Math.Abs(a.X + a.W / 2 - (b.X + b.W / 2)) > 0.004 || Math.Abs(a.Y + a.H / 2 - (b.Y + b.H / 2)) > 0.005)
                    return false;
            }

            return true;
        }
    }

    /// <summary>Color de la tinta del palo: en la baraja de 4 colores identifica el palo por si solo.</summary>
    public enum InkColor
    {
        Unknown,
        Red,
        Blue,
        Green,
        Black
    }

    /// <summary>
    /// Huella de una carta a partir de la zona calibrada (la esquina con el rango y el palo).
    /// Como en un OCR, primero se recorta al recuadro de la "tinta" (lo que no es blanco) y despues
    /// se separan rango y palo por el hueco que hay entre ellos; cada parte se reescala a su propia
    /// rejilla. Asi la lectura no depende de la posicion exacta ni del tamano de la mesa.
    /// </summary>
    public sealed class CardFeatures
    {
        public const int FullW = 16;
        public const int FullH = 22;
        public const int RankW = 12;
        public const int RankH = 16;
        public const int SuitW = 10;
        public const int SuitH = 10;
        private const int InkThreshold = 90;

        public CardFeatures(byte[] full, byte[] rank, byte[] suit, double whiteFraction, InkColor suitColor = InkColor.Unknown)
        {
            Full = full;
            Rank = rank;
            Suit = suit;
            WhiteFraction = whiteFraction;
            SuitColor = suitColor;
        }

        /// <summary>Toda la tinta reescalada, en RGB.</summary>
        public byte[] Full { get; }

        /// <summary>Solo el rango, en tinta sin color (255 - min(R,G,B)): una K roja y una negra se parecen.</summary>
        public byte[] Rank { get; }

        /// <summary>Solo el palo, en RGB: en la baraja de 4 colores el color ya distingue el palo.</summary>
        public byte[] Suit { get; }

        public double WhiteFraction { get; }

        /// <summary>Color dominante de la tinta del palo (rojo, azul, verde o negro).</summary>
        public InkColor SuitColor { get; }

        /// <summary>Las caras de las cartas son blancas; el tapete y una zona vacia no.</summary>
        public bool LooksLikeCard => WhiteFraction >= 0.18;

        /// <summary>Hay rango dibujado (una zona blanca vacia o tapete no tienen tinta en el rango).</summary>
        public bool HasInk => Rank.Any(value => value >= 60);

        public static bool HasValidShape(byte[] full, byte[] rank, byte[] suit) =>
            full.Length == FullW * FullH * 3 && rank.Length == RankW * RankH && suit.Length == SuitW * SuitH * 3;

        public static CardFeatures? Extract(CapturedFrame frame, NormalizedRect rect)
        {
            if (!rect.IsValid)
                return null;

            var x0 = Math.Clamp((int)Math.Round(rect.X * frame.Width), 0, frame.Width - 1);
            var y0 = Math.Clamp((int)Math.Round(rect.Y * frame.Height), 0, frame.Height - 1);
            var w = Math.Clamp((int)Math.Round(rect.W * frame.Width), 1, frame.Width - x0);
            var h = Math.Clamp((int)Math.Round(rect.H * frame.Height), 1, frame.Height - y0);
            if (w < 4 || h < 4)
                return null;

            var image = new Crop(frame, x0, y0, w, h);
            var white = 0;
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                if (image.MinChannel(x, y) >= 190)
                    white++;
            }

            var whiteFraction = white / (double)(w * h);
            var parts = image.FindRankAndSuit();
            if (parts is null)
                return new CardFeatures(Filled(FullW * FullH * 3, 255), new byte[RankW * RankH], Filled(SuitW * SuitH * 3, 255), whiteFraction);

            var (rankBox, suitBox) = parts.Value;
            return new CardFeatures(
                image.ResampleRgb(Box.Union(rankBox, suitBox), FullW, FullH),
                image.ResampleInk(rankBox, RankW, RankH),
                image.ResampleRgb(suitBox, SuitW, SuitH),
                whiteFraction,
                image.ClassifyInk(suitBox));
        }

        /// <summary>
        /// Distancias normalizadas por la tinta de las dos huellas: la misma carta da casi 0 y dos
        /// cartas distintas mucho mas, sin que el fondo blanco diluya la diferencia.
        /// </summary>
        public static double FullDistance(byte[] a, byte[] b) => RgbDistance(a, b);

        /// <summary>Otra carta en la misma zona: cambia el rango, el palo o su color.</summary>
        public static bool LooksDifferent(CardFeatures a, CardFeatures b) =>
            a.SuitColor != b.SuitColor ||
            RankDistance(a.Rank, b.Rank) > CardTemplateStore.RankAccept ||
            SuitDistance(a.Suit, b.Suit) > CardTemplateStore.SuitAccept;

        public static double SuitDistance(byte[] a, byte[] b) => RgbDistance(a, b);

        public static double RankDistance(byte[] a, byte[] b)
        {
            long difference = 0;
            long ink = 0;
            for (var i = 0; i < a.Length; i++)
            {
                difference += Math.Abs(a[i] - b[i]);
                ink += a[i] + b[i];
            }

            return difference / (double)Math.Max(ink, 255);
        }

        private static double RgbDistance(byte[] a, byte[] b)
        {
            long difference = 0;
            long ink = 0;
            for (var i = 0; i < a.Length; i++)
            {
                difference += Math.Abs(a[i] - b[i]);
                ink += 510 - a[i] - b[i];
            }

            return difference / (double)Math.Max(ink, 255);
        }

        private static byte[] Filled(int length, byte value)
        {
            var array = new byte[length];
            Array.Fill(array, value);
            return array;
        }

        private readonly record struct Box(int X, int Y, int W, int H)
        {
            public int Right => X + W - 1;
            public int Bottom => Y + H - 1;

            public static Box FromEdges(int left, int top, int right, int bottom) =>
                new(left, top, right - left + 1, bottom - top + 1);

            public static Box Union(Box a, Box b) =>
                FromEdges(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

            public bool OverlapsColumns(Box other) => X <= other.Right && other.X <= Right;
            public bool OverlapsRows(Box other, int slack = 0) => Y <= other.Bottom + slack && other.Y <= Bottom + slack;
        }

        /// <summary>Vista de la zona calibrada dentro de la captura (coordenadas locales).</summary>
        private readonly struct Crop
        {
            private readonly byte[] _pixels;
            private readonly int _stride;
            private readonly int _x0;
            private readonly int _y0;

            public Crop(CapturedFrame frame, int x0, int y0, int width, int height)
            {
                _pixels = frame.Pixels;
                _stride = frame.Width * 4;
                _x0 = x0;
                _y0 = y0;
                Width = width;
                Height = height;
            }

            public int Width { get; }
            public int Height { get; }

            private int Index(int x, int y) => (_y0 + y) * _stride + (_x0 + x) * 4;

            public int MinChannel(int x, int y)
            {
                var i = Index(x, y);
                return Math.Min(_pixels[i], Math.Min(_pixels[i + 1], _pixels[i + 2]));
            }

            private bool IsInk(int x, int y) => 255 - MinChannel(x, y) >= InkThreshold;

            /// <summary>
            /// Separa rango y palo como un OCR. Agrupa la tinta en manchas conectadas y descarta
            /// ruido, bordes (lineas finas y largas) y fondo (manchas casi del tamano de la zona).
            /// El rango es la mancha con talla de letra mas cercana a la esquina superior izquierda,
            /// mas las que comparten su linea de base y altura ("10" son dos manchas). El palo es
            /// la mancha mas cercana justo debajo del rango y centrada bajo el. Asi los pips del
            /// centro de la carta, que en la baraja de PokerStars quedan a 2 px del indice, no
            /// entran en la huella. Sin palo debajo se prueba rango y palo lado a lado.
            /// </summary>
            public (Box Rank, Box Suit)? FindRankAndSuit()
            {
                var width = Width;
                var height = Height;
                var components = Components()
                    .Where(box => !(box.W <= 2 && box.H >= height * 0.6) && !(box.H <= 2 && box.W >= width * 0.6))
                    .Where(box => box.W < width * 0.9 && box.H < height * 0.9)
                    .ToList();
                if (components.Count == 0)
                    return null;

                var minGlyph = Math.Max(3.0, height * 0.12);
                var glyphs = components.Where(box => box.H >= minGlyph).ToList();
                if (glyphs.Count == 0)
                    glyphs = components;

                var seed = glyphs.OrderBy(box => box.X + box.Y).First();
                var tolerance = Math.Max(1.0, seed.H * 0.2);
                var rank = seed;
                foreach (var box in components.Where(box => box.X > seed.Right).OrderBy(box => box.X))
                {
                    if (Math.Abs(box.Y - seed.Y) <= tolerance &&
                        Math.Abs(box.Bottom - seed.Bottom) <= tolerance &&
                        box.X - rank.Right <= seed.H * 0.6)
                    {
                        rank = Box.Union(rank, box);
                    }
                }

                var rankCenter = rank.X + rank.W / 2.0;
                var suit = components
                    .Where(box => box.Y > rank.Bottom)
                    .Where(box => Math.Abs(box.X + box.W / 2.0 - rankCenter) <= Math.Max(rank.W, box.W) * 0.75)
                    .Where(box => box.H <= rank.H * 1.3 && box.W <= rank.W * 2.2 + 2)
                    .OrderBy(box => box.Y - rank.Bottom)
                    .Cast<Box?>()
                    .FirstOrDefault();
                if (suit is not null)
                    return (rank, suit.Value);

                var beside = components
                    .Where(box => box.X > rank.Right && box.OverlapsRows(rank))
                    .OrderBy(box => box.X - rank.Right)
                    .Cast<Box?>()
                    .FirstOrDefault();
                if (beside is not null)
                    return (rank, beside.Value);

                var all = components.Aggregate(Box.Union);
                var rankHeight = Math.Max(1, (int)Math.Round(all.H * 0.55));
                return (new Box(all.X, all.Y, all.W, rankHeight), new Box(all.X, all.Y + rankHeight, all.W, Math.Max(1, all.H - rankHeight)));
            }

            /// <summary>
            /// Color medio de la tinta fuerte del palo. Los bordes suavizados (mezcla con blanco)
            /// quedan fuera por el umbral, asi un corazon sigue siendo rojo y una pica negra.
            /// </summary>
            public InkColor ClassifyInk(Box box)
            {
                long r = 0, g = 0, b = 0;
                var count = 0;
                for (var y = box.Y; y < box.Y + box.H; y++)
                for (var x = box.X; x < box.X + box.W; x++)
                {
                    if (255 - MinChannel(x, y) < 110)
                        continue;
                    var i = Index(x, y);
                    b += _pixels[i];
                    g += _pixels[i + 1];
                    r += _pixels[i + 2];
                    count++;
                }

                if (count < 3)
                    return InkColor.Unknown;

                double red = r / (double)count, green = g / (double)count, blue = b / (double)count;
                var max = Math.Max(red, Math.Max(green, blue));
                var min = Math.Min(red, Math.Min(green, blue));
                if (max - min < 50)
                    return max < 150 ? InkColor.Black : InkColor.Unknown;
                if (red >= green && red >= blue)
                    return red - Math.Max(green, blue) >= 50 ? InkColor.Red : InkColor.Unknown;
                return blue >= green ? InkColor.Blue : InkColor.Green;
            }

            /// <summary>Recuadros de las manchas de tinta (8-conectadas) de al menos 4 pixeles.</summary>
            private List<Box> Components()
            {
                var seen = new bool[Width * Height];
                var result = new List<Box>();
                var stack = new Stack<(int X, int Y)>();
                for (var y = 0; y < Height; y++)
                for (var x = 0; x < Width; x++)
                {
                    if (seen[y * Width + x] || !IsInk(x, y))
                        continue;

                    int left = x, right = x, top = y, bottom = y, count = 0;
                    seen[y * Width + x] = true;
                    stack.Push((x, y));
                    while (stack.Count > 0)
                    {
                        var (cx, cy) = stack.Pop();
                        count++;
                        left = Math.Min(left, cx);
                        right = Math.Max(right, cx);
                        top = Math.Min(top, cy);
                        bottom = Math.Max(bottom, cy);
                        for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var nx = cx + dx;
                            var ny = cy + dy;
                            if (nx < 0 || ny < 0 || nx >= Width || ny >= Height || seen[ny * Width + nx] || !IsInk(nx, ny))
                                continue;
                            seen[ny * Width + nx] = true;
                            stack.Push((nx, ny));
                        }
                    }

                    if (count >= 4)
                        result.Add(Box.FromEdges(left, top, right, bottom));
                }

                return result;
            }

            public byte[] ResampleRgb(Box box, int gridW, int gridH)
            {
                var result = new byte[gridW * gridH * 3];
                for (var gy = 0; gy < gridH; gy++)
                for (var gx = 0; gx < gridW; gx++)
                {
                    var (sx0, sx1) = Span(box.X, box.W, gx, gridW);
                    var (sy0, sy1) = Span(box.Y, box.H, gy, gridH);
                    long r = 0, g = 0, b = 0;
                    var count = 0;
                    for (var y = sy0; y < sy1; y++)
                    for (var x = sx0; x < sx1; x++)
                    {
                        var i = Index(x, y);
                        b += _pixels[i];
                        g += _pixels[i + 1];
                        r += _pixels[i + 2];
                        count++;
                    }

                    var cell = (gy * gridW + gx) * 3;
                    result[cell] = (byte)(r / count);
                    result[cell + 1] = (byte)(g / count);
                    result[cell + 2] = (byte)(b / count);
                }

                return result;
            }

            public byte[] ResampleInk(Box box, int gridW, int gridH)
            {
                var result = new byte[gridW * gridH];
                for (var gy = 0; gy < gridH; gy++)
                for (var gx = 0; gx < gridW; gx++)
                {
                    var (sx0, sx1) = Span(box.X, box.W, gx, gridW);
                    var (sy0, sy1) = Span(box.Y, box.H, gy, gridH);
                    long ink = 0;
                    var count = 0;
                    for (var y = sy0; y < sy1; y++)
                    for (var x = sx0; x < sx1; x++)
                    {
                        ink += 255 - MinChannel(x, y);
                        count++;
                    }

                    result[gy * gridW + gx] = (byte)(ink / count);
                }

                return result;
            }

            /// <summary>Pixeles de origen de la celda (al menos uno, tambien al ampliar).</summary>
            private static (int Start, int End) Span(int origin, int size, int cell, int cells)
            {
                var start = origin + (int)Math.Floor(cell * size / (double)cells);
                var end = origin + (int)Math.Ceiling((cell + 1) * size / (double)cells);
                end = Math.Min(Math.Max(end, start + 1), origin + size);
                return (Math.Min(start, end - 1), end);
            }
        }
    }

    public readonly record struct CardMatch(string? Card, double Distance, string Method, bool Confident);

    /// <summary>Plantillas aprendidas de la baraja del usuario, guardadas en %LOCALAPPDATA%\APH\trainer.</summary>
    public sealed class CardTemplateStore
    {
        public const int MaxPerCard = 4;

        // Umbrales medidos con la baraja real de PokerStars (plantillas aprendidas en mesa):
        // rango, misma etiqueta <= 0,18 y distinta >= 0,25; palo, misma <= 0,09 y distinta
        // >= 0,19. La carta entera no separa (misma <= 0,10, distinta desde 0,108: un 5 y un 6
        // del mismo palo casi coinciden), asi que solo se acepta por rango y palo por separado.
        public const double RankAccept = 0.215;
        public const double SuitAccept = 0.145;
        private const double Ambiguity = 0.8;
        private const double MinMargin = 0.03;

        private readonly object _sync = new();
        private readonly string _filePath;
        private List<CardTemplate> _templates = new();

        public CardTemplateStore(string? filePath = null) => _filePath = filePath ?? DefaultFilePath;

        public sealed class CardTemplate
        {
            public string Card { get; set; } = "";
            public byte[] Full { get; set; } = Array.Empty<byte>();
            public byte[] Rank { get; set; } = Array.Empty<byte>();
            public byte[] Suit { get; set; } = Array.Empty<byte>();
            public InkColor SuitColor { get; set; }
            public int Conflicts { get; set; }
            public DateTime AddedAt { get; set; } = DateTime.Now;
        }

        private static string DefaultFilePath => Path.Combine(TrainerCalibration.FolderPath, "card-templates.json");

        public int LearnedCards
        {
            get
            {
                lock (_sync)
                    return _templates.Select(template => template.Card).Distinct().Count();
            }
        }

        public int LearnedRanks
        {
            get
            {
                lock (_sync)
                    return _templates.Select(template => template.Card[0]).Distinct().Count();
            }
        }

        public int LearnedSuits
        {
            get
            {
                lock (_sync)
                    return _templates.Select(template => template.Card[1]).Distinct().Count();
            }
        }

        public static CardTemplateStore Load(string? filePath = null)
        {
            var store = new CardTemplateStore(filePath);
            try
            {
                if (File.Exists(store._filePath))
                {
                    var loaded = JsonSerializer.Deserialize<List<CardTemplate>>(File.ReadAllText(store._filePath));
                    if (loaded is not null)
                    {
                        store._templates = loaded
                            .Where(template => template.Card.Length == 2 && CardFeatures.HasValidShape(template.Full, template.Rank, template.Suit))
                            .ToList();
                        store.RemoveColorOutliers();
                    }
                }
            }
            catch
            {
                // Plantillas corruptas: se vuelven a aprender.
            }

            return store;
        }

        private static readonly object FileLock = new();

        /// <summary>
        /// Guarda las plantillas. Aprender (hilo del historial) y ensenar (ventana) pueden guardar
        /// a la vez: se serializa la escritura y, si el archivo esta ocupado, se reintenta en el
        /// siguiente guardado en lugar de romper la sesion.
        /// </summary>
        public void Save()
        {
            List<CardTemplate> snapshot;
            lock (_sync)
                snapshot = _templates.ToList();

            lock (FileLock)
            {
                try
                {
                    var folder = Path.GetDirectoryName(_filePath);
                    if (!string.IsNullOrEmpty(folder))
                        Directory.CreateDirectory(folder);
                    File.WriteAllText(_filePath, JsonSerializer.Serialize(snapshot));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Las plantillas siguen en memoria; se guardaran con el siguiente aprendizaje.
                }
            }
        }

        public void Clear()
        {
            lock (_sync)
                _templates.Clear();
            Save();
        }

        public void Add(string card, CardFeatures features)
        {
            // Una zona sin tinta (tapete, carta vacia) no es una carta: nunca se aprende.
            if (!features.HasInk)
                return;

            lock (_sync)
            {
                _templates.Add(new CardTemplate { Card = card, Full = features.Full, Rank = features.Rank, Suit = features.Suit, SuitColor = features.SuitColor });
                var sameCard = _templates.Where(template => template.Card == card).OrderBy(template => template.AddedAt).ToList();
                foreach (var extra in sameCard.Take(Math.Max(0, sameCard.Count - MaxPerCard)))
                    _templates.Remove(extra);
            }
        }

        /// <summary>
        /// Quita las plantillas de un palo con un color distinto del de casi todas las suyas (un
        /// diamante azul guardado como trebol, verde): una sola asi deja el palo empatado entre los dos
        /// y cada carta de ese palo se queda en "?". Devuelve cuantas quito.
        /// </summary>
        public int RemoveColorOutliers()
        {
            lock (_sync)
            {
                var removed = 0;
                foreach (var suit in _templates.Select(template => template.Card[1]).Distinct().ToList())
                {
                    var known = _templates.Where(template => template.Card[1] == suit && template.SuitColor != InkColor.Unknown).ToList();
                    if (known.Count < 4)
                        continue;
                    var main = known.GroupBy(template => template.SuitColor).OrderByDescending(group => group.Count()).First();
                    if (main.Count() < known.Count * 0.75)
                        continue;
                    removed += _templates.RemoveAll(template => template.Card[1] == suit &&
                                                                template.SuitColor != InkColor.Unknown && template.SuitColor != main.Key);
                }

                return removed;
            }
        }

        public CardMatch Match(CardFeatures features)
        {
            lock (_sync)
            {
                if (_templates.Count == 0)
                    return new CardMatch(null, 1, "sin plantillas", false);

                // El color del palo descarta de entrada las plantillas de otro color: en la baraja
                // de 4 colores un palo verde solo puede ser trebol.
                var sameColor = _templates.Where(template => ColorsCompatible(template.SuitColor, features.SuitColor)).ToList();
                if (sameColor.Count == 0)
                    return new CardMatch(null, 1, "color sin plantillas", false);

                // Rango y palo por separado: con pocas manos ya se conocen los 13 rangos y los 4
                // palos aunque falten muchas de las 52 combinaciones.
                var (rank, rankDistance, rankSecond) = BestByLabel(_templates, template => template.Card[..1], template => CardFeatures.RankDistance(template.Rank, features.Rank));
                var (suit, suitDistance, suitSecond) = BestByLabel(sameColor, template => template.Card[1..], template => CardFeatures.SuitDistance(template.Suit, features.Suit));
                // Ademas del cociente, un margen absoluto: un empate exacto (una plantilla mal
                // etiquetada identica a la buena) deja la lectura en "?" en vez de decidir al azar.
                if (rank is not null && suit is not null &&
                    rankDistance <= RankAccept && rankDistance <= rankSecond * Ambiguity && rankSecond - rankDistance >= MinMargin &&
                    suitDistance <= SuitAccept && suitDistance <= suitSecond * Ambiguity && suitSecond - suitDistance >= MinMargin)
                {
                    return new CardMatch(rank + suit, Math.Max(rankDistance, suitDistance), "rango+palo", true);
                }

                // Sin acuerdo claro: se informa la mejor candidata, pero la lectura queda en "?".
                var guess = rank is not null && suit is not null ? rank + suit : null;
                return new CardMatch(guess, Math.Max(rankDistance, suitDistance), "dudosa", false);
            }
        }

        private static bool ColorsCompatible(InkColor template, InkColor observed) =>
            template == InkColor.Unknown || observed == InkColor.Unknown || template == observed;

        /// <summary>
        /// Color con el que se ha visto ese palo (el mas frecuente en las plantillas). Sin
        /// plantillas del palo se supone la baraja de 4 colores de PokerStars si ya se ha visto
        /// tinta azul o verde (solo existe en esa baraja) y la clasica de 2 colores si no.
        /// </summary>
        public InkColor ExpectedColor(char suit, bool fourColorHint)
        {
            lock (_sync)
            {
                var learned = _templates
                    .Where(template => template.Card[1] == suit && template.SuitColor != InkColor.Unknown)
                    .GroupBy(template => template.SuitColor)
                    .OrderByDescending(group => group.Count())
                    .Select(group => group.Key)
                    .FirstOrDefault();
                if (learned != InkColor.Unknown)
                    return learned;

                var fourColor = fourColorHint || _templates.Any(template => template.SuitColor is InkColor.Blue or InkColor.Green);
                return suit switch
                {
                    'h' => InkColor.Red,
                    'd' => fourColor ? InkColor.Blue : InkColor.Red,
                    'c' => fourColor ? InkColor.Green : InkColor.Black,
                    's' => InkColor.Black,
                    _ => InkColor.Unknown
                };
            }
        }

        /// <summary>
        /// Distancia minima del rango y del palo de la huella a las plantillas de ese rango y ese palo
        /// (NaN si aun no hay plantillas de ellos).
        /// </summary>
        public (double Rank, double Suit) DistanceToRankAndSuit(char rank, char suit, CardFeatures features)
        {
            lock (_sync)
            {
                var rankDistance = _templates.Where(template => template.Card[0] == rank)
                    .Select(template => CardFeatures.RankDistance(template.Rank, features.Rank))
                    .DefaultIfEmpty(double.NaN)
                    .Min();
                var suitDistance = _templates.Where(template => template.Card[1] == suit && ColorsCompatible(template.SuitColor, features.SuitColor))
                    .Select(template => CardFeatures.SuitDistance(template.Suit, features.Suit))
                    .DefaultIfEmpty(double.NaN)
                    .Min();
                return (rankDistance, suitDistance);
            }
        }

        /// <summary>
        /// Quita la plantilla de otra carta que empata con la huella (mal etiquetada): si es casi
        /// identica se borra; si no, suma un conflicto.
        /// </summary>
        public void PunishLookalike(CardFeatures features, string actualCard)
        {
            lock (_sync)
            {
                var culprit = _templates
                    .Where(template => template.Card != actualCard)
                    .Select(template => (Template: template,
                        Rank: CardFeatures.RankDistance(template.Rank, features.Rank),
                        Suit: CardFeatures.SuitDistance(template.Suit, features.Suit)))
                    .Where(item => item.Rank <= RankAccept && item.Suit <= SuitAccept)
                    .OrderBy(item => item.Rank + item.Suit)
                    .FirstOrDefault();
                if (culprit.Template is null)
                    return;

                if (culprit.Rank <= 0.01 && culprit.Suit <= 0.01)
                {
                    _templates.Remove(culprit.Template);
                    return;
                }

                culprit.Template.Conflicts++;
                if (culprit.Template.Conflicts >= 3)
                    _templates.Remove(culprit.Template);
            }
        }

        /// <summary>Distancia de la huella a una carta concreta (para decidir el orden de las cartas del heroe).</summary>
        public double DistanceTo(string card, CardFeatures features)
        {
            lock (_sync)
            {
                var full = _templates.Where(template => template.Card == card)
                    .Select(template => CardFeatures.FullDistance(template.Full, features.Full))
                    .DefaultIfEmpty(double.NaN)
                    .Min();
                if (!double.IsNaN(full))
                    return full;

                var rank = _templates.Where(template => template.Card[0] == card[0])
                    .Select(template => CardFeatures.RankDistance(template.Rank, features.Rank))
                    .DefaultIfEmpty(0.5)
                    .Min();
                var suit = _templates.Where(template => template.Card[1] == card[1])
                    .Select(template => CardFeatures.SuitDistance(template.Suit, features.Suit))
                    .DefaultIfEmpty(0.5)
                    .Min();
                return Math.Max(rank, suit);
            }
        }

        private static (string? Label, double Best, double Second) BestByLabel(
            IEnumerable<CardTemplate> templates,
            Func<CardTemplate, string> label,
            Func<CardTemplate, double> distance)
        {
            string? bestLabel = null;
            var best = double.MaxValue;
            var perLabel = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var template in templates)
            {
                var key = label(template);
                var d = distance(template);
                if (!perLabel.TryGetValue(key, out var current) || d < current)
                    perLabel[key] = d;
            }

            foreach (var (key, d) in perLabel)
            {
                if (d < best)
                {
                    best = d;
                    bestLabel = key;
                }
            }

            var second = perLabel.Where(item => item.Key != bestLabel).Select(item => item.Value).DefaultIfEmpty(double.MaxValue).Min();
            return (bestLabel, best, second);
        }
    }

    /// <summary>Lectura estable de las 7 zonas: null = vacia, "?" = hay carta pero no se reconoce.</summary>
    public sealed class LiveReading
    {
        public LiveReading(IReadOnlyList<string?> slots, IReadOnlyList<CardMatch> matches, IReadOnlyList<double> whiteFractions)
        {
            Slots = slots;
            Matches = matches;
            WhiteFractions = whiteFractions;

            var hero = slots.Take(2).ToList();
            HeroCards = hero.All(card => card is not null && card != "?")
                ? TrainerCards.ParseMany(string.Join(" ", hero))
                : new List<int>();
            if (HeroCards.Count != 2)
                HeroCards = new List<int>();

            var board = new List<int>();
            var boardComplete = true;
            foreach (var slot in slots.Skip(2))
            {
                if (slot is null)
                    break;
                if (slot == "?" || !TrainerCards.TryParse(slot, out var card) || board.Contains(card) || HeroCards.Contains(card))
                {
                    boardComplete = false;
                    break;
                }

                board.Add(card);
            }

            BoardCards = boardComplete && board.Count is 0 or 3 or 4 or 5 ? board : new List<int>();
            BoardUnreadable = !boardComplete || board.Count is 1 or 2;
        }

        public IReadOnlyList<string?> Slots { get; }
        public IReadOnlyList<CardMatch> Matches { get; }
        public IReadOnlyList<double> WhiteFractions { get; }
        public List<int> HeroCards { get; }
        public List<int> BoardCards { get; }
        public bool BoardUnreadable { get; }

        /// <summary>Zonas del board que muestran una carta (leida o no).</summary>
        public int VisibleBoardSlots => Slots.Skip(2).TakeWhile(slot => slot is not null).Count();
        public bool HeroVisible => Slots.Count > 1 && (Slots[0] is not null || Slots[1] is not null);

        /// <summary>Solo se ve una de las dos cartas: animacion de reparto.</summary>
        public bool HeroDealing => HeroVisible && (Slots[0] is null || Slots[1] is null);

        public bool HeroUnreadable => HeroVisible && !HeroDealing && HeroCards.Count != 2;

        public string Signature => string.Join("|", Slots.Select(slot => slot ?? "-"));
    }

    /// <summary>
    /// Lee las cartas de la mesa y aprende la baraja. El aprendizaje es automatico: cuando termina
    /// una mano, PokerStars escribe en el historial las cartas reales del heroe y el board, y el
    /// lector etiqueta con ellas lo que vio en cada zona durante esa mano.
    /// </summary>
    public sealed class TrainerCardReader
    {
        private readonly object _sync = new();
        private readonly SlotTracker[] _trackers = Enumerable.Range(0, TrainerCalibration.SlotCount).Select(_ => new SlotTracker()).ToArray();
        private const double MinSegmentSeconds = 1.0;
        private const double UnknownCardSegmentSeconds = 2.0;
        private readonly string?[] _stable = new string?[TrainerCalibration.SlotCount];
        private readonly string?[] _candidate = new string?[TrainerCalibration.SlotCount];
        private readonly int[] _candidateHits = new int[TrainerCalibration.SlotCount];
        private TrainerCalibration _calibration;

        public TrainerCardReader(TrainerCalibration calibration, CardTemplateStore store)
        {
            _calibration = calibration;
            Store = store;
        }

        public CardTemplateStore Store { get; }

        public TrainerCalibration Calibration
        {
            get { lock (_sync) return _calibration; }
            set
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_calibration, value))
                        return;

                    // Las zonas cambian: lo visto con las anteriores no sirve para aprender ni para
                    // estabilizar lecturas.
                    _calibration = value;
                    for (var slot = 0; slot < _trackers.Length; slot++)
                        _trackers[slot] = new SlotTracker();
                    Array.Clear(_stable);
                    Array.Clear(_candidate);
                    Array.Clear(_candidateHits);
                    CalibrationChangedAt = DateTime.Now;
                }
            }
        }

        public DateTime CalibrationChangedAt { get; private set; } = DateTime.MinValue;

        public LiveReading Observe(CapturedFrame frame)
        {
            lock (_sync)
            {
                var matches = new CardMatch[TrainerCalibration.SlotCount];
                var whites = new double[TrainerCalibration.SlotCount];
                for (var slot = 0; slot < TrainerCalibration.SlotCount; slot++)
                {
                    var features = CardFeatures.Extract(frame, _calibration.Slots[slot]);
                    _trackers[slot].Observe(features, frame.CapturedAt);
                    whites[slot] = features?.WhiteFraction ?? 0;

                    string? reading = null;
                    if (features is not null)
                    {
                        var match = Store.Match(features);
                        matches[slot] = match;
                        if (features.LooksLikeCard || match.Confident)
                            reading = match.Confident ? match.Card : "?";
                    }

                    // Dos lecturas iguales seguidas antes de cambiar: evita parpadeos durante las
                    // animaciones de reparto.
                    if (reading == _candidate[slot])
                    {
                        _candidateHits[slot]++;
                    }
                    else
                    {
                        _candidate[slot] = reading;
                        _candidateHits[slot] = 1;
                    }

                    if (_candidateHits[slot] >= 2)
                        _stable[slot] = reading;
                }

                return new LiveReading(_stable.ToArray(), matches, whites);
            }
        }

        /// <summary>
        /// Etiqueta lo visto en pantalla con las cartas reales de la mano que acaba de terminar.
        /// Solo usa segmentos que empezaron despues de la mano anterior (<paramref name="since"/>),
        /// asi el board que sigue en la mesa de la mano previa no se confunde con el nuevo.
        /// </summary>
        public string LearnFromHand(TrainerHand hand, DateTime since)
        {
            if (hand.IsZoom)
                return "Mesa Zoom: el aprendizaje automatico esta desactivado (las manos se solapan).";

            lock (_sync)
            {
                if (CalibrationChangedAt >= since)
                    return "Lector: la calibracion cambio durante la mano; no se aprende de ella.";

                var learned = 0;
                var conflicts = 0;

                for (var i = 0; i < hand.Board.Count && i < 5; i++)
                {
                    var segment = _trackers[2 + i].LongestSegmentSince(since, MinSegmentSeconds);
                    if (segment is not null)
                        Learn(segment, TrainerCards.ToText(hand.Board[i]), ref learned, ref conflicts, newLookAllowed: true);
                }

                if (hand.HeroCards.Count == 2)
                {
                    var first = _trackers[0].LongestSegmentSince(since, MinSegmentSeconds);
                    var second = _trackers[1].LongestSegmentSince(since, MinSegmentSeconds);

                    if (first is not null && second is not null)
                    {
                        // Con el orden en pantalla seguro (colores de palo distintos o plantillas
                        // claras), las cartas propias tambien aprenden otro aspecto: en la baraja de
                        // indices grandes el 8 o el 2 de la mano se ven distintos que en el board y,
                        // ya conocidos por el board, no se aprendian nunca (la mano quedaba sin consejo).
                        var (left, right, certain) = ResolveHeroOrder(first.Best, second.Best, hand.HeroCards[0], hand.HeroCards[1]);
                        Learn(first, TrainerCards.ToText(left), ref learned, ref conflicts, newLookAllowed: certain);
                        Learn(second, TrainerCards.ToText(right), ref learned, ref conflicts, newLookAllowed: certain);
                    }
                }

                if (learned > 0)
                    Store.Save();

                return conflicts > 0
                    ? $"Lector: {learned} carta(s) aprendidas, {conflicts} lectura(s) no coincidian con el historial."
                    : $"Lector: {learned} carta(s) aprendidas de la mano.";
            }
        }

        /// <summary>
        /// Orden en pantalla de las cartas del heroe. El historial las da en el orden de reparto,
        /// pero el cliente puede ordenarlas (PokerStars pone la mas alta a la izquierda: [9d Kh]
        /// se ve como K-9). Se decide por el color del palo, despues por las plantillas y, si nada
        /// lo resuelve (mismo palo y sin plantillas), por la regla que mas veces ha acertado.
        /// </summary>
        /// <returns>Las cartas en el orden de pantalla y si ese orden es seguro (no sale de la regla por votos).</returns>
        private (int Left, int Right, bool Certain) ResolveHeroOrder(CardFeatures left, CardFeatures right, int first, int second)
        {
            bool? straight = null;
            var fourColor = left.SuitColor is InkColor.Blue or InkColor.Green || right.SuitColor is InkColor.Blue or InkColor.Green;
            var expectedFirst = Store.ExpectedColor(TrainerCards.Suits[TrainerCards.Suit(first)], fourColor);
            var expectedSecond = Store.ExpectedColor(TrainerCards.Suits[TrainerCards.Suit(second)], fourColor);
            if (expectedFirst != expectedSecond && left.SuitColor != InkColor.Unknown && right.SuitColor != InkColor.Unknown)
            {
                if (left.SuitColor == expectedFirst && right.SuitColor == expectedSecond)
                    straight = true;
                else if (left.SuitColor == expectedSecond && right.SuitColor == expectedFirst)
                    straight = false;
            }

            if (straight is null)
            {
                var asDealt = Store.DistanceTo(TrainerCards.ToText(first), left) + Store.DistanceTo(TrainerCards.ToText(second), right);
                var swapped = Store.DistanceTo(TrainerCards.ToText(second), left) + Store.DistanceTo(TrainerCards.ToText(first), right);
                if (Math.Abs(asDealt - swapped) >= 0.08)
                    straight = asDealt < swapped;
            }

            if (straight is { } decided)
            {
                RecordOrderVote(first, second, decided);
                return decided ? (first, second, true) : (second, first, true);
            }

            var (ruleLeft, ruleRight) = OrderByRule(first, second);
            return (ruleLeft, ruleRight, false);
        }

        // Votos por regla: 0 = orden del historial, 1 = orden inverso, 2 = la mas alta a la izquierda.
        // Arranca inclinado a "la mas alta a la izquierda", que es lo que hace PokerStars.
        private readonly int[] _orderVotes = { 0, 0, 1 };

        private void RecordOrderVote(int first, int second, bool asDealt)
        {
            if (asDealt) _orderVotes[0]++;
            else _orderVotes[1]++;

            var rankFirst = TrainerCards.Rank(first);
            var rankSecond = TrainerCards.Rank(second);
            if (rankFirst != rankSecond && (rankFirst > rankSecond) == asDealt)
                _orderVotes[2]++;
        }

        private (int Left, int Right) OrderByRule(int first, int second)
        {
            var rule = _orderVotes[2] >= _orderVotes[0] && _orderVotes[2] >= _orderVotes[1] ? 2
                : _orderVotes[0] >= _orderVotes[1] ? 0
                : 1;
            if (rule == 2 && TrainerCards.Rank(first) != TrainerCards.Rank(second))
                return TrainerCards.Rank(first) > TrainerCards.Rank(second) ? (first, second) : (second, first);
            return rule == 1 ? (second, first) : (first, second);
        }

        /// <summary>
        /// Ensenar a mano: el usuario escribe las cartas que se ven en la captura. Las zonas sin
        /// carta (el turn y el river cuando solo hay flop) se saltan y se devuelven sus nombres.
        /// </summary>
        public (int Taught, IReadOnlyList<string> Skipped) Teach(CapturedFrame frame, string heroCards, string boardCards)
        {
            lock (_sync)
            {
                var taught = 0;
                var skipped = new List<string>();
                var hero = TrainerCards.ParseMany(heroCards);
                var board = TrainerCards.ParseMany(boardCards);
                for (var i = 0; i < hero.Count && i < 2; i++)
                    taught += TeachSlot(frame, i, hero[i], skipped);
                for (var i = 0; i < board.Count && i < 5; i++)
                    taught += TeachSlot(frame, 2 + i, board[i], skipped);

                if (taught > 0)
                    Store.Save();
                return (taught, skipped);
            }
        }

        private int TeachSlot(CapturedFrame frame, int slot, int card, List<string> skipped)
        {
            var features = CardFeatures.Extract(frame, _calibration.Slots[slot]);
            if (features is null || !features.LooksLikeCard || !features.HasInk)
            {
                skipped.Add(TrainerCalibration.SlotNames[slot]);
                return 0;
            }

            Store.Add(TrainerCards.ToText(card), features);
            return 1;
        }

        /// <summary>
        /// Aprende la imagen de un segmento con la carta del historial, solo si encaja: si ya hay
        /// plantillas de ese rango y ese palo, la imagen tiene que parecerse a ellas (una carta
        /// realzada u oscurecida del showdown no se parece y se descarta). Mientras el rango o el palo
        /// sean nuevos, solo se aprende de segmentos largos (la carta estuvo quieta en la mesa).
        /// </summary>
        /// <param name="newLookAllowed">
        /// Para el board (cada zona es una carta concreta del historial, sin dudas de orden) y para las
        /// cartas propias cuando su orden en pantalla es seguro:
        /// una carta conocida que se ve muy distinta se aprende como otro aspecto si se vio un buen
        /// rato y no se parece con seguridad a ninguna otra. Asi el lector se adapta a otra baraja
        /// (por ejemplo la de indices grandes): antes, con la baraja vieja ya aprendida, los rangos y
        /// palos que cambiaban mucho (6, 8, 9, 10, J, Q y el diamante) no se aprendian nunca.
        /// </param>
        private void Learn(Segment segment, string card, ref int learned, ref int conflicts, bool newLookAllowed = false)
        {
            var features = segment.Best;
            if (!features.HasInk)
                return;

            // El color del palo tiene que ser el de ese palo: un diamante azul no se aprende como trebol
            // (verde). Antes, sin plantillas del palo de ese color, la distancia salia "sin datos" y la
            // imagen se aprendia igual: un 7 de diamantes guardado como 7 de treboles dejo todos los
            // diamantes en "?" y la mesa sin consejo.
            var expectedColor = Store.ExpectedColor(card[1], features.SuitColor is InkColor.Blue or InkColor.Green);
            if (features.SuitColor != InkColor.Unknown && expectedColor != InkColor.Unknown && features.SuitColor != expectedColor)
                return;

            var (rankDistance, suitDistance) = Store.DistanceToRankAndSuit(card[0], card[1], features);
            var rankKnown = !double.IsNaN(rankDistance);
            var suitKnown = !double.IsNaN(suitDistance);
            if (rankKnown && suitKnown)
            {
                if ((rankDistance > CardTemplateStore.RankAccept || suitDistance > CardTemplateStore.SuitAccept) &&
                    !(newLookAllowed && features.LooksLikeCard && segment.Duration.TotalSeconds >= UnknownCardSegmentSeconds &&
                      !Store.Match(features).Confident))
                    return;
            }
            else if (segment.Duration.TotalSeconds < UnknownCardSegmentSeconds ||
                     rankKnown && rankDistance > CardTemplateStore.RankAccept ||
                     suitKnown && suitDistance > CardTemplateStore.SuitAccept)
            {
                return;
            }

            // El historial manda: si otra plantilla leia otra carta (con seguridad o empatada),
            // esa plantilla es la sospechosa.
            var match = Store.Match(features);
            if (match.Card is not null && match.Card != card)
            {
                Store.PunishLookalike(features, card);
                conflicts++;
            }

            Store.Add(card, features);
            learned++;
        }

        private sealed class Segment
        {
            public Segment(DateTime start, CardFeatures features)
            {
                Start = start;
                LastSeen = start;
                Best = features;
                Last = features;
                Count = 1;
            }

            public DateTime Start { get; }
            public DateTime LastSeen { get; set; }
            public int Count { get; set; }
            public CardFeatures Best { get; set; }
            public CardFeatures Last { get; set; }
            public TimeSpan Duration => LastSeen - Start;
        }

        /// <summary>
        /// Historial de lo visto en una zona: cada tramo seguido con la misma carta es un segmento.
        /// Un hueco vacio o un cambio claro de imagen abre uno nuevo. De cada segmento se guarda la
        /// captura mas "limpia" (mas blanca), que evita aprender la carta oscurecida del showdown.
        /// </summary>
        private sealed class SlotTracker
        {
            private readonly List<Segment> _segments = new();
            private Segment? _current;

            public void Observe(CardFeatures? features, DateTime at)
            {
                if (features is null || !features.LooksLikeCard)
                {
                    _current = null;
                    return;
                }

                if (_current is null || CardFeatures.LooksDifferent(features, _current.Last))
                {
                    _current = new Segment(at, features);
                    _segments.Add(_current);
                    if (_segments.Count > 16)
                        _segments.RemoveAt(0);
                    return;
                }

                _current.Last = features;
                _current.LastSeen = at;
                _current.Count++;
                if (features.WhiteFraction > _current.Best.WhiteFraction)
                    _current.Best = features;
            }

            /// <summary>
            /// El segmento mas largo de la mano: la carta real esta quieta calles enteras, mientras
            /// que el realce del showdown, el fold o la reordenacion al repartir duran 1-5 capturas.
            /// </summary>
            public Segment? LongestSegmentSince(DateTime since, double minSeconds) =>
                _segments
                    .Where(segment => segment.Start >= since && segment.Count >= 3 && segment.Duration.TotalSeconds >= minSeconds)
                    .OrderByDescending(segment => segment.Duration)
                    .FirstOrDefault();
        }
    }
}
