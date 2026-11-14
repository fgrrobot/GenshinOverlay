namespace TeyvatLens.Core;

/// <summary>Расположение файлов проекта. Данные лежат рядом с решением в data/.</summary>
public static class AppPaths
{
    public static string ProjectRoot { get; } = FindProjectRoot();

    public static string DataDir => Path.Combine(ProjectRoot, "data");

    public static string DbPath => Path.Combine(DataDir, "teyvat_lens.db");

    public static string DemoAtlasPath => Path.Combine(DataDir, "demo_atlas.png");

    /// <summary>Кэш скачанных данных интерактивной карты.</summary>
    public static string GenshinCacheDir => Path.Combine(DataDir, "cache", "hoyolab");

    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TeyvatLens.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return AppContext.BaseDirectory;
    }
}
