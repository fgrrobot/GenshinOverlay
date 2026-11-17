using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace TeyvatLens.Core.Vision;

/// <summary>
/// Снимок экрана карты (клавиша M) для абсолютной привязки.
///
/// Приложение сворачивается, игра остаётся на экране, и GDI снимает клиентскую
/// область окна игры. Из снимка берётся центральный квадрат: так отрезаются
/// кнопки и панели по краям, а центр совпадает с маркером игрока.
///
/// Ограничение: эксклюзивный полноэкранный режим может отдать чёрный кадр.
/// В этом случае нужен оконный режим без рамки — приложение честно сообщит
/// об ошибке.
/// </summary>
public static class MapScreenCapture
{
    /// <summary>Доля меньшей стороны, остающаяся после центрального кропа.</summary>
    public const double DefaultCropFraction = 0.82;

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    /// <summary>Снимает клиентскую область окна игры и вырезает центральный квадрат.</summary>
    public static Mat CaptureGame(double cropFraction = DefaultCropFraction) =>
        CropCenter(ScreenCapture.CaptureArea(FindGameClientRectangle()), cropFraction);

    /// <summary>
    /// Вырезает центральный квадрат. Дробная часть отбрасывается влево/вверх,
    /// чтобы центр кропа совпадал с центром исходного изображения.
    /// </summary>
    public static Mat CropCenter(Mat source, double fraction)
    {
        if (source.Empty())
        {
            throw new ArgumentException("Исходное изображение пусто.", nameof(source));
        }

        var clamped = Math.Clamp(fraction, 0.3, 1.0);
        var side = (int)Math.Round(Math.Min(source.Width, source.Height) * clamped);
        side = Math.Clamp(side, 1, Math.Min(source.Width, source.Height));
        var left = (source.Width - side) / 2;
        var top = (source.Height - side) / 2;

        using var roi = new Mat(source, new OpenCvSharp.Rect(left, top, side, side));
        return roi.Clone();
    }

    /// <summary>
    /// Проверяет, что кадр не чёрный. Низкий разброс яркости означает, что
    /// GDI не смог снять игру (эксклюзивный полный экран, свёрнутое окно).
    /// </summary>
    public static bool IsMostlyBlank(Mat image, double minStdDev = 3.0, double minMean = 8.0)
    {
        if (image.Empty())
        {
            return true;
        }

        using var gray = new Mat();
        if (image.Channels() == 1)
        {
            image.CopyTo(gray);
        }
        else
        {
            Cv2.CvtColor(
                image,
                gray,
                image.Channels() == 4
                    ? ColorConversionCodes.BGRA2GRAY
                    : ColorConversionCodes.BGR2GRAY);
        }

        Cv2.MeanStdDev(gray, out var mean, out var stddev);
        return mean.Val0 < minMean || stddev.Val0 < minStdDev;
    }

    /// <summary>
    /// Ищет клиентскую область окна Genshin Impact. Если окно не найдено,
    /// возвращает весь виртуальный экран.
    /// </summary>
    public static Rectangle FindGameClientRectangle()
    {
        var handle = FindGameWindow();
        if (handle != IntPtr.Zero
            && GetClientRect(handle, out var client)
            && ClientToScreen(handle, out var origin))
        {
            var width = client.Right - client.Left;
            var height = client.Bottom - client.Top;
            if (width >= 320 && height >= 240)
            {
                return new Rectangle(origin.X, origin.Y, width, height);
            }
        }

        return GetVirtualScreen();
    }

    private static IntPtr FindGameWindow()
    {
        var foreground = GetForegroundWindow();
        if (IsGameWindow(foreground))
        {
            return foreground;
        }

        var found = IntPtr.Zero;
        EnumWindows(
            (handle, _) =>
            {
                if (!IsGameWindow(handle))
                {
                    return true;
                }

                found = handle;
                return false;
            },
            IntPtr.Zero);

        return found;
    }

    private static bool IsGameWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsWindowVisible(handle))
        {
            return false;
        }

        GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            var name = process.ProcessName;
            return name.Contains("GenshinImpact", StringComparison.OrdinalIgnoreCase)
                || name.Contains("YuanShen", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static Rectangle GetVirtualScreen()
    {
        var x = GetSystemMetrics(SmXVirtualScreen);
        var y = GetSystemMetrics(SmYVirtualScreen);
        var width = Math.Max(1, GetSystemMetrics(SmCxVirtualScreen));
        var height = Math.Max(1, GetSystemMetrics(SmCyVirtualScreen));
        return new Rectangle(x, y, width, height);
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr handle, out Rect rectangle);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr handle, out Point point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }
}
