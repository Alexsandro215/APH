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
    private readonly string _path;
    private readonly Thread _thread;
    private volatile bool _running = true;

    public event Action<string[]>? OnLines;

    public FileTailReader(string path)
    {
        _path = path;
        _thread = new Thread(Loop) { IsBackground = true, Name = "FileTailReader" };
    }

    public void Start() => _thread.Start();

    private void Loop()
    {
        long position = 0;
        var sb = new StringBuilder(8192);

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
                if (position > fs.Length) position = 0; // truncado/reemplazo
                fs.Seek(position, SeekOrigin.Begin);

                using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 8192, leaveOpen: true);
                string? line;
                var batch = new System.Collections.Generic.List<string>(64);
                while ((line = sr.ReadLine()) is not null)
                {
                    batch.Add(line);
                }

                position = fs.Position;

                if (batch.Count > 0)
                {
                    OnLines?.Invoke(batch.ToArray());
                }
            }
            catch
            {
                // Silencioso: en producción, loguear.
            }

            Thread.Sleep(250); // frecuencia de muestreo
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _thread.Join(1000); } catch { /* ignore */ }
    }
}
