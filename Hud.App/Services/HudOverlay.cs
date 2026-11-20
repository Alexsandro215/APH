using System.IO;
using System.Text.Json;
using HandReader.Core.Models;
using HandReader.Core.Stats;

namespace Hud.App.Services
{
    /// <summary>
    /// Lo que el HUD necesita de una mesa: jugadores con sus asientos y el tamano de la mesa. El
    /// historial de la mesa sirve para sumar lo que ya jugaste con cada rival en tus otras mesas.
    /// </summary>
    public sealed record HudTableSnapshot(
        IReadOnlyList<PlayerStats> Players,
        IReadOnlyList<TableSeat> Seats,
        int TableSize,
        StakeProfile Stake,
        IReadOnlyList<string>? LastHand = null,
        DateTime LastHandAt = default,
        string? SourcePath = null);

    /// <summary>
    /// Donde va cada cuadro del HUD. PokerStars gira la mesa para que el heroe quede abajo; los
    /// asientos siguen en el sentido de las agujas del reloj. El "lugar" 0 es el del heroe y el
    /// lugar k es el jugador k asientos despues de el.
    /// </summary>
    public static class HudLayout
    {
        /// <summary>
        /// Centro de cada cuadro (relativo a la ventana de la mesa), sin tapar el nombre ni las fichas:
        /// en 6-max, encima de la placa de cada jugador y, el del heroe, a la derecha de la suya.
        /// </summary>
        public static (double X, double Y)[] DefaultCenters(int tableSize) => tableSize switch
        {
            2 => new[] { (0.80, 0.735), (0.80, 0.175) },
            6 => new[] { (0.80, 0.735), (0.13, 0.435), (0.135, 0.165), (0.50, 0.075), (0.865, 0.165), (0.87, 0.435) },
            _ => Ellipse(Math.Clamp(tableSize, 2, StatsAggregator.MaxSeats))
        };

        /// <summary>Lugar en pantalla de un asiento: 0 = el heroe (abajo) y luego en el sentido del reloj.</summary>
        public static int PlaceOf(int seat, int heroSeat, int tableSize)
        {
            var size = Math.Max(2, tableSize);
            var reference = heroSeat > 0 ? heroSeat : 1;
            return ((seat - reference) % size + size) % size;
        }

        private static (double X, double Y)[] Ellipse(int count)
        {
            var points = new (double X, double Y)[count];
            for (var k = 0; k < count; k++)
            {
                if (k == 0)
                {
                    points[k] = (0.80, 0.735);
                    continue;
                }
                var angle = Math.PI / 2 + 2 * Math.PI * k / count; // abajo y en el sentido del reloj
                var x = 0.5 + 0.40 * Math.Cos(angle);
                var y = 0.45 + 0.30 * Math.Sin(angle) - 0.10;
                points[k] = (Math.Clamp(x, 0.10, 0.90), Math.Clamp(y, 0.07, 0.80));
            }
            return points;
        }
    }

    /// <summary>
    /// Opciones del HUD sobre la mesa, guardadas en %APPDATA%\APH\hud.json: modo (minimo o
    /// completo), tamano, ocultar el cuadro propio y la posicion de cada cuadro si se movio.
    /// </summary>
    public sealed class HudOverlaySettings
    {
        public bool FullMode { get; set; } = true;

        /// <summary>0 chico, 1 normal, 2 grande.</summary>
        public int Size { get; set; } = 1;

        public bool HideHero { get; set; }

        /// <summary>
        /// Recomendacion del solver de entrenamiento encima de la mesa, en el turno del heroe. Solo en
        /// mesas de dinero ficticio (la lectura es la del Modo Entrenador, que bloquea dinero real y torneos).
        /// </summary>
        public bool ShowSolver { get; set; } = true;

        /// <summary>Repaso de la mano anterior (preflop, board, showdown y quien gano) unos segundos en la mesa.</summary>
        public bool ShowRecap { get; set; } = true;

        /// <summary>
        /// Nota de tu mano anterior (aciertos, dudas y errores) unos segundos junto a tu cuadro.
        /// Apagada por defecto: el usuario la quito de la mesa.
        /// </summary>
        public bool ShowGrade { get; set; }

        /// <summary>"6:3" (asientos de la mesa:lugar) -> centro del cuadro [x, y] relativo a la mesa.</summary>
        public Dictionary<string, double[]> Positions { get; set; } = new();

        public static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "APH", "hud.json");

        private static HudOverlaySettings? _current;

        public static HudOverlaySettings Current => _current ??= Load();

        /// <summary>Modo "Mover HUD": los cuadros se pueden arrastrar y la mesa no recibe clics.</summary>
        public static bool MoveMode { get; private set; }

        public static event EventHandler? MoveModeChanged;

        public static void SetMoveMode(bool on)
        {
            if (MoveMode == on)
                return;
            MoveMode = on;
            MoveModeChanged?.Invoke(null, EventArgs.Empty);
        }

        public double SizeFactor => Size switch { 0 => 0.85, 2 => 1.25, _ => 1.0 };

        /// <summary>Tabla de rango en la mesa: 0 chica, 1 normal, 2 grande.</summary>
        public int RangeSize { get; set; } = 1;

        /// <summary>Lado de cada casilla de la tabla de rango, en proporcion a la letra del HUD.</summary>
        public double RangeCellFactor => RangeSize switch { 0 => 0.8, 2 => 1.45, _ => 1.1 };

        public (double X, double Y) CenterFor(int tableSize, int place)
        {
            if (Positions.TryGetValue(Key(tableSize, place), out var saved) && saved.Length == 2)
                return (saved[0], saved[1]);
            var defaults = HudLayout.DefaultCenters(tableSize);
            return defaults[Math.Clamp(place, 0, defaults.Length - 1)];
        }

        public void SetCenter(int tableSize, int place, double x, double y)
        {
            Positions[Key(tableSize, place)] = new[] { Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1) };
            Save();
        }

        public void ResetPositions()
        {
            Positions.Clear();
            Save();
        }

        private static string Key(int tableSize, int place) => $"{tableSize}:{place}";

        private static HudOverlaySettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<HudOverlaySettings>(File.ReadAllText(FilePath)) ?? new HudOverlaySettings();
            }
            catch (Exception ex)
            {
                AppLog.Warn("No se pudieron leer las opciones del HUD", ex);
            }
            return new HudOverlaySettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                AppLog.Warn("No se pudieron guardar las opciones del HUD", ex);
            }
        }
    }
}
