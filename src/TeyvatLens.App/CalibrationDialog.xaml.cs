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
    private readonly double _detectedDiameterPx;

    public CalibrationDialog(Rectangle rectangle, double pxPerMeter, double detectedDiameterPx = 0)
    {
        InitializeComponent();

        Rectangle = rectangle;
        PxPerMeter = pxPerMeter;
        _detectedDiameterPx = detectedDiameterPx;

        _syncing = true;
        XBox.Text = rectangle.X.ToString(CultureInfo.CurrentCulture);
        YBox.Text = rectangle.Y.ToString(CultureInfo.CurrentCulture);
        SizeBox.Text = rectangle.Width.ToString(CultureInfo.CurrentCulture);
        PpmBox.Text = pxPerMeter.ToString("0.##", CultureInfo.CurrentCulture);
        _syncing = false;
        UpdateHint();
    }

    public Rectangle Rectangle { get; private set; }

    public double PxPerMeter { get; private set; }

    /// <summary>Пользователь правил масштаб вручную — автоподстройка его не тронет.</summary>
    public bool PpmEdited => _ppmEdited;

    /// <summary>
    /// Пока масштаб не правили вручную, подставляем оценку: по найденному
    /// кругу миникарты, а если круг не найден — по размеру квадрата.
    /// </summary>
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
        PpmBox.Text = EstimatePxPerMeter(size).ToString("0.##", CultureInfo.CurrentCulture);
        _syncing = false;
        UpdateHint();
    }

    private void OnPpmChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncing)
        {
            _ppmEdited = true;
            UpdateHint();
        }
    }

    private double EstimatePxPerMeter(int size) =>
        _detectedDiameterPx > 0
            ? SessionSettings.EstimateFromDiameter(_detectedDiameterPx)
            : SessionSettings.EstimateScreenPxPerMeter(size);

    private void UpdateHint()
    {
        if (HintText is null)
        {
            return;
        }

        if (_ppmEdited)
        {
            HintText.Text =
                "Масштаб задан вручную: автоподстройка по найденному кругу его не изменит. " +
                "Уточнить масштаб по пройденному пути всё равно можно второй привязкой.";
            return;
        }

        if (_detectedDiameterPx > 0)
        {
            var ppm = SessionSettings.EstimateFromDiameter(_detectedDiameterPx);
            HintText.Text =
                $"Найден круг миникарты: {_detectedDiameterPx:0} px в поперечнике → " +
                $"{ppm:0.00} px/м. Масштаб подставлен автоматически; точное значение " +
                "уточняется после второй привязки к телепорту или снимку карты.";
            return;
        }

        HintText.Text =
            "Круг миникарты не найден, поэтому масштаб оценён по размеру квадрата: " +
            "93 м в поперечнике. Обведите круг целиком, с небольшими полями, " +
            "или задайте масштаб вручную.";
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
