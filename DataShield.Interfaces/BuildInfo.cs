using System.Reflection;

namespace DataShield.Interfaces;

// ─────────────────────────────────────────────────────────────────────────────
//  Общие метаданные сборки продукта
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Метаданные сборки DataShield, общие для всех проектов решения: маркер
/// этапа разработки для строк вида «DataShield v3.0 Alpha-XV».
/// </summary>
public static class BuildInfo
{
    /// <summary>Маркер этапа разработки, выводимый рядом с версией сборки.</summary>
    public static string BuildLabel { get; } =
        typeof(BuildInfo).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildLabel")?.Value ?? string.Empty;
}
