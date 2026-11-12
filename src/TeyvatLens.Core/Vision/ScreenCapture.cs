using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace TeyvatLens.Core.Vision;

/// <summary>
/// Захват прямоугольной области экрана.
///
/// MVP использует GDI (CopyFromScreen) — это простой и достаточно быстрый
/// путь для borderless-окна. Ограничение: на некоторых конфигурациях с
/// аппаратным ускорением GDI может вернуть чёрный кадр. Целевое решение —
/// Windows.Graphics.Capture (см. PROJECT.md §8.1).
/// </summary>
public sealed class ScreenCapture
{
    public ScreenCapture(Rectangle rectangle, double pxPerMeter)
    {
        if (rectangle.Width < 32 || rectangle.Height < 32)
        {
            throw new ArgumentException("Область захвата должна быть не меньше 32x32 пикселей");
        }

        if (rectangle.Width != rectangle.Height)
        {
            throw new ArgumentException("Область миникарты должна быть квадратной");
        }

        if (pxPerMeter <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pxPerMeter), "px_per_meter должен быть больше нуля");
        }

        Rectangle = rectangle;
        PxPerMeter = pxPerMeter;
    }

    public Rectangle Rectangle { get; }

    public double PxPerMeter { get; }

    /// <summary>Снимает область экрана и возвращает BGR-кадр. Mat нужно освободить.</summary>
    public Mat Grab()
    {
        using var bitmap = new Bitmap(
            Rectangle.Width,
            Rectangle.Height,
            PixelFormat.Format24bppRgb);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                Rectangle.Left,
                Rectangle.Top,
                0,
                0,
                bitmap.Size,
                CopyPixelOperation.SourceCopy);
        }

        return BitmapToMat(bitmap);
    }

    private static Mat BitmapToMat(Bitmap bitmap)
    {
        var mat = new Mat(bitmap.Height, bitmap.Width, MatType.CV_8UC3);
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        try
        {
            var rowBytes = bitmap.Width * 3;
            var row = new byte[rowBytes];
            for (var y = 0; y < bitmap.Height; y++)
            {
                var source = IntPtr.Add(data.Scan0, y * data.Stride);
                var target = IntPtr.Add(mat.Data, (int)(y * mat.Step()));
                Marshal.Copy(source, row, 0, rowBytes);
                Marshal.Copy(row, 0, target, rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return mat;
    }
}
