using System.Net.Http;

namespace TeyvatLens.Core.RealMap.GenshinMap;

/// <summary>Результат автозагрузки карты Тейвата.</summary>
public sealed record GenshinLoadResult(
    MapProfile Profile,
    ImportResult Markers,
    GenshinMapInfo Info,
    GenshinMapRegion Region);

/// <summary>
/// Автозагрузка реальной карты и настоящих телепортов с интерактивной карты.
///
/// Скачивает метаданные, точки, именованные регионы и фрагмент изображения
/// вокруг нужного места. Всё кэшируется, поэтому повторный запуск почти
/// мгновенный.
/// </summary>
public sealed class GenshinMapService : IDisposable
{
    /// <summary>10×10 тайлов = 2560 px ≈ 2,5 км вокруг игрока.</summary>
    public const int DefaultRegionPx = 2560;

    /// <summary>Один пиксель нулевого зума карты примерно равен одному метру.</summary>
    public const double PixelsPerMeter = 1.0;

    private readonly GenshinMapClient _client;
    private readonly GenshinTileComposer _composer;

    private GenshinMapInfo? _info;

    public GenshinMapService(string cacheDir, HttpClient? http = null)
    {
        _client = new GenshinMapClient(cacheDir, http);
        _composer = new GenshinTileComposer(_client, cacheDir);
    }

    public async Task<GenshinLoadResult> LoadAsync(
        double? centerX,
        double? centerZ,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var info = _info ??= await _client.GetInfoAsync(progress, cancellationToken);

        var labels = await _client.GetLabelsAsync(cancellationToken);
        var anchors = await _client.GetAnchorsAsync(cancellationToken);
        var points = await _client.GetPointsAsync(progress, cancellationToken);

        progress?.Report("Разбираю точки карты…");
        var markers = GenshinMapImporter.Convert(points, labels, anchors, info);

        var (x, z) = centerX is not null && centerZ is not null
            ? (centerX.Value, centerZ.Value)
            : CenterOf(points, labels, info);

        var region = await _composer.ComposeAsync(
            info,
            x,
            z,
            DefaultRegionPx,
            zoom: 0,
            progress,
            cancellationToken);

        var profile = new MapProfile(region.Path, PixelsPerMeter, region.Left, region.Top);
        return new GenshinLoadResult(profile, markers, info, region);
    }

    /// <summary>
    /// Проверяет, попадает ли точка в загруженный фрагмент. Если нет —
    /// скачивает новый фрагмент вокруг неё (например, после телепорта).
    /// </summary>
    public async Task<MapProfile?> EnsureRegionAsync(
        MapProfile current,
        int atlasWidthPx,
        double worldX,
        double worldZ,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!current.IsValid || Math.Abs(current.PxPerMeter - PixelsPerMeter) > 1e-6)
        {
            return null;
        }

        var margin = 120.0;
        var width = atlasWidthPx / current.PxPerMeter;
        var inside =
            worldX >= current.OriginXMeters + margin &&
            worldX <= current.OriginXMeters + width - margin &&
            worldZ >= current.OriginZMeters + margin &&
            worldZ <= current.OriginZMeters + width - margin;

        if (inside)
        {
            return null;
        }

        var info = _info ??= await _client.GetInfoAsync(progress, cancellationToken);
        var region = await _composer.ComposeAsync(
            info,
            worldX,
            worldZ,
            DefaultRegionPx,
            zoom: 0,
            progress,
            cancellationToken);

        return new MapProfile(region.Path, PixelsPerMeter, region.Left, region.Top);
    }

    private static (double X, double Z) CenterOf(
        IReadOnlyList<GenshinPoint> points,
        IReadOnlyDictionary<int, GenshinLabel> labels,
        GenshinMapInfo info)
    {
        var waypoints = new List<GenshinPoint>();
        foreach (var point in points)
        {
            if (point.DisplayState == 1
                && labels.TryGetValue(point.LabelId, out var label)
                && label.CategoryId == 1)
            {
                waypoints.Add(point);
            }
        }

        if (waypoints.Count == 0)
        {
            return (info.TotalWidth / 2.0, info.TotalHeight / 2.0);
        }

        var xs = waypoints.Select(point => info.ToPixelX(point.X)).OrderBy(value => value).ToArray();
        var zs = waypoints.Select(point => info.ToPixelZ(point.Y)).OrderBy(value => value).ToArray();
        return (xs[xs.Length / 2], zs[zs.Length / 2]);
    }

    public void Dispose() => _client.Dispose();
}
