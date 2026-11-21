using OpenCvSharp;
using TeyvatLens.Core.Geometry;
using TeyvatLens.Core.Models;

namespace TeyvatLens.Core.Localization;

/// <summary>Результат обработки одного кадра.</summary>
public sealed record LocalizationResult(
    TrackingStatus Status,
    double Confidence,
    double? X,
    double? Z,
    double HeadingDeg,
    double DeltaX,
    double DeltaZ,
    double Response,
    bool RequiresSnap,
    string Message,
    double AccuracyM);

/// <summary>
/// Одометрия по миникарте и автомат состояний локализации.
///
/// MVP реализует только относительный контур: смещение между соседними
/// кадрами оценивается корреляцией. Абсолютная привязка делается вручную —
/// «привязкой» к известной точке телепорта (план B из PROJECT.md).
///
/// Как оценивается сдвиг:
/// 1) грубо — фазовая корреляция с окном Ханна: даёт целый пиксель;
/// 2) точно — прямой поиск по взвешенной невязке (SSD) с бикубической
///    интерполяцией на сетке 1/4 px и параболой по трём точкам.
///
/// Почему не парабола по трём отсчётам фазовой корреляции: пик там острый
/// (почти дельта-функция), и вершина параболы систематически смещается к
/// целому пикселю. Ошибка масштаба в проценты копится за сотни шагов, и
/// карта «уезжает» на десятки метров.
///
/// Почему не Cv2.PhaseCorrelate: встроенный субпиксельный оценщик
/// (взвешенный центроид 3x3) занижает дробную часть сдвига ещё сильнее:
/// при истинном 0.5 px он возвращает ~0.175 px.
///
/// Веса при уточнении убирают центр (стрелка игрока не двигается вместе с
/// картой и «тянет» оценку к нулю) и внешнюю рамку компаса.
///
/// Почему корреляция идёт на нативном разрешении миникарты:
/// предварительный ресайз (например, 360 -> 256) заметно искажает оценку
/// субпиксельного сдвига. Поэтому кадр не масштабируется, а масштаб
/// пересчитывается через px_per_meter исходной миникарты.
///
/// Ограничение MVP: миникарта считается north-up. Если она вращается вместе
/// с камерой, корреляция увидит поворот как плохое совпадение, уверенность
/// упадёт, и приложение честно уйдёт в Lost.
///
/// Что сделано для точности позиции:
/// - смещение меньше 4 см накапливается и применяется только целиком —
///   так не теряется медленное движение и не копится шум;
/// - при стоянке (скорость ниже 1 м/с несколько кадров подряд) включается
///   ZUPT: накопленный субпиксельный шум гасится, позиция замирает;
/// - одиночный «нефизичный» скачок пропускается, а не сдвигает карту;
///   три таких кадра подряд означают смену локации и требуют привязки;
/// - рядом с позицией показывается оценка точности (±м), растущая с пройденным
///   путём.
/// </summary>
public sealed class OdometryTracker : IDisposable
{
    // Кадр нормализуется в float32 0..1, поэтому порог задан в этих единицах
    // (3.5 в шкале 0..255 ≈ 0.0137).
    private const double MinTextureStd = 0.015;
    private const double MinResponse = 0.06;

    /// <summary>Жёсткий предел шага: больше — это уже телепорт, а не ходьба, м.</summary>
    private const double MaxStepMetersDefault = 25.0;

    /// <summary>Правдоподобная скорость перемещения для отбраковки выбросов, м/с.</summary>
    private const double MaxSpeedMps = 60.0;

    /// <summary>Нижняя граница скоростного порога, м (при очень малом dt).</summary>
    private const double MinSpeedLimitMeters = 0.25;

    /// <summary>Сколько подряд неправдоподобных кадров считаем сменой локации.</summary>
    private const int MaxRejectedFrames = 3;

    /// <summary>Скорость, ниже которой кадр считается стоянкой, м/с.</summary>
    private const double StationarySpeedMps = 1.0;

    /// <summary>Сколько кадров подряд нужно для включения ZUPT.</summary>
    private const int StationaryFrames = 3;

    /// <summary>Минимальная длительность стоянки для ZUPT, с.</summary>
    private const double StationaryAfterSeconds = 0.12;

