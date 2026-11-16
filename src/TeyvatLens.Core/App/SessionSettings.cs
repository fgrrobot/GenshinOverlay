using System.Drawing;
using TeyvatLens.Core.Models;
using TeyvatLens.Core.RealMap;

namespace TeyvatLens.Core.App;

/// <summary>Пользовательские настройки, сохраняемые в SQLite.</summary>
public sealed class SessionSettings
{
    /// <summary>
    /// Радиус обзора миникарты в мире, м. Оценка по сообществу: радиус
    /// компаса искателя сокровищ (~170 м) примерно на 82 % больше радиуса
    /// миникарты. Нужен только как стартовое приближение — точный масштаб
    /// уточняется автоматически после второй привязки к телепорту.
    /// </summary>
    public const double MinimapViewRadiusM = 93.0;

    public double RadiusM { get; set; } = 120.0;

    public MarkerKind? KindFilter { get; set; }

    /// <summary>По умолчанию приложение работает с реальной картой и экраном игры.</summary>
    public SessionMode Mode { get; set; } = SessionMode.Screen;

    public Rectangle ScreenRect { get; set; } = new(24, 24, 240, 240);

    /// <summary>Сколько пикселей миникарты приходится на метр мира.</summary>
    public double ScreenPxPerMeter { get; set; } = EstimateScreenPxPerMeter(240);

    /// <summary>Оценка масштаба миникарты по размеру квадрата захвата.</summary>
    public static double EstimateScreenPxPerMeter(int captureSizePx) =>
        Math.Clamp(captureSizePx / 2.0 / MinimapViewRadiusM, 0.2, 20.0);

    /// <summary>Путь к изображению реальной карты (скриншот интерактивной карты).</summary>
    public string RealAtlasPath { get; set; } = string.Empty;

    /// <summary>Сколько пикселей атласа приходится на один метр мира.</summary>
    public double RealPxPerMeter { get; set; } = 1.0;

    /// <summary>Мировые координаты левого верхнего пикселя атласа.</summary>
    public double RealOriginXMeters { get; set; }

    public double RealOriginZMeters { get; set; }

    public MapProfile RealProfile =>
        new(RealAtlasPath, RealPxPerMeter, RealOriginXMeters, RealOriginZMeters);

    public static SessionSettings Load(Data.Database database)
    {
        var settings = new SessionSettings();

        if (double.TryParse(database.GetSetting("radius_m"), out var radius))
        {
            settings.RadiusM = Math.Clamp(radius, 30, 250);
        }

        var kind = database.GetSetting("kind_filter");
        if (!string.IsNullOrWhiteSpace(kind) && !string.Equals(kind, "all", StringComparison.Ordinal))
        {
            try
            {
                settings.KindFilter = MarkerKinds.FromCode(kind);
            }
            catch (ArgumentException)
            {
                settings.KindFilter = null;
            }
        }

        var mode = database.GetSetting("mode");
        settings.Mode = string.Equals(mode, "demo", StringComparison.OrdinalIgnoreCase)
            ? SessionMode.Demo
            : SessionMode.Screen;

        // Один раз переводим существующие установки на реальный режим по умолчанию.
        if (!string.Equals(database.GetSetting("real_default_v1"), "1", StringComparison.Ordinal))
        {
            settings.Mode = SessionMode.Screen;
            database.SetSetting("real_default_v1", "1");
        }

        var rect = database.GetSetting("screen_rect");
        if (!string.IsNullOrWhiteSpace(rect))
        {
            var parts = rect.Split(',');
            if (parts.Length == 4
                && int.TryParse(parts[0], out var x)
                && int.TryParse(parts[1], out var y)
                && int.TryParse(parts[2], out var width)
                && int.TryParse(parts[3], out var height)
                && width >= 32
                && height >= 32)
            {
                settings.ScreenRect = new Rectangle(x, y, width, height);
            }
        }

        if (double.TryParse(database.GetSetting("screen_ppm"), out var ppm) && ppm > 0)
        {
            settings.ScreenPxPerMeter = Math.Clamp(ppm, 0.2, 20.0);
        }

        // Прежний default 2.0 px/м был завышен примерно в 1,5 раза: миникарта
        // показывает около 93 м в радиусе. Один раз пересчитываем по размеру
        // области, если пользователь не задавал собственное значение.
        if (!string.Equals(database.GetSetting("screen_ppm_v2"), "1", StringComparison.Ordinal))
        {
            if (Math.Abs(settings.ScreenPxPerMeter - 2.0) < 1e-6)
            {
                settings.ScreenPxPerMeter = EstimateScreenPxPerMeter(settings.ScreenRect.Width);
            }

            database.SetSetting("screen_ppm_v2", "1");
        }

        settings.RealAtlasPath = database.GetSetting("real_atlas_path") ?? string.Empty;
        if (double.TryParse(database.GetSetting("real_ppm"), out var realPpm) && realPpm > 0)
        {
            settings.RealPxPerMeter = Math.Clamp(realPpm, 0.001, 1000.0);
        }

        if (double.TryParse(database.GetSetting("real_origin_x"), out var originX))
        {
            settings.RealOriginXMeters = originX;
        }

        if (double.TryParse(database.GetSetting("real_origin_z"), out var originZ))
        {
            settings.RealOriginZMeters = originZ;
        }

        return settings;
    }

    public void Save(Data.Database database)
    {
        database.SetSetting("radius_m", RadiusM.ToString("0.##"));
        database.SetSetting("kind_filter", KindFilter is null ? "all" : MarkerKinds.ToCode(KindFilter.Value));
        database.SetSetting("mode", Mode == SessionMode.Demo ? "demo" : "screen");
        database.SetSetting(
            "screen_rect",
            $"{ScreenRect.X},{ScreenRect.Y},{ScreenRect.Width},{ScreenRect.Height}");
        database.SetSetting("screen_ppm", ScreenPxPerMeter.ToString("0.##"));
        database.SetSetting("real_atlas_path", RealAtlasPath);
        database.SetSetting("real_ppm", RealPxPerMeter.ToString("0.####"));
        database.SetSetting("real_origin_x", RealOriginXMeters.ToString("0.##"));
        database.SetSetting("real_origin_z", RealOriginZMeters.ToString("0.##"));
    }
}
