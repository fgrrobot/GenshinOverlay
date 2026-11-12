using Microsoft.Data.Sqlite;
using TeyvatLens.Core.Models;
using TeyvatLens.Core.Simulation;

namespace TeyvatLens.Core.Data;

/// <summary>
/// Детерминированное наполнение демонстрационной базы. Данные синтетические:
/// это тестовый полигон, а не выгрузка из Genshin Impact.
/// </summary>
internal static class SeedData
{
    private static readonly IReadOnlyDictionary<MarkerKind, int> Counts =
        new Dictionary<MarkerKind, int>
        {
            [MarkerKind.Chest] = 40,
            [MarkerKind.Ore] = 18,
            [MarkerKind.Flower] = 24,
            [MarkerKind.Mushroom] = 14,
            [MarkerKind.Fish] = 10,
            [MarkerKind.Crystal] = 8
        };

    private static readonly IReadOnlyDictionary<MarkerKind, string[]> Names =
        new Dictionary<MarkerKind, string[]>
        {
            [MarkerKind.Chest] = new[]
            {
                "Сундук (обычный)",
                "Сундук (изысканный)",
                "Сундук (драгоценный)",
                "Сундук (роскошный)"
            },
            [MarkerKind.Ore] = new[]
            {
                "Железная руда",
                "Белая железная руда",
                "Кристаллический осколок"
            },
            [MarkerKind.Flower] = new[]
            {
                "Мята",
                "Сахарный цветок",
                "Цветок-падальщик",
                "Сесилия"
            },
            [MarkerKind.Mushroom] = new[]
            {
                "Гриб-светлячок",
                "Звёздный гриб",
                "Пион"
            },
            [MarkerKind.Fish] = new[]
            {
                "Меченосец",
                "Золотая рыбка",
                "Морской конёк"
            },
            [MarkerKind.Crystal] = new[]
            {
                "Кристалл-бабочка",
                "Осколок лазурита",
                "Аметист"
            }
        };

    public static int Seed(SqliteConnection connection, SqliteTransaction transaction)
    {
        InsertKinds(connection, transaction);

        var random = new Random(20261112);
        var added = 0;

        foreach (var (kind, count) in Counts)
        {
            for (var index = 0; index < count; index++)
            {
                var (x, z) = DemoRoute.SampleNearRoute(random);
                var name = Pick(random, Names[kind]);
                var y = random.NextDouble() < 0.6
                    ? Math.Round(-2 + random.NextDouble() * 11, 1)
                    : (double?)null;
                InsertMarker(
                    connection,
                    transaction,
                    kind,
                    name,
                    Math.Round(x, 1),
                    Math.Round(z, 1),
                    y);
                added++;
            }
        }

        // Плотные скопления ресурсов.
        foreach (var (centerX, centerZ) in DemoRoute.ClusterCenters())
        {
            var count = random.Next(3, 6);
            for (var index = 0; index < count; index++)
            {
                var kind = random.Next(3) switch
                {
                    0 => MarkerKind.Ore,
                    1 => MarkerKind.Flower,
                    _ => MarkerKind.Mushroom
                };
                var x = centerX + random.NextDouble() * 24 - 12;
                var z = centerZ + random.NextDouble() * 24 - 12;
                InsertMarker(
                    connection,
                    transaction,
                    kind,
                    Pick(random, Names[kind]),
                    Math.Round(x, 1),
                    Math.Round(z, 1),
                    Math.Round(-2 + random.NextDouble() * 11, 1));
                added++;
            }
        }

        for (var index = 0; index < DemoRoute.WaypointIndexes.Length; index++)
        {
            var (x, z) = DemoRoute.Points[DemoRoute.WaypointIndexes[index]];
            InsertMarker(
                connection,
                transaction,
                MarkerKind.Waypoint,
                DemoRoute.WaypointNames[index],
                x,
                z,
                0.0);
            added++;
        }

        return added;
    }

    private static void InsertKinds(SqliteConnection connection, SqliteTransaction transaction)
    {
        foreach (var info in MarkerKinds.All.Values)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT OR IGNORE INTO marker_kind (code, name_ru, color, priority, glyph)
                VALUES ($code, $name, $color, $priority, $glyph);
                """;
            command.Parameters.AddWithValue("$code", info.Code);
            command.Parameters.AddWithValue("$name", info.NameRu);
            command.Parameters.AddWithValue("$color", info.ColorHex);
            command.Parameters.AddWithValue("$priority", info.Priority);
            command.Parameters.AddWithValue("$glyph", info.Glyph);
            command.ExecuteNonQuery();
        }
    }

    private static void InsertMarker(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MarkerKind kind,
        string name,
        double x,
        double z,
        double? y)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO marker (kind_code, name, x, z, y, layer_id, region_code, source)
            VALUES ($kind, $name, $x, $z, $y, 0, 'demo', 'synthetic')
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$kind", MarkerKinds.ToCode(kind));
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$x", x);
        command.Parameters.AddWithValue("$z", z);
        command.Parameters.AddWithValue("$y", y is null ? DBNull.Value : y.Value);
        var id = Convert.ToInt64(command.ExecuteScalar());

        using var indexCommand = connection.CreateCommand();
        indexCommand.Transaction = transaction;
        indexCommand.CommandText =
            "INSERT INTO marker_rtree (id, min_x, max_x, min_z, max_z) VALUES ($id, $x, $x, $z, $z);";
        indexCommand.Parameters.AddWithValue("$id", id);
        indexCommand.Parameters.AddWithValue("$x", x);
        indexCommand.Parameters.AddWithValue("$z", z);
        indexCommand.ExecuteNonQuery();
    }

    private static string Pick(Random random, string[] values) =>
        values[random.Next(values.Length)];
}
