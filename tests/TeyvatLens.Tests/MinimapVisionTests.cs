using System.Runtime.InteropServices;
using OpenCvSharp;
using TeyvatLens.Core.Localization;
using TeyvatLens.Core.Vision;

namespace TeyvatLens.Tests;

public sealed class MinimapVisionTests
{
    [Fact]
    public void Detect_PrefersOuterCircle_WhenItIsClippedByCaptureEdge()
    {
        // Настоящий кадр: миникарта диаметром ~210 px в квадрате 240 px.
        // Круг выходит за правый и верхний край, а внутри есть тёмное
        // «кольцо» текстуры — детектор обязан выбрать внешнюю границу.
        using var frame = new Mat(240, 240, MatType.CV_8UC1, new Scalar(25));
        Cv2.Circle(frame, new Point(144, 102), 104, new Scalar(190), -1, LineTypes.AntiAlias);
        Cv2.Circle(frame, new Point(150, 92), 60, new Scalar(60), -1, LineTypes.AntiAlias);
        Cv2.Line(frame, new Point(60, 150), new Point(220, 110), new Scalar(150), 4);

        var circle = MinimapCircleDetector.Detect(frame);

        Assert.True(circle.Found, "Круг миникарты не найден");
        Assert.InRange(circle.CenterX, 140, 148);
        Assert.InRange(circle.CenterY, 98, 106);
        Assert.InRange(circle.RadiusPx, 100, 108);
        Assert.True(circle.TouchesBorder(240, 240));
    }

    [Fact]
    public void Detect_FindsCircle_WhenItFitsInsideCapture()
    {
        using var frame = new Mat(420, 420, MatType.CV_8UC1, new Scalar(30));
        Cv2.Circle(frame, new Point(168, 125), 104, new Scalar(200), -1, LineTypes.AntiAlias);
        Cv2.Circle(frame, new Point(150, 160), 24, new Scalar(90), -1, LineTypes.AntiAlias);

        var circle = MinimapCircleDetector.Detect(frame);

        Assert.True(circle.Found);
        Assert.InRange(circle.CenterX, 164, 172);
        Assert.InRange(circle.CenterY, 121, 129);
        Assert.InRange(circle.RadiusPx, 100, 108);
        Assert.False(circle.TouchesBorder(420, 420));
    }

    [Fact]
    public void Detect_EmptyFrame_ReturnsNone()
    {
        using var frame = new Mat(240, 240, MatType.CV_8UC1, Scalar.Black);

        var circle = MinimapCircleDetector.Detect(frame);

        Assert.False(circle.Found);
    }

    [Fact]
    public void Normalize_CropsToCircleAndMasksArrowHole()
    {
        var circle = new MinimapCircle(true, 150, 92, 86, 20);
        using var frame = new Mat(240, 240, MatType.CV_8UC1, Scalar.Black);
        Cv2.Circle(frame, new Point(150, 92), 80, Scalar.White, -1, LineTypes.AntiAlias);
        // Метка внутри карты: после нормализации должна остаться на месте
        // относительно центра круга и не измениться в размере.
        Cv2.Rectangle(frame, new Rect(180, 82, 20, 20), new Scalar(128), -1);

        using var normalized = MinimapNormalizer.Normalize(frame, circle);

        Assert.Equal(174, normalized.Width);
        Assert.Equal(174, normalized.Height);

        var center = normalized.Width / 2;
        Assert.Equal(0, normalized.At<byte>(center, center)); // слепое пятно
        Assert.Equal(255, normalized.At<byte>(center, center + 50)); // карта
        Assert.Equal(0, normalized.At<byte>(2, 2)); // вне круга

        // Квадрат 128 в исходнике был на 40 px правее центра круга —
        // после обрезки он там же, без масштабирования.
        Assert.Equal(128, normalized.At<byte>(center, center + 40));
    }

    [Fact]
    public void Normalize_KeepsPixelScale_SoShiftIsNotRescaled()
    {
        var circle = new MinimapCircle(true, 144, 102, 104, 20);
        using var first = Texture(240, 240, seed: 11);
        using var second = Shift(first, 5, 4);

        using var normalizedFirst = MinimapNormalizer.Normalize(first, circle);
        using var normalizedSecond = MinimapNormalizer.Normalize(second, circle);

        Assert.Equal(normalizedFirst.Size(), normalizedSecond.Size());

        // Пиксели не пересчитываются: сдвиг на экране остаётся тем же сдвигом
        // в нормализованном кадре. Иначе px/м, посчитанный по диаметру круга,
        // был бы неверным и метка снова отставала бы.
        foreach (var (y, x) in new[] { (105, 150), (90, 140), (60, 105), (150, 150), (105, 60) })
        {
            Assert.Equal(
                normalizedFirst.At<byte>(y, x),
                normalizedSecond.At<byte>(y + 4, x + 5));
        }
    }

