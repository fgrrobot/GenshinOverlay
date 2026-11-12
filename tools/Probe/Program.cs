using System.Diagnostics;
using Microsoft.Data.Sqlite;
using TeyvatLens.Core.App;
using TeyvatLens.Core.Data;
using TeyvatLens.Core.Models;

// Диагностический прогон полного конвейера без интерфейса.
// Запуск: dotnet run --project tools/Probe/Probe.csproj
// Не входит в решение: используется для замера времени кадра и дрейфа.

var directory = Path.Combine(Path.GetTempPath(), "teyvat-probe-run", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);

try
{
    var dbPath = Path.Combine(directory, "probe.db");
    var atlasPath = Path.Combine(directory, "atlas.png");

    var database = new Database(dbPath);
    database.Initialize();
    var seeded = database.SeedIfEmpty();

    var settings = SessionSettings.Load(database);

    // AppSession владеет базой и освобождает её в Dispose.
    using (var session = new AppSession(database, atlasPath, settings))
    {
        if (!session.StartOrResume(out var error))
        {
            Console.WriteLine($"start failed: {error}");
            return;
        }

        var dt = 1.0 / 15.0;
        var frameTimes = new List<double>();
        var last = session.CurrentSnapshot();

        for (var step = 0; step < 900; step++)
        {
            var stopwatch = Stopwatch.StartNew();
            last = session.Tick(dt);
            stopwatch.Stop();
            frameTimes.Add(stopwatch.Elapsed.TotalMilliseconds);

            if (step % 150 == 149)
            {
                Console.WriteLine(
                    $"step {step,3}: status={last.Status,-8} pos=({last.Geo?.X:F1},{last.Geo?.Z:F1}) " +
                    $"err={last.TruthErrorM:F2} m markers={last.Markers.Count} " +
                    $"hint={(last.Hint?.DirectionRu ?? "-")} frame={last.FrameMs:F2} ms");
            }
        }

        frameTimes.Sort();
        Console.WriteLine($"seeded={seeded} total={last.TotalMarkers} collected={last.CollectedMarkers}");
        Console.WriteLine(
            $"frame ms: avg={frameTimes.Average():F2} " +
            $"p50={frameTimes[frameTimes.Count / 2]:F2} " +
            $"p95={frameTimes[(int)(frameTimes.Count * 0.95)]:F2} " +
            $"max={frameTimes[^1]:F2}");

        // Привязка к телепорту и отметка «собрано».
        var waypoints = session.GetWaypoints();
        Console.WriteLine($"waypoints={waypoints.Count}");
        if (waypoints.Count > 0)
        {
            session.SnapTo(waypoints[1].Id);
            var snap = session.Tick(dt);
            Console.WriteLine(
                $"after snap: status={snap.Status} " +
                $"pos=({snap.Geo?.X:F1},{snap.Geo?.Z:F1}) err={snap.TruthErrorM:F2}");
        }

        var target = last.Markers.FirstOrDefault(
            item => item.Marker.Kind != MarkerKind.Waypoint);
        if (target is not null)
        {
            session.MarkCollected(target.Marker.Id);
            Console.WriteLine($"collected={session.CurrentSnapshot().CollectedMarkers}");
        }
    }
}
finally
{
    // Пул соединений держит файл БД открытым даже после Dispose.
    SqliteConnection.ClearAllPools();
    Directory.Delete(directory, recursive: true);
}
