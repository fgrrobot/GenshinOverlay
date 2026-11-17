using System.Net.Http;
using OpenCvSharp;

namespace TeyvatLens.Core.RealMap.GenshinMap;

/// <summary>
/// Собирает фрагмент карты Тейвата из тайлов.
///
/// Целиком карта — 36864×32768 px (около 3.6 ГБ в памяти), поэтому скачивается
/// только квадрат вокруг нужной точки: 10×10 тайлов = 2560×2560 px ≈ 2,5 км.
/// </summary>
public sealed class GenshinTileComposer
{
    private readonly GenshinMapClient _client;
    private readonly string _cacheDir;

    public GenshinTileComposer(GenshinMapClient client, string cacheDir)
    {
        _client = client;
        _cacheDir = cacheDir;
    }

    /// <summary>
    /// Вычисляет сетку тайлов для квадрата вокруг точки. Левый верхний угол
    /// выравнивается по границе тайла, чтобы координаты не «плыли».
    /// </summary>
    public static (int LeftTile, int TopTile, int Tiles) ComputeRegion(
        double centerX,
        double centerZ,
        int sizePx)
    {
        if (sizePx <= 0 || sizePx % GenshinMapInfo.TileSize != 0)
        {
            throw new ArgumentException(
                $"Размер области должен быть кратен {GenshinMapInfo.TileSize} px.",
                nameof(sizePx));
        }

        var tiles = sizePx / GenshinMapInfo.TileSize;
        var leftTile = (int)Math.Floor((centerX - sizePx / 2.0) / GenshinMapInfo.TileSize);
        var topTile = (int)Math.Floor((centerZ - sizePx / 2.0) / GenshinMapInfo.TileSize);
        return (leftTile, topTile, tiles);
    }

    /// <summary>
    /// Сетка уменьшенного атласа всей карты для отрицательного зума.
    /// Например, для карты 36864×32768 и зума −3 получается 18×16 тайлов,
    /// то есть изображение 4608×4096 px.
    /// </summary>
    public static (int TilesX, int TilesY, int Width, int Height) ComputeGlobalGrid(
        GenshinMapInfo info,
        int zoom)
    {
        if (zoom >= 0 || zoom < info.MinZoom)
        {
            throw new ArgumentOutOfRangeException(
                nameof(zoom),
                "Глобальный атлас строится только для отрицательных зумов карты.");
        }

        var divisor = 1 << Math.Abs(zoom);
        var width = info.TotalWidth / divisor;
        var height = info.TotalHeight / divisor;
        var tilesX = (int)Math.Ceiling(width / (double)GenshinMapInfo.TileSize);
        var tilesY = (int)Math.Ceiling(height / (double)GenshinMapInfo.TileSize);
        return (tilesX, tilesY, tilesX * GenshinMapInfo.TileSize, tilesY * GenshinMapInfo.TileSize);
    }

    /// <summary>
    /// Собирает и кэширует атлас всей карты в уменьшенном масштабе.
    /// Первый запуск скачивает все тайлы (для зума −3 — 288 штук), повторный
    /// читает готовый PNG из кэша.
    /// </summary>
    public async Task<GenshinGlobalAtlas> ComposeGlobalAsync(
        GenshinMapInfo info,
        int zoom,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var (tilesX, tilesY, width, height) = ComputeGlobalGrid(info, zoom);
        var directory = Path.Combine(_cacheDir, "global");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"global_z{zoom}.png");
        var pxPerMeter = 1.0 / (1 << Math.Abs(zoom));

        if (File.Exists(path))
        {
            progress?.Report("Глобальный атлас уже в кэше.");
            return new GenshinGlobalAtlas(path, zoom, pxPerMeter, width, height);
        }

        progress?.Report($"Скачиваю глобальный атлас: {tilesX}×{tilesY} тайлов…");

        using var canvas = new Mat(
            height,
            width,
            MatType.CV_8UC1,
            new Scalar(0x18));

        var done = 0;
        var total = tilesX * tilesY;

        for (var row = 0; row < tilesY; row++)
        {
            for (var column = 0; column < tilesX; column++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var bytes = await GetTileOrEmptyAsync(
                    info,
                    column,
                    row,
                    zoom,
                    cancellationToken);

                DrawTileGray(canvas, bytes, column * GenshinMapInfo.TileSize, row * GenshinMapInfo.TileSize);

                done++;
                if (done % 32 == 0 || done == total)
                {
                    progress?.Report($"Скачиваю глобальный атлас: {done}/{total} тайлов…");
                }
            }
        }

        if (!Cv2.ImWrite(path, canvas))
        {
            throw new InvalidOperationException("Не удалось сохранить глобальный атлас.");
        }

