using System.Drawing;
using TeyvatLens.Core.Models;

namespace TeyvatLens.Core.App;

/// <summary>Пользовательские настройки, сохраняемые в SQLite.</summary>
public sealed class SessionSettings
{
    public double RadiusM { get; set; } = 120.0;

    public MarkerKind? KindFilter { get; set; }

    public SessionMode Mode { get; set; } = SessionMode.Demo;

    public Rectangle ScreenRect { get; set; } = new(24, 24, 240, 240);

    public double ScreenPxPerMeter { get; set; } = 2.0;

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
        if (string.Equals(mode, "screen", StringComparison.OrdinalIgnoreCase))
        {
            settings.Mode = SessionMode.Screen;
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

        return settings;
    }

    public void Save(Data.Database database)
    {
        database.SetSetting("radius_m", RadiusM.ToString("0.##"));
        database.SetSetting("kind_filter", KindFilter is null ? "all" : MarkerKinds.ToCode(KindFilter.Value));
        database.SetSetting("mode", Mode == SessionMode.Screen ? "screen" : "demo");
        database.SetSetting(
            "screen_rect",
            $"{ScreenRect.X},{ScreenRect.Y},{ScreenRect.Width},{ScreenRect.Height}");
        database.SetSetting("screen_ppm", ScreenPxPerMeter.ToString("0.##"));
    }
}
