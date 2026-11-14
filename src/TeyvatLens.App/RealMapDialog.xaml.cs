using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using TeyvatLens.Core.RealMap;

namespace TeyvatLens.App;

public partial class RealMapDialog : Window
{
    public RealMapDialog(MapProfile profile)
    {
        InitializeComponent();

        Profile = profile;
        PathBox.Text = profile.AtlasPath;
        PpmBox.Text = profile.PxPerMeter.ToString("0.####", CultureInfo.CurrentCulture);
        OriginXBox.Text = profile.OriginXMeters.ToString("0.##", CultureInfo.CurrentCulture);
        OriginZBox.Text = profile.OriginZMeters.ToString("0.##", CultureInfo.CurrentCulture);
        UpdateMetersPerPixel();
        PpmBox.TextChanged += (_, _) => UpdateMetersPerPixel();
    }

    public MapProfile Profile { get; private set; }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Изображение карты Тейвата",
            Filter = "Изображения (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp|Все файлы (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            PathBox.Text = dialog.FileName;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text.Trim();
        if (!File.Exists(path))
        {
            ErrorText.Text = "Файл карты не найден.";
            return;
        }

        if (!TryParseDouble(PpmBox.Text, out var ppm) || ppm is <= 0 or > 1000)
        {
            ErrorText.Text = "Масштаб должен быть числом от 0.001 до 1000 пикселей на метр.";
            return;
        }

        if (!TryParseDouble(OriginXBox.Text, out var originX)
            || !TryParseDouble(OriginZBox.Text, out var originZ))
        {
            ErrorText.Text = "Начало координат должно быть числом (можно 0).";
            return;
        }

        Profile = new MapProfile(Path.GetFullPath(path), ppm, originX, originZ);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void UpdateMetersPerPixel()
    {
        if (TryParseDouble(PpmBox.Text, out var ppm) && ppm > 0)
        {
            MetersPerPixelText.Text = $"{1.0 / ppm:0.###} м";
        }
        else
        {
            MetersPerPixelText.Text = "—";
        }
    }

    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
