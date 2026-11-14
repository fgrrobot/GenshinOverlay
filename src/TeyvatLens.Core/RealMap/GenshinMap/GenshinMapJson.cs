using System.Text.Json;

namespace TeyvatLens.Core.RealMap.GenshinMap;

/// <summary>
/// Разбор ответов API интерактивной карты Тейвата.
///
/// Выделен в отдельный класс без сетевых вызовов: формат ответов внешний и
/// может измениться, поэтому его разбор покрыт модульными тестами.
/// </summary>
public static class GenshinMapJson
{
    public static GenshinMapInfo ParseInfo(string json)
    {
        using var document = JsonDocument.Parse(json);
        var info = document.RootElement
            .GetProperty("data")
            .GetProperty("info");

        var mapId = info.GetProperty("id").GetInt32();
        var name = ReadString(info, "name") ?? string.Empty;

        // Новый формат отдаёт detail_v2 объектом, старый — строкой detail.
        if (info.TryGetProperty("detail_v2", out var detailV2)
            && detailV2.ValueKind == JsonValueKind.Object)
        {
            return ReadInfo(mapId, name, detailV2);
        }

        if (info.TryGetProperty("detail", out var detail)
            && detail.ValueKind == JsonValueKind.String)
        {
            using var parsed = JsonDocument.Parse(detail.GetString() ?? "{}");
            return ReadInfo(mapId, name, parsed.RootElement);
        }

        throw new InvalidOperationException("В ответе карты нет метаданных detail/detail_v2.");
    }

    public static IReadOnlyDictionary<int, GenshinLabel> ParseLabelTree(string json)
    {
        using var document = JsonDocument.Parse(json);
        var tree = document.RootElement
            .GetProperty("data")
            .GetProperty("tree");

        var labels = new Dictionary<int, GenshinLabel>();
        foreach (var category in tree.EnumerateArray())
        {
            var categoryId = category.GetProperty("id").GetInt32();
            AddLabel(labels, category, parentId: 0, categoryId);

            if (category.TryGetProperty("children", out var children)
                && children.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in children.EnumerateArray())
                {
                    AddLabel(labels, child, categoryId, categoryId);
                }
            }
        }

        return labels;
    }

    public static IReadOnlyList<GenshinAnchor> ParseAnchors(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement.GetProperty("data");

        var anchors = new List<GenshinAnchor>();
        if (root.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                AddAnchor(anchors, item);
            }
        }

        return anchors;
    }

    public static IReadOnlyList<GenshinPoint> ParsePoints(string json)
    {
        using var document = JsonDocument.Parse(json);
        var list = document.RootElement
            .GetProperty("data")
            .GetProperty("point_list");

        var points = new List<GenshinPoint>(list.GetArrayLength());
        foreach (var item in list.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idValue) ? idValue.GetInt64() : 0;
            var labelId = item.TryGetProperty("label_id", out var labelValue)
                ? labelValue.GetInt32()
                : 0;
            var x = ReadDouble(item, "x_pos");
            var y = ReadDouble(item, "y_pos");
            if (x is null || y is null)
            {
                continue;
            }

            var displayState = item.TryGetProperty("display_state", out var state)
                ? state.GetInt32()
                : 1;

            points.Add(new GenshinPoint(id, labelId, x.Value, y.Value, displayState));
        }

        return points;
    }

    private static GenshinMapInfo ReadInfo(int mapId, string name, JsonElement detail)
    {
        var total = ReadPair(detail, "total_size");
        var origin = ReadPair(detail, "origin");
        var version = ReadString(detail, "map_version") ?? string.Empty;

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException(
                "Ответ карты не содержит map_version — тайлы недоступны.");
        }

        var minZoom = detail.TryGetProperty("min_zoom", out var min) ? min.GetInt32() : -4;
        var maxZoom = detail.TryGetProperty("max_zoom", out var max) ? max.GetInt32() : 0;

        return new GenshinMapInfo(
            mapId,
            name,
            version,
            origin.X,
            origin.Y,
            (int)Math.Round(total.X),
            (int)Math.Round(total.Y),
            minZoom,
            maxZoom);
    }

    private static void AddLabel(
        IDictionary<int, GenshinLabel> labels,
        JsonElement element,
        int parentId,
        int categoryId)
    {
        if (!element.TryGetProperty("id", out var idValue))
        {
            return;
        }

        var id = idValue.GetInt32();
        labels[id] = new GenshinLabel(
            id,
            ReadString(element, "name") ?? $"Тип {id}",
            parentId,
            categoryId);
    }

    private static void AddAnchor(ICollection<GenshinAnchor> anchors, JsonElement element)
    {
        var name = ReadString(element, "name");
        var left = ReadDouble(element, "l_x");
        var top = ReadDouble(element, "l_y");
        var right = ReadDouble(element, "r_x");
        var bottom = ReadDouble(element, "r_y");

        if (!string.IsNullOrWhiteSpace(name)
            && left is not null && top is not null
            && right is not null && bottom is not null)
        {
            anchors.Add(new GenshinAnchor(name, left.Value, top.Value, right.Value, bottom.Value));
        }

        if (element.TryGetProperty("children", out var children)
            && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                AddAnchor(anchors, child);
            }
        }
    }

    private static (double X, double Y) ReadPair(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var pair)
            && pair.ValueKind == JsonValueKind.Array
            && pair.GetArrayLength() >= 2)
        {
            return (pair[0].GetDouble(), pair[1].GetDouble());
        }

        throw new InvalidOperationException($"В метаданных карты нет поля {name}.");
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        return null;
    }

    private static double? ReadDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;
    }
}
