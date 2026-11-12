using System.Windows;
using System.Windows.Media;
using TeyvatLens.Core.Models;

namespace TeyvatLens.App.Controls;

/// <summary>
/// Векторные иконки маркеров в духе карты Genshin Impact.
/// Все фигуры нарисованы в системе координат 24×24 и заморожены,
/// чтобы их можно было рисовать на карте без лишних аллокаций.
/// </summary>
public static class MarkerIcons
{
    private const string WarmOutline = "#4A3618";
    private const string CoolOutline = "#2E3A42";

    private static readonly IReadOnlyDictionary<MarkerKind, Drawing> Drawings = Build();
    private static readonly Dictionary<MarkerKind, DrawingImage> Images = new();

    public static Drawing GetDrawing(MarkerKind kind) => Drawings[kind];

    public static DrawingImage GetImage(MarkerKind kind)
    {
        if (!Images.TryGetValue(kind, out var image))
        {
            image = new DrawingImage(Drawings[kind]);
            image.Freeze();
            Images[kind] = image;
        }

        return image;
    }

    private static IReadOnlyDictionary<MarkerKind, Drawing> Build() =>
        new Dictionary<MarkerKind, Drawing>
        {
            [MarkerKind.Chest] = Chest(),
            [MarkerKind.Ore] = Ore(),
            [MarkerKind.Flower] = Flower(),
            [MarkerKind.Mushroom] = Mushroom(),
            [MarkerKind.Fish] = Fish(),
            [MarkerKind.Crystal] = Crystal(),
            [MarkerKind.Waypoint] = Waypoint()
        };

    private static Drawing Chest()
    {
        var body = Shape(
            "M3.6,10.4 H20.4 V18.2 Q20.4,20 18.8,20 H5.2 Q3.6,20 3.6,18.2 Z",
            "#D9A94F");
        var lid = Shape(
            "M3.6,10.4 Q3.6,5 12,5 Q20.4,5 20.4,10.4 Z",
            "#E7BC63");
        var lidLight = Shape(
            "M5.6,9.2 Q5.8,6.6 12,6.6 Q18.2,6.6 18.4,9.2 Z",
            "#F6D98C",
            null);
        var band = Shape("M10.4,5.2 H13.6 V20 H10.4 Z", "#8A6226", WarmOutline, 0.8);
        var lockPlate = Circle(12, 13.9, 1.8, "#F4DF9A");
        return Group(body, lid, lidLight, band, lockPlate);
    }

    private static Drawing Ore()
    {
        var left = Shape("M7.2,10.4 L9.8,7.6 L9.2,16.6 L6.2,14.4 Z", "#5E92C8", CoolOutline);
        var right = Shape("M16.8,10.8 L19.4,13.6 L17.2,18.8 L15.2,16 Z", "#5E92C8", CoolOutline);
        var main = Shape(
            "M12,2.4 L16.4,8.8 L13.8,21 L10.2,21 L7.6,8.8 Z",
            "#7FB4E8",
            CoolOutline);
        var facet = Shape(
            "M12,2.4 L13.6,8.8 L12.4,21 L10.2,21 L7.6,8.8 Z",
            "#C9E2FA",
            null);
        return Group(left, right, main, facet);
    }

    private static Drawing Flower()
    {
        var petals = new GeometryGroup { FillRule = FillRule.Nonzero };
        for (var index = 0; index < 5; index++)
        {
            var petal = new EllipseGeometry(new Point(12, 6.9), 2.9, 3.7)
            {
                Transform = new RotateTransform(index * 72, 12, 12)
            };
            petal.Freeze();
            petals.Children.Add(petal);
        }

        petals.Freeze();

        var petalDrawing = new GeometryDrawing(
            Brush("#F0A6C4"),
            Pen(WarmOutline, 1.1),
            petals);
        petalDrawing.Freeze();

        return Group(petalDrawing, Circle(12, 12, 2.0, "#F6D96B"));
    }