    /// <summary>Смещение меньше этого не показываем, а копим, м.</summary>
    private const double MinApplyMeters = 0.04;

    /// <summary>Базовая оценка точности сразу после привязки, м.</summary>
    private const double BaseAccuracyM = 0.75;

    /// <summary>Прирост оценки точности на метр пройденного пути.</summary>
    private const double AccuracyPerMeter = 0.008;

    /// <summary>Шаг сетки уточнения, px.</summary>
    private const double RefineStep = 0.25;

    /// <summary>Полуширина сетки уточнения в шагах (±1 px).</summary>
    private const int RefineHalf = 2;

    /// <summary>Радиус центрального «слепого» диска (стрелка игрока), доля от меньшей стороны.</summary>
    private const double CenterHoleFraction = 0.14;

    /// <summary>Ширина внешней рамки (компас), доля от меньшей стороны.</summary>
    private const double BorderFraction = 0.08;

    /// <summary>Радиус рабочей зоны миникарты, доля от меньшей стороны.</summary>
    private const double DiscFraction = 0.42;

    /// <summary>Поле, отбрасываемое при уточнении, доля от меньшей стороны.</summary>
    private const double RoiMarginFraction = 0.18;

    /// <summary>
    /// Радиус плавно погашенного центра в окне корреляции (стрелка игрока),
    /// доля от меньшей стороны. Должен накрывать стрелку целиком.
    /// </summary>
    private const double CorrelationHoleFraction = 0.12;

    /// <summary>Ширина плавного перехода вокруг центрального пятна, доля от меньшей стороны.</summary>
    private const double CorrelationFeatherFraction = 0.04;

    /// <summary>Минимальный шаг, при котором курс оценивается по направлению движения, м.</summary>
    private const double MinAutoHeadingStepMeters = 0.2;

    /// <summary>Доля, с которой новый азимут подмешивается к текущему курсу.</summary>
    private const double AutoHeadingSmoothing = 0.35;

    private readonly double _pxPerMeter;
    private readonly double _lostAfterSeconds;
    private readonly double _lostConfidence;
    private readonly double _degradedConfidence;
    private readonly double _maxStepMeters;
    private readonly CLAHE _clahe;

    private Mat? _previousPatch;
    private Mat? _correlationWindow;
    private Size _correlationWindowSize;
    private Mat? _fineMask;
    private Size _fineMaskSize;
    private double _lostTimer;

    // Накопленное субпиксельное смещение: применяется, когда станет заметным.
    private double _pendingX;
    private double _pendingZ;

    // Детектор стоянки для ZUPT.
    private int _stillFrames;
    private double _stillTime;

    // Подряд идущие кадры с неправдоподобным смещением.
    private int _rejectedFrames;

    // Пройденный с момента привязки путь — основа оценки точности.
    private double _travelM;

