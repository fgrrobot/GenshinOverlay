using OpenCvSharp;

namespace TeyvatLens.Core.Vision;

/// <summary>Приведение кадра миникарты к виду, пригодному для одометрии.</summary>
public static class MinimapNormalizer
{
    /// <summary>
    /// Радиус «слепого» пятна вокруг центра миникарты, доля от радиуса круга.
    /// Закрывает стрелку игрока и основание конуса обзора: они не двигаются
    /// вместе с картой и без маски тянут оценку сдвига к нулю.
    /// </summary>
    public const double ArrowHoleFraction = 0.30;

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

    /// <summary>
    /// Вырезает квадрат, описанный вокруг найденного круга миникарты, и
    /// обнуляет всё вне круга и в центральном пятне под стрелку.
    ///
    /// Квадрат центрируется по кругу, поэтому дальнейшие доли (слепое пятно,
    /// рамка компаса, рабочая зона) снова отсчитываются от центра кадра.
    /// Пиксели не масштабируются: сдвиг в нормализованном кадре равен сдвигу
    /// на экране, и px/м, посчитанный по диаметру круга, остаётся верным.
    ///
    /// Если круг выходит за кадр, недостающая часть остаётся чёрной.
    /// Возвращённый Mat нужно освободить.
    /// </summary>
    public static Mat Normalize(Mat image, MinimapCircle circle)
    {
        if (image is null || image.Empty())
        {
            throw new ArgumentException("Пустой кадр миникарты", nameof(image));
        }

        if (!circle.Found || circle.RadiusPx < 8)
        {
            return Normalize(image);
        }

        using var gray = ToGray(image);

        var radius = circle.RadiusPx;
        var side = (int)Math.Ceiling(radius * 2.0) + 2;
        if (side % 2 != 0)
        {
            side++;
        }

        var half = side / 2;
        var left = (int)Math.Round(circle.CenterX) - half;
        var top = (int)Math.Round(circle.CenterY) - half;

        var result = new Mat(side, side, MatType.CV_8UC1, Scalar.Black);
        var sourceX = Math.Max(0, left);
        var sourceY = Math.Max(0, top);
        var copyWidth = Math.Min(gray.Width, left + side) - sourceX;
        var copyHeight = Math.Min(gray.Height, top + side) - sourceY;

        if (copyWidth > 0 && copyHeight > 0)
        {
            using var source = new Mat(
                gray,
                new Rect(sourceX, sourceY, copyWidth, copyHeight));
            using var target = new Mat(
                result,
                new Rect(sourceX - left, sourceY - top, copyWidth, copyHeight));
            source.CopyTo(target);
        }

        using var mask = new Mat(side, side, MatType.CV_8UC1, Scalar.Black);
        var center = new Point(half, half);
        Cv2.Circle(
            mask,
            center,
            (int)Math.Round(radius),
            Scalar.White,
            -1,
            LineTypes.AntiAlias);
        Cv2.Circle(
            mask,
            center,
            (int)Math.Round(radius * ArrowHoleFraction),
            Scalar.Black,
            -1,
            LineTypes.AntiAlias);

        var output = new Mat();
        result.CopyTo(output, mask);
        result.Dispose();
        return output;
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

    private static Mat ToGray(Mat source)
    {
        if (source.Channels() == 1)
        {
            return source.Clone();
        }

        var gray = new Mat();
        if (source.Channels() == 4)
        {
            Cv2.CvtColor(source, gray, ColorConversionCodes.BGRA2GRAY);
        }
        else
        {
            Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
        }

        return gray;
    }
}
