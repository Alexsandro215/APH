using System;
using System.Collections.Generic;
using System.Linq;

namespace Hud.App.Services.Trainer
{
    /// <summary>
    /// Autocalibracion del lector: busca en la captura las caras blancas de las cartas, reconoce
    /// la fila del board (3 a 5 cartas alineadas en el centro) y la pareja de cartas del heroe
    /// (debajo del board, cerca del centro inferior) y calcula la zona del indice (rango y palo)
    /// de cada una. Con el flop en la mesa ya se deducen las posiciones del turn y el river.
    /// </summary>
    public static class CardLocator
    {
        // Zona del indice dentro de la carta, en fracciones del ancho/alto de una carta completa.
        // Medido sobre la baraja de PokerStars: el rango y el palo pequeno ocupan aprox. el 7-30%
        // del ancho y el 4-46% del alto; los pips del centro empiezan hacia el 34% del ancho.
        private const double IndexLeft = 0.03;
        private const double IndexTop = 0.025;
        private const double IndexWidth = 0.34;
        private const double IndexHeight = 0.50;

        public readonly record struct CardBox(int X, int Y, int W, int H)
        {
            public int Right => X + W - 1;
            public int Bottom => Y + H - 1;
            public double CenterX => X + W / 2.0;
            public double CenterY => Y + H / 2.0;
        }

        public sealed record LocateResult(
            TrainerCalibration? Calibration,
            IReadOnlyList<CardBox> Board,
            IReadOnlyList<CardBox> Hero,
            string Report);

        public static LocateResult Locate(CapturedFrame frame)
        {
            var faces = FindCardFaces(frame);
            var board = FindBoardRow(faces, frame);
            if (board.Count < 3)
            {
                return new LocateResult(null, board, Array.Empty<CardBox>(),
                    "No veo el board. Autocalibra con al menos el flop en la mesa (y mejor con tus cartas a la vista).");
            }

            var cardW = Median(board.Select(card => (double)card.W));
            var cardH = Median(board.Select(card => (double)card.H));
            var step = board.Count > 1
                ? Median(board.Zip(board.Skip(1), (left, right) => (double)(right.X - left.X)))
                : cardW * 1.1;
            var boardY = (int)Math.Round(Median(board.Select(card => (double)card.Y)));

            // Showdown: el cliente sube las cartas ganadoras y oscurece las demas. Esa fila esta
            // desplazada y no sirve para calibrar (se reconoce por una carta gris al lado).
            if (HasDarkenedCardBeside(frame, board, step, cardW, cardH))
            {
                return new LocateResult(null, board, Array.Empty<CardBox>(),
                    "Showdown en curso (cartas realzadas): se calibra en la siguiente mano.");
            }

            var hero = FindHeroPair(faces.Where(face => !board.Contains(face)).ToList(), board, cardW, cardH, frame);

            var calibration = new TrainerCalibration { ClientWidth = frame.Width, ClientHeight = frame.Height };
            for (var slot = 0; slot < 5; slot++)
            {
                var x = slot < board.Count ? board[slot].X : (int)Math.Round(board[0].X + slot * step);
                var y = slot < board.Count ? board[slot].Y : boardY;
                calibration.Slots[2 + slot] = IndexRect(frame, x, y, cardW, cardH, cardH);
            }

            if (hero.Count == 2)
            {
                for (var i = 0; i < 2; i++)
                    calibration.Slots[i] = IndexRect(frame, hero[i].X, hero[i].Y, cardW, cardH, hero[i].H);
            }

            var report = hero.Count == 2
                ? $"Autocalibrado: board ({board.Count} cartas a la vista, el resto deducidas) y tus 2 cartas."
                : $"Board autocalibrado ({board.Count} cartas a la vista). No veo tus cartas: repite la autocalibracion cuando te repartan.";
            return new LocateResult(calibration, board, hero, report);
        }