    public OdometryTracker(
        double pxPerMeter,
        double lostAfterSeconds = 1.5,
        double lostConfidence = 0.4,
        double degradedConfidence = 0.7,
        double maxStepMeters = MaxStepMetersDefault)
    {
        if (pxPerMeter <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pxPerMeter), "px_per_meter должен быть больше нуля");
        }

        _pxPerMeter = pxPerMeter;
        _lostAfterSeconds = lostAfterSeconds;
        _lostConfidence = lostConfidence;
        _degradedConfidence = degradedConfidence;
        _maxStepMeters = maxStepMeters;
        _clahe = Cv2.CreateCLAHE(2.0, new Size(8, 8));

        RequiresSnap = true;
    }

    public double? X { get; private set; }

    public double? Z { get; private set; }

    public double HeadingDeg { get; private set; }

    public double Confidence { get; private set; }

    /// <summary>Оценка точности текущей позиции, ±м.</summary>
    public double AccuracyM { get; private set; } = BaseAccuracyM;

    /// <summary>Пройденный с момента привязки путь, м.</summary>
    public double TravelSinceSnapM => _travelM;

    public TrackingStatus Status { get; private set; } = TrackingStatus.Lost;

    public string Message { get; private set; } = "Позиция не привязана";

    /// <summary>После потери позиции требуется ручная привязка, а не «доползание».</summary>
    public bool RequiresSnap { get; private set; }

    public bool HasPosition => X is not null && Z is not null;

    public void Snap(double x, double z, double headingDeg = 0.0, double? accuracyM = null)
    {
        X = x;
        Z = z;
        HeadingDeg = GeoMath.NormalizeAngle(headingDeg);
        Confidence = 1.0;
        Status = TrackingStatus.Tracking;
        Message = "Позиция привязана";
        RequiresSnap = false;
        _previousPatch?.Dispose();
        _previousPatch = null;
        _lostTimer = 0;
        _pendingX = 0;
        _pendingZ = 0;
        _stillFrames = 0;
        _stillTime = 0;
        _rejectedFrames = 0;
        _travelM = 0;
        AccuracyM = accuracyM is { } value
            ? Math.Clamp(value, 0.1, 1000.0)
            : BaseAccuracyM;
    }

    public void Reset()
    {
        X = null;
        Z = null;
        Confidence = 0;
        Status = TrackingStatus.Lost;
        Message = "Позиция не привязана";
        RequiresSnap = true;
        _previousPatch?.Dispose();
        _previousPatch = null;
        _lostTimer = 0;
        _pendingX = 0;
        _pendingZ = 0;
        _stillFrames = 0;
        _stillTime = 0;
        _rejectedFrames = 0;
        _travelM = 0;
        AccuracyM = BaseAccuracyM;
    }

    public void SetHeading(double headingDeg) =>
        HeadingDeg = GeoMath.NormalizeAngle(headingDeg);

    /// <summary>
    /// Оценивать курс по направлению движения. Нужно в режиме захвата экрана:
    /// отдельного источника курса там нет, а стрелка должна смотреть туда,
    /// куда идёт игрок. На месте курс не обновляется — шум в один пиксель
    /// дал бы случайный азимут.
    /// </summary>
    public bool AutoHeading { get; set; }

    /// <summary>Обрабатывает кадр миникарты в исходном разрешении.</summary>
    public LocalizationResult Update(Mat? patch, double dt)
    {
        dt = GeoMath.Clamp(dt, 0.0, 0.5);

        if (patch is null || patch.Empty())
        {
            return Decay(dt, "Нет изображения миникарты");
        }

        using var current = Prepare(patch);
        Cv2.MeanStdDev(current, out _, out var stddev);
        if (stddev.Val0 < MinTextureStd)
        {
            // Сигнал пропал. Сбрасываем предыдущий кадр, чтобы после
            // восстановления не сравнивать картинку с чёрным экраном.
            _previousPatch?.Dispose();
            _previousPatch = null;
            return Decay(dt, "Миникарта не видна");
        }

        if (_previousPatch is null || _previousPatch.Size() != current.Size())
        {
            ReplacePrevious(current);
            _pendingX = 0;
            _pendingZ = 0;
            _stillFrames = 0;
            _stillTime = 0;
            if (RequiresSnap || !HasPosition)
            {
                Status = TrackingStatus.Lost;
                Message = "Нужна привязка к точке телепорта";
            }
            else
            {
                Confidence = Math.Max(Confidence, 0.55);
                Status = TrackingStatus.Degraded;
                Message = "Восстановление сигнала";
            }

            return Result(0, 0, 0);
        }

        var (shiftX, shiftY, response) = Correlate(_previousPatch, current);

        if (!double.IsFinite(response) || response < MinResponse)
        {
            ReplacePrevious(current);
            return Decay(dt, "Недостаточно текстуры для одометрии");
        }

        var deltaX = -shiftX / _pxPerMeter;
        var deltaZ = -shiftY / _pxPerMeter;
        var step = GeoMath.Distance(0, 0, deltaX, deltaZ);

        if (!HasPosition)
        {
            ReplacePrevious(current);
            return Result(deltaX, deltaZ, response);
        }

        if (step > _maxStepMeters)
        {
            // Жёсткий порог: так выглядит телепорт или смена локации.
            // Предыдущий кадр не обновляем — после привязки он всё равно
            // сбрасывается, а до неё новые кадры будут так же неправдоподобны.
            MarkLost("Обнаружен скачок позиции — нужна повторная привязка");
            return Result(0, 0, response);
        }

        // Скоростной порог отсекает одиночные выбросы корреляции: настоящий
        // игрок не перемещается на метры за один кадр. Кадр пропускаем, но
        // предыдущий оставляем — следующий нормальный кадр сравняется с ним.
        var speedLimit = Math.Max(MinSpeedLimitMeters, MaxSpeedMps * dt);
        if (step > speedLimit)
        {
            _rejectedFrames++;
            if (_rejectedFrames >= MaxRejectedFrames)
            {
                MarkLost("Смещение не подтверждается — нужна повторная привязка");
                return Result(0, 0, response);
            }

            Confidence *= 0.97;
            Message = "Кадр пропущен: неправдоподобный скачок";
            return Result(0, 0, response);
        }

        _rejectedFrames = 0;

        if (RequiresSnap)
        {
            // Позиция была потеряна: не показываем правдоподобную, но
            // неизвестно насколько устаревшую точку.
            ReplacePrevious(current);
            Status = TrackingStatus.Lost;
            Message = "Нужна повторная привязка к точке телепорта";
            return Result(0, 0, response);
        }

        // Копим смещение: на стоянке это шум, который нельзя показывать,
        // а при медленном движении — реальный путь, который нельзя терять.
        _pendingX += deltaX;
        _pendingZ += deltaZ;

        var speed = step / Math.Max(dt, 1e-3);
        if (speed < StationarySpeedMps)
        {
            _stillFrames++;
            _stillTime += dt;
        }
        else
        {
            _stillFrames = 0;
            _stillTime = 0;
        }

        if (_stillFrames >= StationaryFrames && _stillTime >= StationaryAfterSeconds)
        {
            // ZUPT: игрок стоит. Гасим накопленный субпиксельный шум, иначе
            // за минуты стоянки карта уползает на метры.
            _pendingX = 0;
            _pendingZ = 0;
            UpdateConfidence(response);
            _lostTimer = 0;
            ReplacePrevious(current);
            Message = "Игрок стоит — позиция зафиксирована";
            return Result(0, 0, response);
        }

        var pendingStep = GeoMath.Distance(0, 0, _pendingX, _pendingZ);
        if (pendingStep < MinApplyMeters)
        {
            UpdateConfidence(response);
            _lostTimer = 0;
            ReplacePrevious(current);
            Message = "Одометрия работает";
            return Result(0, 0, response);
        }

        var appliedX = _pendingX;
        var appliedZ = _pendingZ;
        _pendingX = 0;
        _pendingZ = 0;
        step = pendingStep;

        X = X!.Value + appliedX;
        Z = Z!.Value + appliedZ;

        if (AutoHeading && step >= MinAutoHeadingStepMeters)
        {
            // Курс = азимут последнего смещения. Сглаживаем по кратчайшей
            // дуге, иначе на переходе через 0°/360° стрелка дёргается.
            var bearing = GeoMath.BearingDeg(X.Value - appliedX, Z.Value - appliedZ, X.Value, Z.Value);
            var turn = GeoMath.AngleDelta(bearing, HeadingDeg);
            HeadingDeg = GeoMath.NormalizeAngle(HeadingDeg + turn * AutoHeadingSmoothing);
        }

        _travelM += step;
        UpdateConfidence(response);
        UpdateAccuracy();
        _lostTimer = 0;
        Message = "Одометрия работает";

        ReplacePrevious(current);
        return Result(appliedX, appliedZ, response);
    }

    /// <summary>Запоминает кадр как опорный для следующей корреляции.</summary>
    private void ReplacePrevious(Mat current)
    {
        _previousPatch?.Dispose();
        _previousPatch = current.Clone();
    }

    private void MarkLost(string message)
    {
        Confidence = 0.1;
        Status = TrackingStatus.Lost;
        RequiresSnap = true;
        _lostTimer = _lostAfterSeconds;
        _pendingX = 0;
        _pendingZ = 0;
        _stillFrames = 0;
        _stillTime = 0;
        _rejectedFrames = 0;
        Message = message;
    }

    private void UpdateConfidence(double response)
    {
        var target = GeoMath.Clamp((response - 0.03) / 0.35, 0.0, 1.0);
        Confidence = 0.72 * Confidence + 0.28 * target;
        Status = Confidence > _degradedConfidence
            ? TrackingStatus.Tracking
            : TrackingStatus.Degraded;
    }

    /// <summary>
    /// Оценка точности: базовая ошибка привязки плюс накопленная ошибка пути
    /// (около 0,8 % пройденного расстояния) плюс штраф за низкую уверенность.
    /// </summary>
    private void UpdateAccuracy() =>
        AccuracyM = BaseAccuracyM
            + AccuracyPerMeter * _travelM
            + 2.5 * (1.0 - GeoMath.Clamp(Confidence, 0.0, 1.0));

    /// <summary>Серый кадр, выравнивание контраста, float 0..1. Размер не меняется.</summary>
    private Mat Prepare(Mat patch)
    {
        using var gray = patch.Channels() == 1 ? patch.Clone() : new Mat();
        if (patch.Channels() != 1)
        {
            Cv2.CvtColor(patch, gray, ColorConversionCodes.BGR2GRAY);
        }

        using var equalized = new Mat();
        _clahe.Apply(gray, equalized);

        var result = new Mat();
        equalized.ConvertTo(result, MatType.CV_32F, 1.0 / 255.0);
        return result;
    }

    /// <summary>
    /// Фазовая корреляция с окном Ханна и параболическим уточнением пика.
    /// Возвращает сдвиг контента в пикселях и высоту пика как меру совпадения.
    /// </summary>
    private (double X, double Y, double Response) Correlate(Mat previous, Mat current)
    {
        var window = EnsureCorrelationWindow(current.Size());

        using var a = new Mat();
        using var b = new Mat();
        Cv2.Multiply(previous, window, a);
        Cv2.Multiply(current, window, b);

        using var fa = new Mat();
        using var fb = new Mat();
        Cv2.Dft(a, fa, DftFlags.ComplexOutput);
        Cv2.Dft(b, fb, DftFlags.ComplexOutput);

        // Взаимный спектр: F(current) * conj(F(previous)).
        using var cross = new Mat();
        Cv2.MulSpectrums(fb, fa, cross, 0, conjB: true);

        Cv2.Split(cross, out var planes);
        using var re = planes[0];
        using var im = planes[1];
        using var magnitude = new Mat();
        Cv2.Magnitude(re, im, magnitude);
        Cv2.Add(magnitude, Scalar.All(1e-9), magnitude);

        using var reNormalized = new Mat();
        using var imNormalized = new Mat();
        Cv2.Divide(re, magnitude, reNormalized);
        Cv2.Divide(im, magnitude, imNormalized);

        using var merged = new Mat();
        Cv2.Merge(new[] { reNormalized, imNormalized }, merged);

        using var surface = new Mat();
        Cv2.Idft(merged, surface, DftFlags.RealOutput | DftFlags.Scale);

        Cv2.MinMaxLoc(surface, out _, out var peakValue, out _, out var peak);
        var size = surface.Cols;

        var shiftX = (double)peak.X;
        var shiftY = (double)peak.Y;
        if (shiftX > size / 2.0)
        {
            shiftX -= size;
        }

        if (shiftY > size / 2.0)
        {
            shiftY -= size;
        }

        // Уточняем сдвиг прямым поиском по взвешенной невязке. Начинаем с
        // целого пика: дальше ищем в пределах ±0.5 px, то есть там же, где
        // лежит настоящий субпиксельный минимум.
        var mask = EnsureFineMask(current.Size());
        var roi = RoiRect(current.Size());
        using var referenceRoi = new Mat(previous, roi);
        using var currentRoi = new Mat(current, roi);
        using var maskRoi = new Mat(mask, roi);

        var (refinedX, refinedY) = RefineShift(
            referenceRoi,
            currentRoi,
            maskRoi,
            -Math.Round(shiftX),
            -Math.Round(shiftY));

        return (-refinedX, -refinedY, peakValue);
    }

    /// <summary>
    /// Три прохода поиска по SSD: X, Y и снова X (сдвиги по осям слегка
    /// связаны, повторный проход убирает остаток). Возвращает сдвиг, при
    /// котором <paramref name="current"/> лучше всего совпадает с
    /// <paramref name="reference"/>.
    /// </summary>
    private static (double X, double Y) RefineShift(
        Mat reference,
        Mat current,
        Mat weights,
        double startX,
        double startY)
    {
        var x = startX;
        var y = startY;
        x += BestOffset(reference, current, weights, x, y, alongX: true);
        y += BestOffset(reference, current, weights, x, y, alongX: false);
        x += BestOffset(reference, current, weights, x, y, alongX: true);
        return (x, y);
    }

    /// <summary>Парабола по трём точкам SSD-кривой вокруг лучшего отсчёта.</summary>
    private static double BestOffset(
        Mat reference,
        Mat current,
        Mat weights,
        double fixedX,
        double fixedY,
        bool alongX)
    {
        var points = RefineHalf * 2 + 1;
        var values = new double[points];
        var bestIndex = 0;
        var bestValue = double.MaxValue;

        for (var index = 0; index < points; index++)
        {
            var offset = (index - RefineHalf) * RefineStep;
            var dx = alongX ? fixedX + offset : fixedX;
            var dy = alongX ? fixedY : fixedY + offset;

            // Кадры сдвигаются навстречу друг другу на половину смещения.
            // Если двигать только текущий кадр, интерполяция размывает его
            // тем сильнее, чем больше сдвиг, и SSD систематически выбирает
            // меньшее смещение. На 30 fps сдвиг за кадр — доли пикселя, и
            // такая ошибка съедала почти всё движение: метка замирала.
            using var warpedCurrent = Warp(current, dx / 2.0, dy / 2.0);
            using var warpedReference = Warp(reference, -dx / 2.0, -dy / 2.0);
            var value = MaskedSsd(warpedReference, warpedCurrent, weights);
            values[index] = value;
            if (value < bestValue)
            {
                bestValue = value;
                bestIndex = index;
            }
        }

        var refine = bestIndex > 0 && bestIndex < points - 1
            ? ParabolicOffset(values[bestIndex - 1], values[bestIndex], values[bestIndex + 1]) * RefineStep
            : 0.0;

        return (bestIndex - RefineHalf) * RefineStep + refine;
    }

    /// <summary>Сдвиг кадра на дробное число пикселей с бикубической интерполяцией.</summary>
    private static Mat Warp(Mat source, double dx, double dy)
    {
        using var matrix = new Mat<double>(2, 3);
        matrix.Set(0, 0, 1.0);
        matrix.Set(0, 1, 0.0);
        matrix.Set(0, 2, dx);
        matrix.Set(1, 0, 0.0);
        matrix.Set(1, 1, 1.0);
        matrix.Set(1, 2, dy);

        var result = new Mat();
        Cv2.WarpAffine(
            source,
            result,
            matrix,
            source.Size(),
            InterpolationFlags.Cubic,
            BorderTypes.Replicate);
        return result;
    }

    /// <summary>Взвешенная сумма квадратов разницы.</summary>
    private static double MaskedSsd(Mat reference, Mat warped, Mat weights)
    {
        using var difference = new Mat();
        Cv2.Subtract(warped, reference, difference);
        using var squared = new Mat();
        Cv2.Multiply(difference, difference, squared);
        using var weighted = new Mat();
        Cv2.Multiply(squared, weights, weighted);
        return Cv2.Sum(weighted).Val0;
    }

    private static Rect RoiRect(Size size)
    {
        var side = Math.Min(size.Width, size.Height);
        var margin = Math.Max(4, (int)Math.Round(side * RoiMarginFraction));
        margin = Math.Min(margin, side / 3);
        return new Rect(margin, margin, size.Width - 2 * margin, size.Height - 2 * margin);
    }

    /// <summary>
    /// Веса уточнения: окно Ханна без центрального диска (там стрелка
    /// игрока — она не двигается вместе с картой и «тянет» оценку к нулю)
    /// и без внешней рамки компаса.
    /// </summary>
    private Mat EnsureFineMask(Size size)
    {
        if (_fineMask is not null && _fineMaskSize == size)
        {
            return _fineMask;
        }

        _fineMask?.Dispose();
        _fineMask = new Mat();
        Cv2.CreateHanningWindow(_fineMask, size, MatType.CV_32F);

        var side = Math.Min(size.Width, size.Height);
        var centerX = size.Width / 2.0;
        var centerY = size.Height / 2.0;
        var radius = side * CenterHoleFraction;
        var border = side * BorderFraction;
        var disc = side * DiscFraction;

        for (var y = 0; y < size.Height; y++)
        {
            for (var x = 0; x < size.Width; x++)
            {
                var dx = x - centerX + 0.5;
                var dy = y - centerY + 0.5;
                var distanceSquared = dx * dx + dy * dy;
                var insideHole = distanceSquared < radius * radius;

                // Углы квадратного кадра вне круга миникарты статичны: если их
                // не обнулить, корреляция «увидит» совпадение при нулевом сдвиге.
                var outsideDisc = distanceSquared > disc * disc;
                var insideBorder = x < border
                    || y < border
                    || x >= size.Width - border
                    || y >= size.Height - border;
                if (insideHole || outsideDisc || insideBorder)
                {
                    _fineMask.Set(y, x, 0f);
                }
            }
        }

        _fineMaskSize = size;
        return _fineMask;
    }

    private static double ParabolicOffset(double before, double center, double after)
    {
        var denominator = before - 2 * center + after;
        if (Math.Abs(denominator) < 1e-12)
        {
            return 0.0;
        }

        var offset = 0.5 * (before - after) / denominator;
        return double.IsFinite(offset) ? Math.Clamp(offset, -0.5, 0.5) : 0.0;
    }

    /// <summary>
    /// Окно фазовой корреляции: Ханн плюс плавно погашенный центр. Стрелка
    /// игрока не двигается вместе с картой и без гашения тянет пик к нулю.
    /// Гасим именно плавно: резкая маска сама создаёт статичную границу,
    /// за которую цепляется корреляция, и оценка снова съезжает к нулю.
    /// </summary>
    private Mat EnsureCorrelationWindow(Size size)
    {
        if (_correlationWindow is not null && _correlationWindowSize == size)
        {
            return _correlationWindow;
        }

        _correlationWindow?.Dispose();
        _correlationWindow = new Mat();
        Cv2.CreateHanningWindow(_correlationWindow, size, MatType.CV_32F);

        var side = Math.Min(size.Width, size.Height);
        var centerX = size.Width / 2.0;
        var centerY = size.Height / 2.0;
        var hole = side * CorrelationHoleFraction;
        var feather = Math.Max(2.0, side * CorrelationFeatherFraction);

        for (var y = 0; y < size.Height; y++)
        {
            for (var x = 0; x < size.Width; x++)
            {
                var dx = x - centerX + 0.5;
                var dy = y - centerY + 0.5;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                var weight = SmoothStep((distance - hole) / feather);
                _correlationWindow.Set(y, x, _correlationWindow.At<float>(y, x) * (float)weight);
            }
        }

        _correlationWindowSize = size;
        return _correlationWindow;
    }

    private static double SmoothStep(double value)
    {
        var t = GeoMath.Clamp(value, 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    private LocalizationResult Decay(double dt, string message)
    {
        Confidence *= Math.Exp(-dt / 0.7);
        if (Confidence < _lostConfidence)
        {
            _lostTimer += dt;
        }
        else
        {
            _lostTimer = 0;
        }

        if (!HasPosition || _lostTimer >= _lostAfterSeconds)
        {
            Status = TrackingStatus.Lost;
            RequiresSnap = true;
        }
        else if (Confidence > _degradedConfidence)
        {
            Status = TrackingStatus.Tracking;
        }
        else
        {
            Status = TrackingStatus.Degraded;
        }

        Message = message;
        if (HasPosition)
        {
            UpdateAccuracy();
        }

        return Result(0, 0, 0);
    }

    private LocalizationResult Result(double deltaX, double deltaZ, double response) =>
        new(
            Status,
            GeoMath.Clamp(Confidence, 0, 1),
            X,
            Z,
            HeadingDeg,
            deltaX,
            deltaZ,
            response,
            RequiresSnap,
            Message,
            AccuracyM);

    public void Dispose()
    {
        _previousPatch?.Dispose();
        _previousPatch = null;
        _correlationWindow?.Dispose();
        _correlationWindow = null;
        _fineMask?.Dispose();
        _fineMask = null;
        _clahe.Dispose();
    }
}
