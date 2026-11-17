using OpenCvSharp;
using TeyvatLens.Core.RealMap;

namespace TeyvatLens.Core.Localization;

/// <summary>Результат абсолютной привязки по атласу.</summary>
public sealed record RelocalizationResult(
    bool Success,
    double WorldX,
    double WorldZ,
    double Score,
    int Inliers,
    int Matches,
    double Scale,
    double RotationDeg,
    double AccuracyM,
    double Ncc,
    string Message)
{
    public static RelocalizationResult Fail(string message) =>
        new(false, 0, 0, 0, 0, 0, 0, 0, double.PositiveInfinity, 0, message);
}

/// <summary>
/// Предвычисленные признаки атласа: серое изображение, ключевые точки и
/// дескрипторы ORB. Индекс строится один раз на атлас и переиспользуется,
/// потому что ORB на глобальном атласе занимает секунды.
/// </summary>
public sealed class AtlasIndex : IDisposable
{
    internal AtlasIndex(Mat gray, KeyPoint[] keyPoints, Mat descriptors)
    {
        Gray = gray;
        KeyPoints = keyPoints;
        Descriptors = descriptors;
    }

    public Mat Gray { get; }

    public KeyPoint[] KeyPoints { get; }

    public Mat Descriptors { get; }

    public int Width => Gray.Width;

    public int Height => Gray.Height;

    public int FeatureCount => KeyPoints.Length;

    public void Dispose()
    {
        Gray.Dispose();
        Descriptors.Dispose();
    }
}

/// <summary>
/// Абсолютная привязка снимка карты к атласу Тейвата.
///
/// Идея: снимок экрана карты (клавиша M) и атлас интерактивной карты —
/// одно и то же изображение мира в разных масштабах. ORB находит характерные
/// точки, BFMatcher с ratio-тестом отбирает пары, а RANSAC-аффинность
/// (масштаб + поворот + сдвиг) отбрасывает ложные совпадения. Центр снимка
/// переводится в пиксель атласа и затем в мировые координаты через
/// <see cref="MapProfile"/>.
///
/// Почему перебираются масштабы запроса: карта в игре может быть открыта на
/// любом зуме, и заранее неизвестно, сколько пикселей карты приходится на
/// метр. ORB-пирамида покрывает примерно 3,6×, поэтому запрос дополнительно
/// уменьшается в несколько раз — так диапазон перекрывается целиком.
///
/// Ограничения:
/// - карта в игре должна быть север-вверх (стандартное поведение карты M);
/// - центр снимка считается позицией игрока — карту нельзя сдвигать мышью;
/// - подземные и бедные текстурами области могут дать ложное совпадение,
///   поэтому результат всегда сопровождается оценкой ±м и числом подтверждений.
/// </summary>
public sealed class AtlasRelocalizer : IDisposable
{
    /// <summary>Порог ratio-теста Лоу: пара принимается, если она ближе второго кандидата.</summary>
    public const double RatioTest = 0.75;

    /// <summary>Сколько подтверждённых RANSAC точек нужно, чтобы доверять результату.</summary>
    public const int MinInliers = 12;

    /// <summary>Минимум пар после ratio-теста.</summary>
    public const int MinMatches = 18;

    /// <summary>
    /// Сколько подтверждений нужно резервному пути (взаимные соседи), где
    /// пар больше, но они менее надёжны. Порог ниже, чем у ratio-теста,
    /// потому что гипотезу дополнительно проверяет корреляция.
    /// </summary>
    public const int MinFallbackInliers = 12;

    /// <summary>Порог корреляции для резервного пути — строже основного.</summary>
    public const double MinFallbackNcc = 0.5;

    /// <summary>Порог расстояния Хэмминга для резервного пути.</summary>
    public const int MaxCrossCheckDistance = 64;

    /// <summary>
    /// Минимальная корреляция между запросом и атласом. Геометрически
    /// согласованная гипотеза без похожей картинки — почти всегда случайность.
    /// </summary>
    public const double MinNcc = 0.35;