        /// <summary>
        /// Zonas blancas con forma y tamano de carta. Una cara de carta es una sola mancha blanca
        /// conectada (el margen blanco rodea los pips), aunque la tape en parte la placa del jugador.
        /// </summary>
        public static List<CardBox> FindCardFaces(CapturedFrame frame)
        {
            var width = frame.Width;
            var height = frame.Height;
            var pixels = frame.Pixels;
            var white = new bool[width * height];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var b = pixels[i];
                var g = pixels[i + 1];
                var r = pixels[i + 2];
                var min = Math.Min(r, Math.Min(g, b));
                var max = Math.Max(r, Math.Max(g, b));
                white[y * width + x] = min >= 200 && max - min <= 35;
            }

            var seen = new bool[width * height];
            var stack = new Stack<int>();
            var faces = new List<CardBox>();
            for (var start = 0; start < white.Length; start++)
            {
                if (!white[start] || seen[start])
                    continue;

                int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1, count = 0;
                seen[start] = true;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    var index = stack.Pop();
                    var x = index % width;
                    var y = index / width;
                    count++;
                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;

                    if (x > 0) Visit(index - 1);
                    if (x < width - 1) Visit(index + 1);
                    if (y > 0) Visit(index - width);
                    if (y < height - 1) Visit(index + width);
                }

                var box = new CardBox(left, top, right - left + 1, bottom - top + 1);
                var fill = count / (double)(box.W * box.H);
                var aspect = box.W / (double)box.H;
                if (box.W >= width * 0.025 && box.W <= width * 0.14 &&
                    box.H >= height * 0.035 && box.H <= height * 0.25 &&
                    fill >= 0.30 && aspect >= 0.45 && aspect <= 1.7)
                {
                    faces.Add(box);
                }
            }

            return faces;

