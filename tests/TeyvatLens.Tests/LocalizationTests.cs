using System.Runtime.InteropServices;
using OpenCvSharp;
using TeyvatLens.Core.Localization;
using TeyvatLens.Core.Models;

namespace TeyvatLens.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void Update_EstimatesTranslationFromPhaseCorrelation()
    {
        using var tracker = new OdometryTracker(pxPerMeter: 2.0);
        tracker.Snap(100, 100);

        using var first = Pattern();
        tracker.Update(first, 1.0 / 15);

        using var second = Shift(first, 6, 4);
        var result = tracker.Update(second, 1.0 / 15);

        Assert.True(result.Status is TrackingStatus.Tracking or TrackingStatus.Degraded);
        Assert.NotNull(result.X);
        Assert.NotNull(result.Z);
        Assert.InRange(result.X!.Value, 96.0, 98.0);
        Assert.InRange(result.Z!.Value, 97.0, 99.0);
    }

    [Fact]
    public void FlatFrames_DegradeToLostAndRequireSnap()
    {
        using var tracker = new OdometryTracker(pxPerMeter: 2.0);
        tracker.Snap(100, 100);

        using var flat = new Mat(256, 256, MatType.CV_8UC1, new Scalar(8));
        for (var index = 0; index < 40; index++)
        {
            tracker.Update(flat, 0.1);
        }

        Assert.Equal(TrackingStatus.Lost, tracker.Status);
        Assert.True(tracker.RequiresSnap);
    }

    [Fact]
    public void LargeJump_IsTreatedAsTeleport()
    {
        using var tracker = new OdometryTracker(pxPerMeter: 2.0, maxStepMeters: 10.0);
        tracker.Snap(100, 100);

        using var first = Pattern();
        tracker.Update(first, 1.0 / 15);

        // 40 px при 2 px/м = 20 м, больше тестового порога 10 м.
        using var second = Shift(first, 40, 0);
        var result = tracker.Update(second, 1.0 / 15);

        Assert.Equal(TrackingStatus.Lost, result.Status);
        Assert.True(result.RequiresSnap);
    }

    [Fact]
    public void StationaryJitter_DoesNotDriftPosition()
    {
        // Игрок стоит, но субпиксельная оценка каждый кадр даёт ~0,5 px.
        // Без ZUPT это 0,025 м за кадр — за 300 кадров карта уползла бы на 7,5 м.
        using var tracker = new OdometryTracker(pxPerMeter: 20.0);
        tracker.Snap(1000, 1000);

        using var first = Pattern();
        tracker.Update(first, 1.0 / 15);

        var dt = 1.0 / 15;
        for (var step = 1; step <= 300; step++)
        {
            using var frame = Shift(first, 0.5 * step, 0);
            tracker.Update(frame, dt);
        }

        Assert.NotNull(tracker.X);
        Assert.InRange(tracker.X!.Value, 999.8, 1000.2);
        Assert.InRange(tracker.TravelSinceSnapM, 0.0, 1.0);
    }

    [Fact]
    public void MovementResumesAfterStandingStill()
    {
        using var tracker = new OdometryTracker(pxPerMeter: 20.0);
        tracker.Snap(1000, 1000);

        using var first = Pattern();
        tracker.Update(first, 1.0 / 15);

        var dt = 1.0 / 15;
        for (var step = 1; step <= 60; step++)
        {
            using var frame = Shift(first, 0.5 * step, 0);
            tracker.Update(frame, dt);
        }

        // Резкое, но правдоподобное движение: 20 px при 20 px/м = 1 м.
        using var moved = Shift(first, 50, 0);
        tracker.Update(moved, dt);

        Assert.NotNull(tracker.X);
        Assert.InRange(tracker.X!.Value, 998.5, 999.5);
    }

    [Fact]
    public void SingleImplausibleJump_IsSkippedAndTrackingContinues()
    {
        using var tracker = new OdometryTracker(pxPerMeter: 2.0);
        tracker.Snap(100, 100);

        using var first = Pattern();
        tracker.Update(first, 1.0 / 15);

        // 30 px при 2 px/м = 15 м за кадр — это 225 м/с, такого не бывает.
        using var jump = Shift(first, 30, 0);
        var rejected = tracker.Update(jump, 1.0 / 15);

        Assert.NotEqual(TrackingStatus.Lost, rejected.Status);
        Assert.InRange(rejected.X!.Value, 99.9, 100.1);

        // Следующий нормальный кадр сравнивается с сохранённым опорным.
        using var normal = Shift(first, 1, 0);
        var result = tracker.Update(normal, 1.0 / 15);

        Assert.True(result.Status is TrackingStatus.Tracking or TrackingStatus.Degraded);
        Assert.InRange(result.X!.Value, 99.4, 99.6);
    }

    [Fact]
    public void RepeatedImplausibleJumps_RequireSnap()
    {
        using var tracker = new OdometryTracker(pxPerMeter: 2.0);
        tracker.Snap(100, 100);

        using var first = Pattern();
        tracker.Update(first, 1.0 / 15);

        using var jump = Shift(first, 30, 0);
        tracker.Update(jump, 1.0 / 15);
        tracker.Update(jump, 1.0 / 15);
        var result = tracker.Update(jump, 1.0 / 15);

        Assert.Equal(TrackingStatus.Lost, result.Status);
        Assert.True(result.RequiresSnap);
    }

    [Fact]
    public void Accuracy_GrowsWithTravel()
    {
        using var tracker = new OdometryTracker(pxPerMeter: 2.0);
        tracker.Snap(100, 100);
        var baseAccuracy = tracker.AccuracyM;

        using var first = Pattern();
        tracker.Update(first, 1.0 / 15);

        var dt = 1.0 / 15;
        for (var step = 1; step <= 100; step++)
        {
            // 2 px при 2 px/м = 1 м за кадр.
            using var frame = Shift(first, 2.0 * step, 0);
            tracker.Update(frame, dt);
        }

        Assert.InRange(tracker.TravelSinceSnapM, 90.0, 110.0);
        Assert.True(
            tracker.AccuracyM > baseAccuracy + 0.5,
            $"Оценка точности не выросла: {tracker.AccuracyM:F2} м");
        Assert.True(tracker.AccuracyM < 10.0, $"Слишком пессимистичная точность: {tracker.AccuracyM:F2} м");
    }

    private static Mat Pattern()
    {
        var random = new Random(42);
        var bytes = new byte[256 * 256];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)random.Next(256);
        }

        var mat = new Mat(256, 256, MatType.CV_8UC1);
        Marshal.Copy(bytes, 0, mat.Data, bytes.Length);
        Cv2.GaussianBlur(mat, mat, new Size(3, 3), 0);
        Cv2.Circle(mat, new Point(70, 80), 26, Scalar.White, -1, LineTypes.AntiAlias);
        Cv2.Circle(mat, new Point(190, 150), 34, Scalar.Black, -1, LineTypes.AntiAlias);
        Cv2.Line(mat, new Point(10, 220), new Point(240, 30), Scalar.White, 5);
        return mat;
    }

    private static Mat Shift(Mat source, double dx, double dy)
    {
        var matrix = Mat.FromArray(new double[,]
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
        matrix.Dispose();
        return result;
    }
}