    /// <summary>Начиная с этой уверенности перебор масштабов прекращается.</summary>
    public const double StrongMatchScore = 0.9;

    /// <summary>
    /// Минимальный размер уменьшенного запроса, px. Должен быть достаточно
    /// малым: грубый атлас в 4–8 раз мельче нулевого зума, поэтому снимок
    /// 800–1600 px приходится уменьшать до 100–200 px.
    /// </summary>
    public const int MinQuerySize = 96;

    /// <summary>Допустимый поворот карты, градусы. Карта Тейвата всегда север-вверх.</summary>
    public const double MaxRotationDeg = 12.0;

    public const double MinScale = 0.05;

    public const double MaxScale = 20.0;

    /// <summary>
    /// Во сколько раз уменьшать запрос перед сопоставлением. Перебор
    /// компенсирует неизвестный зум карты в игре. Нижние ступени (1/8 и
    /// меньше) нужны для грубого атласа: на зуме −2 один пиксель — это 4 м,
    /// то есть карта в игре в 4–8 раз подробнее атласа.
    /// </summary>
    private static readonly double[] DefaultQueryScales =
        { 1.0, 0.7, 0.5, 0.35, 0.25, 0.18, 0.125, 0.09, 0.07 };

    /// <summary>
    /// Сколько признаков брать с атласа: примерно один на 300 px². Плотность
    /// важнее абсолютного числа — при 12 000 признаков на всю карту Тейвата
    /// в маленьком регионе остаётся слишком мало точек для ratio-теста.
    /// </summary>
    public const int AtlasPixelsPerFeature = 300;

    public const int MinAtlasFeatures = 15000;

    public const int MaxAtlasFeatures = 80000;

    private readonly ORB _orb;
    private readonly BFMatcher _matcher;
    private readonly BFMatcher _crossMatcher;
    private readonly double[] _queryScales;

    public AtlasRelocalizer(IReadOnlyList<double>? queryScales = null)
    {
        _orb = CreateOrb(nfeatures: 2000);
        _matcher = new BFMatcher(NormTypes.Hamming, crossCheck: false);
        _crossMatcher = new BFMatcher(NormTypes.Hamming, crossCheck: true);
        _queryScales = queryScales is { Count: > 0 }
            ? queryScales.Where(value => value > 0).ToArray()
            : DefaultQueryScales;
    }

    /// <summary>
    /// Сколько признаков разумно снять с атласа данного размера.
    /// </summary>
    public static int SuggestFeatureCount(Mat atlas) =>
        Math.Clamp(
            (int)((long)atlas.Width * atlas.Height / AtlasPixelsPerFeature),
            MinAtlasFeatures,
            MaxAtlasFeatures);

    /// <summary>
    /// Строит индекс атласа. Вызывающий владеет результатом.
    /// <paramref name="features"/> ≤ 0 означает «подобрать по размеру».
    /// </summary>
    public AtlasIndex BuildIndex(Mat atlas, int features = 0)
    {
        if (atlas.Empty())
        {
            throw new ArgumentException("Атлас пуст.", nameof(atlas));
        }

        if (features <= 0)
        {
            features = SuggestFeatureCount(atlas);
        }

        using var gray = ToGray(atlas);
        using var orb = CreateOrb(features);
        var descriptors = new Mat();
        orb.DetectAndCompute(gray, null, out var keyPoints, descriptors);

        if (descriptors.Empty() || keyPoints.Length == 0)
        {
            descriptors.Dispose();
            throw new InvalidOperationException("В атласе не найдено характерных точек.");
        }

        return new AtlasIndex(gray.Clone(), keyPoints, descriptors);
    }

