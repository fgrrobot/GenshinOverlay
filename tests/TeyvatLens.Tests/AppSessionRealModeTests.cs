using TeyvatLens.Core.App;
using TeyvatLens.Core.Data;
using TeyvatLens.Core.Models;

namespace TeyvatLens.Tests;

public sealed class AppSessionRealModeTests : IDisposable
{
    private readonly string _directory;

    public AppSessionRealModeTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "teyvat-real", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void ImportRealMarkers_ReplacesRealSetAndKeepsDemo()
    {
        var database = new Database(Path.Combine(_directory, "real.db"));
        database.Initialize();
        database.SeedIfEmpty();

        var settings = new SessionSettings
        {
            Mode = SessionMode.Screen,
            RealAtlasPath = Path.Combine(_directory, "map.png"),
            RealPxPerMeter = 2.0
        };

        using var session = new AppSession(
            database,
            Path.Combine(_directory, "demo.png"),
            settings);

        var jsonPath = Path.Combine(_directory, "points.json");
        File.WriteAllText(
            jsonPath,
            """
            { "points": [
                { "name": "Телепорт: Монштадт", "x": 1000, "y": 400, "icon": "Teleport" },
                { "name": "Сундук", "x": 1100, "y": 500, "icon": "Chest" }
            ] }
            """);

        var result = session.ImportRealMarkers(jsonPath);

        Assert.Equal(2, result.Total);
        Assert.Equal(2, session.CountRealMarkers());
        var waypoint = Assert.Single(session.GetWaypoints());
        Assert.Equal("Телепорт: Монштадт", waypoint.Name);
        Assert.Equal(MarkerKind.Waypoint, waypoint.Kind);
        Assert.Equal(500.0, waypoint.X, 3);
        Assert.Equal(200.0, waypoint.Z, 3);
    }

    [Fact]
    public void SnapTo_ShowsPositionImmediatelyWithoutStart()
    {
        var database = new Database(Path.Combine(_directory, "snap.db"));
        database.Initialize();
        database.SeedIfEmpty();

        var settings = new SessionSettings
        {
            Mode = SessionMode.Screen,
            RealAtlasPath = Path.Combine(_directory, "map.png"),
            RealPxPerMeter = 2.0
        };

        using var session = new AppSession(
            database,
            Path.Combine(_directory, "demo.png"),
            settings);

        var jsonPath = Path.Combine(_directory, "points.json");
        File.WriteAllText(
            jsonPath,
            """
            { "points": [
                { "name": "Телепорт: Монштадт", "x": 1000, "y": 400, "icon": "Teleport" },
                { "name": "Сундук", "x": 1100, "y": 500, "icon": "Chest" }
            ] }
            """);

        session.ImportRealMarkers(jsonPath);
        var waypoint = Assert.Single(session.GetWaypoints());

        Assert.True(session.SnapTo(waypoint.Id));

        var snapshot = session.CurrentSnapshot();
        Assert.NotNull(snapshot.Geo);
        Assert.Equal(waypoint.X, snapshot.Geo!.X, 3);
        Assert.Equal(waypoint.Z, snapshot.Geo.Z, 3);
        Assert.Equal(TrackingStatus.Tracking, snapshot.Status);
        Assert.False(snapshot.RequiresSnap);
        Assert.Contains(snapshot.Markers, marker => marker.Marker.Name == "Сундук");
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
