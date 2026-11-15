using System.Diagnostics;
using System.Drawing;
using OpenCvSharp;
using TeyvatLens.Core.Data;
using TeyvatLens.Core.Geometry;
using TeyvatLens.Core.Localization;
using TeyvatLens.Core.Models;
using TeyvatLens.Core.Navigation;
using TeyvatLens.Core.RealMap;
using TeyvatLens.Core.Simulation;
using TeyvatLens.Core.Vision;

namespace TeyvatLens.Core.App;

/// <summary>
/// Прикладной сценарий: связывает локализацию, базу и подсказки.
/// Интерфейс не знает, откуда пришёл кадр — из демо-мира или с экрана.
/// </summary>
public sealed class AppSession : IDisposable
{
    private readonly Database _database;
    private readonly DemoWorld _demo;
    private readonly SessionSettings _settings;

    private OdometryTracker _tracker;
    private ScreenCapture? _capture;

    private IReadOnlyList<NearbyMarker> _markers = Array.Empty<NearbyMarker>();
    private NavigationHint? _hint;
    private GeoState? _geo;
    private TrackingStatus _status = TrackingStatus.Idle;
    private string _message = "Нажмите «Старт»";
    private long? _selectedMarkerId;
    private double? _truthErrorM;
    private double _frameMs;
    private double _headingSmooth;
    private bool _running;
    private bool _paused;
    private bool _hasStarted;
    private bool _requiresSnap;

    public AppSession(Database database, string demoAtlasPath, SessionSettings settings)
    {
        _database = database;
        _settings = settings;
        _demo = new DemoWorld(demoAtlasPath);
        _tracker = CreateTracker();
    }

    public SessionMode Mode => _settings.Mode;

    public SessionSettings Settings => _settings;

    public bool IsRunning => _running;

    public bool IsPaused => _paused;

    public IReadOnlyList<Marker> GetWaypoints() => _database.GetWaypoints(CurrentSource);

    private string CurrentSource =>
        _settings.Mode == SessionMode.Demo ? MarkerSources.Synthetic : MarkerSources.Real;

    /// <summary>Запускает сценарий с нуля или продолжает после паузы.</summary>
    public bool StartOrResume(out string? error)
    {
        error = null;

        if (_hasStarted && _paused)
        {
            _paused = false;
            _running = true;
            _message = _settings.Mode == SessionMode.Demo
                ? "Демо-мир продолжен"
                : "Захват продолжен";
            return true;
        }

        if (_settings.Mode == SessionMode.Screen && _settings.ScreenRect.Width < 32)
        {
            error = "Сначала настройте область захвата миникарты.";
            return false;
        }

        _tracker.Dispose();
        _tracker = CreateTracker();
        _capture = _settings.Mode == SessionMode.Screen
            ? new ScreenCapture(_settings.ScreenRect, _settings.ScreenPxPerMeter)
            : null;

        if (_settings.Mode == SessionMode.Demo)
        {
            _demo.Reset();
            _tracker.Snap(_demo.TruthX, _demo.TruthZ, _demo.TruthHeadingDeg);
            _headingSmooth = _demo.TruthHeadingDeg;
            _status = TrackingStatus.Tracking;
            _message = "Демо-мир запущен";
        }
        else
        {
            _tracker.Reset();
            _headingSmooth = 0;
            _status = TrackingStatus.Lost;
            _message = "Выберите телепорт на карте и нажмите «Привязать позицию»";
        }

        _running = true;
        _paused = false;
        _hasStarted = true;
        _selectedMarkerId = null;
        _markers = Array.Empty<NearbyMarker>();
        _hint = null;
        _truthErrorM = null;
        return true;
    }

    public void Pause()
    {
        _running = false;
        _paused = true;
        _status = TrackingStatus.Idle;
        _message = "Пауза";
    }

    public void SetMode(SessionMode mode)
    {
        if (_settings.Mode == mode)
        {
            return;
        }

        _settings.Mode = mode;
        _settings.Save(_database);

        _running = false;
        _paused = false;
        _hasStarted = false;
        _capture = null;
        _tracker.Reset();
        _markers = Array.Empty<NearbyMarker>();
        _hint = null;
        _geo = null;
        _selectedMarkerId = null;
        _truthErrorM = null;
        _status = TrackingStatus.Idle;
        _message = mode == SessionMode.Demo
            ? "Демо-мир готов к запуску"
            : "Загрузите карту Тейвата, импортируйте точки и привяжитесь к телепорту";
    }

