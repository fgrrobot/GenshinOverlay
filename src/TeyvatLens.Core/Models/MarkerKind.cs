namespace TeyvatLens.Core.Models;

/// <summary>Типы объектов, которые поддерживает прототип.</summary>
public enum MarkerKind
{
    Chest,
    Ore,
    Flower,
    Mushroom,
    Fish,
    Crystal,
    Waypoint
}

/// <summary>Справочная информация о типе маркера.</summary>
public sealed record MarkerKindInfo(
    MarkerKind Kind,
    string Code,
    string NameRu,
    string ColorHex,
    double Priority,
    string Glyph)
{
    /// <summary>Телепорты — опорные точки, а не цель для навигации.</summary>
    public bool IsTarget => Kind != MarkerKind.Waypoint;
}

/// <summary>Единый справочник типов: используется и базой, и интерфейсом.</summary>
public static class MarkerKinds
{
    public static readonly IReadOnlyDictionary<MarkerKind, MarkerKindInfo> All =
        new Dictionary<MarkerKind, MarkerKindInfo>
        {
            [MarkerKind.Chest] = new(MarkerKind.Chest, "chest", "Сундук", "#d8b25a", 1.0, "▣"),
            [MarkerKind.Ore] = new(MarkerKind.Ore, "ore", "Руда", "#6fa8dc", 0.7, "◆"),
            [MarkerKind.Flower] = new(MarkerKind.Flower, "flower", "Цветок", "#e58bb8", 0.6, "✿"),
            [MarkerKind.Mushroom] = new(MarkerKind.Mushroom, "mushroom", "Гриб", "#b18ae0", 0.5, "●"),
            [MarkerKind.Fish] = new(MarkerKind.Fish, "fish", "Рыба", "#5ad0c8", 0.5, "≈"),
            [MarkerKind.Crystal] = new(MarkerKind.Crystal, "crystal", "Кристалл", "#7ad6e8", 0.8, "◇"),
            [MarkerKind.Waypoint] = new(MarkerKind.Waypoint, "waypoint", "Телепорт", "#e8eef5", 0.0, "✧")
        };

    public static string ToCode(MarkerKind kind) => All[kind].Code;

    public static MarkerKind FromCode(string code)
    {
        foreach (var pair in All)
        {
            if (string.Equals(pair.Value.Code, code, StringComparison.Ordinal))
            {
                return pair.Key;
            }
        }

        throw new ArgumentException($"Неизвестный тип маркера: {code}", nameof(code));
    }

    public static MarkerKindInfo Info(MarkerKind kind) => All[kind];
}
