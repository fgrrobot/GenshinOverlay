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

    /// <summary>
    /// Вырезает квадрат, описанный вокруг найденного круга миникарты, и
    /// обнуляет всё вне круга. Стрелку игрока здесь не трогаем: чёрное пятно
    /// в кадре усиливается локальным выравниванием контраста (CLAHE) и даёт
    /// статичное светлое кольцо, которое тянет корреляцию к нулю. Стрелку
    /// закрывает маска трекера, которая накладывается уже после CLAHE.
    ///
    /// Квадрат центрируется по кругу, поэтому дальнейшие доли (пятно стрелки,
    /// рамка компаса, рабочая зона) снова отсчитываются от центра кадра.
    /// Пиксели не масштабируются: сдвиг в нормализованном кадре равен сдвигу
    /// на экране, и px/м, посчитанный по диаметру круга, остаётся верным.
    ///
    /// Если круг выходит за кадр, берётся только целиком видимая его часть.
    /// Дополнять кадр чёрным нельзя: чёрная полоса стоит на месте, попадает
    /// внутрь маски и тянет оценку сдвига к нулю — метка замирает.
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

        var centerX = (int)Math.Round(circle.CenterX);
        var centerY = (int)Math.Round(circle.CenterY);

        // Радиус, при котором круг целиком помещается в кадр.
        var visible = Math.Min(
            circle.RadiusPx,
            Math.Min(
                Math.Min(centerX, centerY),
                Math.Min(image.Width - centerX - 1, image.Height - centerY - 1)));

        var half = (int)Math.Floor(visible);
        if (half < 8)
        {
            return Normalize(image);
        }

        var side = half * 2;
        var left = centerX - half;
        var top = centerY - half;

        var result = new Mat(side, side, MatType.CV_8UC1, Scalar.Black);
        using (var source = new Mat(gray, new Rect(left, top, side, side)))
        using (var target = new Mat(result, new Rect(0, 0, side, side)))
        {
            source.CopyTo(target);
        }

        using var mask = new Mat(side, side, MatType.CV_8UC1, Scalar.Black);
        var center = new Point(half, half);
        Cv2.Circle(
            mask,
            center,
            half,
            Scalar.White,
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