    /// <summary>Обрабатывает один кадр. Возвращает снимок для интерфейса.</summary>
    public SessionSnapshot Tick(double dt)
    {
        if (!_running)
        {
            return BuildSnapshot();
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            Mat frame;
            if (_settings.Mode == SessionMode.Demo)
            {
                _demo.Advance(dt);
                _tracker.SetHeading(_demo.TruthHeadingDeg);
                frame = _demo.RenderMinimap();
            }
            else
            {
                if (_capture is null)
                {
                    throw new InvalidOperationException("Область захвата не настроена");
                }

                frame = _capture.Grab();
            }

            using (frame)
            using (var normalized = MinimapNormalizer.Normalize(frame))
            {
                Apply(_tracker.Update(normalized, dt));
            }

            _truthErrorM = _settings.Mode == SessionMode.Demo && _geo is not null
                ? GeoMath.Distance(_geo.X, _geo.Z, _demo.TruthX, _demo.TruthZ)
                : null;

            RefreshMarkers();
        }
        catch (Exception exception)
        {
            _status = TrackingStatus.Lost;
            _message = $"Ошибка захвата: {exception.Message}";
            _running = false;
            _paused = false;
            _geo = null;
            _markers = Array.Empty<NearbyMarker>();
            _hint = null;
            _truthErrorM = null;
        }

        _frameMs = stopwatch.Elapsed.TotalMilliseconds;
        return BuildSnapshot();
    }

    public SessionSnapshot CurrentSnapshot() => BuildSnapshot();

    public void SelectMarker(long? markerId) => _selectedMarkerId = markerId;

    public void MarkCollected(long markerId)
    {
        var marker = _database.GetMarker(markerId);
        if (marker is null)
        {
            return;
        }

        if (!MarkerKinds.Info(marker.Kind).IsTarget)
        {
            _message = "Телепорт нельзя отметить собранным";
            return;
        }

        _database.MarkCollected(markerId);
        _message = $"Собрано: {marker.Name}";
        RefreshMarkers();
    }

    public bool SnapTo(long waypointId)
    {
        var marker = _database.GetMarker(waypointId);
        if (marker is null)
        {
            _message = "Точка привязки не найдена";
            return false;
        }

        if (_settings.Mode == SessionMode.Demo)
        {
            _demo.TeleportTo(marker.X, marker.Z);
            _tracker.Snap(marker.X, marker.Z, _demo.TruthHeadingDeg);
            _headingSmooth = _demo.TruthHeadingDeg;
        }
        else
        {
            _tracker.Snap(marker.X, marker.Z);
            _headingSmooth = _tracker.HeadingDeg;
        }

        _status = TrackingStatus.Tracking;
        _requiresSnap = false;
        _message = $"Позиция привязана: {marker.Name}";

        // Показываем позицию сразу после привязки, не дожидаясь первого кадра.
        _geo = new GeoState(
            marker.X,
            marker.Z,
            _settings.Mode == SessionMode.Demo ? 0.0 : null,
            _headingSmooth,
            1.0,
            "snap",
            0,
            null,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);

        RefreshMarkers();
        return true;
    }

    public void SetRadius(double radiusM)
    {
        _settings.RadiusM = Math.Clamp(radiusM, 30, 250);
        _settings.Save(_database);
        RefreshMarkers();
    }

    public void SetKindFilter(MarkerKind? kind)
    {
        _settings.KindFilter = kind;
        _settings.Save(_database);
        RefreshMarkers();
    }

    public void ConfigureScreen(Rectangle rectangle, double pxPerMeter)
    {
        if (rectangle.Width < 32 || rectangle.Height < 32)
        {
            throw new ArgumentException("Область захвата должна быть не меньше 32x32 пикселей");
        }

        if (pxPerMeter <= 0)
        {
            throw new ArgumentException("Масштаб должен быть больше нуля");
        }

        _settings.ScreenRect = rectangle;
        _settings.ScreenPxPerMeter = pxPerMeter;
        _settings.Save(_database);

        _running = false;
        _paused = false;
        _hasStarted = false;
        _capture = null;
        _tracker.Dispose();
        _tracker = CreateTracker();
        _tracker.Reset();
        _status = TrackingStatus.Idle;
        _message = "Настройки захвата сохранены";
        _markers = Array.Empty<NearbyMarker>();
        _hint = null;
        _geo = null;
    }

    /// <summary>Сохраняет привязку реального атласа и сбрасывает текущую сессию.</summary>
    public void SetRealMap(MapProfile profile)
    {
        if (!profile.IsValid)
        {
            throw new ArgumentException("Укажите файл карты и положительный масштаб");
        }

        _settings.RealAtlasPath = profile.AtlasPath;
        _settings.RealPxPerMeter = profile.PxPerMeter;
        _settings.RealOriginXMeters = profile.OriginXMeters;
        _settings.RealOriginZMeters = profile.OriginZMeters;
        _settings.Save(_database);

        ResetSessionState("Карта Тейвата загружена — импортируйте точки и привяжитесь к телепорту");
    }

    /// <summary>Импортирует точки интерактивной карты и заменяет предыдущий набор.</summary>
    public ImportResult ImportRealMarkers(string jsonPath)
    {
        if (!_settings.RealProfile.IsValid)
        {
            throw new InvalidOperationException("Сначала загрузите карту Тейвата и задайте масштаб");
        }

        var result = WaypointImporter.LoadFile(jsonPath, _settings.RealProfile);
        _database.ReplaceMarkers(MarkerSources.Real, result.Markers);

        ResetSessionState(result.Total == 0
            ? "В файле не найдено поддерживаемых точек"
            : $"Импортировано точек: {result.Total} — привяжитесь к телепорту");

        return result;
    }