    /// <summary>
    /// Ищет центр запроса на атласе. Возвращает мировые координаты центра
    /// запроса либо неуспех с пояснением.
    /// </summary>
    public RelocalizationResult Locate(Mat query, AtlasIndex index, MapProfile profile)
    {
        if (query.Empty())
        {
            return RelocalizationResult.Fail("Пустой снимок карты.");
        }

        if (!profile.IsValid || index.FeatureCount == 0)
        {
            return RelocalizationResult.Fail("Атлас не привязан к миру.");
        }

        using var gray = ToGray(query);
        RelocalizationResult? best = null;
        RelocalizationResult? failure = null;

        foreach (var factor in _queryScales)
        {
            var width = (int)Math.Round(gray.Width * factor);
            var height = (int)Math.Round(gray.Height * factor);
            if (width < MinQuerySize || height < MinQuerySize)
            {
                continue;
            }

            using var scaled = new Mat();
            if (Math.Abs(factor - 1.0) < 1e-6)
            {
                gray.CopyTo(scaled);
            }
            else
            {
                // Лёгкое размытие перед сильным уменьшением: снимок карты
                // приходит сжатым (артефакты, шум), и на 1/8 они превращаются
                // в ложные углы, которые ломают ORB.
                if (factor < 0.3)
                {
                    using var smoothed = BlurForDownscale(gray);
                    Cv2.Resize(smoothed, scaled, new Size(width, height), 0, 0, InterpolationFlags.Area);
                }
                else
                {
                    Cv2.Resize(gray, scaled, new Size(width, height), 0, 0, InterpolationFlags.Area);
                }
            }

            using var mask = BuildQueryMask(scaled.Size());
            var descriptors = new Mat();
            _orb.DetectAndCompute(scaled, mask, out var keyPoints, descriptors);

            if (descriptors.Empty() || keyPoints.Length < 8)
            {
                descriptors.Dispose();
                continue;
            }

            using (descriptors)
            {
                var result = Match(scaled, keyPoints, descriptors, index, profile, width, height);
                if (result.Success && (best is null || result.Score > best.Score))
                {
                    best = result;

                    // Уверенное совпадение дальше перебирать незачем.
                    if (result.Score >= StrongMatchScore)
                    {
                        return result;
                    }
                }
                else if (!result.Success
                    && (failure is null || result.Matches > failure.Matches))
                {
                    // Запоминаем самую содержательную неудачу: по ней проще
                    // понять, что именно помешало (мало пар, слабая корреляция).
                    failure = result;
                }
            }
        }

        return best ?? failure ?? RelocalizationResult.Fail(
            "Не удалось найти карту на атласе. Откройте карту (M) и отдалите её.");
    }

    private RelocalizationResult Match(
        Mat queryGray,
        KeyPoint[] queryKeyPoints,
        Mat queryDescriptors,
        AtlasIndex index,
        MapProfile profile,
        int queryWidth,
        int queryHeight)
    {
        // Основной путь: ratio-тест Лоу. Он даёт мало, но очень надёжных пар,
        // поэтому проверяется первым.
        var source = new List<Point2f>();
        var target = new List<Point2f>();

        foreach (var pair in _matcher.KnnMatch(queryDescriptors, index.Descriptors, k: 2))
        {
            if (pair.Length < 2)
            {
                continue;
            }

            var first = pair[0];
            var second = pair[1];
            if (first.Distance >= RatioTest * second.Distance)
            {
                continue;
            }

            source.Add(queryKeyPoints[first.QueryIdx].Pt);
            target.Add(index.KeyPoints[first.TrainIdx].Pt);
        }

        var primary = source.Count >= MinMatches
            ? Fit(
                source,
                target,
                source.Count,
                requireInlierRatio: true,
                queryGray,
                index,
                profile,
                queryWidth,
                queryHeight)
            : FailMatch(source.Count, 0, 0, $"Мало совпадений: {source.Count}.");

        if (primary.Success)
        {
            return primary;
        }

        // Резерв: взаимные ближайшие соседи. На грубом атласе (1 пиксель =
        // 8 м) запрос уменьшается в 8 раз, и ratio-тест часто отбраковывает
        // почти всё. Взаимность даёт больше пар; ложные отсекает RANSAC
        // вместе с фотометрической проверкой.
        var crossSource = new List<Point2f>();
        var crossTarget = new List<Point2f>();

        foreach (var match in _crossMatcher.Match(queryDescriptors, index.Descriptors))
        {
            if (match.Distance > MaxCrossCheckDistance)
            {
                continue;
            }

            crossSource.Add(queryKeyPoints[match.QueryIdx].Pt);
            crossTarget.Add(index.KeyPoints[match.TrainIdx].Pt);
        }

        var fallback = crossSource.Count >= MinMatches
            ? Fit(
                crossSource,
                crossTarget,
                crossSource.Count,
                requireInlierRatio: false,
                queryGray,
                index,
                profile,
                queryWidth,
                queryHeight)
            : FailMatch(crossSource.Count, 0, 0, $"Мало взаимных пар: {crossSource.Count}.");

        if (fallback.Success)
        {
            return fallback;
        }

        // Оставляем неудачу, которая продвинулась дальше: по ней понятнее,
        // что именно помешало.
        return fallback.Inliers > primary.Inliers ? fallback : primary;
    }

