using System.Globalization;
using System.Net.Http;

namespace TeyvatLens.Core.RealMap.GenshinMap;

/// <summary>
/// Клиент данных интерактивной карты Тейвата (HoYoLAB).
///
/// API неофициальный: адреса взяты из веб-версии карты и могут измениться.
/// Всё, что скачивается, кэшируется на диск — повторный запуск не тянет
/// 22 МБ точек заново.
/// </summary>
public sealed class GenshinMapClient : IDisposable
{
    private const string PrimaryBase =
        "https://sg-public-api-static.hoyolab.com/common/map_user/ys_obc";

    private const string FallbackBase =
        "https://api-static.mihoyo.com/common/map_user/ys_obc";

    private const string TileBase = "https://act-webstatic.hoyoverse.com/map_manage/map/";

    private static readonly TimeSpan InfoTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan LabelsTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan PointsTtl = TimeSpan.FromHours(12);

    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private readonly bool _ownsHttp;

    public GenshinMapClient(string cacheDir, HttpClient? http = null)
    {
        _cacheDir = cacheDir;
        _http = http ?? new HttpClient();
        _ownsHttp = http is null;

        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) TeyvatLens/0.3");
        }

        Directory.CreateDirectory(_cacheDir);
    }

    public string CacheDirectory => _cacheDir;

    /// <summary>Адрес тайла карты: 256×256 px, зум 0 — максимальная детализация.</summary>
    public static string TileUrl(GenshinMapInfo info, int tileX, int tileY, int zoom)
    {
        var suffix = (zoom < 0 ? "N" : "P") + Math.Abs(zoom).ToString(CultureInfo.InvariantCulture);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{TileBase}{info.MapId}/{info.Version}/{tileX}_{tileY}_{suffix}.png");
    }

    public async Task<GenshinMapInfo> GetInfoAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report("Читаю метаданные карты…");
        var json = await GetCachedTextAsync(
            "info.json",
            InfoTtl,
            $"{PrimaryBase}/v3/map/info?map_id=2&app_sn=ys_obc&lang=ru-ru",
            cancellationToken);

        return GenshinMapJson.ParseInfo(json);
    }

    public async Task<IReadOnlyDictionary<int, GenshinLabel>> GetLabelsAsync(
        CancellationToken cancellationToken)
    {
        var json = await GetCachedTextAsync(
            "labels.json",
            LabelsTtl,
            $"{PrimaryBase}/v1/map/label/tree?map_id=2&app_sn=ys_obc&lang=ru-ru",
            cancellationToken);

        return GenshinMapJson.ParseLabelTree(json);
    }

    public async Task<IReadOnlyList<GenshinAnchor>> GetAnchorsAsync(
        CancellationToken cancellationToken)
    {
        var json = await GetCachedTextAsync(
            "anchors.json",
            LabelsTtl,
            $"{PrimaryBase}/v1/map/map_anchor/list?map_id=2&app_sn=ys_obc&lang=ru-ru",
            cancellationToken);

        return GenshinMapJson.ParseAnchors(json);
    }

    public async Task<IReadOnlyList<GenshinPoint>> GetPointsAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report("Скачиваю точки карты (около 22 МБ)…");
        var json = await GetCachedTextAsync(
            "points.json",
            PointsTtl,
            $"{PrimaryBase}/v1/map/point/list?map_id=2&app_sn=ys_obc&lang=ru-ru",
            cancellationToken);

        return GenshinMapJson.ParsePoints(json);
    }

    /// <summary>Скачивает тайл, используя дисковый кэш.</summary>
    public async Task<byte[]> GetTileAsync(
        GenshinMapInfo info,
        int tileX,
        int tileY,
        int zoom,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_cacheDir, "tiles");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"{tileX}_{tileY}_z{zoom}.png");
        if (File.Exists(path))
        {
            return await File.ReadAllBytesAsync(path, cancellationToken);
        }

        var url = TileUrl(info, tileX, tileY, zoom);
        var bytes = await _http.GetByteArrayAsync(url, cancellationToken);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        return bytes;
    }

    private async Task<string> GetCachedTextAsync(
        string fileName,
        TimeSpan ttl,
        string url,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(_cacheDir, fileName);
        if (File.Exists(path)
            && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < ttl)
        {
            return await File.ReadAllTextAsync(path, cancellationToken);
        }

        try
        {
            var text = await _http.GetStringAsync(url, cancellationToken);
            await File.WriteAllTextAsync(path, text, cancellationToken);
            return text;
        }
        catch (HttpRequestException) when (url.Contains("hoyolab", StringComparison.Ordinal))
        {
            // Резервный хост отдаёт тот же формат, но без map_version в detail_v2.
            var fallback = url.Replace(PrimaryBase, FallbackBase, StringComparison.Ordinal);
            var text = await _http.GetStringAsync(fallback, cancellationToken);
            await File.WriteAllTextAsync(path, text, cancellationToken);
            return text;
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
