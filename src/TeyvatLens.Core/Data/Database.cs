using Microsoft.Data.Sqlite;
using TeyvatLens.Core.Geometry;
using TeyvatLens.Core.Models;

namespace TeyvatLens.Core.Data;

/// <summary>
/// Локальное хранилище на SQLite.
///
/// Выбор обоснован задачами проекта: нужны пространственные запросы
/// (R*Tree), изменяемое состояние «собрано» и сохранение настроек.
/// JSON не даёт индексов и транзакций, CSV неудобен для связей.
/// </summary>
public sealed class Database : IDisposable
{
    private readonly SqliteConnection _connection;

    public Database(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection($"Data Source={path}");
        _connection.Open();
    }

    public string? DatabasePath => _connection.DataSource;

    public void Initialize()
    {
        Execute("PRAGMA journal_mode = WAL;");
        Execute("PRAGMA synchronous = NORMAL;");
        Execute("PRAGMA foreign_keys = ON;");

        Execute(
            """
            CREATE TABLE IF NOT EXISTS marker_kind (
                code     TEXT PRIMARY KEY,
                name_ru  TEXT NOT NULL,
                color    TEXT NOT NULL,
                priority REAL NOT NULL DEFAULT 0.5,
                glyph    TEXT NOT NULL DEFAULT '•'
            );

            CREATE TABLE IF NOT EXISTS marker (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                kind_code   TEXT NOT NULL REFERENCES marker_kind(code),
                name        TEXT NOT NULL,
                x           REAL NOT NULL,
                z           REAL NOT NULL,
                y           REAL,
                layer_id    INTEGER NOT NULL DEFAULT 0,
                region_code TEXT,
                source      TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_marker_kind  ON marker(kind_code);
            CREATE INDEX IF NOT EXISTS idx_marker_layer ON marker(layer_id);

            CREATE VIRTUAL TABLE IF NOT EXISTS marker_rtree USING rtree(
                id, min_x, max_x, min_z, max_z
            );

            CREATE TABLE IF NOT EXISTS marker_state (
                marker_id    INTEGER PRIMARY KEY REFERENCES marker(id) ON DELETE CASCADE,
                state        TEXT NOT NULL CHECK(state IN ('collected', 'ignored')),
                collected_at TEXT,
                respawn_at   TEXT
            );

            CREATE TABLE IF NOT EXISTS setting (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);

        RebuildRTreeIfNeeded();
    }

    public int SeedIfEmpty()
    {
        var existing = ScalarLong("SELECT COUNT(*) FROM marker;");
        if (existing > 0)
        {
            return 0;
        }

        using var transaction = _connection.BeginTransaction();
        var added = SeedData.Seed(_connection, transaction);
        transaction.Commit();
        return added;
    }

    public long CountMarkers() => ScalarLong("SELECT COUNT(*) FROM marker;");

    public long CountCollected() =>
        ScalarLong("SELECT COUNT(*) FROM marker_state WHERE state = 'collected';");

    public Marker? GetMarker(long id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT m.id, m.kind_code, m.name, m.x, m.z, m.y, m.layer_id, m.region_code,
                   COALESCE(s.state, 'unknown'), s.respawn_at
            FROM marker m
            LEFT JOIN marker_state s ON s.marker_id = m.id
            WHERE m.id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadMarker(reader) : null;
    }

    public IReadOnlyList<Marker> GetWaypoints()
    {
        var result = new List<Marker>();
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT m.id, m.kind_code, m.name, m.x, m.z, m.y, m.layer_id, m.region_code,
                   COALESCE(s.state, 'unknown'), s.respawn_at
            FROM marker m
            LEFT JOIN marker_state s ON s.marker_id = m.id
            WHERE m.kind_code = 'waypoint'
            ORDER BY m.name;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadMarker(reader));
        }

        return result;
    }

    /// <summary>Маркеры в квадрате радиуса вокруг точки, отсортированные по расстоянию.</summary>
    public IReadOnlyList<NearbyMarker> QueryNearby(
        double x,
        double z,
        double radiusM,
        int layerId = 0,
        MarkerKind? kindFilter = null,
        int limit = 200)
    {
        if (radiusM <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radiusM));
        }

        var result = new List<NearbyMarker>();
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT m.id, m.kind_code, m.name, m.x, m.z, m.y, m.layer_id, m.region_code,
                   COALESCE(s.state, 'unknown'), s.respawn_at,
                   ((m.x - $x) * (m.x - $x) + (m.z - $z) * (m.z - $z)) AS d2
            FROM marker_rtree r
            JOIN marker m ON m.id = r.id
            LEFT JOIN marker_state s ON s.marker_id = m.id
            WHERE r.max_x >= $minX AND r.min_x <= $maxX
              AND r.max_z >= $minZ AND r.min_z <= $maxZ
              AND m.layer_id = $layer
              AND ($kind = 'all' OR m.kind_code = $kind)
            ORDER BY d2
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$x", x);
        command.Parameters.AddWithValue("$z", z);
        command.Parameters.AddWithValue("$minX", x - radiusM);
        command.Parameters.AddWithValue("$maxX", x + radiusM);
        command.Parameters.AddWithValue("$minZ", z - radiusM);
        command.Parameters.AddWithValue("$maxZ", z + radiusM);
        command.Parameters.AddWithValue("$layer", layerId);
        command.Parameters.AddWithValue("$kind", kindFilter is null ? "all" : MarkerKinds.ToCode(kindFilter.Value));
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var marker = ReadMarker(reader);
            var distance = GeoMath.Distance(x, z, marker.X, marker.Z);
            if (distance > radiusM)
            {
                continue;
            }

            result.Add(new NearbyMarker(
                marker,
                distance,
                GeoMath.BearingDeg(x, z, marker.X, marker.Z)));
        }

        return result;
    }

    public void MarkCollected(long markerId, bool collected = true)
    {
        using var command = _connection.CreateCommand();
        if (collected)
        {
            command.CommandText =
                """
                INSERT INTO marker_state (marker_id, state, collected_at)
                VALUES ($id, 'collected', $at)
                ON CONFLICT(marker_id) DO UPDATE SET
                    state = 'collected',
                    collected_at = $at,
                    respawn_at = NULL;
                """;
            command.Parameters.AddWithValue("$id", markerId);
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        }
        else
        {
            command.CommandText = "DELETE FROM marker_state WHERE marker_id = $id;";
            command.Parameters.AddWithValue("$id", markerId);
        }

        command.ExecuteNonQuery();
    }

    public void ResetProgress() => Execute("DELETE FROM marker_state;");

    public string? GetSetting(string key)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT value FROM setting WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO setting (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = $value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static Marker ReadMarker(SqliteDataReader reader)
    {
        var kind = MarkerKinds.FromCode(reader.GetString(1));
        var y = reader.IsDBNull(5) ? (double?)null : reader.GetDouble(5);
        var region = reader.IsDBNull(7) ? null : reader.GetString(7);
        var state = reader.GetString(8);
        var respawn = reader.IsDBNull(9)
            ? (DateTimeOffset?)null
            : DateTimeOffset.Parse(reader.GetString(9));

        return new Marker(
            reader.GetInt64(0),
            kind,
            reader.GetString(2),
            reader.GetDouble(3),
            reader.GetDouble(4),
            y,
            reader.GetInt32(6),
            region,
            string.Equals(state, "collected", StringComparison.Ordinal),
            respawn);
    }

    private void RebuildRTreeIfNeeded()
    {
        var markers = ScalarLong("SELECT COUNT(*) FROM marker;");
        var indexed = ScalarLong("SELECT COUNT(*) FROM marker_rtree;");
        if (markers == indexed)
        {
            return;
        }

        Execute("DELETE FROM marker_rtree;");
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, x, z FROM marker;";
        using var reader = command.ExecuteReader();
        var rows = new List<(long Id, double X, double Z)>();
        while (reader.Read())
        {
            rows.Add((reader.GetInt64(0), reader.GetDouble(1), reader.GetDouble(2)));
        }

        reader.Close();

        using var transaction = _connection.BeginTransaction();
        foreach (var row in rows)
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO marker_rtree (id, min_x, max_x, min_z, max_z) VALUES ($id, $x, $x, $z, $z);";
            insert.Parameters.AddWithValue("$id", row.Id);
            insert.Parameters.AddWithValue("$x", row.X);
            insert.Parameters.AddWithValue("$z", row.Z);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private long ScalarLong(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