    [Fact]
    public void Normalize_RemovesStaticArrowFromOdometry()
    {
        // Стрелка игрока не двигается вместе с картой. Если её не замаскировать,
        // она тянет оценку сдвига к нулю и метка отстаёт.
        // Круг смещён от центра захвата: старое пятно в центре кадра стрелку
        // не накрывает, поэтому без привязки к кругу она остаётся в корреляции.
        var circle = new MinimapCircle(true, 150, 100, 90, 20);
        using var texture = Texture(240, 240, seed: 5);
        using var moved = Shift(texture, 5, 4);
        using var first = WithArrow(texture, 150, 100);
        using var second = WithArrow(moved, 150, 100);

        using var normalized = MinimapNormalizer.Normalize(first, circle);
        Assert.Equal(0, normalized.At<byte>(normalized.Height / 2, normalized.Width / 2));

        // С маской круга стрелка не меняет оценку вообще.
        var withArrow = MeasureDelta(first, second, circle, useCircle: true);
        var withoutArrow = MeasureDelta(texture, moved, circle, useCircle: true);
        Assert.Equal(withoutArrow.X, withArrow.X, 6);
        Assert.Equal(withoutArrow.Z, withArrow.Z, 6);

        // Без обрезки по кругу та же стрелка на оценку влияет.
        var plainWith = MeasureDelta(first, second, circle, useCircle: false);
        var plainWithout = MeasureDelta(texture, moved, circle, useCircle: false);
        Assert.True(
            Math.Abs(plainWith.X - plainWithout.X) > 1e-3,
            $"Стрелка не повлияла на оценку без маски круга: " +
            $"{plainWith.X:F4} и {plainWithout.X:F4}");
    }

    private static (double X, double Z) MeasureDelta(
        Mat first,
        Mat second,
        MinimapCircle circle,
        bool useCircle)
    {
        using var tracker = new OdometryTracker(pxPerMeter: 2.2);
        tracker.Snap(100, 100);

        using var normalizedFirst = useCircle
            ? MinimapNormalizer.Normalize(first, circle)
            : MinimapNormalizer.Normalize(first);
        using var normalizedSecond = useCircle
            ? MinimapNormalizer.Normalize(second, circle)
            : MinimapNormalizer.Normalize(second);

        tracker.Update(normalizedFirst, 0.1);
        var result = tracker.Update(normalizedSecond, 0.1);
        return (result.DeltaX, result.DeltaZ);
    }

    private static Mat Texture(int width, int height, int seed)
    {
        var random = new Random(seed);
        var bytes = new byte[width * height];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)random.Next(40, 230);
        }

        var mat = new Mat(height, width, MatType.CV_8UC1);
        Marshal.Copy(bytes, 0, mat.Data, bytes.Length);
        Cv2.GaussianBlur(mat, mat, new Size(3, 3), 0);
        return mat;
    }

    private static Mat WithArrow(Mat source, int x, int y)
    {
        var result = source.Clone();
        Cv2.Circle(result, new Point(x, y), 13, new Scalar(40), -1, LineTypes.AntiAlias);
        Cv2.Circle(result, new Point(x, y), 11, new Scalar(240), -1, LineTypes.AntiAlias);
        Cv2.Line(result, new Point(x - 8, y + 8), new Point(x, y - 9), new Scalar(20), 3);
        Cv2.Line(result, new Point(x + 8, y + 8), new Point(x, y - 9), new Scalar(20), 3);
        return result;
    }

    private static Mat Shift(Mat source, int dx, int dy)
    {
        using var matrix = Mat.FromArray(new double[,]
        {
            { 1, 0, dx },
            { 0, 1, dy }
        });

        var result = new Mat();
        Cv2.WarpAffine(
            source,
            result,
            matrix,
            source.Size(),
            InterpolationFlags.Linear,
            BorderTypes.Wrap);
        return result;
    }
}
