using System.ComponentModel;
using System.Windows.Media;
using TeyvatLens.App.Controls;
using TeyvatLens.Core.Geometry;
using TeyvatLens.Core.Models;

namespace TeyvatLens.App;

/// <summary>Строка списка ближайших объектов. Обновляется на месте, чтобы не мигал список.</summary>
public sealed class MarkerRow : INotifyPropertyChanged
{
    private string _distanceText = string.Empty;
    private string _directionText = string.Empty;
    private bool _isCollected;

    public MarkerRow(NearbyMarker item, double headingDeg)
    {
        Id = item.Marker.Id;
        Name = item.Marker.Name;
        Kind = item.Marker.Kind;
        Icon = MarkerIcons.GetImage(item.Marker.Kind);
        KindLabel = MarkerKinds.Info(item.Marker.Kind).NameRu;
        Accent = BrushFrom(MarkerKinds.Info(item.Marker.Kind).ColorHex);
        Update(item, headingDeg);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public long Id { get; }

    public string Name { get; }

    public MarkerKind Kind { get; }

    public ImageSource Icon { get; }

    public string KindLabel { get; }

    public Brush Accent { get; }

    public string DistanceText
    {
        get => _distanceText;
        private set
        {
            if (_distanceText == value)
            {
                return;
            }

            _distanceText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DistanceText)));
        }
    }

    public string DirectionText
    {
        get => _directionText;
        private set
        {
            if (_directionText == value)
            {
                return;
            }

            _directionText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DirectionText)));
        }
    }

    public bool IsCollected
    {
        get => _isCollected;
        private set
        {
            if (_isCollected == value)
            {
                return;
            }

            _isCollected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCollected)));
        }
    }

    public void Update(NearbyMarker item, double headingDeg)
    {
        DistanceText = $"{item.DistanceM:F0} м";
        var relative = GeoMath.AngleDelta(item.BearingDeg, headingDeg);
        DirectionText = GeoMath.DirectionLabel(relative);
        IsCollected = item.Marker.Collected;
    }

    private static Brush BrushFrom(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
