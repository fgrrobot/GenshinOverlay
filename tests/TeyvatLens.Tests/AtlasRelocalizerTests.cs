using OpenCvSharp;
using TeyvatLens.Core.Localization;
using TeyvatLens.Core.RealMap;
using TeyvatLens.Core.Vision;

namespace TeyvatLens.Tests;

public sealed class AtlasRelocalizerTests
{
    [Fact]
    public void Locate_FindsCropCenterOnSyntheticAtlas()
    {
        using var atlas = BuildAtlas(1600, seed: 7);
        using var relocalizer = new AtlasRelocalizer();
        using var index = relocalizer.BuildIndex(atlas, features: 8000);

        const int center = 800;
        const int half = 400;
        using var crop = new Mat(
            atlas,
            new OpenCvSharp.Rect(center - half, center - half, half * 2, half * 2)).Clone();
        using var query = new Mat();
        Cv2.Resize(crop, query, new Size(400, 400), 0, 0, InterpolationFlags.Area);
        AddNoise(query, sigma: 4, seed: 3);

        var profile = new MapProfile("atlas.png", 1.0, 0, 0);
        var result = relocalizer.Locate(query, index, profile);

        Assert.True(result.Success, result.Message);
        Assert.True(
            result.Inliers >= AtlasRelocalizer.MinInliers,
            $"Подтверждений слишком мало: {result.Inliers}.");
        Assert.InRange(result.WorldX, center - 12, center + 12);
        Assert.InRange(result.WorldZ, center - 12, center + 12);
        Assert.InRange(result.RotationDeg, -AtlasRelocalizer.MaxRotationDeg, AtlasRelocalizer.MaxRotationDeg);
    }

    [Fact]
    public void Locate_AppliesAtlasProfileOriginAndScale()
    {
        using var atlas = BuildAtlas(1200, seed: 11);
        using var relocalizer = new AtlasRelocalizer();
        using var index = relocalizer.BuildIndex(atlas, features: 8000);

        const int center = 600;
        using var query = new Mat(
            atlas,
            new OpenCvSharp.Rect(center - 250, center - 250, 500, 500)).Clone();
        AddNoise(query, sigma: 2, seed: 5);

        // Атлас уменьшен вдвое: один пиксель = два метра, левый верхний угол — (100, 200).
        var profile = new MapProfile("atlas.png", 0.5, 100, 200);
        var result = relocalizer.Locate(query, index, profile);

        Assert.True(result.Success, result.Message);
        Assert.InRange(result.WorldX, 100 + center * 2 - 20, 100 + center * 2 + 20);
        Assert.InRange(result.WorldZ, 200 + center * 2 - 20, 200 + center * 2 + 20);
    }

    [Fact]
    public void Locate_FailsOnBlankQuery()
    {
        using var atlas = BuildAtlas(800, seed: 2);
        using var relocalizer = new AtlasRelocalizer();
        using var index = relocalizer.BuildIndex(atlas, features: 4000);
        using var query = new Mat(400, 400, MatType.CV_8UC3, Scalar.Black);

        var result = relocalizer.Locate(query, index, new MapProfile("atlas.png", 1.0));

        Assert.False(result.Success);
        Assert.NotEmpty(result.Message);
    }

    [Fact]
    public void BuildQueryMask_ClearsCenterAndBorder()
    {
        using var mask = AtlasRelocalizer.BuildQueryMask(new Size(200, 200));

        Assert.Equal(0, mask.At<byte>(100, 100));
        Assert.Equal(0, mask.At<byte>(1, 1));
        Assert.Equal(255, mask.At<byte>(100, 40));
    }

    [Fact]
    public void SuggestFeatureCount_ScalesWithAreaAndClamps()
    {
        using var tiny = new Mat(200, 200, MatType.CV_8UC3, Scalar.Black);
        using var region = new Mat(2560, 2560, MatType.CV_8UC3, Scalar.Black);
        using var global = new Mat(8192, 9216, MatType.CV_8UC3, Scalar.Black);

        Assert.Equal(AtlasRelocalizer.MinAtlasFeatures, AtlasRelocalizer.SuggestFeatureCount(tiny));
        Assert.Equal(AtlasRelocalizer.MaxAtlasFeatures, AtlasRelocalizer.SuggestFeatureCount(global));

        var regionFeatures = AtlasRelocalizer.SuggestFeatureCount(region);
        Assert.InRange(regionFeatures, AtlasRelocalizer.MinAtlasFeatures, AtlasRelocalizer.MaxAtlasFeatures);
    }

    [Fact]
    public void BuildIndex_UsesAdaptiveFeatureCountWhenNotSpecified()
    {
        using var atlas = BuildAtlas(900, seed: 21);
        using var relocalizer = new AtlasRelocalizer();

        using var index = relocalizer.BuildIndex(atlas);

        Assert.True(index.FeatureCount > 0);
        Assert.True(index.FeatureCount <= AtlasRelocalizer.MaxAtlasFeatures);
    }

