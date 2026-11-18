using OpenCvSharp;

namespace TeyvatLens.Core.Vision;

/// <summary>Найденный круг миникарты в кадре захвата.</summary>
public readonly record struct MinimapCircle(
    bool Found,
    double CenterX,
    double CenterY,
    double RadiusPx,
    double Score)
{
    public static MinimapCircle None => default;

    /// <summary>Поперечник видимой области миникарты, px.</summary>
    public double DiameterPx => RadiusPx * 2.0;

    /// <summary>
    /// Круг упирается в край кадра: часть карты потеряна, и область захвата
    /// стоит расширить. Для одометрии это допустимо, для масштаба — нет.
    /// </summary>
    public bool TouchesBorder(int width, int height, double margin = 2.0) =>
        Found
        && (CenterX - RadiusPx <= margin
            || CenterY - RadiusPx <= margin
            || CenterX + RadiusPx >= width - margin
            || CenterY + RadiusPx >= height - margin);
}

/// <summary>
/// Ищет границу круга миникарты в кадре захвата.
///
/// Зачем это нужно:
/// 1) масштаб миникарты зависит не от размера обведённого квадрата, а от
///    диаметра самого круга. Если пользователь оставил поля или захват
///    смещён, оценка «размер захвата / 93» завышает px/м, и метка отстаёт;
/// 2) центр круга почти никогда не совпадает с центром квадрата. Одометрия
///    вырезает в центре «слепое» пятно под стрелку игрока; если стрелка
///    оказалась вне этого пятна, она не двигается вместе с картой и тянет
///    оценку сдвига к нулю. Это второй источник отставания.
///
/// Граница ищется по радиальному перепаду яркости: карта внутри круга
/// заметно отличается от игрового мира снаружи, и перепад одинаков по всем
/// направлениям. Оценка = средний перепад × доля направлений с тем же знаком,
/// поэтому случайные линии и берега озёр не выигрывают у настоящего круга.
///
/// Два приёма, без которых детектор ошибается на реальном кадре:
/// - направления, выходящие за кадр, пропускаются, а не заменяются краевым
///   пикселем: иначе у круга, обрезанного краем захвата, средний перепад
///   размывается и выигрывает ложное внутреннее кольцо;
/// - из нескольких сильных кандидатов выбирается самый крупный: настоящая
///   граница миникарты — внешняя, а не похожая на кольцо текстура внутри.
/// </summary>
public static class MinimapCircleDetector
{
    /// <summary>Минимальный радиус как доля меньшей стороны кадра.</summary>
    public const double MinRadiusFraction = 0.22;

    /// <summary>Максимальный радиус как доля меньшей стороны кадра.</summary>
    public const double MaxRadiusFraction = 0.48;

    /// <summary>Порог уверенности: ниже — считаем, что круг не найден.</summary>
    public const double MinScore = 3.0;

    /// <summary>Минимальная доля направлений, попавших в кадр.</summary>
    public const double MinValidArc = 0.35;

    /// <summary>Смещение внутрь/наружу при измерении радиального перепада, px.</summary>
    private const double GradientOffsetPx = 2.0;

    private const int CoarseAngles = 90;
    private const int FineAngles = 180;
    private const int MaxCandidates = 8;

    private static readonly double[] CoarseCosines = BuildCosines(CoarseAngles);
    private static readonly double[] CoarseSines = BuildSines(CoarseAngles);
    private static readonly double[] FineCosines = BuildCosines(FineAngles);
    private static readonly double[] FineSines = BuildSines(FineAngles);

    /// <summary>
    /// Находит круг миникарты. Возвращает <see cref="MinimapCircle.None"/>,
    /// если кадр пустой или граница недостаточно выражена.
    /// </summary>
    public static MinimapCircle Detect(Mat frame)
    {
        if (frame is null || frame.Empty())
        {
            return MinimapCircle.None;
        }

        using var gray = ToGray(frame);
        return DetectGray(gray);
    }

