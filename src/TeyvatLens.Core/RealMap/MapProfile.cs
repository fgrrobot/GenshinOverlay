namespace TeyvatLens.Core.RealMap;

/// <summary>
/// Привязка атласа реальной карты к мировым координатам.
///
/// Атлас — это изображение карты Тейвата (например, скриншот интерактивной
/// карты). Его пиксели переводятся в метры по масштабу <see cref="PxPerMeter"/>,
/// а <see cref="OriginXMeters"/> / <see cref="OriginZMeters"/> задают мировую
/// точку, которой соответствует левый верхний пиксель изображения.
/// </summary>
public sealed record MapProfile(
    string AtlasPath,
    double PxPerMeter,
    double OriginXMeters = 0,
    double OriginZMeters = 0)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(AtlasPath) && PxPerMeter > 0;

    public double ToWorldX(double atlasPx) => OriginXMeters + atlasPx / PxPerMeter;

    public double ToWorldZ(double atlasPx) => OriginZMeters + atlasPx / PxPerMeter;

    public double ToAtlasX(double worldX) => (worldX - OriginXMeters) * PxPerMeter;

    public double ToAtlasZ(double worldZ) => (worldZ - OriginZMeters) * PxPerMeter;
}
