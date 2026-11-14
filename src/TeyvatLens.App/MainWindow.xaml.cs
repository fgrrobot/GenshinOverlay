using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using TeyvatLens.Core;
using TeyvatLens.Core.App;
using TeyvatLens.Core.Models;
using TeyvatLens.Core.RealMap.GenshinMap;
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
    private readonly GenshinMapService _genshin = new(AppPaths.GenshinCacheDir);
    private BitmapSource? _atlas;
    private BitmapSource? _realAtlas;
    private long? _selectedMarkerId;
    private double _lastTick;
    private bool _suppressEvents = true;
    private bool _updating;
    private bool _busy;

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
        LoadRealAtlas();
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
        _genshin.Dispose();
        base.OnClosed(e);
    }

    private void LoadAtlas() => _atlas = LoadBitmap(AppPaths.DemoAtlasPath);

    private void LoadRealAtlas() => _realAtlas = LoadBitmap(_session.Settings.RealAtlasPath);

    private static BitmapSource? LoadBitmap(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
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
                return source;
            }
            finally
            {
                DeleteObject(handle);
            }
        }
        catch (Exception)
        {
            // Атлас не критичен: карта нарисует сетку.
            return null;
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
        RealMapCard.Visibility = screen ? Visibility.Visible : Visibility.Collapsed;
        DemoCard.Visibility = screen ? Visibility.Collapsed : Visibility.Visible;
        CaptureInfoText.Text = screen
            ? $"Область {settings.ScreenRect.X},{settings.ScreenRect.Y} · " +
              $"{settings.ScreenRect.Width}×{settings.ScreenRect.Height} px · " +
              $"{settings.ScreenPxPerMeter:F1} px/м"
            : "Синтетический мир 900×900 м: атлас, маршрут и объекты генерируются локально.";

        var profile = settings.RealProfile;
        RealMapInfoText.Text = !screen
            ? string.Empty
            : !profile.IsValid
                ? "Карта не загружена — показана сетка. Загрузите изображение карты и импортируйте точки."
                : $"{Path.GetFileName(profile.AtlasPath)} · {profile.PxPerMeter:0.###} px/м · " +
                  $"точек: {_session.CountRealMarkers()}";
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

            var settings = _session.Settings;
            if (snapshot.Mode == SessionMode.Demo)
            {
                Map.Atlas = _atlas;
                Map.AtlasPxPerMeter = 2.0;
                Map.AtlasOriginXMeters = 0;
                Map.AtlasOriginZMeters = 0;
                Map.WorldSizeM = 900.0;
            }
            else
            {
                Map.Atlas = _realAtlas;
                Map.AtlasPxPerMeter = settings.RealPxPerMeter;
                Map.AtlasOriginXMeters = settings.RealOriginXMeters;
                Map.AtlasOriginZMeters = settings.RealOriginZMeters;
                Map.WorldSizeM = _realAtlas is null
                    ? 900.0
                    : _realAtlas.PixelWidth / Math.Max(0.001, settings.RealPxPerMeter);
            }

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
        LoadWaypoints();
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

    private async void OnLoadFromGenshinMap(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        double? centerX = null;
        double? centerZ = null;
        if (WaypointCombo.SelectedItem is Marker selected)
        {
            centerX = selected.X;
            centerZ = selected.Z;
        }

        SetBusy(true, "Загружаю карту Тейвата…");

        try
        {
            var progress = new Progress<string>(text => RealMapInfoText.Text = text);
            var result = await _genshin.LoadAsync(centerX, centerZ, progress, CancellationToken.None);

            _session.ApplyRealMap(result.Profile, result.Markers.Markers);
            LoadRealAtlas();
            LoadWaypoints();
            _timer.Stop();
            UpdateUi(_session.CurrentSnapshot());

            var message =
                $"Загружено точек: {result.Markers.Total}.\n" +
                $"Фрагмент карты: {result.Region.Left}, {result.Region.Top} · {result.Region.SizePx} px.\n" +
                "Выберите телепорт и нажмите «Привязать позицию».";

            if (result.Markers.Warnings.Count > 0)
            {
                message += "\n" + string.Join("\n", result.Markers.Warnings.Take(4));
            }

            MessageBox.Show(
                message,
                "Teyvat Lens",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Не удалось загрузить карту:\n{exception.Message}",
                "Teyvat Lens",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, null);
            SyncModeUi();
        }
    }

    private void SetBusy(bool busy, string? message)
    {
        _busy = busy;
        AutoMapButton.IsEnabled = !busy;
        LoadMapButton.IsEnabled = !busy;
        ImportPointsButton.IsEnabled = !busy;

        if (message is not null)
        {
            RealMapInfoText.Text = message;
        }

        Mouse.OverrideCursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private void OnLoadRealMap(object sender, RoutedEventArgs e)
    {
        var dialog = new RealMapDialog(_session.Settings.RealProfile)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _session.SetRealMap(dialog.Profile);
            LoadRealAtlas();
            LoadWaypoints();
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

    private void OnImportPoints(object sender, RoutedEventArgs e)
    {
        if (!_session.Settings.RealProfile.IsValid)
        {
            MessageBox.Show(
                "Сначала загрузите карту Тейвата и задайте масштаб.",
                "Teyvat Lens",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Точки интерактивной карты",
            Filter = "JSON (*.json)|*.json|Все файлы (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var result = _session.ImportRealMarkers(dialog.FileName);
            LoadWaypoints();
            SyncModeUi();
            UpdateUi(_session.CurrentSnapshot());

            var message = result.Total == 0
                ? "В файле не найдено поддерживаемых точек."
                : $"Импортировано точек: {result.Total}.";
            if (result.Skipped > 0)
            {
                message += $"\nПропущено: {result.Skipped}.\n" +
                           string.Join("\n", result.Warnings.Take(5));
            }

            MessageBox.Show(
                message,
                "Teyvat Lens",
                MessageBoxButton.OK,
                result.Total == 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Не удалось импортировать точки:\n{exception.Message}",
                "Teyvat Lens",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void OnSnap(object sender, RoutedEventArgs e)
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

        // Если точка вне загруженного фрагмента карты — догружаем нужный квадрат.
        if (_session.Mode != SessionMode.Screen || _realAtlas is null || _busy)
        {
            return;
        }

        try
        {
            var profile = _session.Settings.RealProfile;
            var updated = await _genshin.EnsureRegionAsync(
                profile,
                _realAtlas.PixelWidth,
                waypoint.X,
                waypoint.Z,
                null,
                CancellationToken.None);

            if (updated is null)
            {
                return;
            }

            _session.UpdateRealAtlas(updated);
            LoadRealAtlas();
            SyncModeUi();
            UpdateUi(_session.CurrentSnapshot());
        }
        catch (Exception exception)
        {
            RealMapInfoText.Text = $"Карта не догрузилась: {exception.Message}";
        }
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