    private static RelocalizationResult FailMatch(
        int matches,
        int inliers,
        double ncc,
        string message) =>
        new(false, 0, 0, 0, inliers, matches, 0, 0, double.PositiveInfinity, ncc, message);

    /// <summary>
    /// Подбирает аффинность по парам точек и проверяет её фотометрически.
    /// Неудача возвращается как результат с диагностикой, а не как исключение.
    /// </summary>
    private RelocalizationResult Fit(
        List<Point2f> source,
        List<Point2f> target,
        int matches,
        bool requireInlierRatio,
        Mat queryGray,
        AtlasIndex index,
        MapProfile profile,
        int queryWidth,
        int queryHeight)
    {
        if (source.Count < 6)
        {
            return FailMatch(matches, 0, 0, "Мало пар для оценки преобразования.");
        }

        using var src = Mat.FromArray(source);
        using var dst = Mat.FromArray(target);
        using var inliers = new Mat();
        using var matrix = Cv2.EstimateAffinePartial2D(
            src,
            dst,
            inliers,
            RobustEstimationAlgorithms.RANSAC,
            ransacReprojThreshold: 4.0,
            maxIters: 3000,
            confidence: 0.995,
            refineIters: 10);

        if (matrix is null || matrix.Empty())
        {
            return FailMatch(matches, 0, 0, "Не удалось оценить преобразование.");
        }

        var inlierCount = inliers.Empty() ? 0 : Cv2.CountNonZero(inliers);
        var requiredInliers = requireInlierRatio ? MinInliers : MinFallbackInliers;
        if (inlierCount < requiredInliers)
        {
            return FailMatch(
                matches,
                inlierCount,
                0,
                $"Мало подтверждений: {inlierCount} из {matches}.");
        }

        if (requireInlierRatio && inlierCount < matches * 0.4)
        {
            return FailMatch(
                matches,
                inlierCount,
                0,
                $"Слабая доля подтверждений: {inlierCount} из {matches}.");
        }

        var a = matrix.At<double>(0, 0);
        var b = matrix.At<double>(0, 1);
        var c = matrix.At<double>(1, 0);
        var d = matrix.At<double>(1, 1);
        var scale = Math.Sqrt(c * c + d * d);
        var rotation = Math.Atan2(c, a) * 180.0 / Math.PI;

        if (scale < MinScale || scale > MaxScale)
        {
            return FailMatch(matches, inlierCount, 0, $"Неправдоподобный масштаб: {scale:0.###}.");
        }

        if (Math.Abs(rotation) > MaxRotationDeg)
        {
            return FailMatch(matches, inlierCount, 0, $"Карта повёрнута на {rotation:0.#}°.");
        }

        var cx = queryWidth / 2.0;
        var cy = queryHeight / 2.0;
        var atlasX = a * cx + b * cy + matrix.At<double>(0, 2);
        var atlasY = c * cx + d * cy + matrix.At<double>(1, 2);

        if (atlasX < 0 || atlasY < 0 || atlasX >= index.Width || atlasY >= index.Height)
        {
            return FailMatch(matches, inlierCount, 0, "Карта оказалась за пределами атласа.");
        }

        // Фотометрическая проверка: переносим область атласа в систему
        // координат запроса и считаем корреляцию. Геометрия без картинки
        // легко даёт случайное совпадение на однообразном рельефе.
        var ncc = VerifyPatch(queryGray, index, matrix, new Size(queryWidth, queryHeight));
        var requiredNcc = requireInlierRatio ? MinNcc : MinFallbackNcc;
        if (double.IsNaN(ncc) || ncc < requiredNcc)
        {
            return FailMatch(
                matches,
                inlierCount,
                ncc,
                $"Слабая корреляция с атласом: {ncc:0.00}.");
        }

        var metersPerAtlasPx = 1.0 / profile.PxPerMeter;
        var accuracy = Math.Clamp(
            metersPerAtlasPx * 2.5 / Math.Sqrt(inlierCount),
            1.0,
            50.0);

        // Оценка уверенности: подтверждения, их доля и фотометрическое
        // согласие. Нужна, чтобы выбирать лучший масштаб из перебора.
        var inlierRatio = matches > 0 ? inlierCount / (double)matches : 0.0;
        var score = Math.Clamp(
            0.45 * Math.Min(1.0, inlierCount / 40.0) +
            0.25 * Math.Min(1.0, inlierRatio) +
            0.30 * Math.Clamp((ncc - MinNcc) / (0.9 - MinNcc), 0.0, 1.0),
            0.0,
            1.0);

        return new RelocalizationResult(
            true,
            profile.ToWorldX(atlasX),
            profile.ToWorldZ(atlasY),
            score,
            inlierCount,
            matches,
            scale,
            rotation,
            accuracy,
            ncc,
            $"Совпадений: {matches}, подтверждений: {inlierCount}, корреляция {ncc:0.00}.");
    }

