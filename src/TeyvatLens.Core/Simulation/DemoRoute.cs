namespace TeyvatLens.Core.Simulation;

/// <summary>Кольцевой маршрут демонстрационного мира (x, z в метрах).</summary>
public static class DemoRoute
{
    public static readonly (double X, double Z)[] Points =
    {
        (380.0, 430.0),
        (410.0, 360.0),
        (480.0, 320.0),
        (560.0, 340.0),
        (620.0, 400.0),
        (640.0, 480.0),
        (610.0, 560.0),
        (540.0, 610.0),
        (450.0, 620.0),
        (370.0, 580.0),
        (330.0, 510.0),
        (340.0, 450.0)
    };

    public static readonly int[] WaypointIndexes = { 0, 4, 7, 10 };

    public static readonly string[] WaypointNames =
    {
        "Телепорт: Ветряной холм",
        "Телепорт: Каменный мост",
        "Телепорт: Старый лагерь",
        "Телепорт: Западная роща"
    };

    private static readonly double[] Segments = BuildSegments();

    public static double TotalLength => Segments.Sum();

    /// <summary>Точка и направление движения на заданном расстоянии по маршруту.</summary>
    public static (double X, double Z, double HeadingDeg) Sample(double distanceM)
    {
        var total = TotalLength;
        if (total <= 0)
        {
            return (Points[0].X, Points[0].Z, 0);
        }

        var remaining = (distanceM % total + total) % total;
        for (var index = 0; index < Segments.Length; index++)
        {
            var length = Segments[index];
            if (remaining <= length)
            {
                var (x1, z1) = Points[index];
                var (x2, z2) = Points[(index + 1) % Points.Length];
                var fraction = length > 0 ? remaining / length : 0;
                var x = x1 + (x2 - x1) * fraction;
                var z = z1 + (z2 - z1) * fraction;
                var heading = Math.Atan2(x2 - x1, -(z2 - z1)) * 180.0 / Math.PI;
                return (x, z, (heading % 360.0 + 360.0) % 360.0);
            }

            remaining -= length;
        }

        return (Points[0].X, Points[0].Z, 0);
    }

    /// <summary>Ближайшее расстояние по маршруту к заданной точке.</summary>
    public static double NearestDistance(double x, double z)
    {
        var bestDistance = double.MaxValue;
        var bestAlong = 0.0;
        var step = 2.0;
        for (var along = 0.0; along <= TotalLength; along += step)
        {
            var (sampleX, sampleZ, _) = Sample(along);
            var distance = Math.Sqrt((sampleX - x) * (sampleX - x) + (sampleZ - z) * (sampleZ - z));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestAlong = along;
            }
        }

        return bestAlong;
    }

    /// <summary>Случайная точка рядом с маршрутом (для наполнения базы).</summary>
    public static (double X, double Z) SampleNearRoute(
        Random random,
        double minOffset = 8.0,
        double maxOffset = 70.0,
        double worldSizeM = 900.0)
    {
        var (x, z, heading) = Sample(random.NextDouble() * TotalLength);
        var angle = heading * Math.PI / 180.0;

        // Нормаль к направлению движения.
        var normalX = Math.Cos(angle);
        var normalZ = Math.Sin(angle);
        var offset = minOffset + random.NextDouble() * (maxOffset - minOffset);
        if (random.Next(2) == 0)
        {
            offset = -offset;
        }

        x += normalX * offset + (random.NextDouble() * 12.0 - 6.0);
        z += normalZ * offset + (random.NextDouble() * 12.0 - 6.0);
        return (
            Math.Clamp(x, 20.0, worldSizeM - 20.0),
            Math.Clamp(z, 20.0, worldSizeM - 20.0));
    }

    public static IReadOnlyList<(double X, double Z)> ClusterCenters()
    {
        var random = new Random(2026);
        return Enumerable.Range(0, 6)
            .Select(_ => SampleNearRoute(random, 15.0, 55.0))
            .ToList();
    }

    private static double[] BuildSegments()
    {
        var segments = new double[Points.Length];
        for (var index = 0; index < Points.Length; index++)
        {
            var (x1, z1) = Points[index];
            var (x2, z2) = Points[(index + 1) % Points.Length];
            segments[index] = Math.Sqrt((x2 - x1) * (x2 - x1) + (z2 - z1) * (z2 - z1));
        }

        return segments;
    }
}