    private static Drawing Mushroom()
    {
        var stem = Shape(
            "M9.2,11.6 V18.1 Q9.2,19.8 10.8,19.8 H13.2 Q14.8,19.8 14.8,18.1 V11.6 Z",
            "#EFE3C8");
        var cap = Shape(
            "M3.4,12 Q3.4,4.4 12,4.4 Q20.6,4.4 20.6,12 Z",
            "#B98BE0");
        var spots = Group(
            Circle(8.3, 8.1, 1.2, "#F3E6FF", null, 0),
            Circle(14.8, 7.4, 1.0, "#F3E6FF", null, 0),
            Circle(11.4, 10.1, 0.8, "#F3E6FF", null, 0));
        return Group(stem, cap, spots);
    }

    private static Drawing Fish()
    {
        var tail = Shape("M4.6,12 L1.4,8.4 L2.3,12 L1.4,15.6 Z", "#8FE3DA", CoolOutline);
        var fin = Shape("M9.8,7.4 L11.9,4.2 L13.5,7.6 Z", "#8FE3DA", CoolOutline);
        var body = Shape(
            "M4,12 C7,7.2 15,6.6 19.6,10.5 C20.6,11.3 20.6,12.7 19.6,13.5 " +
            "C15,17.4 7,16.8 4,12 Z",
            "#69D3CB",
            CoolOutline);
        var eye = Circle(16.1, 11.1, 0.95, "#23414A", null, 0);
        return Group(tail, fin, body, eye);
    }

    private static Drawing Crystal()
    {
        var gem = Shape("M7.4,3.4 H16.6 L21,10 L12,21 L3,10 Z", "#7AD6E8", CoolOutline);
        var left = Shape("M7.4,3.4 L12,10 L3,10 Z", "#C9F0F8", null);
        var right = Shape("M16.6,3.4 L21,10 L12,10 Z", "#5FC1D6", null);
        var middle = Shape("M12,10 L16.6,3.4 L12,21 Z", "#A8E7F2", null);
        return Group(gem, left, right, middle);
    }

    private static Drawing Waypoint()
    {
        var outer = Shape("M12,1.4 L18.6,9 L12,22.6 L5.4,9 Z", "#8FE3F2", CoolOutline, 1.2);
        var inner = Shape("M12,5.2 L15.6,9.4 L12,18.2 L8.4,9.4 Z", "#EAFBFF", null);
        var core = Circle(12, 9.6, 2.1, "#FFFFFF", "#4FB6CC", 0.8);
        var baseBar = Shape("M9.4,20.8 H14.6 V22.2 H9.4 Z", "#4FB6CC", CoolOutline, 0.6);
        return Group(outer, inner, core, baseBar);
    }

    private static GeometryDrawing Shape(
        string data,
        string fill,
        string? stroke = WarmOutline,
        double thickness = 1.15)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        var drawing = new GeometryDrawing(
            Brush(fill),
            stroke is null ? null : Pen(stroke, thickness),
            geometry);
        drawing.Freeze();
        return drawing;
    }

    private static GeometryDrawing Circle(
        double centerX,
        double centerY,
        double radius,
        string fill,
        string? stroke = WarmOutline,
        double thickness = 1.0)
    {
        var geometry = new EllipseGeometry(new Point(centerX, centerY), radius, radius);
        geometry.Freeze();
        var drawing = new GeometryDrawing(
            Brush(fill),
            stroke is null ? null : Pen(stroke, thickness),
            geometry);
        drawing.Freeze();
        return drawing;
    }

    private static DrawingGroup Group(params Drawing[] parts)
    {
        var group = new DrawingGroup();
        foreach (var part in parts)
        {
            group.Children.Add(part);
        }

        group.Freeze();
        return group;
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static Pen Pen(string hex, double thickness)
    {
        var pen = new Pen(Brush(hex), thickness);
        pen.Freeze();
        return pen;
    }
}
