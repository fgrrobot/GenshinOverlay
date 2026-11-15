using TeyvatLens.Core.Geometry;
using TeyvatLens.Core.Localization;
using TeyvatLens.Core.Models;
using TeyvatLens.Core.Simulation;

namespace TeyvatLens.Tests;

public sealed class DemoIntegrationTests : IDisposable
{
    private readonly string _directory;
    private readonly string _atlasPath;

    public DemoIntegrationTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "teyvat-lens-demo", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _atlasPath = Path.Combine(_directory, "atlas.png");
    }

    [Fact]
    public void Tracker_FollowsDemoWorld()
    {
        using var world = new DemoWorld(_atlasPath);
        using var tracker = new OdometryTracker(world.PxPerMeter);

        tracker.Snap(world.TruthX, world.TruthZ, world.TruthHeadingDeg);

        var dt = 1.0 / 15.0;
        var lastStatus = TrackingStatus.Lost;
        for (var step = 0; step < 120; step++)
        {
            world.Advance(dt);
            tracker.SetHeading(world.TruthHeadingDeg);
            using var frame = world.RenderMinimap();
            var result = tracker.Update(frame, dt);
            lastStatus = result.Status;
        }

        Assert.True(lastStatus is TrackingStatus.Tracking or TrackingStatus.Degraded);
        Assert.NotNull(tracker.X);
        Assert.NotNull(tracker.Z);
        var error = GeoMath.Distance(
            tracker.X!.Value,
            tracker.Z!.Value,
            world.TruthX,
            world.TruthZ);
        Assert.True(error < 5.0, $"Ошибка позиции {error:F2} м превышает 5 м");
    }

    [Fact]
    public void LostMinimap_TransitionsToLostAndStopsShowingPosition()
    {
        using var world = new DemoWorld(_atlasPath);
        using var tracker = new OdometryTracker(world.PxPerMeter);
        tracker.Snap(world.TruthX, world.TruthZ, world.TruthHeadingDeg);

        world.Lost = true;
        var dt = 0.1;
        for (var step = 0; step < 30; step++)
        {
            using var frame = world.RenderMinimap();
            tracker.Update(frame, dt);
        }

        Assert.Equal(TrackingStatus.Lost, tracker.Status);
        Assert.True(tracker.RequiresSnap);
    }

    [Fact]
    public void Tracker_DoesNotDriftOnLongWalk()
    {
        // Долгая прогулка (~160 м) по демо-маршруту: раньше субпиксельная
        // ошибка копилась и карта «уезжала» на десятки метров.
        using var world = new DemoWorld(_atlasPath);
        using var tracker = new OdometryTracker(world.PxPerMeter);
        tracker.Snap(world.TruthX, world.TruthZ, world.TruthHeadingDeg);

        var dt = 1.0 / 15.0;
        for (var step = 0; step < 300; step++)
        {
            world.Advance(dt);
            tracker.SetHeading(world.TruthHeadingDeg);
            using var frame = world.RenderMinimap();
            tracker.Update(frame, dt);
        }

        Assert.NotNull(tracker.X);
        Assert.NotNull(tracker.Z);
        var error = GeoMath.Distance(
            tracker.X!.Value,
            tracker.Z!.Value,
            world.TruthX,
            world.TruthZ);
        Assert.True(error < 3.5, $"Накопленная ошибка {error:F2} м превышает 3.5 м");
    }

    [Fact]
    public void AutoHeading_FollowsMovementDirection()
    {
        using var world = new DemoWorld(_atlasPath);
        using var tracker = new OdometryTracker(world.PxPerMeter) { AutoHeading = true };
        tracker.Snap(world.TruthX, world.TruthZ);

        var dt = 1.0 / 15.0;
        for (var step = 0; step < 90; step++)
        {
            world.Advance(dt);
            using var frame = world.RenderMinimap();
            tracker.Update(frame, dt);
        }

        var difference = Math.Abs(GeoMath.AngleDelta(world.TruthHeadingDeg, tracker.HeadingDeg));
        Assert.True(difference < 25.0, $"Курс {tracker.HeadingDeg:F1}° отличается от истинного {world.TruthHeadingDeg:F1}° на {difference:F1}°");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
