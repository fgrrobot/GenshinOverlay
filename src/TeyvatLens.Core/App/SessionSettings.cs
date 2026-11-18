using System.Drawing;
using System.Globalization;
using TeyvatLens.Core.Models;
using TeyvatLens.Core.RealMap;

namespace TeyvatLens.Core.App;

/// <summary>Пользовательские настройки, сохраняемые в SQLite.</summary>
public sealed class SessionSettings
{
    /// <summary>
    /// Поперечник обзора миникарты в мире, м. Квадрат захвата обводится вокруг
    /// миникарты, поэтому его ширина примерно равна этому поперечнику.
    ///
    /// Раньше здесь хранился радиус 93 м, и метка в приложении двигалась ровно
    /// в 2 раза медленнее игрока: на самом деле ~93 м — это диаметр обзора.
    /// Значение остаётся лишь стартовым: после второй привязки масштаб
    /// уточняется автоматически по пройденному пути.
    /// </summary>
    public const double MinimapViewDiameterM = 93.0;

    public double RadiusM { get; set; } = 120.0;

    public MarkerKind? KindFilter { get; set; }

    /// <summary>По умолчанию приложение работает с реальной картой и экраном игры.</summary>
    public SessionMode Mode { get; set; } = SessionMode.Screen;

    public Rectangle ScreenRect { get; set; } = new(24, 24, 240, 240);

    /// <summary>Сколько пикселей миникарты приходится на метр мира.</summary>
    public double ScreenPxPerMeter { get; set; } = EstimateScreenPxPerMeter(240);

    /// <summary>
    /// Масштаб задан вручную в диалоге калибровки. Автоподстройка по
    /// найденному кругу миникарты его не трогает.
    /// </summary>
    public bool ScreenScaleManual { get; set; }

    /// <summary>
    /// Масштаб уточнён по пройденному пути между двумя привязками. Это самый
    /// точный источник, поэтому он тоже защищён от автоподстройки.
    /// </summary>
    public bool ScreenScaleCalibrated { get; set; }

    /// <summary>
    /// Поперечник найденного круга миникарты, px. Ноль — круг ещё не найден.
    /// </summary>
    public double ScreenCircleDiameterPx { get; set; }

    /// <summary>
    /// Грубая оценка масштаба по размеру квадрата захвата. Годится, только
    /// если круг миникарты не найден и квадрат обведён вплотную.
    /// </summary>
    public static double EstimateScreenPxPerMeter(int captureSizePx) =>
        Math.Clamp(captureSizePx / MinimapViewDiameterM, 0.2, 20.0);

    /// <summary>
    /// Оценка масштаба по диаметру найденного круга миникарты. Это точнее
    /// размера квадрата: в захват часто попадают поля и элементы интерфейса.
    /// </summary>
    public static double EstimateFromDiameter(double diameterPx) =>
        Math.Clamp(diameterPx / MinimapViewDiameterM, 0.2, 20.0);

    /// <summary>
    /// Старая (ошибочная) оценка: 93 м принимались за радиус, поэтому масштаб
    /// получался вдвое меньше нужного. Нужна только для миграции настроек.
    /// </summary>
    public static double LegacyScreenPxPerMeter(int captureSizePx) =>
        Math.Clamp(captureSizePx / 2.0 / MinimapViewDiameterM, 0.2, 20.0);

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

        // Прежний default 2.0 px/м был неточен. Один раз пересчитываем по
        // размеру области, если пользователь не задавал собственное значение.
        if (!string.Equals(database.GetSetting("screen_ppm_v2"), "1", StringComparison.Ordinal))
        {
            if (Math.Abs(settings.ScreenPxPerMeter - 2.0) < 1e-6)
            {
                settings.ScreenPxPerMeter = EstimateScreenPxPerMeter(settings.ScreenRect.Width);
            }

            database.SetSetting("screen_ppm_v2", "1");
        }

        // v3: раньше 93 м считались радиусом обзора миникарты, из-за чего метка
        // двигалась вдвое медленнее игрока. Пересчитываем сохранённое значение,
        // если оно совпадает со старой оценкой; своё значение пользователя
        // (например, из диалога калибровки) не трогаем.
        if (!string.Equals(database.GetSetting("screen_ppm_v3"), "1", StringComparison.Ordinal))
        {
            var legacy = LegacyScreenPxPerMeter(settings.ScreenRect.Width);
            if (Math.Abs(settings.ScreenPxPerMeter - legacy) < 1e-3)
            {
                settings.ScreenPxPerMeter = EstimateScreenPxPerMeter(settings.ScreenRect.Width);

                // Сохраняем сразу: если приложение закроется до следующего
                // Save, флаг миграции уже не даст пересчитать значение снова.
                database.SetSetting(
                    "screen_ppm",
                    settings.ScreenPxPerMeter.ToString("0.##", CultureInfo.CurrentCulture));
            }

            database.SetSetting("screen_ppm_v3", "1");
        }

        settings.ScreenScaleManual =
            string.Equals(database.GetSetting("screen_scale_manual"), "1", StringComparison.Ordinal);
        settings.ScreenScaleCalibrated =
            string.Equals(database.GetSetting("screen_scale_calibrated"), "1", StringComparison.Ordinal);

        if (double.TryParse(database.GetSetting("screen_circle_diameter"), out var diameter)
            && diameter > 0)
        {
            settings.ScreenCircleDiameterPx = Math.Clamp(diameter, 16.0, 4000.0);
        }

        // Один раз решаем, трогать ли уже сохранённый масштаб. Если он не
        // совпадает ни с одной из оценок, значит пользователь задал его сам
        // (или откалибровал по пути) — автоподстройка по кругу не должна его
        // перезаписывать.
        if (!string.Equals(database.GetSetting("screen_scale_flags_v1"), "1", StringComparison.Ordinal))
        {
            if (!settings.ScreenScaleManual && !settings.ScreenScaleCalibrated)
            {
                var estimate = EstimateScreenPxPerMeter(settings.ScreenRect.Width);
                if (Math.Abs(settings.ScreenPxPerMeter - estimate) > 0.05)
                {
                    settings.ScreenScaleManual = true;
                }
            }

            database.SetSetting("screen_scale_flags_v1", "1");
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
        database.SetSetting("screen_scale_manual", ScreenScaleManual ? "1" : "0");
        database.SetSetting("screen_scale_calibrated", ScreenScaleCalibrated ? "1" : "0");
        database.SetSetting(
            "screen_circle_diameter",
            ScreenCircleDiameterPx.ToString("0.#"));
        database.SetSetting("real_atlas_path", RealAtlasPath);
        database.SetSetting("real_ppm", RealPxPerMeter.ToString("0.####"));
        database.SetSetting("real_origin_x", RealOriginXMeters.ToString("0.##"));
        database.SetSetting("real_origin_z", RealOriginZMeters.ToString("0.##"));
    }
}
