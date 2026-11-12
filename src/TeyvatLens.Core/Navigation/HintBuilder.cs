using TeyvatLens.Core.Geometry;
using TeyvatLens.Core.Models;

namespace TeyvatLens.Core.Navigation;

/// <summary>Выбор и ранжирование цели для подсказки навигации.</summary>
public static class HintBuilder
{
    // Насколько тип объекта может «перевесить» лишние метры расстояния.
    private const double PriorityBonusM = 12.0;

    /// <summary>
    /// Сортирует маркеры по полезности: расстояние с поправкой на тип.
    /// Сундук с приоритетом 1.0 получает бонус 12 м, цветок 0.6 — 7.2 м.
    /// </summary>
    public static IReadOnlyList<NearbyMarker> Rank(IEnumerable<NearbyMarker> markers) =>
        markers
            .OrderBy(item =>
                item.DistanceM - MarkerKinds.Info(item.Marker.Kind).Priority * PriorityBonusM)
            .ThenBy(item => item.DistanceM)
            .ToList();

    public static NavigationHint? BuildHint(
        IEnumerable<NearbyMarker> markers,
        double headingDeg,
        double? playerY)
    {
        var candidates = markers
            .Where(item => !item.Marker.Collected)
            .Where(item => MarkerKinds.Info(item.Marker.Kind).IsTarget)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        var target = Rank(candidates)[0];
        var relative = GeoMath.AngleDelta(target.BearingDeg, headingDeg);
        return new NavigationHint(
            target,
            relative,
            GeoMath.DirectionLabel(relative),
            GeoMath.HeightHint(target.Marker.Y, playerY));
    }
}
