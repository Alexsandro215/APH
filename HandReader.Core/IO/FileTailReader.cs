using System;
using System.IO;
using System.Text;
using System.Threading;

namespace HandReader.Core.IO;

/// <summary>
/// Tail simple por sondeo, tolerante a append y a reemplazos del archivo.
/// Lee nuevas líneas y dispara OnLines(string[]) cuando hay novedades.
/// </summary>
public sealed class FileTailReader : IDisposable
{
    private const int MaxChunkBytes = 4 * 1024 * 1024;

    // BOM reconocidos, del mas largo al mas corto: el de UTF-32 LE (FF FE 00 00) empieza
    // igual que el de UTF-16 LE (FF FE) y hay que comprobarlo antes.
    private static readonly (byte[] Bom, Encoding Encoding)[] ByteOrderMarks =
    {
        (new byte[] { 0xFF, 0xFE, 0x00, 0x00 }, Encoding.UTF32),
        (new byte[] { 0x00, 0x00, 0xFE, 0xFF }, new UTF32Encoding(bigEndian: true, byteOrderMark: true)),
        (new byte[] { 0xEF, 0xBB, 0xBF }, Encoding.UTF8),
        (new byte[] { 0xFF, 0xFE }, Encoding.Unicode),
        (new byte[] { 0xFE, 0xFF }, Encoding.BigEndianUnicode),
    };

    private readonly string _path;
    private readonly Thread _thread;
    private volatile bool _running = true;

    public event Action<string[]>? OnLines;

    /// <summary>Error al leer el archivo (bloqueado, borrado, sin permisos). El lector sigue sondeando.</summary>
    public event Action<Exception>? OnError;

    public FileTailReader(string path)
    {
        _path = path;
        _thread = new Thread(Loop) { IsBackground = true, Name = "FileTailReader" };
    }

    public void Start() => _thread.Start();

    private void Loop()
    {
        long position = 0;

        // El BOM solo esta al principio del archivo: la codificacion que marca, y con ella los
        // bytes del salto de linea, se conserva para todo lo que se escriba despues.
        var encoding = Encoding.UTF8;
        var newline = new[] { (byte)'\n' };

        while (_running)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    Thread.Sleep(500);
                    continue;
                }

                using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                var length = fs.Length;
                if (position > length) position = 0; // truncado/reemplazo
                if (position == length)
                {
                    Thread.Sleep(250);
                    continue;
                }

                fs.Seek(position, SeekOrigin.Begin);

                var pending = (int)Math.Min(length - position, MaxChunkBytes);
                var bytes = new byte[pending];
                var filled = 0;
                while (filled < pending)
                {
                    var read = fs.Read(bytes, filled, pending - filled);
                    if (read <= 0) break;
                    filled += read;
                }

                // Leyendo desde el byte 0 (inicio, truncado o reemplazo) se decide otra vez la
                // codificacion por el BOM, que puede ser distinta si el archivo es otro.
                var start = 0;
                if (position == 0)
                {
                    var detected = DetectEncoding(bytes.AsSpan(0, filled), out start);
                    if (detected is null)
                    {
                        // Aun no se distingue entre BOM que empiezan igual: siguiente sondeo.
                        Thread.Sleep(250);
                        continue;
                    }

                    encoding = detected;
                    newline = encoding.GetBytes("\n");
                }

                // Solo se consume hasta el ultimo salto de linea. La sala escribe el archivo
                // mientras jugamos: sin este corte, una linea a medio escribir se entregaba
                // como si estuviera completa y su resto llegaba despues como otra linea.
                var consumed = EndOfLastLine(bytes, start, filled, newline);
                if (consumed < 0)
                {
                    Thread.Sleep(250);
                    continue;
                }

                // Cortar tras un salto de linea entero nunca parte un caracter: no aparece
                // dentro de una secuencia multibyte de UTF-8 ni de un par sustituto de UTF-16.
                var text = encoding.GetString(bytes, start, consumed - start);
                position += consumed;

                var batch = text.Split('\n');
                var lines = new System.Collections.Generic.List<string>(batch.Length);
                foreach (var line in batch)
                {
                    if (line.Length == 0) continue;
                    lines.Add(line[^1] == '\r' ? line[..^1] : line);
                }

                if (lines.Count > 0)
                    OnLines?.Invoke(lines.ToArray());
            }
            catch (Exception ex)
            {
                // El lector sigue intentando; quien lo use decide si registrarlo.
                try { OnError?.Invoke(ex); } catch { /* el aviso nunca debe parar la lectura */ }
            }

            Thread.Sleep(250); // frecuencia de muestreo
        }
    }

    // Codificacion que marca el BOM, como hacia StreamReader; sin BOM, UTF-8. Devuelve null
    // mientras lo leido aun pueda ser el principio de un BOM mas largo.
    private static Encoding? DetectEncoding(ReadOnlySpan<byte> head, out int bomLength)
    {
        bomLength = 0;
        foreach (var (bom, encoding) in ByteOrderMarks)
        {
            if (head.Length < bom.Length)
            {
                if (bom.AsSpan().StartsWith(head)) return null;
            }
            else if (head.StartsWith(bom))
            {
                bomLength = bom.Length;
                return encoding;
            }
        }

        return Encoding.UTF8;
    }

    // Fin del ultimo salto de linea completo dentro de bytes[start..count), o -1 si no hay.
    // Se recorre por unidades de codigo enteras: bytes empieza siempre en el limite de una
    // unidad (position solo avanza por unidades enteras, BOM incluido). En UTF-16/32 el byte
    // 0x0A tambien aparece dentro de otros caracteres, y una unidad a medio escribir al
    // final se deja para el siguiente sondeo.
    private static int EndOfLastLine(byte[] bytes, int start, int count, byte[] newline)
    {
        var unit = newline.Length;
        for (var i = start + (count - start) / unit * unit - unit; i >= start; i -= unit)
        {
            if (bytes.AsSpan(i, unit).SequenceEqual(newline))
                return i + unit;
        }

        return -1;
    }

    public void Dispose()
    {
        _running = false;
        try { _thread.Join(1000); } catch { /* ignore */ }
    }
}
