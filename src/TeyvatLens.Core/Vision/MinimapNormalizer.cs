using OpenCvSharp;

namespace TeyvatLens.Core.Vision;

/// <summary>Приведение кадра миникарты к виду, пригодному для одометрии.</summary>
public static class MinimapNormalizer
{
    /// <summary>
    /// Оставляет только круг миникарты, отрезая интерфейс по углам.
    /// Размер кадра сохраняется: масштабирование перед корреляцией
    /// искажает субпиксельную оценку сдвига.
    /// Возвращённый Mat нужно освободить.
    /// </summary>
    public static Mat Normalize(Mat image)
    {
        if (image is null || image.Empty())
        {
            throw new ArgumentException("Пустой кадр миникарты", nameof(image));
        }

        if (image.Channels() == 1)
        {
            return ApplyCircularMask(image);
        }

        using var gray = new Mat();
        Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        return ApplyCircularMask(gray);
    }

    /// <summary>Оставляет только круг, вписанный в кадр, остальное — чёрное.</summary>
    public static Mat ApplyCircularMask(Mat image)
    {
        var height = image.Rows;
        var width = image.Cols;
        using var mask = new Mat(height, width, MatType.CV_8UC1, Scalar.Black);
        var radius = Math.Max(1, Math.Min(width, height) / 2 - 1);
        Cv2.Circle(
            mask,
            new Point(width / 2, height / 2),
            radius,
            Scalar.White,
            -1,
            LineTypes.AntiAlias);

        var result = new Mat();
        image.CopyTo(result, mask);
        return result;
    }
}