        return new GenshinGlobalAtlas(path, zoom, pxPerMeter, width, height);
    }

    public async Task<GenshinMapRegion> ComposeAsync(
        GenshinMapInfo info,
        double centerX,
        double centerZ,
        int sizePx,
        int zoom,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var (leftTile, topTile, tiles) = ComputeRegion(centerX, centerZ, sizePx);
        var maxTileX = Math.Max(0, info.TotalWidth / GenshinMapInfo.TileSize - tiles);
        var maxTileY = Math.Max(0, info.TotalHeight / GenshinMapInfo.TileSize - tiles);
        leftTile = Math.Clamp(leftTile, 0, maxTileX);
        topTile = Math.Clamp(topTile, 0, maxTileY);

        var directory = Path.Combine(_cacheDir, "regions");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"region_{leftTile}_{topTile}_{tiles}_z{zoom}.png");
        var left = leftTile * GenshinMapInfo.TileSize;
        var top = topTile * GenshinMapInfo.TileSize;

        if (File.Exists(path))
        {
            progress?.Report("Фрагмент карты уже в кэше.");
            return new GenshinMapRegion(path, left, top, sizePx);
        }

        progress?.Report($"Скачиваю фрагмент карты: {tiles}×{tiles} тайлов…");

        using var canvas = new Mat(
            sizePx,
            sizePx,
            MatType.CV_8UC3,
            new Scalar(0x1f, 0x18, 0x12));

        var done = 0;
        var total = tiles * tiles;

        for (var row = 0; row < tiles; row++)
        {
            for (var column = 0; column < tiles; column++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var bytes = await GetTileOrEmptyAsync(
                    info,
                    leftTile + column,
                    topTile + row,
                    zoom,
                    cancellationToken);

                DrawTile(canvas, bytes, column * GenshinMapInfo.TileSize, row * GenshinMapInfo.TileSize);

                done++;
                if (done % 10 == 0 || done == total)
                {
                    progress?.Report($"Скачиваю фрагмент карты: {done}/{total} тайлов…");
                }
            }
        }

        if (!Cv2.ImWrite(path, canvas))
        {
            throw new InvalidOperationException("Не удалось сохранить фрагмент карты.");
        }

        return new GenshinMapRegion(path, left, top, sizePx);
    }

    /// <summary>
    /// Скачивает тайл, но не роняет всю сборку, если отдельного тайла нет
    /// (край карты, дыра в пирамиде). Такой тайл остаётся фоном.
    /// </summary>
    private async Task<byte[]> GetTileOrEmptyAsync(
        GenshinMapInfo info,
        int tileX,
        int tileY,
        int zoom,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _client.GetTileAsync(info, tileX, tileY, zoom, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Копирует тайл в серый атлас. Глобальный атлас нужен только для
    /// сопоставления, поэтому хранится одноканальным: 9216×8192 в цвете —
    /// это 226 МБ, а в сером — 75 МБ.
    /// </summary>
    private static void DrawTileGray(Mat canvas, byte[] bytes, int x, int y)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        using var decoded = Cv2.ImDecode(bytes, ImreadModes.Unchanged);
        if (decoded.Empty())
        {
            return;
        }

        var width = Math.Min(decoded.Width, canvas.Width - x);
        var height = Math.Min(decoded.Height, canvas.Height - y);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        using var tile = new Mat(decoded, new Rect(0, 0, width, height));
        using var roi = new Mat(canvas, new Rect(x, y, width, height));

        switch (tile.Channels())
        {
            case 1:
                tile.CopyTo(roi);
                break;
            case 3:
            {
                using var gray = new Mat();
                Cv2.CvtColor(tile, gray, ColorConversionCodes.BGR2GRAY);
                gray.CopyTo(roi);
                break;
            }
            case 4:
            {
                using var gray = new Mat();
                using var alpha = new Mat();
                Cv2.CvtColor(tile, gray, ColorConversionCodes.BGRA2GRAY);
                Cv2.ExtractChannel(tile, alpha, 3);
                gray.CopyTo(roi, alpha);
                break;
            }
        }
    }

    private static void DrawTile(Mat canvas, byte[] bytes, int x, int y)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        using var decoded = Cv2.ImDecode(bytes, ImreadModes.Unchanged);
        if (decoded.Empty())
        {
            return;
        }

        var width = Math.Min(decoded.Width, canvas.Width - x);
        var height = Math.Min(decoded.Height, canvas.Height - y);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        using var tile = new Mat(decoded, new Rect(0, 0, width, height));
        using var roi = new Mat(canvas, new Rect(x, y, width, height));

        if (tile.Channels() == 4)
        {
            using var color = new Mat();
            using var alpha = new Mat();
            Cv2.CvtColor(tile, color, ColorConversionCodes.BGRA2BGR);
            Cv2.ExtractChannel(tile, alpha, 3);
            color.CopyTo(roi, alpha);
            return;
        }

        if (tile.Channels() == 3)
        {
            tile.CopyTo(roi);
            return;
        }

        using var gray = new Mat();
        Cv2.CvtColor(tile, gray, ColorConversionCodes.GRAY2BGR);
        gray.CopyTo(roi);
    }
}
