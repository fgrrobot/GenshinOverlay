namespace TeyvatLens.Core.Geometry;

/// <summary>
/// Геометрия мира. Соглашения: X — восток, Z — юг, Y — высота;
/// 0° — север (-Z), углы растут по часовой стрелке; миникарта north-up.
/// </summary>
public static class GeoMath
{
    public static double Distance(double x1, double z1, double x2, double z2) =>
        Math.Sqrt((x2 - x1) * (x2 - x1) + (z2 - z1) * (z2 - z1));

    /// <summary>Азимут от точки 1 к точке 2: 0° — север, 90° — восток.</summary>
    public static double BearingDeg(double x1, double z1, double x2, double z2)
    {
        var dx = x2 - x1;
        var dz = z2 - z1;
        return NormalizeAngle(Math.Atan2(dx, -dz) * 180.0 / Math.PI);
    }

    public static double NormalizeAngle(double angle) => (angle % 360.0 + 360.0) % 360.0;

    /// <summary>Кратчайшая разница углов в диапазоне (-180, 180].</summary>
    public static double AngleDelta(double target, double heading)
    {
        var delta = ((target - heading + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;
        return delta == -180.0 ? 180.0 : delta;
    }

    public static string DirectionLabel(double relativeAngle)
    {
        var value = Math.Abs(relativeAngle);
        if (value <= 22.5) return "прямо";
        if (value <= 112.5) return relativeAngle > 0 ? "справа" : "слева";
        if (value <= 157.5) return relativeAngle > 0 ? "позади справа" : "позади слева";
        return "позади";
    }

    public static string HeightHint(double? targetY, double? playerY, double thresholdM = 3.0)
    {
        if (targetY is null || playerY is null) return "высота неизвестна";
        var delta = targetY.Value - playerY.Value;
        if (Math.Abs(delta) < thresholdM) return "на одном уровне";
        return delta > 0 ? "цель выше" : "цель ниже";
    }

    /// <summary>Переводит мировые метры в пиксели карты (север сверху).</summary>
    public static (double X, double Y) WorldToMap(
        double x,
        double z,
        double centerX,
        double centerZ,
        double scale) =>
        ((x - centerX) * scale, (z - centerZ) * scale);

    public static double Clamp(double value, double low, double high) =>
        Math.Max(low, Math.Min(high, value));
}
