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
