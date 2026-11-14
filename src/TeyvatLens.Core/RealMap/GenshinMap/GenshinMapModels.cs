namespace TeyvatLens.Core.RealMap.GenshinMap;

/// <summary>
/// Метаданные официальной интерактивной карты Тейвата.
///
/// Координаты точек карты (<c>x_pos</c>, <c>y_pos</c>) заданы в «мировых»
/// единицах карты. Пиксель изображения на нулевом зуме получается сдвигом на
/// <see cref="OriginX"/> / <see cref="OriginY"/>; один пиксель нулевого зума
/// приблизительно равен одному метру мира.
/// </summary>
public sealed record GenshinMapInfo(
    int MapId,
    string Name,
    string Version,
    double OriginX,
    double OriginY,
    int TotalWidth,
    int TotalHeight,
    int MinZoom,
    int MaxZoom)
{
    /// <summary>Размер тайла изображения карты, px.</summary>
    public const int TileSize = 256;

    /// <summary>Пиксель по X на нулевом зуме для координаты точки карты.</summary>
    public double ToPixelX(double xPos) => xPos + OriginX;

    /// <summary>Пиксель по Z на нулевом зуме для координаты точки карты.</summary>
    public double ToPixelZ(double yPos) => yPos + OriginY;

    /// <summary>Лежит ли пиксель внутри изображения карты.</summary>
    public bool Contains(double pixelX, double pixelZ) =>
        pixelX >= 0 && pixelZ >= 0 && pixelX < TotalWidth && pixelZ < TotalHeight;
}

/// <summary>
/// Тип маркера из дерева категорий интерактивной карты.
/// <see cref="CategoryId"/> — идентификатор категории верхнего уровня.
/// </summary>
public sealed record GenshinLabel(int Id, string Name, int ParentId, int CategoryId);

/// <summary>Именованный регион карты (город, локация) с прямоугольной областью.</summary>
public sealed record GenshinAnchor(
    string Name,
    double Left,
    double Top,
    double Right,
    double Bottom)
{
    public bool Contains(double x, double y) =>
        x >= Left && x <= Right && y >= Top && y <= Bottom;

    public double Area =>
        Math.Max(0, Right - Left) * Math.Max(0, Bottom - Top);
}

/// <summary>Одна точка интерактивной карты в её собственных координатах.</summary>
public sealed record GenshinPoint(
    long Id,
    int LabelId,
    double X,
    double Y,
    int DisplayState);

/// <summary>Какие категории карты переносить в базу.</summary>
public sealed record GenshinImportOptions(
    bool IncludeWaypoints = true,
    bool IncludeChests = true,
    bool IncludeResources = true,
    int MaxMarkers = 60000);

/// <summary>Вырезанный из карты прямоугольный фрагмент изображения.</summary>
public sealed record GenshinMapRegion(
    string Path,
    int Left,
    int Top,
    int SizePx)
{
    public int Right => Left + SizePx;

    public int Bottom => Top + SizePx;

    public bool Contains(double x, double z, double marginM = 0) =>
        x >= Left + marginM && x <= Right - marginM &&
        z >= Top + marginM && z <= Bottom - marginM;
}
