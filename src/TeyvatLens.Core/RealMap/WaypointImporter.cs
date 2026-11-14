using System.Globalization;
using System.Text.Json;
using TeyvatLens.Core.Models;

namespace TeyvatLens.Core.RealMap;

/// <summary>Точка, готовая к записи в базу.</summary>
public sealed record ImportedMarker(
    string Name,
    MarkerKind Kind,
    double X,
    double Z,
    double? Y = null,
    string? Region = null);

/// <summary>Результат импорта: точки и предупреждения о пропущенных записях.</summary>
public sealed record ImportResult(
    IReadOnlyList<ImportedMarker> Markers,
    IReadOnlyList<string> Warnings)
{
    public int Total => Markers.Count;
    public int Skipped => Warnings.Count;
}

/// <summary>
/// Импорт точек интерактивной карты из JSON.
///
/// Поддерживаются оба распространённых формата:
///   [ { "name": "...", "x": 123, "y": 456, "icon": "Teleport" } ]
///   { "units": "pixels", "point_list": [ ... ] }
/// Координаты по умолчанию — пиксели атласа. Если указано "units": "meters",
/// значения считаются мировыми координатами в метрах.
/// </summary>
public static class WaypointImporter
{
    public static ImportResult Parse(string json, MapProfile profile)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ImportResult(Array.Empty<ImportedMarker>(), new[] { "Файл пуст." });
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var points = FindPoints(root);

        var markers = new List<ImportedMarker>();
        var warnings = new List<string>();
        var globalUnits = root.ValueKind == JsonValueKind.Object
            ? ReadString(root, "units") ?? "pixels"
            : "pixels";
        var index = 0;

        foreach (var point in points)
        {
            index++;
            if (point.ValueKind != JsonValueKind.Object)
            {
                warnings.Add($"Запись {index}: ожидался объект.");
                continue;
            }

            var name = ReadString(point, "name")
                       ?? ReadString(point, "label")
                       ?? ReadString(point, "title")
                       ?? $"Точка {index}";

            var rawX = ReadDouble(point, "x", "px", "mapX");
            var rawZ = ReadDouble(point, "y", "pz", "mapY", "z");
            if (rawX is null || rawZ is null)
            {
                warnings.Add($"{name}: нет координат x/y.");
                continue;
            }

            var kindText = ReadString(point, "kind")
                           ?? ReadString(point, "type")
                           ?? ReadString(point, "icon")
                           ?? ReadString(point, "marker_type");
            var kind = DetectKind(kindText);
            if (kind is null)
            {
                warnings.Add($"{name}: неизвестный тип «{kindText}».");
                continue;
            }

            var units = (ReadString(point, "units") ?? globalUnits).ToLowerInvariant();
            var meters = units is "meters" or "meter" or "m" or "метры";
            var x = meters ? profile.OriginXMeters + rawX.Value : profile.ToWorldX(rawX.Value);
            var z = meters ? profile.OriginZMeters + rawZ.Value : profile.ToWorldZ(rawZ.Value);

            markers.Add(new ImportedMarker(
                name.Trim(),
                kind.Value,
                x,
                z,
                ReadDouble(point, "height", "altitude", "h"),
                ReadString(point, "region", "region_name")));
        }

        return new ImportResult(markers, warnings);
    }

    public static ImportResult LoadFile(string path, MapProfile profile) =>
        Parse(File.ReadAllText(path), profile);

    private static IEnumerable<JsonElement> FindPoints(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<JsonElement>();
        }

        foreach (var key in new[] { "points", "point_list", "markers", "items" })
        {
            if (root.TryGetProperty(key, out var array) && array.ValueKind == JsonValueKind.Array)
            {
                return array.EnumerateArray();
            }
        }

        if (root.TryGetProperty("data", out var data))
        {
            return FindPoints(data);
        }

        return Array.Empty<JsonElement>();
    }

    private static MarkerKind? DetectKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return MarkerKind.Chest;
        }

        var text = value.ToLowerInvariant();

        if (Contains(text, "teleport", "waypoint", "телепорт", "statue", "стату", "anchor", "ядро"))
        {
            return MarkerKind.Waypoint;
        }

        if (Contains(text, "chest", "сундук"))
        {
            return MarkerKind.Chest;
        }

        if (Contains(text, "ore", "руда", "mining", "желез"))
        {
            return MarkerKind.Ore;
        }

        if (Contains(text, "flower", "цвет", "plant", "растен"))
        {
            return MarkerKind.Flower;
        }

        if (Contains(text, "mushroom", "гриб"))
        {
            return MarkerKind.Mushroom;
        }

        if (Contains(text, "fish", "рыб"))
        {
            return MarkerKind.Fish;
        }

        if (Contains(text, "crystal", "кристалл", "gem", "самоцвет"))
        {
            return MarkerKind.Crystal;
        }

        return null;
    }

    private static bool Contains(string text, params string[] parts) =>
        parts.Any(part => text.Contains(part, StringComparison.Ordinal));

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }

            if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                return value.ToString();
            }
        }

        return null;
    }

    private static double? ReadDouble(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String
                && double.TryParse(
                    value.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out number))
            {
                return number;
            }
        }

        return null;
    }
}
