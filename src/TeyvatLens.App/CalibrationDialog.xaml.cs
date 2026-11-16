using System.Drawing;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TeyvatLens.Core.App;

namespace TeyvatLens.App;

public partial class CalibrationDialog : Window
{
    private bool _syncing;
    private bool _ppmEdited;

    public CalibrationDialog(Rectangle rectangle, double pxPerMeter)
    {
        InitializeComponent();

        Rectangle = rectangle;
        PxPerMeter = pxPerMeter;

        _syncing = true;
        XBox.Text = rectangle.X.ToString(CultureInfo.CurrentCulture);
        YBox.Text = rectangle.Y.ToString(CultureInfo.CurrentCulture);
        SizeBox.Text = rectangle.Width.ToString(CultureInfo.CurrentCulture);
        PpmBox.Text = pxPerMeter.ToString("0.##", CultureInfo.CurrentCulture);
        _syncing = false;
    }

    public Rectangle Rectangle { get; private set; }

    public double PxPerMeter { get; private set; }

    /// <summary>Пока масштаб не правили вручную, подставляем оценку по размеру.</summary>
    private void OnSizeChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || _ppmEdited)
        {
            return;
        }

        if (!int.TryParse(SizeBox.Text, out var size) || size < 32)
        {
            return;
        }

        _syncing = true;
        PpmBox.Text = SessionSettings
            .EstimateScreenPxPerMeter(size)
            .ToString("0.##", CultureInfo.CurrentCulture);
        _syncing = false;
    }

    private void OnPpmChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncing)
        {
            _ppmEdited = true;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(XBox.Text, out var x)
            || !int.TryParse(YBox.Text, out var y)
            || !int.TryParse(SizeBox.Text, out var size))
        {
            ErrorText.Text = "X, Y и размер должны быть целыми числами.";
            return;
        }

        if (!TryParseDouble(PpmBox.Text, out var ppm))
        {
            ErrorText.Text = "Масштаб должен быть числом, например 2.0.";
            return;
        }

        if (size < 32)
        {
            ErrorText.Text = "Размер области — не меньше 32 пикселей.";
            return;
        }

        if (ppm is <= 0 or > 20)
        {
            ErrorText.Text = "Масштаб должен быть в диапазоне 0.2–20 пикселей на метр.";
            return;
        }

        Rectangle = new Rectangle(x, y, size, size);
        PxPerMeter = ppm;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
