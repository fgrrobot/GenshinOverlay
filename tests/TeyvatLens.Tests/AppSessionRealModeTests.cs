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

    [Fact]
    public void SnapTo_TwiceWithoutTravelKeepsScale()
    {
        var database = new Database(Path.Combine(_directory, "snap2.db"));
        database.Initialize();
        database.SeedIfEmpty();

        var settings = new SessionSettings
        {
            Mode = SessionMode.Screen,
            RealAtlasPath = Path.Combine(_directory, "map.png"),
            RealPxPerMeter = 2.0,
            ScreenPxPerMeter = 1.8
        };

        using var session = new AppSession(
            database,
            Path.Combine(_directory, "demo.png"),
            settings);

        var jsonPath = Path.Combine(_directory, "points2.json");
        File.WriteAllText(
            jsonPath,
            """
            { "points": [
                { "name": "Телепорт: Монштадт", "x": 1000, "y": 400, "icon": "Teleport" },
                { "name": "Телепорт: Ли Юэ", "x": 1400, "y": 900, "icon": "Teleport" }
            ] }
            """);

        session.ImportRealMarkers(jsonPath);
        var waypoints = session.GetWaypoints();
        Assert.Equal(2, waypoints.Count);

        Assert.True(session.SnapTo(waypoints[0].Id));
        Assert.True(session.SnapTo(waypoints[1].Id));

        // Без реального перемещения калибровать нечего: масштаб не меняется.
        Assert.Equal(1.8, session.Settings.ScreenPxPerMeter, 6);
        var snapshot = session.CurrentSnapshot();
        Assert.Equal(waypoints[1].X, snapshot.Geo!.X, 3);
        Assert.Equal(waypoints[1].Z, snapshot.Geo.Z, 3);
    }

    [Fact]
    public void SnapToWorld_SetsAbsolutePositionAndAccuracy()
    {
        var database = new Database(Path.Combine(_directory, "world.db"));
        database.Initialize();
        database.SeedIfEmpty();

        var settings = new SessionSettings
        {
            Mode = SessionMode.Screen,
            RealAtlasPath = Path.Combine(_directory, "map.png"),
            RealPxPerMeter = 1.0
        };

        using var session = new AppSession(
            database,
            Path.Combine(_directory, "demo.png"),
            settings);

        Assert.True(session.SnapToWorld(1234, 5678, 3.5));

        var snapshot = session.CurrentSnapshot();
        Assert.NotNull(snapshot.Geo);
        Assert.Equal(1234, snapshot.Geo!.X, 3);
        Assert.Equal(5678, snapshot.Geo.Z, 3);
        Assert.Equal(3.5, snapshot.Geo.AccuracyM, 3);
        Assert.Equal("map", snapshot.Geo.Source);
        Assert.Equal(TrackingStatus.Tracking, snapshot.Status);
        Assert.False(snapshot.RequiresSnap);
    }

    [Fact]
    public void SnapToWorld_RejectsNonFiniteCoordinates()
    {
        var database = new Database(Path.Combine(_directory, "world-bad.db"));
        database.Initialize();
        database.SeedIfEmpty();

        using var session = new AppSession(
            database,
            Path.Combine(_directory, "demo.png"),
            new SessionSettings { Mode = SessionMode.Screen });

        Assert.False(session.SnapToWorld(double.NaN, 10, 2));
        Assert.Null(session.CurrentSnapshot().Geo);
    }

    [Fact]
    public void EstimateScreenPxPerMeter_UsesMinimapViewDiameter()
    {
        // 93 м — поперечник обзора миникарты, поэтому на него делится весь
        // размер квадрата захвата, а не половина.
        Assert.Equal(240 / 93.0, SessionSettings.EstimateScreenPxPerMeter(240), 6);
        Assert.Equal(300 / 93.0, SessionSettings.EstimateScreenPxPerMeter(300), 6);
        Assert.Equal(0.2, SessionSettings.EstimateScreenPxPerMeter(16), 6);
    }

    [Fact]
    public void Load_MigratesOldDefaultScaleOnce()
    {
        var database = new Database(Path.Combine(_directory, "settings.db"));
        database.Initialize();
        database.SetSetting("screen_rect", "10,20,240,240");
        database.SetSetting("screen_ppm", "2");
        database.SetSetting("real_default_v1", "1");

        var settings = SessionSettings.Load(database);
        Assert.Equal(SessionSettings.EstimateScreenPxPerMeter(240), settings.ScreenPxPerMeter, 6);

        // После миграции пользовательское значение больше не перезаписывается.
        settings.ScreenPxPerMeter = 1.5;
        settings.Save(database);
        var reloaded = SessionSettings.Load(database);
        Assert.Equal(1.5, reloaded.ScreenPxPerMeter, 6);
    }

    [Fact]
    public void Load_DoublesScaleStoredWithOldRadiusEstimate()
    {
        var database = new Database(Path.Combine(_directory, "settings-v3.db"));
        database.Initialize();
        database.SetSetting("screen_rect", "10,20,240,240");
        database.SetSetting("screen_ppm", SessionSettings.LegacyScreenPxPerMeter(240).ToString("0.####"));
        database.SetSetting("screen_ppm_v2", "1");
        database.SetSetting("real_default_v1", "1");

        var settings = SessionSettings.Load(database);

        // Старое значение считалось как 240 / 2 / 93; после миграции — 240 / 93.
        Assert.Equal(SessionSettings.EstimateScreenPxPerMeter(240), settings.ScreenPxPerMeter, 3);
        Assert.Equal(2.0 * SessionSettings.LegacyScreenPxPerMeter(240), settings.ScreenPxPerMeter, 3);

        // Новое значение сохранено сразу, поэтому повторная загрузка (даже
        // без Save) не откатывает масштаб обратно.
        var reloaded = SessionSettings.Load(database);
        Assert.Equal(2.58, reloaded.ScreenPxPerMeter, 2);
    }

    [Fact]
    public void Load_KeepsCustomScaleThatDiffersFromOldEstimate()
    {
        var database = new Database(Path.Combine(_directory, "settings-v3-custom.db"));
        database.Initialize();
        database.SetSetting("screen_rect", "10,20,240,240");
        database.SetSetting(
            "screen_ppm",
            3.7.ToString(System.Globalization.CultureInfo.CurrentCulture));
        database.SetSetting("screen_ppm_v2", "1");
        database.SetSetting("real_default_v1", "1");

        var settings = SessionSettings.Load(database);

        Assert.Equal(3.7, settings.ScreenPxPerMeter, 3);
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
