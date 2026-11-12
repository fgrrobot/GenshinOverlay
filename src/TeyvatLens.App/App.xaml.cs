using System.IO;
using System.Windows;
using TeyvatLens.Core;
using TeyvatLens.Core.App;
using TeyvatLens.Core.Data;

namespace TeyvatLens.App;

public partial class App : Application
{
    private Database? _database;
    private AppSession? _session;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);

            _database = new Database(AppPaths.DbPath);
            _database.Initialize();
            _database.SeedIfEmpty();

            var settings = SessionSettings.Load(_database);
            _session = new AppSession(_database, AppPaths.DemoAtlasPath, settings);

            var window = new MainWindow(_session);
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Не удалось запустить приложение.\n\n{exception.Message}",
                "Teyvat Lens",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_session is not null)
        {
            _session.Dispose();
        }
        else
        {
            _database?.Dispose();
        }

        base.OnExit(e);
    }
}
