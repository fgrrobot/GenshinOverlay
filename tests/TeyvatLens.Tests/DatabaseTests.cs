using TeyvatLens.Core.Data;
using TeyvatLens.Core.Models;
using TeyvatLens.Core.RealMap;

namespace TeyvatLens.Tests;

public sealed class DatabaseTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public DatabaseTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "teyvat-lens-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "test.db");
    }

    [Fact]
    public void Seed_IsIdempotentAndCreatesMarkers()
    {
        using var database = new Database(_path);
        database.Initialize();

        var first = database.SeedIfEmpty();
        var second = database.SeedIfEmpty();

        Assert.True(first > 100);
        Assert.Equal(0, second);
        Assert.Equal(first, database.CountMarkers());
    }

    [Fact]
    public void QueryNearby_ReturnsMarkersSortedByDistance()
    {
        using var database = new Database(_path);
        database.Initialize();
        database.SeedIfEmpty();

        var result = database.QueryNearby(380, 430, 200);

        Assert.NotEmpty(result);
        Assert.True(result.Count <= 200);
        Assert.True(result[0].DistanceM <= result[^1].DistanceM);
        Assert.All(result, item => Assert.True(item.DistanceM <= 200));
    }

    [Fact]
    public void QueryNearby_AppliesKindFilter()
    {
        using var database = new Database(_path);
        database.Initialize();
        database.SeedIfEmpty();

        var result = database.QueryNearby(450, 470, 250, kindFilter: MarkerKind.Chest);

        Assert.NotEmpty(result);
        Assert.All(result, item => Assert.Equal(MarkerKind.Chest, item.Marker.Kind));
    }

    [Fact]
    public void CollectedState_SurvivesReopen()
    {
        long markerId;
        using (var database = new Database(_path))
        {
            database.Initialize();
            database.SeedIfEmpty();
            markerId = database.QueryNearby(450, 470, 250)[0].Marker.Id;
            database.MarkCollected(markerId);
            Assert.Equal(1, database.CountCollected());
        }

        using (var reopened = new Database(_path))
        {
            reopened.Initialize();
            var marker = reopened.GetMarker(markerId);
            Assert.NotNull(marker);
            Assert.True(marker!.Collected);
            Assert.Equal(1, reopened.CountCollected());
        }
    }

    [Fact]
    public void Settings_ArePersisted()
    {
        using (var database = new Database(_path))
        {
            database.Initialize();
            database.SetSetting("radius_m", "77");
            database.SetSetting("mode", "screen");
        }

        using (var reopened = new Database(_path))
        {
            reopened.Initialize();
            Assert.Equal("77", reopened.GetSetting("radius_m"));
            Assert.Equal("screen", reopened.GetSetting("mode"));
        }
    }

    [Fact]
    public void ResetProgress_ClearsCollectedState()
    {
        using var database = new Database(_path);
        database.Initialize();
        database.SeedIfEmpty();
        var id = database.QueryNearby(450, 470, 250)[0].Marker.Id;
        database.MarkCollected(id);

        database.ResetProgress();

        Assert.Equal(0, database.CountCollected());
    }

    [Fact]
    public void ReplaceMarkers_SeparatesRealAndSynthetic()
    {
        using var database = new Database(_path);
        database.Initialize();
        database.SeedIfEmpty();

        var syntheticCount = database.CountMarkers(MarkerSources.Synthetic);
        var imported = new[]
        {
            new ImportedMarker("Телепорт: Монштадт", MarkerKind.Waypoint, 500, 500),
            new ImportedMarker("Сундук у моста", MarkerKind.Chest, 505, 500)
        };

        database.ReplaceMarkers(MarkerSources.Real, imported);

        Assert.Equal(2, database.CountMarkers(MarkerSources.Real));
        Assert.Equal(syntheticCount, database.CountMarkers(MarkerSources.Synthetic));
        Assert.Equal(2, database.QueryNearby(500, 500, 50, source: MarkerSources.Real).Count);
        Assert.Single(database.GetWaypoints(MarkerSources.Real));
        Assert.Empty(database.QueryNearby(0, 0, 50, source: MarkerSources.Synthetic));

        database.ReplaceMarkers(MarkerSources.Real, new[] { imported[0] });

        Assert.Equal(1, database.CountMarkers(MarkerSources.Real));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // SQLite может держать WAL-файл; для теста это не критично.
        }
    }
}