            void Visit(int neighbor)
            {
                if (!white[neighbor] || seen[neighbor])
                    return;
                seen[neighbor] = true;
                stack.Push(neighbor);
            }
        }

        /// <summary>3 a 5 cartas del mismo tamano en fila; si hay varias filas, la mas centrada.</summary>
        private static List<CardBox> FindBoardRow(List<CardBox> faces, CapturedFrame frame)
        {
            var best = new List<CardBox>();
            var bestScore = double.MaxValue;
            foreach (var anchor in faces)
            {
                var row = faces
                    .Where(face =>
                        Math.Abs(face.Y - anchor.Y) <= anchor.H * 0.12 &&
                        Math.Abs(face.H - anchor.H) <= anchor.H * 0.15 &&
                        Math.Abs(face.W - anchor.W) <= anchor.W * 0.15)
                    .OrderBy(face => face.X)
                    .ToList();
                if (row.Count < 3)
                    continue;

                // Las cartas del board van seguidas: separacion parecida al ancho de una carta.
                var steps = row.Zip(row.Skip(1), (left, right) => right.X - left.X).ToList();
                if (steps.Any(step => step < anchor.W * 0.9 || step > anchor.W * 1.6))
                    continue;

                var score = Math.Abs(row.Average(face => face.CenterY) - frame.Height * 0.42) +
                            Math.Abs(row.Average(face => face.CenterX) - frame.Width * 0.5) * 0.5;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = row.Take(5).ToList();
                }
            }

            return best;
        }

        /// <summary>
        /// Las dos cartas del heroe: del ancho de las del board, por debajo de el y lo mas cerca
        /// posible del centro inferior. Pueden verse recortadas por la placa del jugador.
        /// </summary>
        private static List<CardBox> FindHeroPair(List<CardBox> faces, IReadOnlyList<CardBox> board, double cardW, double cardH, CapturedFrame frame)
        {
            // El heroe se sienta abajo en el centro: solo se aceptan cartas ahi. Las que ensena un
            // rival en el showdown (asientos laterales, mas arriba) no se confunden con las suyas.
            var boardBottom = board.Max(card => card.Bottom);
            var candidates = faces
                .Where(face => face.Y > boardBottom + cardH * 0.1 &&
                               face.Y >= frame.Height * 0.55 &&
                               face.H >= cardH * 0.3 &&
                               Math.Abs(face.W - cardW) <= cardW * 0.2)
                .ToList();

            var best = new List<CardBox>();
            var bestScore = double.MaxValue;
            for (var i = 0; i < candidates.Count; i++)
            for (var j = 0; j < candidates.Count; j++)
            {
                var left = candidates[i];
                var right = candidates[j];
                var gap = right.X - left.X;
                if (gap < cardW * 0.35 || gap > cardW * 1.5 || Math.Abs(left.Y - right.Y) > cardH * 0.1)
                    continue;

                var centerX = (left.CenterX + right.CenterX) / 2;
                if (Math.Abs(centerX - frame.Width * 0.5) > frame.Width * 0.10)
                    continue;

                var score = Math.Abs(centerX - frame.Width * 0.5) + Math.Abs(frame.Height - left.CenterY) * 0.5;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = new List<CardBox> { left, right };
                }
            }

            return best;
        }

        /// <summary>
        /// Carta oscurecida (gris uniforme, poco color) en el hueco anterior o siguiente de la fila, a
        /// la altura de la fila o un poco mas abajo (las ganadoras suben). Pasa en el showdown.
        /// </summary>
        private static bool HasDarkenedCardBeside(CapturedFrame frame, IReadOnlyList<CardBox> row, double step, double cardW, double cardH)
        {
            foreach (var x in new[] { row[0].X - step, row[^1].X + step })
            {
                for (var dy = -0.1; dy <= 0.45; dy += 0.15)
                {
                    var y = row[0].Y + dy * cardH;
                    if (GrayFraction(frame, x + cardW * 0.15, y + cardH * 0.15, cardW * 0.7, cardH * 0.5) >= 0.5)
                        return true;
                }
            }

            return false;
        }

        private static double GrayFraction(CapturedFrame frame, double left, double top, double width, double height)
        {
            var x0 = (int)Math.Round(left);
            var y0 = (int)Math.Round(top);
            var x1 = (int)Math.Round(left + width);
            var y1 = (int)Math.Round(top + height);
            if (x0 < 0 || y0 < 0 || x1 >= frame.Width || y1 >= frame.Height || x1 <= x0 || y1 <= y0)
                return 0;

            var gray = 0;
            var total = 0;
            for (var y = y0; y < y1; y += 2)
            for (var x = x0; x < x1; x += 2)
            {
                var i = (y * frame.Width + x) * 4;
                var b = frame.Pixels[i];
                var g = frame.Pixels[i + 1];
                var r = frame.Pixels[i + 2];
                var min = Math.Min(r, Math.Min(g, b));
                var max = Math.Max(r, Math.Max(g, b));
                total++;
                if (min >= 90 && min <= 190 && max - min <= 25)
                    gray++;
            }

            return total == 0 ? 0 : gray / (double)total;
        }

        private static NormalizedRect IndexRect(CapturedFrame frame, int x, int y, double cardW, double cardH, double visibleH)
        {
            var left = x + cardW * IndexLeft;
            var top = y + cardH * IndexTop;
            var width = cardW * IndexWidth;
            var height = Math.Min(cardH * IndexHeight, visibleH - cardH * 0.04);
            left = Math.Clamp(left, 0, frame.Width - 2);
            top = Math.Clamp(top, 0, frame.Height - 2);
            width = Math.Clamp(width, 1, frame.Width - left);
            height = Math.Clamp(height, 1, frame.Height - top);
            return new NormalizedRect(left / frame.Width, top / frame.Height, width / frame.Width, height / frame.Height);
        }

        private static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(value => value).ToList();
            if (sorted.Count == 0)
                return 0;
            return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        }
    }
}
