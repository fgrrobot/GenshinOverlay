namespace TeyvatLens.Core.Models;

/// <summary>Режим работы локализации (см. PROJECT.md §5.6).</summary>
public enum TrackingStatus
{
    Idle,
    Tracking,
    Degraded,
    Lost
}

/// <summary>Источник данных о позиции.</summary>
public enum SessionMode
{
    Demo,
    Screen
}

public static class StatusText
{
    public static string Label(TrackingStatus status) => status switch
    {
        TrackingStatus.Idle => "Пауза",
        TrackingStatus.Tracking => "Tracking",
        TrackingStatus.Degraded => "Degraded",
        TrackingStatus.Lost => "Lost",
        _ => status.ToString()
    };

    public static string Hint(TrackingStatus status) => status switch
    {
        TrackingStatus.Idle => "Приложение остановлено",
        TrackingStatus.Tracking => "Позиция определена",
        TrackingStatus.Degraded => "Позиция уточняется",
        TrackingStatus.Lost => "Позиция неизвестна",
        _ => string.Empty
    };

    public static string ModeLabel(SessionMode mode) => mode switch
    {
        SessionMode.Demo => "Демо-мир",
        SessionMode.Screen => "Реальный мир",
        _ => mode.ToString()
    };
}

/// <summary>Полный снимок состояния для отрисовки одного кадра интерфейса.</summary>
public sealed record SessionSnapshot(
    SessionMode Mode,
    TrackingStatus Status,
    GeoState? Geo,
    IReadOnlyList<NearbyMarker> Markers,
    NavigationHint? Hint,
    long? SelectedMarkerId,
    double? TruthErrorM,
    double FrameMs,
    string Message,
    double RadiusM,
    bool IsRunning,
    bool IsPaused,
    bool DemoLost,
    bool RequiresSnap,
    double? PlayerY,
    long TotalMarkers,
    long CollectedMarkers)
{
    public static SessionSnapshot Empty(SessionMode mode, double radiusM) =>
        new(
            mode,
            TrackingStatus.Idle,
            null,
            Array.Empty<NearbyMarker>(),
            null,
            null,
            null,
            0,
            "Нажмите «Старт»",
            radiusM,
            false,
            false,
            false,
            false,
            null,
            0,
            0);
}