    private static MinimapCircle DetectGray(Mat gray)
    {
        var width = gray.Width;
        var height = gray.Height;
        if (width < 16 || height < 16)
        {
            return MinimapCircle.None;
        }

        // Крупный кадр сначала уменьшаем: грубый проход по 420×420 занимает
        // сотни миллисекунд, а полпикселя точности на этом этапе не нужны.
        var minSide = Math.Min(width, height);
        var scale = minSide > 320 ? 0.5 : 1.0;
        using var reduced = new Mat();
        Mat work = gray;
        if (scale < 1.0)
        {
            Cv2.Resize(gray, reduced, new Size(), scale, scale, InterpolationFlags.Area);
            work = reduced;
        }

        var workWidth = work.Width;
        var workHeight = work.Height;
        var workSide = Math.Min(workWidth, workHeight);
        var minRadius = Math.Max(8, (int)Math.Round(workSide * MinRadiusFraction));
        var maxRadius = Math.Max(minRadius + 4, (int)Math.Round(workSide * MaxRadiusFraction));
        var step = Math.Max(4, workSide / 40);

        work.GetArray(out byte[] pixels);
        var frameCenterX = workWidth / 2.0;
        var frameCenterY = workHeight / 2.0;

        // Грубый проход: центр разрешён в любой точке кадра, потому что круг
        // может быть обрезан краем захвата. Отсекает такие кандидаты
        // требование к доле направлений, попавших в кадр, и требование
        // накрывать центр кадра: пользователь обводит область вокруг
        // миникарты, поэтому круг заведомо содержит её середину. Без этого
        // яркий угол интерфейса (меню Пекаря) даёт ложный круг.
        var candidates = new List<Candidate>();
        for (var radius = minRadius; radius <= maxRadius; radius += 4)
        {
            var radiusSquared = (double)radius * radius;
            for (var cy = 0; cy < workHeight; cy += step)
            {
                var dy = cy - frameCenterY;
                for (var cx = 0; cx < workWidth; cx += step)
                {
                    var dx = cx - frameCenterX;
                    if (dx * dx + dy * dy > radiusSquared)
                    {
                        continue;
                    }

                    var score = Score(
                        pixels,
                        workWidth,
                        workHeight,
                        cx,
                        cy,
                        radius,
                        CoarseCosines,
                        CoarseSines);
                    if (score >= MinScore)
                    {
                        candidates.Add(new Candidate(score, cx, cy, radius));
                    }
                }
            }
        }

        if (candidates.Count == 0)
        {
            return MinimapCircle.None;
        }

        candidates.Sort(static (a, b) => b.Score.CompareTo(a.Score));

        // Оставляем несколько лучших и непохожих кандидатов: настоящий круг
        // может проиграть грубому проходу, но выиграть после уточнения.
        var selected = new List<Candidate>();
        foreach (var candidate in candidates)
        {
            var duplicate = false;
            foreach (var picked in selected)
            {
                if (Math.Abs(picked.X - candidate.X) < step
                    && Math.Abs(picked.Y - candidate.Y) < step
                    && Math.Abs(picked.Radius - candidate.Radius) < 6)
                {
                    duplicate = true;
                    break;
                }
            }

            if (duplicate)
            {
                continue;
            }

            selected.Add(candidate);
            if (selected.Count >= MaxCandidates)
            {
                break;
            }
        }

        gray.GetArray(out byte[] fullPixels);
        var spread = (int)Math.Ceiling(step / scale) + 1;
        var fullMinRadius = Math.Max(8, (int)Math.Round(minSide * MinRadiusFraction));
        var fullMaxRadius = Math.Max(fullMinRadius + 4, (int)Math.Round(minSide * MaxRadiusFraction));

        var refined = new List<Candidate>();
        foreach (var candidate in selected)
        {
            var baseX = (int)Math.Round(candidate.X / scale);
            var baseY = (int)Math.Round(candidate.Y / scale);
            var baseRadius = (int)Math.Round(candidate.Radius / scale);
            var best = new Candidate(0, baseX, baseY, baseRadius);

            for (var radius = Math.Max(fullMinRadius, baseRadius - 4);
                 radius <= Math.Min(fullMaxRadius, baseRadius + 4);
                 radius++)
            {
                for (var cy = Math.Max(0, baseY - spread);
                     cy <= Math.Min(height - 1, baseY + spread);
                     cy++)
                {
                    for (var cx = Math.Max(0, baseX - spread);
                         cx <= Math.Min(width - 1, baseX + spread);
                         cx++)
                    {
                        var score = Score(
                            fullPixels,
                            width,
                            height,
                            cx,
                            cy,
                            radius,
                            FineCosines,
                            FineSines);
                        if (score > best.Score)
                        {
                            best = new Candidate(score, cx, cy, radius);
                        }
                    }
                }
            }

            if (best.Score > 0)
            {
                refined.Add(best);
            }
        }

        if (refined.Count == 0)
        {
            return MinimapCircle.None;
        }

        // Мягкое предпочтение крупному кругу: настоящая граница миникарты
        // снаружи, а внутри бывают похожие на кольцо берега и обрывы. Вес
        // небольшой, поэтому действительно сильная внутренняя граница
        // всё равно может победить.
        Candidate result = default;
        var bestAdjusted = 0.0;
        foreach (var candidate in refined)
        {
            // Уточнение могло сдвинуть центр за пределы круга — такой
            // кандидат уже не накрывает середину области захвата.
            var dx = candidate.X - width / 2.0;
            var dy = candidate.Y - height / 2.0;
            if (dx * dx + dy * dy > (double)candidate.Radius * candidate.Radius)
            {
                continue;
            }

            var radiusFactor = 0.75 + 0.25 * candidate.Radius / (double)fullMaxRadius;
            var adjusted = candidate.Score * radiusFactor;
            if (adjusted > bestAdjusted)
            {
                bestAdjusted = adjusted;
                result = candidate;
            }
        }

        if (result.Score < MinScore)
        {
            return MinimapCircle.None;
        }

        return new MinimapCircle(
            true,
            result.X,
            result.Y,
            result.Radius,
            result.Score);
    }

