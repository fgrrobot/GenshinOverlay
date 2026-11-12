using System.Runtime.InteropServices;
using OpenCvSharp;

namespace TeyvatLens.Core.Simulation;

/// <summary>
/// Синтетический демонстрационный мир: атлас, движение игрока и кадры
/// миникарты. Позволяет проверить весь конвейер без запущенной игры.
/// </summary>
public sealed class DemoWorld : IDisposable
{
    private readonly Mat _atlas;
    private readonly double _pxPerMeter;
    private readonly double _radiusM;
    private readonly double _baseSpeedMps;
    private double _speed;
    private double _distance;

    public DemoWorld(
        string atlasPath,
        double worldSizeM = 900.0,
        double pxPerMeter = 2.0,
        double radiusM = 90.0,
        double speedMps = 8.0)
    {
        _pxPerMeter = pxPerMeter;
        _radiusM = radiusM;
        _baseSpeedMps = speedMps;
        _speed = speedMps;

        var size = (int)Math.Round(worldSizeM * pxPerMeter);
        _atlas = LoadOrGenerateAtlas(atlasPath, size);
        Reset();
    }

    public double TruthX { get; private set; }

    public double TruthZ { get; private set; }

    public double TruthHeadingDeg { get; private set; }

    public bool Lost { get; set; }

    /// <summary>Масштаб кадра миникарты: пикселей на метр мира.</summary>
    public double PxPerMeter => _pxPerMeter;