    /// <summary>Применяет скачанную с интерактивной карты карту и её точки за один шаг.</summary>
    public void ApplyRealMap(MapProfile profile, IReadOnlyList<ImportedMarker> markers)
    {
        if (!profile.IsValid)
        {
            throw new ArgumentException("Укажите файл карты и положительный масштаб");
        }

        _settings.RealAtlasPath = profile.AtlasPath;
        _settings.RealPxPerMeter = profile.PxPerMeter;
        _settings.RealOriginXMeters = profile.OriginXMeters;
        _settings.RealOriginZMeters = profile.OriginZMeters;
        _settings.Save(_database);

        _database.ReplaceMarkers(MarkerSources.Real, markers);

        ResetSessionState(markers.Count == 0
            ? "Карта загружена, но подходящих точек не найдено"
            : $"Загружено с карты Тейвата: {markers.Count} точек — привяжитесь к телепорту");
    }

    /// <summary>Меняет только фрагмент карты, не сбрасывая текущую привязку.</summary>
    public void UpdateRealAtlas(MapProfile profile)
    {
        if (!profile.IsValid)
        {
            return;
        }

        _settings.RealAtlasPath = profile.AtlasPath;
        _settings.RealPxPerMeter = profile.PxPerMeter;
        _settings.RealOriginXMeters = profile.OriginXMeters;
        _settings.RealOriginZMeters = profile.OriginZMeters;
        _settings.Save(_database);
    }

    public long CountRealMarkers() => _database.CountMarkers(MarkerSources.Real);

    private void ResetSessionState(string message)
    {
        _running = false;
        _paused = false;
        _hasStarted = false;
        _capture = null;
        _tracker.Reset();
        _geo = null;
        _selectedMarkerId = null;
        _markers = Array.Empty<NearbyMarker>();
        _hint = null;
        _truthErrorM = null;
        _requiresSnap = false;
        _status = TrackingStatus.Idle;
        _message = message;
    }

    public void SetDemoLost(bool lost)
    {
        _demo.Lost = lost;
        _message = lost
            ? "Сигнал миникарты отключён"
            : "Сигнал восстановлен — привяжите позицию к телепорту";
    }

    public void ResetProgress()
    {
        _database.ResetProgress();
        _message = "Прогресс сброшен";
        RefreshMarkers();
    }

    private OdometryTracker CreateTracker()
    {
        var pxPerMeter = _settings.Mode == SessionMode.Demo
            ? _demo.PxPerMeter
            : _settings.ScreenPxPerMeter;
        return new OdometryTracker(pxPerMeter)
        {
            // В демо курс известен точно, в реальном режиме его приходится
            // выводить из направления движения.
            AutoHeading = _settings.Mode != SessionMode.Demo
        };
    }

    private void Apply(LocalizationResult result)
    {
        _status = result.Status;
        _message = result.Message;
        _requiresSnap = result.RequiresSnap;

        if (result.X is null || result.Z is null)
        {
            _geo = null;
            return;
        }

        // Сглаживаем курс, чтобы карта не дёргалась на поворотах маршрута.
        _headingSmooth = SmoothAngle(_headingSmooth, result.HeadingDeg, 0.28);

        _geo = new GeoState(
            result.X.Value,
            result.Z.Value,
            _settings.Mode == SessionMode.Demo ? 0.0 : null,
            _headingSmooth,
            result.Confidence,
            "odometry",
            0,
            null,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
    }

    private static double SmoothAngle(double current, double target, double factor)
    {
        var delta = GeoMath.AngleDelta(target, current);
        return GeoMath.NormalizeAngle(current + delta * factor);
    }

    private void RefreshMarkers()
    {
        var show = _geo is not null
            && (_status == TrackingStatus.Tracking || _status == TrackingStatus.Degraded);

        if (!show)
        {
            _markers = Array.Empty<NearbyMarker>();
            _hint = null;
            return;
        }

        _markers = _database.QueryNearby(
            _geo!.X,
            _geo.Z,
            _settings.RadiusM,
            kindFilter: _settings.KindFilter,
            source: CurrentSource);
        _hint = HintBuilder.BuildHint(_markers, _geo.HeadingDeg, _geo.Y);
    }

    private SessionSnapshot BuildSnapshot()
    {
        var show = (_status == TrackingStatus.Tracking || _status == TrackingStatus.Degraded)
            && _geo is not null;

        return new SessionSnapshot(
            _settings.Mode,
            _status,
            _geo,
            show ? _markers : Array.Empty<NearbyMarker>(),
            show ? _hint : null,
            _selectedMarkerId,
            _truthErrorM,
            _frameMs,
            _message,
            _settings.RadiusM,
            _running,
            _paused,
            _demo.Lost,
            _requiresSnap,
            _settings.Mode == SessionMode.Demo ? 0.0 : null,
            _database.CountMarkers(CurrentSource),
            _database.CountCollected(CurrentSource));
    }

    public void Dispose()
    {
        _tracker.Dispose();
        _demo.Dispose();
        _database.Dispose();
    }
}