    /// <summary>
    /// Считает нормированную корреляцию между запросом и перенесённым в его
    /// систему координат фрагментом атласа. Рамка и центральный маркер
    /// игрока исключаются маской.
    /// </summary>
    private static double VerifyPatch(Mat queryGray, AtlasIndex index, Mat affine, Size querySize)
    {
        var corners = new[]
        {
            new Point2d(0, 0),
            new Point2d(querySize.Width, 0),
            new Point2d(querySize.Width, querySize.Height),
            new Point2d(0, querySize.Height)
        };

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var corner in corners)
        {
            var x = affine.At<double>(0, 0) * corner.X
                + affine.At<double>(0, 1) * corner.Y
                + affine.At<double>(0, 2);
            var y = affine.At<double>(1, 0) * corner.X
                + affine.At<double>(1, 1) * corner.Y
                + affine.At<double>(1, 2);
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        var left = Math.Max(0, (int)Math.Floor(minX) - 4);
        var top = Math.Max(0, (int)Math.Floor(minY) - 4);
        var right = Math.Min(index.Width, (int)Math.Ceiling(maxX) + 4);
        var bottom = Math.Min(index.Height, (int)Math.Ceiling(maxY) + 4);

        if (right - left < 8 || bottom - top < 8)
        {
            return double.NaN;
        }

        using var roi = new Mat(index.Gray, new Rect(left, top, right - left, bottom - top));
        using var adjusted = affine.Clone();
        adjusted.At<double>(0, 2) -= left;
        adjusted.At<double>(1, 2) -= top;

        using var inverse = new Mat();
        Cv2.InvertAffineTransform(adjusted, inverse);

        using var warped = new Mat();
        Cv2.WarpAffine(
            roi,
            warped,
            inverse,
            querySize,
            InterpolationFlags.Linear,
            BorderTypes.Constant,
            Scalar.Black);

        using var a = new Mat();
        using var q = new Mat();
        warped.ConvertTo(a, MatType.CV_32F);
        queryGray.ConvertTo(q, MatType.CV_32F);

        using var mask = BuildVerifyMask(querySize);
        var meanA = Cv2.Mean(a, mask).Val0;
        var meanQ = Cv2.Mean(q, mask).Val0;

        using var a2 = new Mat();
        using var q2 = new Mat();
        using var aq = new Mat();
        Cv2.Multiply(a, a, a2);
        Cv2.Multiply(q, q, q2);
        Cv2.Multiply(a, q, aq);

        var varA = Math.Max(0.0, Cv2.Mean(a2, mask).Val0 - meanA * meanA);
        var varQ = Math.Max(0.0, Cv2.Mean(q2, mask).Val0 - meanQ * meanQ);
        if (varA < 1e-6 || varQ < 1e-6)
        {
            return 0.0;
        }

        var covariance = Cv2.Mean(aq, mask).Val0 - meanA * meanQ;
        return covariance / Math.Sqrt(varA * varQ);
    }

