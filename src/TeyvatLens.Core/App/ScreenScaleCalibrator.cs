using TeyvatLens.Core.Geometry;

namespace TeyvatLens.Core.App;

/// <summary>Результат автоматической калибровки масштаба миникарты.</summary>
public readonly record struct ScaleCalibration(
    bool Applied,
    double PxPerMeter,
    double Ratio,
    string Message);

/// <summary>
/// Уточняет масштаб миникарты (px/м) по двум привязкам к телепортам.
///
/// Одометрия меряет путь в пикселях миникарты, а координаты телепортов —
/// в метрах мира. Отношение этих двух перемещений и есть истинный масштаб:
/// если игрок прошёл 100 м, а одометрия насчитала 150 «метров», масштаб
/// занижен в 1,5 раза.
///
/// Сравниваются векторы перемещения, а не длина пути: извилистый маршрут
/// дал бы завышенное отношение. Направление дополнительно проверяется,
/// чтобы отбросить привязку к чужому телепорту.
/// </summary>
public static class ScreenScaleCalibrator
{
    /// <summary>Минимальное перемещение по одометрии, м.</summary>
    public const double MinOdomDistanceM = 25.0;

    /// <summary>Минимальное перемещение между телепортами, м.</summary>
    public const double MinAtlasDistanceM = 20.0;

    /// <summary>
    /// Допустимое отношение измеренного и картографического пути. Диапазон
    /// широкий намеренно: до первой калибровки стартовая оценка масштаба может
    /// ошибаться в 2–3 раза, и её всё равно нужно исправить. Направление при
    /// этом проверяется отдельно, поэтому «чужую» привязку это не пропустит.
    /// </summary>
    public const double MinRatio = 0.3;

    public const double MaxRatio = 3.0;

    /// <summary>Допустимое расхождение направлений, градусы.</summary>
    public const double MaxBearingErrorDeg = 45.0;

    /// <summary>Изменение меньше 2 % считаем шумом и не сохраняем.</summary>
    public const double MinChangeFraction = 0.02;

    public const double MinPxPerMeter = 0.2;

    public const double MaxPxPerMeter = 20.0;

    public static ScaleCalibration Calibrate(
        double pxPerMeter,
        double odomDx,
        double odomDz,
        double atlasDx,
        double atlasDz)
    {
        if (!double.IsFinite(pxPerMeter) || pxPerMeter <= 0)
        {
            return new ScaleCalibration(false, pxPerMeter, 1.0, string.Empty);
        }

        var odom = Math.Sqrt(odomDx * odomDx + odomDz * odomDz);
        var atlas = Math.Sqrt(atlasDx * atlasDx + atlasDz * atlasDz);
        if (odom < MinOdomDistanceM || atlas < MinAtlasDistanceM)
        {
            return new ScaleCalibration(false, pxPerMeter, odom / Math.Max(atlas, 1e-6), string.Empty);
        }

        var ratio = odom / atlas;
        if (ratio < MinRatio || ratio > MaxRatio)
        {
            return new ScaleCalibration(
                false,
                pxPerMeter,
                ratio,
                "Масштаб не обновлён: пройденный путь расходится с картой.");
        }

        var odomBearing = BearingDeg(odomDx, odomDz);
        var atlasBearing = BearingDeg(atlasDx, atlasDz);
        if (Math.Abs(GeoMath.AngleDelta(atlasBearing, odomBearing)) > MaxBearingErrorDeg)
        {
            return new ScaleCalibration(
                false,
                pxPerMeter,
                ratio,
                "Масштаб не обновлён: направление пути не совпало с картой.");
        }

        var value = Math.Clamp(pxPerMeter * ratio, MinPxPerMeter, MaxPxPerMeter);
        if (Math.Abs(value - pxPerMeter) < pxPerMeter * MinChangeFraction)
        {
            return new ScaleCalibration(false, pxPerMeter, ratio, string.Empty);
        }

        return new ScaleCalibration(
            true,
            value,
            ratio,
            $"Масштаб откалиброван: {value:0.00} px/м (было {pxPerMeter:0.00}).");
    }

    private static double BearingDeg(double dx, double dz) =>
        GeoMath.NormalizeAngle(Math.Atan2(dx, -dz) * 180.0 / Math.PI);
}
