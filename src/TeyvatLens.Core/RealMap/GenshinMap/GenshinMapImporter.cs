using TeyvatLens.Core.Models;

namespace TeyvatLens.Core.RealMap.GenshinMap;

/// <summary>
/// Превращает точки интерактивной карты в маркеры базы.
///
/// Категории карты сопоставляются типам прототипа приблизительно: у HoYoLAB
/// несколько десятков категорий, а в MVP семь типов. Неизвестные категории
/// пропускаются, чтобы не засорять базу.
/// </summary>
public static class GenshinMapImporter
{
    private static readonly IReadOnlyDictionary<int, MarkerKind> CategoryKinds =
        new Dictionary<int, MarkerKind>
        {
            [1] = MarkerKind.Waypoint,   //传送点: статуи, якоря, подземелья
            [4] = MarkerKind.Crystal,    //贵重收集物: окули
            [13] = MarkerKind.Chest,     //露天宝箱
            [186] = MarkerKind.Chest,    //解谜宝箱
            [51] = MarkerKind.Chest,     //见闻: тайники и сундуки
            [11] = MarkerKind.Ore,       //矿物
            [10] = MarkerKind.Flower,    //区域特产
            [232] = MarkerKind.Fish,     //垂钓
            [60] = MarkerKind.Mushroom   //背包/素材: грибы, ягоды, мясо
        };

    public static ImportResult Convert(
        IReadOnlyList<GenshinPoint> points,
        IReadOnlyDictionary<int, GenshinLabel> labels,
        IReadOnlyList<GenshinAnchor> anchors,
        GenshinMapInfo info,
        GenshinImportOptions? options = null)
    {
        options ??= new GenshinImportOptions();

        var markers = new List<ImportedMarker>();
        var seen = new HashSet<(MarkerKind Kind, long X, long Z)>();
        var waypointCells = new HashSet<(long X, long Z)>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var unknownCategories = new Dictionary<int, int>();
        var skippedHidden = 0;
        var skippedOutside = 0;
        var duplicates = 0;

        // Сначала опорные точки: даже при ограничении на количество они нужны.
        foreach (var point in Order(points))
        {
            if (markers.Count >= options.MaxMarkers)
            {
                break;
            }

            if (point.DisplayState != 1)
            {
                skippedHidden++;
                continue;
            }

            if (!labels.TryGetValue(point.LabelId, out var label))
            {
                unknownCategories.TryGetValue(-1, out var missing);
                unknownCategories[-1] = missing + 1;
                continue;
            }

            var kind = KindForCategory(label.CategoryId);
            if (kind is null || !Allowed(kind.Value, options))
            {
                unknownCategories.TryGetValue(label.CategoryId, out var count);
                unknownCategories[label.CategoryId] = count + 1;
                continue;
            }

            var pixelX = info.ToPixelX(point.X);
            var pixelZ = info.ToPixelZ(point.Y);
            if (!info.Contains(pixelX, pixelZ))
            {
                skippedOutside++;
                continue;
            }

            var key = (kind.Value, (long)Math.Round(pixelX), (long)Math.Round(pixelZ));
            if (!seen.Add(key))
            {
                duplicates++;
                continue;
            }

            // Телепорты сообщество добавляет по нескольку раз: схлопываем
            // точки ближе 30 м, иначе в списке привязки сотни дублей.
            if (kind.Value == MarkerKind.Waypoint)
            {
                var cell = ((long)Math.Round(pixelX / 30.0), (long)Math.Round(pixelZ / 30.0));
                if (!waypointCells.Add(cell))
                {
                    duplicates++;
                    continue;
                }
            }

            var name = MakeName(label, anchors, point.X, point.Y, kind.Value, names);
            markers.Add(new ImportedMarker(name, kind.Value, pixelX, pixelZ));
        }

        var warnings = new List<string>();
        if (skippedOutside > 0)
        {
            warnings.Add($"Вне изображения карты: {skippedOutside}.");
        }

        if (skippedHidden > 0)
        {
            warnings.Add($"Скрытые точки: {skippedHidden}.");
        }

        if (duplicates > 0)
        {
            warnings.Add($"Дубликаты: {duplicates}.");
        }

        if (unknownCategories.TryGetValue(-1, out var withoutLabel))
        {
            warnings.Add($"Неизвестный тип точки: {withoutLabel}.");
        }

        var unsupported = unknownCategories
            .Where(pair => pair.Key >= 0)
            .Sum(pair => pair.Value);
        if (unsupported > 0)
        {
            warnings.Add($"Категории вне прототипа: {unsupported}.");
        }

        return new ImportResult(markers, warnings);
    }

    public static MarkerKind? KindForCategory(int categoryId) =>
        CategoryKinds.TryGetValue(categoryId, out var kind) ? kind : null;

    private static bool Allowed(MarkerKind kind, GenshinImportOptions options) => kind switch
    {
        MarkerKind.Waypoint => options.IncludeWaypoints,
        MarkerKind.Chest => options.IncludeChests,
        _ => options.IncludeResources
    };

    private static IEnumerable<GenshinPoint> Order(IReadOnlyList<GenshinPoint> points) =>
        points.OrderBy(point => point.LabelId == 2 || point.LabelId == 3 ? 0 : 1);

    private static string MakeName(
        GenshinLabel label,
        IReadOnlyList<GenshinAnchor> anchors,
        double x,
        double y,
        MarkerKind kind,
        ISet<string> used)
    {
        var area = FindAnchor(anchors, x, y);
        var baseName = area is null ? label.Name : $"{label.Name} · {area}";

        // Статуи и якоря полезно различать, остальным хватает типа и региона.
        if (used.Add(baseName))
        {
            return baseName;
        }

        for (var index = 2; index < 10000; index++)
        {
            var candidate = $"{baseName} #{index}";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }

        var fallback = $"{MarkerKinds.All[kind].NameRu} {(long)x},{(long)y}";
        used.Add(fallback);
        return fallback;
    }

    private static string? FindAnchor(
        IReadOnlyList<GenshinAnchor> anchors,
        double x,
        double y)
    {
        GenshinAnchor? best = null;
        foreach (var anchor in anchors)
        {
            if (!anchor.Contains(x, y))
            {
                continue;
            }

            if (best is null || anchor.Area < best.Area)
            {
                best = anchor;
            }
        }

        return best?.Name;
    }
}
