using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeyvatLens.Core;
using TeyvatLens.Core.App;
using TeyvatLens.Core.Models;
using CoreStatus = TeyvatLens.Core.Models.StatusText;

namespace TeyvatLens.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<MarkerRow> _rows = new();
    private readonly Dictionary<long, MarkerRow> _rowsById = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _radiusTimer;

    private readonly AppSession _session;
    private BitmapSource? _atlas;
    private long? _selectedMarkerId;
    private double _lastTick;
    private bool _suppressEvents = true;
    private bool _updating;

    public MainWindow(AppSession session)
    {
        InitializeComponent();

        _session = session;

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(66)
        };
        _timer.Tick += OnTick;

        _radiusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _radiusTimer.Tick += OnRadiusCommit;

        MarkerList.ItemsSource = _rows;

        LoadAtlas();
        BuildKindFilter(session.Settings.KindFilter);
        LoadWaypoints();
        ApplySettingsToUi();

        _suppressEvents = false;
        UpdateUi(_session.CurrentSnapshot());
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _radiusTimer.Stop();
        base.OnClosed(e);
    }

    private void LoadAtlas()
    {
        var path = AppPaths.DemoAtlasPath;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using var bitmap = new System.Drawing.Bitmap(path);
            var handle = bitmap.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    handle,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                _atlas = source;
            }
            finally
            {
                DeleteObject(handle);
            }
        }
        catch (Exception)
        {
            // Атлас не критичен: карта нарисует сетку.
            _atlas = null;
        }
    }

    private void BuildKindFilter(MarkerKind? selected)
    {
        var options = new List<KindOption> { new(null, "Все типы") };
        options.AddRange(
            MarkerKinds.All.Values
                .Where(info => info.IsTarget)
                .OrderByDescending(info => info.Priority)
                .Select(info => new KindOption(info.Kind, info.NameRu)));

        KindFilterCombo.ItemsSource = options;
        KindFilterCombo.SelectedItem = options.FirstOrDefault(item => item.Kind == selected) ?? options[0];
    }

    private void LoadWaypoints()
    {
        var waypoints = _session.GetWaypoints();
        WaypointCombo.ItemsSource = waypoints;
        if (waypoints.Count > 0)
        {
            WaypointCombo.SelectedIndex = 0;
        }
    }

    private void ApplySettingsToUi()
    {
        var settings = _session.Settings;

        DemoModeRadio.IsChecked = settings.Mode == SessionMode.Demo;
        ScreenModeRadio.IsChecked = settings.Mode == SessionMode.Screen;

        RadiusSlider.Value = settings.RadiusM;
        RadiusValueText.Text = $"{settings.RadiusM:F0} м";

        SyncModeUi();
    }

    private void SyncModeUi()
    {
        var settings = _session.Settings;
        var screen = settings.Mode == SessionMode.Screen;

        CalibrateButton.Visibility = screen ? Visibility.Visible : Visibility.Collapsed;
        CaptureInfoText.Text = screen
            ? $"Область {settings.ScreenRect.X},{settings.ScreenRect.Y} · " +
              $"{settings.ScreenRect.Width}×{settings.ScreenRect.Height} px · " +
              $"{settings.ScreenPxPerMeter:F1} px/м"
            : "Синтетический мир 900×900 м: атлас, маршрут и объекты генерируются локально.";
    }

    private void UpdateUi(SessionSnapshot snapshot)
    {
        if (_updating)
        {
            return;
        }

        _updating = true;
        try
        {
            StartButton.Content = snapshot.IsRunning
                ? "Пауза"
                : snapshot.IsPaused ? "Продолжить" : "Старт";

            ApplyStatus(snapshot.Status);
            StatusLabel.Text = CoreStatus.Label(snapshot.Status);

            ModeText.Text = CoreStatus.ModeLabel(snapshot.Mode);
            CoordsText.Text = BuildCoords(snapshot);

            if (snapshot.Hint is { } hint)
            {
                HintText.Text =
                    $"{hint.Target.Marker.Name} · {hint.Target.DistanceM:F0} м · " +
                    $"{hint.DirectionRu} · {hint.HeightHint}";
                HintArrow.Visibility = Visibility.Visible;
                HintArrow.RenderTransform = new RotateTransform(hint.RelativeBearingDeg - 90);
            }
            else
            {
                HintText.Text = snapshot.Geo is null ? "Нет привязки позиции" : "Целей рядом нет";
                HintArrow.Visibility = Visibility.Collapsed;
            }

            Compass.HeadingDeg = snapshot.Geo?.HeadingDeg ?? 0;
            Compass.TargetBearingDeg = snapshot.Hint?.Target.BearingDeg;

            Map.Atlas = snapshot.Mode == SessionMode.Demo ? _atlas : null;
            Map.Snapshot = snapshot;

            NearbyCountText.Text = snapshot.Markers.Count == 0
                ? "—"
                : snapshot.Markers.Count.ToString(CultureInfo.CurrentCulture);

            FooterLeft.Text =
                $"{snapshot.TotalMarkers} маркеров · собрано {snapshot.CollectedMarkers} · " +
                $"кадр {snapshot.FrameMs:F1} мс";
            FooterRight.Text = snapshot.Message;

            RadiusValueText.Text = $"{snapshot.RadiusM:F0} м";
            if (Math.Abs(RadiusSlider.Value - snapshot.RadiusM) > 0.5)
            {
                RadiusSlider.Value = snapshot.RadiusM;
            }

            LostSwitch.IsChecked = snapshot.DemoLost;

            SnapStatusText.Text = snapshot.RequiresSnap
                ? "Требуется привязка: выберите телепорт и нажмите «Привязать позицию»."
                : snapshot.Geo is null
                    ? "Позиция ещё не определена."
                    : "Позиция привязана.";
            SnapStatusText.Foreground = snapshot.RequiresSnap
                ? (Brush)FindResource("DangerBrush")
                : (Brush)FindResource("MutedBrush");

            SyncMarkers(snapshot);
            UpdateCollectButton();
        }
        finally
        {
            _updating = false;
        }
    }

    private void ApplyStatus(TrackingStatus status)
    {
        var (dot, pill, border) = status switch
        {
            TrackingStatus.Tracking => ("SuccessBrush", "SuccessSoftBrush", "#2C4A3A"),
            TrackingStatus.Degraded => ("GoldBrush", "GoldSoftBrush", "#4A3F26"),
            TrackingStatus.Lost => ("DangerBrush", "DangerSoftBrush", "#4A2A31"),
            _ => ("MutedBrush", "PanelAltBrush", "#2A3342")
        };

        StatusDot.Fill = (Brush)FindResource(dot);
        StatusPill.Background = (Brush)FindResource(pill);
        StatusPill.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(border));
    }

    private static string BuildCoords(SessionSnapshot snapshot)
    {
        if (snapshot.Geo is not { } geo)
        {
            return "Позиция не определена";
        }

        var text = $"X {geo.X:F0} · Z {geo.Z:F0} · уверенность {geo.Confidence:P0}";
        if (snapshot.TruthErrorM is { } error)
        {
            text += $" · ошибка {error:F1} м";
        }

        return text;
    }

    private void SyncMarkers(SessionSnapshot snapshot)
    {
        var heading = snapshot.Geo?.HeadingDeg ?? 0;
        var ids = snapshot.Markers.Select(item => item.Marker.Id).ToList();

        var sameOrder = ids.Count == _rows.Count;
        if (sameOrder)
        {
            for (var index = 0; index < ids.Count; index++)
            {
                if (_rows[index].Id != ids[index])
                {
                    sameOrder = false;
                    break;
                }
            }
        }

        if (!sameOrder)
        {
            _rows.Clear();
            _rowsById.Clear();

            foreach (var item in snapshot.Markers)
            {
                var row = new MarkerRow(item, heading);
                _rows.Add(row);
                _rowsById[row.Id] = row;
            }

            RestoreSelection();
            return;
        }

        foreach (var item in snapshot.Markers)
        {
            if (_rowsById.TryGetValue(item.Marker.Id, out var row))
            {
                row.Update(item, heading);
            }
        }
    }

    private void RestoreSelection()
    {
        if (_selectedMarkerId is null)
        {
            MarkerList.SelectedItem = null;
            return;
        }

        if (_rowsById.TryGetValue(_selectedMarkerId.Value, out var row))
        {
            MarkerList.SelectedItem = row;
            return;
        }

        _selectedMarkerId = null;
        MarkerList.SelectedItem = null;
        _session.SelectMarker(null);
    }

    private void UpdateCollectButton()
    {
        var row = MarkerList.SelectedItem as MarkerRow;
        CollectButton.IsEnabled = row is not null
            && MarkerKinds.Info(row.Kind).IsTarget
            && !row.IsCollected;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - _lastTick, 0, 0.5);
        _lastTick = now;

        var snapshot = _session.Tick(dt);
        UpdateUi(snapshot);

        if (!snapshot.IsRunning)
        {
            _timer.Stop();
        }
    }

    private void OnStartPause(object sender, RoutedEventArgs e)
    {
        if (_session.IsRunning)
        {
            _session.Pause();
            _timer.Stop();
            UpdateUi(_session.CurrentSnapshot());
            return;
        }

        if (!_session.StartOrResume(out var error))
        {
            MessageBox.Show(
                error ?? "Не удалось запустить сеанс.",
                "Teyvat Lens",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _lastTick = _clock.Elapsed.TotalSeconds;
        _timer.Start();
        UpdateUi(_session.CurrentSnapshot());
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _updating)
        {
            return;
        }

        var mode = ScreenModeRadio.IsChecked == true ? SessionMode.Screen : SessionMode.Demo;
        _session.SetMode(mode);
        _timer.Stop();
        SyncModeUi();
        UpdateUi(_session.CurrentSnapshot());
    }

    private void OnCalibrate(object sender, RoutedEventArgs e)
    {
        var dialog = new CalibrationDialog(
            _session.Settings.ScreenRect,
            _session.Settings.ScreenPxPerMeter)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _session.ConfigureScreen(dialog.Rectangle, dialog.PxPerMeter);
            _timer.Stop();
            SyncModeUi();
            UpdateUi(_session.CurrentSnapshot());
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(
                exception.Message,
                "Teyvat Lens",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnSnap(object sender, RoutedEventArgs e)
    {
        if (WaypointCombo.SelectedItem is not Marker waypoint)
        {
            MessageBox.Show(
                "Нет доступных точек телепорта.",
                "Teyvat Lens",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _session.SnapTo(waypoint.Id);
        UpdateUi(_session.CurrentSnapshot());
    }

    private void OnRadiusChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        RadiusValueText.Text = $"{e.NewValue:F0} м";
        if (_suppressEvents || _updating)
        {
            return;
        }

        _radiusTimer.Stop();
        _radiusTimer.Start();
    }

    private void OnRadiusCommit(object? sender, EventArgs e)
    {
        _radiusTimer.Stop();
        _session.SetRadius(RadiusSlider.Value);
        UpdateUi(_session.CurrentSnapshot());
    }

    private void OnKindFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _updating || KindFilterCombo.SelectedItem is not KindOption option)
        {
            return;
        }

        _session.SetKindFilter(option.Kind);
        UpdateUi(_session.CurrentSnapshot());
    }

    private void OnMarkerSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        var row = MarkerList.SelectedItem as MarkerRow;
        _selectedMarkerId = row?.Id;
        _session.SelectMarker(_selectedMarkerId);
        UpdateCollectButton();
    }

    private void OnCollect(object sender, RoutedEventArgs e)
    {
        if (MarkerList.SelectedItem is not MarkerRow row)
        {
            return;
        }

        _session.MarkCollected(row.Id);
        UpdateUi(_session.CurrentSnapshot());
    }

    private void OnLostToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _updating)
        {
            return;
        }

        _session.SetDemoLost(LostSwitch.IsChecked == true);
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Сбросить все отметки «собрано»?",
            "Teyvat Lens",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _session.ResetProgress();
        UpdateUi(_session.CurrentSnapshot());
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    private sealed class KindOption
    {
        public KindOption(MarkerKind? kind, string label)
        {
            Kind = kind;
            Label = label;
        }

        public MarkerKind? Kind { get; }

        public string Label { get; }

        public override string ToString() => Label;
    }
}