    /// <summary>
    /// Средний радиальный перепад × доля направлений с тем же знаком.
    /// Направления за пределами кадра не учитываются.
    /// </summary>
    private static double Score(
        byte[] pixels,
        int width,
        int height,
        int centerX,
        int centerY,
        double radius,
        double[] cosines,
        double[] sines)
    {
        var inner = radius - GradientOffsetPx;
        var outer = radius + GradientOffsetPx;
        var angles = cosines.Length;
        double sum = 0;
        var valid = 0;
        var positive = 0;
        var negative = 0;

        for (var index = 0; index < angles; index++)
        {
            var cos = cosines[index];
            var sin = sines[index];

            var innerX = centerX + inner * cos;
            var innerY = centerY + inner * sin;
            var outerX = centerX + outer * cos;
            var outerY = centerY + outer * sin;

            var ix = (int)Math.Round(innerX);
            var iy = (int)Math.Round(innerY);
            var ox = (int)Math.Round(outerX);
            var oy = (int)Math.Round(outerY);

            if (ix < 0 || ix >= width || iy < 0 || iy >= height
                || ox < 0 || ox >= width || oy < 0 || oy >= height)
            {
                continue;
            }

            var innerIndex = iy * width + ix;
            var outerIndex = oy * width + ox;
            var gradient = pixels[outerIndex] - pixels[innerIndex];

            sum += gradient;
            valid++;
            if (gradient > 0)
            {
                positive++;
            }
            else if (gradient < 0)
            {
                negative++;
            }
        }

        if (valid < angles * MinValidArc)
        {
            return 0.0;
        }

        var mean = sum / valid;
        var consistency = Math.Max(positive, negative) / (double)valid;
        return Math.Abs(mean) * consistency;
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

    private static double[] BuildCosines(int angles)
    {
        var values = new double[angles];
        for (var index = 0; index < angles; index++)
        {
            values[index] = Math.Cos(2.0 * Math.PI * index / angles);
        }

        return values;
    }

    private static double[] BuildSines(int angles)
    {
        var values = new double[angles];
        for (var index = 0; index < angles; index++)
        {
            values[index] = Math.Sin(2.0 * Math.PI * index / angles);
        }

        return values;
    }

    private readonly record struct Candidate(
        double Score,
        int X,
        int Y,
        int Radius);
}
