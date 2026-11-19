using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Hud.App.Services.Trainer
{
    /// <summary>Area cliente de una ventana capturada en BGRX de 32 bits, de arriba a abajo.</summary>
    public sealed class CapturedFrame
    {
        public CapturedFrame(int width, int height, byte[] pixels, DateTime? capturedAt = null)
        {
            Width = width;
            Height = height;
            Pixels = pixels;
            CapturedAt = capturedAt ?? DateTime.Now;
        }

        public int Width { get; }
        public int Height { get; }
        public byte[] Pixels { get; }

        /// <summary>Momento de la captura (una grabacion reproducida trae el suyo).</summary>
        public DateTime CapturedAt { get; }

        public BitmapSource ToBitmapSource()
        {
            // Bgr32 y no Bgra32: GDI deja el alfa a cero y la imagen saldria transparente.
            var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr32, null, Pixels, Width * 4);
            bitmap.Freeze();
            return bitmap;
        }
    }

    /// <summary>
    /// Captura de la mesa de PokerStars. Primero PrintWindow con PW_RENDERFULLCONTENT, que funciona
    /// aunque la mesa este tapada por otra ventana; si devuelve negro (algunas ventanas aceleradas
    /// por GPU), copia de la pantalla en la posicion de la mesa, pero solo si la mesa esta a la vista.
    /// </summary>
    public static class TableCapture
    {
        public static CapturedFrame? CaptureClient(IntPtr window)
        {
            if (window == IntPtr.Zero || !IsWindow(window) || IsIconic(window))
                return null;

            // En pantallas escaladas (125%, 150%) las medidas de otra ventana se virtualizan si este
            // hilo no es consciente del DPI por monitor; la captura saldria recortada.
            var previousContext = TrySetPerMonitorDpiAwareness();
            try
            {
                if (!GetClientRect(window, out var rect))
                    return null;

                var width = rect.Right - rect.Left;
                var height = rect.Bottom - rect.Top;
                if (width < 50 || height < 50)
                    return null;

                var frame = CaptureWithPrintWindow(window, width, height);
                if (frame is null || IsBlank(frame))
                {
                    // La copia de pantalla toma lo que haya encima de la mesa: solo vale si la mesa se
                    // ve. Tapada por otra ventana (otra mesa, incluso de dinero real) no se lee nada.
                    frame = IsUncovered(window, width, height)
                        ? CaptureFromScreen(window, width, height) ?? frame
                        : null;
                }

                return frame;
            }
            finally
            {
                RestoreDpiAwareness(previousContext);
            }
        }

        public static string GetWindowTitle(IntPtr window)
        {
            var length = GetWindowTextLength(window);
            if (length <= 0)
                return "";

            var builder = new StringBuilder(length + 1);
            GetWindowText(window, builder, builder.Capacity);
            return builder.ToString();
        }

        public static bool IsAlive(IntPtr window) => window != IntPtr.Zero && IsWindow(window);

        private static CapturedFrame? CaptureWithPrintWindow(IntPtr window, int width, int height)
        {
            var windowDc = GetDC(window);
            if (windowDc == IntPtr.Zero)
                return null;

            var memoryDc = CreateCompatibleDC(windowDc);
            var bitmap = CreateCompatibleBitmap(windowDc, width, height);
            try
            {
                var old = SelectObject(memoryDc, bitmap);
                var printed = PrintWindow(window, memoryDc, PwClientOnly | PwRenderFullContent);
                SelectObject(memoryDc, old);
                return printed ? ReadBitmap(memoryDc, bitmap, width, height) : null;
            }
            finally
            {
                DeleteObject(bitmap);
                DeleteDC(memoryDc);
                ReleaseDC(window, windowDc);
            }
        }

        /// <summary>
        /// Ninguna ventana visible queda por encima de la mesa sobre su area cliente, en el orden de
        /// ventanas de Windows y contando las transparentes (como el HUD de APH). Solo entonces la copia
        /// de pantalla es de verdad la mesa.
        /// </summary>
        private static bool IsUncovered(IntPtr window, int width, int height)
        {
            if (!IsWindowVisible(window))
                return false;

            var origin = new NativePoint();
            if (!ClientToScreen(window, ref origin))
                return false;

            var right = origin.X + width;
            var bottom = origin.Y + height;
            var root = GetAncestor(window, GaRoot);
            for (var above = GetWindow(root, GwHwndPrev); above != IntPtr.Zero; above = GetWindow(above, GwHwndPrev))
            {
                if (!IsWindowVisible(above) || IsIconic(above) || IsCloaked(above) || !VisibleBounds(above, out var bounds))
                    continue;

                if (bounds.Left < right && bounds.Right > origin.X && bounds.Top < bottom && bounds.Bottom > origin.Y)
                    return false;
            }

            return true;
        }

        /// <summary>Lo que se ve de la ventana, sin los bordes invisibles de Windows 10/11.</summary>
        private static bool VisibleBounds(IntPtr window, out NativeRect bounds)
        {
            if (DwmGetWindowAttribute(window, DwmwaExtendedFrameBounds, out bounds, Marshal.SizeOf<NativeRect>()) == 0)
                return bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
            return GetWindowRect(window, out bounds) && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
        }

        /// <summary>Ventanas "visibles" que el escritorio oculta (apps en segundo plano, otro escritorio virtual).</summary>
        private static bool IsCloaked(IntPtr window) =>
            DwmGetWindowAttribute(window, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

        private static CapturedFrame? CaptureFromScreen(IntPtr window, int width, int height)
        {
            var origin = new NativePoint();
            if (!ClientToScreen(window, ref origin))
                return null;

            var screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
                return null;

            var memoryDc = CreateCompatibleDC(screenDc);
            var bitmap = CreateCompatibleBitmap(screenDc, width, height);
            try
            {
                var old = SelectObject(memoryDc, bitmap);
                var copied = BitBlt(memoryDc, 0, 0, width, height, screenDc, origin.X, origin.Y, SrcCopy | CaptureBlt);
                SelectObject(memoryDc, old);
                return copied ? ReadBitmap(memoryDc, bitmap, width, height) : null;
            }
            finally
            {
                DeleteObject(bitmap);
                DeleteDC(memoryDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        // GetDIBits exige que el bitmap no este seleccionado en ningun DC: se llama despues de
        // devolver el objeto original al DC de memoria.
        private static CapturedFrame? ReadBitmap(IntPtr dc, IntPtr bitmap, int width, int height)
        {
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0
                },
                Colors = new uint[4]
            };

            var pixels = new byte[width * height * 4];
            var lines = GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref info, 0);
            return lines == height ? new CapturedFrame(width, height, pixels) : null;
        }

        private static bool IsBlank(CapturedFrame frame)
        {
            var dark = 0;
            var samples = 0;
            for (var i = 0; i < frame.Pixels.Length; i += 4 * 97)
            {
                samples++;
                if (frame.Pixels[i] < 8 && frame.Pixels[i + 1] < 8 && frame.Pixels[i + 2] < 8)
                    dark++;
            }

            return samples == 0 || dark > samples * 0.97;
        }

        private static IntPtr TrySetPerMonitorDpiAwareness()
        {
            try
            {
                return SetThreadDpiAwarenessContext(new IntPtr(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
            }
            catch (EntryPointNotFoundException)
            {
                return IntPtr.Zero;
            }
        }

        private static void RestoreDpiAwareness(IntPtr previous)
        {
            if (previous == IntPtr.Zero)
                return;

            try
            {
                SetThreadDpiAwarenessContext(previous);
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        private const uint GaRoot = 2;
        private const uint GwHwndPrev = 3;
        private const int DwmwaExtendedFrameBounds = 9;
        private const int DwmwaCloaked = 14;
        private const uint PwClientOnly = 0x1;
        private const uint PwRenderFullContent = 0x2;
        private const uint SrcCopy = 0x00CC0020;
        private const uint CaptureBlt = 0x40000000;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public int Size;
            public int Width;
            public int Height;
            public short Planes;
            public short BitCount;
            public int Compression;
            public int SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public int ClrUsed;
            public int ClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public BitmapInfoHeader Header;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public uint[] Colors;
        }

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out NativeRect lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr ho);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, byte[] lpvBits, ref BitmapInfo lpbmi, uint usage);
    }
}
