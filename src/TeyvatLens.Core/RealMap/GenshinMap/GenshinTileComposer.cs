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

                var bytes = await _client.GetTileAsync(
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

    private static void DrawTile(Mat canvas, byte[] bytes, int x, int y)
    {
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
