using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TeyvatLens.Core.Geometry;
using TeyvatLens.Core.Models;

namespace TeyvatLens.App.Controls;

/// <summary>
/// Карта мира в духе Genshin Impact. Карта всегда ориентирована на север
/// (как интерактивная карта Тейвата), а по курсу поворачивается только
/// стрелка игрока в центре. Север отмечен буквой «С» сверху.
/// </summary>
public sealed class MapView : FrameworkElement
{
    private static readonly Brush BackgroundBrush = FrozenBrush(Color.FromRgb(13, 16, 22));
    private static readonly Brush GridBrush = FrozenBrush(Color.FromArgb(38, 46, 122, 136));
    private static readonly Pen GridPen = FrozenPen(GridBrush, 1);
    private static readonly Pen RingPen = FrozenPen(FrozenBrush(Color.FromArgb(80, 46, 122, 136)), 1);
    private static readonly Pen OuterRingPen = FrozenPen(FrozenBrush(Color.FromArgb(150, 31, 102, 117)), 1.5);
    private static readonly Pen BorderPen = FrozenPen(FrozenBrush(Color.FromArgb(90, 42, 51, 66)), 1);
    private static readonly Pen SelectedPen = FrozenPen(FrozenBrush(Color.FromRgb(31, 102, 117)), 2.2);
    private static readonly Brush PlayerBrush = FrozenBrush(Color.FromRgb(250, 253, 255));
    private static readonly Pen PlayerPen = FrozenPen(FrozenBrush(Color.FromArgb(220, 27, 36, 48)), 1.5);
    private static readonly Brush GlowBrush = CreateGlow();
    private static readonly Brush MarkerShadowBrush = FrozenBrush(Color.FromArgb(70, 20, 26, 34));
    private static readonly Brush MutedBrush = FrozenBrush(Color.FromRgb(92, 106, 120));
    private static readonly Brush AccentBrush = FrozenBrush(Color.FromRgb(31, 102, 117));
    private static readonly Pen HintPen = CreateHintPen();

    private SessionSnapshot? _snapshot;

    public MapView()
    {
        // Карта — растровая картинка, которую почти всегда увеличивают.
        // Сглаживание делает её «мыльной», поэтому пиксели не размываем.
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    public SessionSnapshot? Snapshot
    {
        get => _snapshot;
        set
        {
            _snapshot = value;
            InvalidateVisual();
        }
    }

    public BitmapSource? Atlas { get; set; }

    public double AtlasPxPerMeter { get; set; } = 2.0;

    /// <summary>Мировые координаты левого верхнего пикселя атласа.</summary>
    public double AtlasOriginXMeters { get; set; }

    public double AtlasOriginZMeters { get; set; }

    public double WorldSizeM { get; set; } = 900.0;

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (bounds.Width < 2 || bounds.Height < 2)
        {
            return;
        }

        dc.DrawRectangle(BackgroundBrush, null, bounds);

        var side = Math.Min(bounds.Width, bounds.Height);
        var square = new Rect(
            (bounds.Width - side) / 2,
            (bounds.Height - side) / 2,
            side,
            side);
        var center = new Point(square.X + side / 2, square.Y + side / 2);

        var snapshot = _snapshot;
        var geo = snapshot?.Geo;
        var centerX = geo?.X ?? AtlasOriginXMeters + WorldSizeM / 2;
        var centerZ = geo?.Z ?? AtlasOriginZMeters + WorldSizeM / 2;
        var radius = snapshot?.RadiusM ?? 120.0;
        var halfExtent = Math.Max(24.0, radius * 1.14);
        var scale = side / (2 * halfExtent);
        var heading = geo?.HeadingDeg ?? 0.0;

        dc.PushClip(new RectangleGeometry(bounds, 16, 16));

        // Карта всегда «на север»: так её проще сопоставлять с интерактивной
        // картой и маркерами. По курсу поворачивается только стрелка игрока.
        if (Atlas is not null)
        {
            DrawAtlas(dc, square, centerX, centerZ, halfExtent, scale);
        }
        else
        {
            DrawGrid(dc, bounds, center, centerX, centerZ, halfExtent, scale);
        }

        if (geo is not null)
        {
            DrawRings(dc, center, radius, scale);
        }

        if (snapshot is not null && snapshot.Hint is not null && geo is not null)
        {
            DrawHint(dc, center, geo, snapshot.Hint, scale);
        }

        if (snapshot is not null && geo is not null)
        {
            DrawMarkers(dc, square, center, geo, snapshot, scale);
        }

        if (geo is not null)
        {
            DrawPlayer(dc, center, heading);
        }
        else
        {
            DrawPlaceholder(dc, center);
        }

        DrawNorth(dc, center, side);

        dc.Pop();
        dc.DrawRoundedRectangle(null, BorderPen, new Rect(0.5, 0.5, bounds.Width - 1, bounds.Height - 1), 16, 16);
    }