    public double SpeedMps
    {
        get => _speed;
        set
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _speed = value;
        }
    }

    public void Reset()
    {
        _distance = 0;
        _speed = _baseSpeedMps;
        Lost = false;
        UpdateTruth();
    }

    public void Advance(double dt)
    {
        _distance += _speed * Math.Max(0, dt);
        UpdateTruth();
    }

    /// <summary>Телепорт в точку маршрута (демонстрация сценария телепорта).</summary>
    public void TeleportTo(double x, double z)
    {
        _distance = DemoRoute.NearestDistance(x, z);
        UpdateTruth();
    }

    /// <summary>Возвращает кадр миникарты (BGR). Mat нужно освободить.</summary>
    public Mat RenderMinimap()
    {
        var diameter = Math.Max(64, (int)Math.Round(_radiusM * 2 * _pxPerMeter));
        if (Lost)
        {
            return new Mat(diameter, diameter, MatType.CV_8UC3, new Scalar(9, 11, 15));
        }

        var center = new Point2f(
            (float)(TruthX * _pxPerMeter),
            (float)(TruthZ * _pxPerMeter));
        var crop = new Mat();
        Cv2.GetRectSubPix(_atlas, new Size(diameter, diameter), center, crop);

        using var mask = new Mat(diameter, diameter, MatType.CV_8UC1, Scalar.Black);
        Cv2.Circle(
            mask,
            new Point(diameter / 2, diameter / 2),
            diameter / 2 - 2,
            Scalar.White,
            -1,
            LineTypes.AntiAlias);
        using var inverted = new Mat();
        Cv2.BitwiseNot(mask, inverted);
        crop.SetTo(Scalar.Black, inverted);

        return crop;
    }

    private void UpdateTruth()
    {
        var (x, z, heading) = DemoRoute.Sample(_distance);
        TruthX = x;
        TruthZ = z;
        TruthHeadingDeg = heading;
    }

    private static Mat LoadOrGenerateAtlas(string path, int size)
    {
        if (File.Exists(path))
        {
            var existing = Cv2.ImRead(path, ImreadModes.Color);
            if (!existing.Empty() && existing.Width == size && existing.Height == size)
            {
                return existing;
            }

            existing.Dispose();
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var atlas = GenerateAtlas(size);
        Cv2.ImWrite(path, atlas);
        return atlas;
    }

    private static Mat GenerateAtlas(int size)
    {
        var random = new Random(20261112);

        // Детерминированный шум без зависимости от глобального RNG OpenCV.
        using var noise = new Mat(size, size, MatType.CV_8UC1);
        var noiseBytes = new byte[size * size];
        for (var index = 0; index < noiseBytes.Length; index++)
        {
            noiseBytes[index] = (byte)Math.Clamp(
                128 + (random.NextDouble() - 0.5) * 84,
                0,
                255);
        }

        Marshal.Copy(noiseBytes, 0, noise.Data, noiseBytes.Length);

        using var smooth = new Mat();
        Cv2.GaussianBlur(noise, smooth, new Size(0, 0), 6.0);
        Cv2.Normalize(smooth, smooth, 0, 255, NormTypes.MinMax);

        // Спокойная «картографическая» палитра вместо яркого colormap.
        // Cv2.LUT требует, чтобы каналов в таблице было столько же, сколько
        // в источнике, поэтому красим каждый канал отдельно и собираем BGR.
        using var palette = BuildPalette();
        Cv2.Split(palette, out var palettePlanes);
        using var blueLut = palettePlanes[0];
        using var greenLut = palettePlanes[1];
        using var redLut = palettePlanes[2];
        using var blue = new Mat();
        using var green = new Mat();
        using var red = new Mat();
        Cv2.LUT(smooth, blueLut, blue);
        Cv2.LUT(smooth, greenLut, green);
        Cv2.LUT(smooth, redLut, red);

        var atlas = new Mat();
        Cv2.Merge(new[] { blue, green, red }, atlas);

        var scale = size / 1800.0;
        var water = new Scalar(155, 112, 62);
        var road = new Scalar(170, 192, 208);
        var tree = new Scalar(48, 92, 54);
        var rock = new Scalar(105, 112, 120);

        // Река.
        var river = new[]
        {
            new Point(0, (int)(1330 * scale)),
            new Point((int)(320 * scale), (int)(1180 * scale)),
            new Point((int)(590 * scale), (int)(920 * scale)),
            new Point((int)(820 * scale), (int)(680 * scale)),
            new Point((int)(1120 * scale), (int)(540 * scale)),
            new Point((int)(1460 * scale), (int)(400 * scale)),
            new Point(size, (int)(330 * scale))
        };
        Cv2.Polylines(
            atlas,
            new[] { river },
            false,
            water,
            Math.Max(8, (int)(26 * scale)),
            LineTypes.AntiAlias);

        // Озеро.
        Cv2.Ellipse(
            atlas,
            new Point((int)(1260 * scale), (int)(1240 * scale)),
            new Size((int)(235 * scale), (int)(150 * scale)),
            12,
            0,
            360,
            water,
            -1,
            LineTypes.AntiAlias);

        // Дороги.
        var roadA = new[]
        {
            new Point((int)(120 * scale), (int)(520 * scale)),
            new Point((int)(380 * scale), (int)(470 * scale)),
            new Point((int)(620 * scale), (int)(490 * scale)),
            new Point((int)(900 * scale), (int)(560 * scale)),
            new Point((int)(1200 * scale), (int)(620 * scale)),
            new Point(size, (int)(610 * scale))
        };
        var roadB = new[]
        {
            new Point((int)(470 * scale), (int)(80 * scale)),
            new Point((int)(500 * scale), (int)(380 * scale)),
            new Point((int)(520 * scale), (int)(700 * scale)),
            new Point((int)(560 * scale), (int)(1050 * scale)),
            new Point((int)(640 * scale), (int)(1400 * scale))
        };
        Cv2.Polylines(
            atlas,
            new[] { roadA, roadB },
            false,
            road,
            Math.Max(3, (int)(7 * scale)),
            LineTypes.AntiAlias);

        // Леса и камни — дают характерные точки для корреляции.
        for (var index = 0; index < 2200; index++)
        {
            var x = random.Next(10, size - 10);
            var y = random.Next(10, size - 10);
            var radius = Math.Max(2, (int)(random.Next(3, 9) * scale));
            var color = random.Next(4) == 0 ? rock : tree;
            Cv2.Circle(
                atlas,
                new Point(x, y),
                radius,
                color,
                -1,
                LineTypes.AntiAlias);
        }

        return atlas;
    }

    /// <summary>Палитра «земля → лес → луг → песок» в BGR.</summary>
    private static Mat BuildPalette()
    {
        var stops = new (double Position, Vec3b Color)[]
        {
            (0.00, new Vec3b(30, 42, 36)),
            (0.35, new Vec3b(52, 78, 56)),
            (0.60, new Vec3b(78, 100, 62)),
            (0.82, new Vec3b(116, 114, 80)),
            (1.00, new Vec3b(150, 141, 112))
        };

        var palette = new Mat(1, 256, MatType.CV_8UC3);
        for (var index = 0; index < 256; index++)
        {
            var t = index / 255.0;
            var segment = 0;
            while (segment < stops.Length - 2 && t > stops[segment + 1].Position)
            {
                segment++;
            }

            var start = stops[segment];
            var end = stops[segment + 1];
            var span = Math.Max(1e-6, end.Position - start.Position);
            var k = Math.Clamp((t - start.Position) / span, 0.0, 1.0);

            palette.Set(0, index, new Vec3b(
                (byte)Math.Round(start.Color.Item0 + (end.Color.Item0 - start.Color.Item0) * k),
                (byte)Math.Round(start.Color.Item1 + (end.Color.Item1 - start.Color.Item1) * k),
                (byte)Math.Round(start.Color.Item2 + (end.Color.Item2 - start.Color.Item2) * k)));
        }

        return palette;
    }

    public void Dispose() => _atlas.Dispose();
}