    /// <summary>
    /// Маска для фотометрической проверки: чуть уже, чем маска поиска
    /// признаков, чтобы не захватить ореол маркера игрока.
    /// </summary>
    private static Mat BuildVerifyMask(Size size)
    {
        var mask = new Mat(size, MatType.CV_8UC1, Scalar.Black);
        var margin = Math.Max(2, (int)Math.Round(Math.Min(size.Width, size.Height) * 0.02));
        var width = size.Width - margin * 2;
        var height = size.Height - margin * 2;
        if (width > 0 && height > 0)
        {
            Cv2.Rectangle(mask, new Rect(margin, margin, width, height), Scalar.White, -1);
        }

        var radius = Math.Max(4, (int)Math.Round(Math.Min(size.Width, size.Height) * 0.05));
        Cv2.Circle(mask, new Point(size.Width / 2, size.Height / 2), radius, Scalar.Black, -1);
        return mask;
    }

    /// <summary>
    /// Маска запроса: убирает внешнюю рамку (кнопки интерфейса) и центральный
    /// маркер игрока. Маркер есть только на снимке карты и не совпадает с
    /// атласом, поэтому его точки только мешают RANSAC.
    /// </summary>
    public static Mat BuildQueryMask(Size size)
    {
        var mask = new Mat(size, MatType.CV_8UC1, Scalar.Black);
        var margin = Math.Max(2, (int)Math.Round(Math.Min(size.Width, size.Height) * 0.04));
        var width = size.Width - margin * 2;
        var height = size.Height - margin * 2;
        if (width > 0 && height > 0)
        {
            Cv2.Rectangle(
                mask,
                new Rect(margin, margin, width, height),
                Scalar.White,
                thickness: -1);
        }

        var radius = Math.Max(3, (int)Math.Round(Math.Min(size.Width, size.Height) * 0.06));
        Cv2.Circle(
            mask,
            new Point(size.Width / 2, size.Height / 2),
            radius,
            Scalar.Black,
            thickness: -1);
        return mask;
    }

    /// <summary>
    /// Лёгкое размытие перед уменьшением в 3+ раза. Гасит шум сжатия,
    /// который иначе превращается в ложные углы на маленьком запросе.
    /// </summary>
    private static Mat BlurForDownscale(Mat gray)
    {
        var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new Size(0, 0), sigmaX: 1.2, sigmaY: 1.2);
        return blurred;
    }

    private static Mat ToGray(Mat source)
    {
        var gray = new Mat();
        switch (source.Channels())
        {
            case 1:
                source.CopyTo(gray);
                break;
            case 3:
                Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
                break;
            case 4:
                Cv2.CvtColor(source, gray, ColorConversionCodes.BGRA2GRAY);
                break;
            default:
                gray.Dispose();
                throw new ArgumentException("Ожидается изображение с 1, 3 или 4 каналами.", nameof(source));
        }

        return gray;
    }

    private static ORB CreateOrb(int nfeatures) =>
        ORB.Create(
            nFeatures: nfeatures,
            scaleFactor: 1.2f,
            nLevels: 8,
            edgeThreshold: 31,
            firstLevel: 0,
            wtaK: 2,
            scoreType: ORBScoreType.Harris,
            patchSize: 31,
            fastThreshold: 12);

    public void Dispose()
    {
        _crossMatcher.Dispose();
        _matcher.Dispose();
        _orb.Dispose();
    }
}
