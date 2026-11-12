namespace TeyvatLens.Core.Models;

/// <summary>Точка интереса из локальной базы.</summary>
public sealed record Marker(
    long Id,
    MarkerKind Kind,
    string Name,
    double X,
    double Z,
    double? Y,
    int LayerId,
    string? RegionCode,
    bool Collected,
    DateTimeOffset? RespawnAt)
{
    // В выпадающих списках и подписях показываем имя, а не дамп записи.
    public override string ToString() => Name;
}

/// <summary>Маркер вместе с результатом пространственного запроса.</summary>
public sealed record NearbyMarker(
    Marker Marker,
    double DistanceM,
    double BearingDeg);

/// <summary>Текущее состояние позиции игрока.</summary>
public sealed record GeoState(
    double X,
    double Z,
    double? Y,
    double HeadingDeg,
    double Confidence,
    string Source,
    int LayerId,
    string? RegionCode,
    double Timestamp);

/// <summary>Подсказка «куда идти» для ближайшей цели.</summary>
public sealed record NavigationHint(
    NearbyMarker Target,
    double RelativeBearingDeg,
    string DirectionRu,
    string HeightHint);
