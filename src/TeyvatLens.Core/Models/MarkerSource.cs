namespace TeyvatLens.Core.Models;

/// <summary>Откуда взялись маркеры: синтетический демо-мир или реальная карта.</summary>
public static class MarkerSources
{
    /// <summary>Демонстрационные данные, сгенерированные DemoWorld.</summary>
    public const string Synthetic = "synthetic";

    /// <summary>Точки, импортированные с интерактивной карты.</summary>
    public const string Real = "real";
}
