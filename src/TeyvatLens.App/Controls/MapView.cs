using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TeyvatLens.Core.Geometry;
using TeyvatLens.Core.Models;

namespace TeyvatLens.App.Controls;

/// <summary>
/// Карта мира: север сверху, игрок в центре. Рисует атлас (если он есть),
/// кольца радиуса, маркеры, подсказку и стрелку игрока.
/// </summary>
public sealed class MapView : FrameworkElement
{
    private static readonly Brush BackgroundBrush = FrozenBrush(Color.FromRgb(13, 16, 22));
    private static readonly Brush GridBrush = FrozenBrush(Color.FromArgb(24, 111, 211, 232));
    private static readonly Pen GridPen = FrozenPen(GridBrush, 1);
    private static readonly Pen RingPen = FrozenPen(FrozenBrush(Color.FromArgb(70, 140, 160, 180)), 1);
    private static readonly Pen OuterRingPen = FrozenPen(FrozenBrush(Color.FromArgb(110, 111, 211, 232)), 1.5);
    private static readonly Pen BorderPen = FrozenPen(FrozenBrush(Color.FromArgb(90, 42, 51, 66)), 1);
    private static readonly Pen SelectedPen = FrozenPen(FrozenBrush(Color.FromRgb(111, 211, 232)), 2);
    private static readonly Brush PlayerBrush = FrozenBrush(Color.FromRgb(231, 237, 245));
    private static readonly Pen PlayerPen = FrozenPen(FrozenBrush(Color.FromArgb(160, 7, 19, 26)), 1.5);
    private static readonly Brush GlowBrush = CreateGlow();
    private static readonly Brush ShadowBrush = FrozenBrush(Color.FromArgb(45, 0, 0, 0));
    private static readonly Brush MutedBrush = FrozenBrush(Color.FromRgb(139, 150, 168));
    private static readonly Brush AccentBrush = FrozenBrush(Color.FromRgb(111, 211, 232));
    private static readonly Brush GlyphBrush = FrozenBrush(Color.FromRgb(7, 19, 26));
    private static readonly Brush HintPenBrush = FrozenBrush(Color.FromArgb(160, 217, 180, 92));
    private static readonly Pen HintPen = CreateHintPen();

    private static readonly IReadOnlyDictionary<MarkerKind, Brush> KindBrushes =
        MarkerKinds.All.ToDictionary(
            pair => pair.Key,
            pair => (Brush)FrozenBrush(ParseColor(pair.Value.ColorHex)));

    private static readonly IReadOnlyDictionary<MarkerKind, Brush> KindDimBrushes =
        MarkerKinds.All.ToDictionary(
            pair => pair.Key,
            pair => (Brush)FrozenBrush(WithAlpha(ParseColor(pair.Value.ColorHex), 90)));

    private SessionSnapshot? _snapshot;

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

    public double WorldSizeM { get; set; } = 900.0;

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (bounds.Width < 2 || bounds.Height < 2)
        {
            return;
        }

        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
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
        var centerX = geo?.X ?? WorldSizeM / 2;
        var centerZ = geo?.Z ?? WorldSizeM / 2;
        var radius = snapshot?.RadiusM ?? 120.0;
        var halfExtent = Math.Max(24.0, radius * 1.14);
        var scale = side / (2 * halfExtent);

        dc.PushClip(new RectangleGeometry(bounds, 16, 16));

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
            DrawPlayer(dc, center, geo.HeadingDeg);
        }
        else
        {
            DrawPlaceholder(dc, center);
        }

        DrawNorth(dc, square);

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
            -worldLeft * AtlasPxPerMeter,
            -worldTop * AtlasPxPerMeter));
        dc.DrawImage(Atlas!, new Rect(0, 0, Atlas!.PixelWidth, Atlas!.PixelHeight));
        dc.Pop();
        dc.Pop();
        dc.Pop();

        dc.DrawRectangle(ShadowBrush, null, square);
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
            var brush = marker.Collected
                ? KindDimBrushes[marker.Kind]
                : KindBrushes[marker.Kind];

            if (isSelected)
            {
                dc.DrawEllipse(null, SelectedPen, point, 12, 12);
            }

            var radius = marker.Collected ? 3.5 : 6.0;
            dc.DrawEllipse(brush, null, point, radius, radius);

            if (!marker.Collected)
            {
                var glyph = new FormattedText(
                    MarkerKinds.Info(marker.Kind).Glyph,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    8.5,
                    GlyphBrush,
                    1.0);
                dc.DrawText(glyph, new Point(point.X - glyph.Width / 2, point.Y - glyph.Height / 2));
            }
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

        var angle = headingDeg * Math.PI / 180.0;
        var direction = new Vector(Math.Sin(angle), -Math.Cos(angle));
        var side = new Vector(-direction.Y, direction.X);

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
            FrozenBrush(Color.FromRgb(92, 102, 118)),
            1.0);
        dc.DrawText(hint, new Point(center.X - hint.Width / 2, center.Y + 56));
    }

    private static void DrawNorth(DrawingContext dc, Rect square)
    {
        var text = new FormattedText(
            "С",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            12,
            AccentBrush,
            1.0);

        var origin = new Point(square.X + square.Width / 2 - text.Width / 2, square.Y + 10);
        dc.DrawText(text, origin);
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
        var pen = new Pen(HintPenBrush, 1.5)
        {
            DashStyle = new DashStyle(new double[] { 4, 4 }, 0)
        };
        pen.Freeze();
        return pen;
    }

    private static Color ParseColor(string hex) =>
        (Color)ColorConverter.ConvertFromString(hex);

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

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