    [Fact]
    public void Locate_RejectsQueryFromDifferentRegion()
    {
        // Два независимых «мира»: запрос из второго не должен находиться
        // на первом атласе — иначе привязка уводит игрока в случайную точку.
        using var atlas = BuildAtlas(1400, seed: 31);
        using var other = BuildAtlas(1400, seed: 77);
        using var relocalizer = new AtlasRelocalizer();
        using var index = relocalizer.BuildIndex(atlas, features: 12000);

        using var query = new Mat(other, new OpenCvSharp.Rect(500, 500, 400, 400)).Clone();
        var result = relocalizer.Locate(query, index, new MapProfile("atlas.png", 1.0));

        Assert.False(result.Success);
    }

    [Fact]
    public void Locate_ReportsCorrelationForSuccessfulMatch()
    {
        using var atlas = BuildAtlas(1200, seed: 13);
        using var relocalizer = new AtlasRelocalizer();
        using var index = relocalizer.BuildIndex(atlas, features: 10000);

        using var query = new Mat(atlas, new OpenCvSharp.Rect(400, 400, 400, 400)).Clone();
        var result = relocalizer.Locate(query, index, new MapProfile("atlas.png", 1.0));

        Assert.True(result.Success, result.Message);
        Assert.True(
            result.Ncc >= AtlasRelocalizer.MinNcc,
            $"Корреляция слишком низкая: {result.Ncc:0.00}.");
        Assert.True(result.Score > 0);
    }

    [Fact]
    public void Locate_WorksWithGrayscaleAtlas()
    {
        // Глобальный атлас хранится одноканальным (75 МБ вместо 226 МБ),
        // поэтому серый вход — не экзотика, а основной путь.
        using var color = BuildAtlas(1200, seed: 19);
        using var atlas = new Mat();
        Cv2.CvtColor(color, atlas, ColorConversionCodes.BGR2GRAY);
        using var relocalizer = new AtlasRelocalizer();
        using var index = relocalizer.BuildIndex(atlas, features: 10000);

        using var query = new Mat(atlas, new OpenCvSharp.Rect(400, 400, 400, 400)).Clone();
        var result = relocalizer.Locate(query, index, new MapProfile("atlas.png", 1.0));

        Assert.True(result.Success, result.Message);
        Assert.InRange(result.WorldX, 588, 612);
        Assert.InRange(result.WorldZ, 588, 612);
    }

    [Fact]
    public void CropCenter_ReturnsSquareAroundCenter()
    {
        using var source = new Mat(1000, 800, MatType.CV_8UC3, new Scalar(1, 2, 3));

        using var crop = MapScreenCapture.CropCenter(source, 0.5);

        Assert.Equal(400, crop.Width);
        Assert.Equal(400, crop.Height);
    }

    [Fact]
    public void IsMostlyBlank_DetectsBlackAndTexturedFrames()
    {
        using var black = new Mat(200, 200, MatType.CV_8UC3, Scalar.Black);
        using var textured = BuildAtlas(200, seed: 4);

        Assert.True(MapScreenCapture.IsMostlyBlank(black));
        Assert.False(MapScreenCapture.IsMostlyBlank(textured));
    }

    private static Mat BuildAtlas(int size, int seed)
    {
        var atlas = new Mat(size, size, MatType.CV_8UC3, new Scalar(28, 34, 40));
        var random = new Random(seed);

        for (var index = 0; index < size * 2; index++)
        {
            var center = new Point(random.Next(0, size), random.Next(0, size));
            var radius = random.Next(4, 36);
            var color = new Scalar(
                random.Next(0, 256),
                random.Next(0, 256),
                random.Next(0, 256));
            Cv2.Circle(atlas, center, radius, color, random.Next(1, 4), LineTypes.AntiAlias);
        }

        for (var index = 0; index < size; index++)
        {
            var first = new Point(random.Next(0, size), random.Next(0, size));
            var second = new Point(random.Next(0, size), random.Next(0, size));
            var color = new Scalar(
                random.Next(0, 256),
                random.Next(0, 256),
                random.Next(0, 256));
            Cv2.Line(atlas, first, second, color, random.Next(1, 3), LineTypes.AntiAlias);
        }

        return atlas;
    }

    private static void AddNoise(Mat image, double sigma, int seed)
    {
        Cv2.SetTheRNG((ulong)seed);
        using var noise = new Mat(image.Size(), image.Type());
        Cv2.Randn(noise, Scalar.All(0), Scalar.All(sigma));
        Cv2.Add(image, noise, image);
    }
}
