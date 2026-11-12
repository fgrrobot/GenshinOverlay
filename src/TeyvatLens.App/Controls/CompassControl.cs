using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace TeyvatLens.App.Controls;

/// <summary>Компас: направление взгляда и азимут ближайшей цели.</summary>
public sealed class CompassControl : FrameworkElement
{
    private static readonly Brush DialBrush = FrozenBrush(Color.FromArgb(190, 18, 23, 31));
    private static readonly Pen DialPen = FrozenPen(FrozenBrush(Color.FromArgb(120, 42, 51, 66)), 1);
    private static readonly Pen TickPen = FrozenPen(FrozenBrush(Color.FromArgb(90, 139, 150, 168)), 1);
    private static readonly Pen NeedlePen = FrozenPen(FrozenBrush(Color.FromRgb(231, 237, 245)), 2.5);
    private static readonly Pen TargetPen = FrozenPen(FrozenBrush(Color.FromRgb(217, 180, 92)), 2);
    private static readonly Brush TextBrush = FrozenBrush(Color.FromRgb(139, 150, 168));
    private static readonly Brush NorthBrush = FrozenBrush(Color.FromRgb(111, 211, 232));
    private static readonly Brush CenterBrush = FrozenBrush(Color.FromRgb(231, 237, 245));

    private double _headingDeg;
    private double? _targetBearingDeg;

    public double HeadingDeg
    {
        get => _headingDeg;
        set
        {
            _headingDeg = value;
            InvalidateVisual();
        }
    }

    public double? TargetBearingDeg
    {
        get => _targetBearingDeg;
        set
        {
            _targetBearingDeg = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 8)
        {
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = size / 2 - 4;

        dc.DrawEllipse(DialBrush, DialPen, center, radius, radius);

        for (var index = 0; index < 12; index++)
        {
            var angle = index * 30.0 * Math.PI / 180.0;
            var outer = new Vector(Math.Sin(angle), -Math.Cos(angle));
            var inner = outer * (index % 3 == 0 ? 0.74 : 0.84);
            dc.DrawLine(
                TickPen,
                center + outer * radius,
                center + inner * radius);
        }

        DrawCardinal(dc, center, radius, "С", 0, NorthBrush);
        DrawCardinal(dc, center, radius, "В", 90, TextBrush);
        DrawCardinal(dc, center, radius, "Ю", 180, TextBrush);
        DrawCardinal(dc, center, radius, "З", 270, TextBrush);

        if (_targetBearingDeg is not null)
        {
            dc.PushTransform(new RotateTransform(_targetBearingDeg.Value, center.X, center.Y));
            dc.DrawLine(TargetPen, center, new Point(center.X, center.Y - radius * 0.7));
            dc.Pop();
        }

        dc.PushTransform(new RotateTransform(_headingDeg, center.X, center.Y));
        dc.DrawLine(NeedlePen, center + new Vector(0, radius * 0.22), new Point(center.X, center.Y - radius * 0.72));
        dc.Pop();

        dc.DrawEllipse(CenterBrush, null, center, 2.5, 2.5);
    }

    private static void DrawCardinal(
        DrawingContext dc,
        Point center,
        double radius,
        string text,
        double bearing,
        Brush brush)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            10,
            brush,
            1.0);

        var angle = bearing * Math.PI / 180.0;
        var direction = new Vector(Math.Sin(angle), -Math.Cos(angle));
        var position = center + direction * (radius - 13);
        dc.DrawText(formatted, new Point(position.X - formatted.Width / 2, position.Y - formatted.Height / 2));
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