    private void DrawAtlas(
        DrawingContext dc,
        Rect square,
        double centerX,
        double centerZ,
        double halfExtent,
        double scale)
    {
        var worldLeft = centerX - halfExtent;
        var worldTop = centerZ - halfExtent;
        var atlasScale = scale / AtlasPxPerMeter;

        dc.PushTransform(new TranslateTransform(square.X, square.Y));
        dc.PushTransform(new ScaleTransform(atlasScale, atlasScale));
        dc.PushTransform(new TranslateTransform(
            -(worldLeft - AtlasOriginXMeters) * AtlasPxPerMeter,
            -(worldTop - AtlasOriginZMeters) * AtlasPxPerMeter));
        dc.DrawImage(Atlas!, new Rect(0, 0, Atlas!.PixelWidth, Atlas!.PixelHeight));
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private static void DrawGrid(
        DrawingContext dc,
        Rect bounds,
        Point center,
        double centerX,
        double centerZ,
        double halfExtent,
        double scale)
    {
        var left = centerX - halfExtent;
        var top = centerZ - halfExtent;
        var step = 50.0;

        for (var x = Math.Ceiling(left / step) * step; x <= left + 2 * halfExtent; x += step)
        {
            var sx = center.X + (x - centerX) * scale;
            dc.DrawLine(GridPen, new Point(sx, bounds.Top), new Point(sx, bounds.Bottom));
        }

        for (var z = Math.Ceiling(top / step) * step; z <= top + 2 * halfExtent; z += step)
        {
            var sy = center.Y + (z - centerZ) * scale;
            dc.DrawLine(GridPen, new Point(bounds.Left, sy), new Point(bounds.Right, sy));
        }
    }

    private static void DrawRings(DrawingContext dc, Point center, double radius, double scale)
    {
        foreach (var fraction in new[] { 1.0 / 3.0, 2.0 / 3.0, 1.0 })
        {
            var r = radius * fraction * scale;
            var pen = fraction >= 1.0 ? OuterRingPen : RingPen;
            dc.DrawEllipse(null, pen, center, r, r);
        }
    }

    private static void DrawMarkers(
        DrawingContext dc,
        Rect square,
        Point center,
        GeoState geo,
        SessionSnapshot snapshot,
        double scale)
    {
        foreach (var item in snapshot.Markers)
        {
            var marker = item.Marker;
            var point = new Point(
                center.X + (marker.X - geo.X) * scale,
                center.Y + (marker.Z - geo.Z) * scale);

            if (!square.Contains(point))
            {
                continue;
            }

            var isSelected = snapshot.SelectedMarkerId == marker.Id;
            var baseSize = marker.Kind == MarkerKind.Waypoint ? 20.0 : 16.0;
            var size = marker.Collected ? baseSize * 0.8 : baseSize;

            // Мягкая тень помогает иконке читаться на светлой карте.
            dc.DrawEllipse(MarkerShadowBrush, null, point, size * 0.55, size * 0.55);

            if (isSelected)
            {
                dc.DrawEllipse(null, SelectedPen, point, size * 0.85, size * 0.85);
            }

            dc.PushOpacity(marker.Collected ? 0.45 : 1.0);

            // Карта не вращается, поэтому иконки всегда стоят вертикально.
            dc.PushTransform(new TranslateTransform(point.X - size / 2, point.Y - size / 2));
            dc.PushTransform(new ScaleTransform(size / 24.0, size / 24.0));
            dc.DrawDrawing(MarkerIcons.GetDrawing(marker.Kind));
            dc.Pop();
            dc.Pop();
            dc.Pop();
        }
    }

    private static void DrawHint(
        DrawingContext dc,
        Point center,
        GeoState geo,
        NavigationHint hint,
        double scale)
    {
        var target = new Point(
            center.X + (hint.Target.Marker.X - geo.X) * scale,
            center.Y + (hint.Target.Marker.Z - geo.Z) * scale);

        dc.DrawLine(HintPen, center, target);
    }

    private static void DrawPlayer(DrawingContext dc, Point center, double headingDeg)
    {
        dc.DrawEllipse(GlowBrush, null, center, 26, 26);

        // Карта стоит «на север», поэтому по курсу поворачиваем саму стрелку.
        // 0° — север (вверх), углы растут по часовой стрелке.
        dc.PushTransform(new RotateTransform(headingDeg, center.X, center.Y));

        var direction = new Vector(0, -1);
        var side = new Vector(1, 0);

        var tip = center + direction * 12;
        var backLeft = center - direction * 8 + side * 7.5;
        var backRight = center - direction * 8 - side * 7.5;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(tip, true, true);
            context.LineTo(backLeft, true, false);
            context.LineTo(center - direction * 4, true, false);
            context.LineTo(backRight, true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(PlayerBrush, PlayerPen, geometry);
        dc.Pop();
    }

    private static void DrawPlaceholder(DrawingContext dc, Point center)
    {
        var text = new FormattedText(
            "Позиция не привязана",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            14,
            MutedBrush,
            1.0);
        dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y + 34));

        var hint = new FormattedText(
            "Выберите телепорт в панели справа",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            12,
            MutedBrush,
            1.0);
        dc.DrawText(hint, new Point(center.X - hint.Width / 2, center.Y + 56));
    }

    private static void DrawNorth(DrawingContext dc, Point center, double side)
    {
        // Карта north-up, поэтому «С» всегда сверху по центру.
        var position = new Point(center.X, center.Y - side / 2 + 20);

        var text = new FormattedText(
            "С",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            12,
            AccentBrush,
            1.0);

        dc.DrawText(text, new Point(position.X - text.Width / 2, position.Y - text.Height / 2));
    }

    private static Brush CreateGlow()
    {
        var brush = new RadialGradientBrush(
            Color.FromArgb(110, 111, 211, 232),
            Color.FromArgb(0, 111, 211, 232));
        brush.Freeze();
        return brush;
    }

    private static Pen CreateHintPen()
    {
        var pen = new Pen(FrozenBrush(Color.FromRgb(184, 134, 47)), 1.6)
        {
            DashStyle = new DashStyle(new double[] { 4, 4 }, 0)
        };
        pen.Freeze();
        return pen;
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
