namespace DataShield.Codec.StreamProcessor.Versions;

// ─────────────────────────────────────────────────────────────────────────────
//  Точка ветвления при поиске правильной комбинации версий
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Неоднозначный слот сектора: несколько равновероятных наиболее
/// подтверждённых версий. Является точкой ветвления полного перебора
/// или эвристической прокрутки при сборке файла.
/// </summary>
internal sealed class ChoicePoint
{
    internal ChoicePoint(
        int sectorNumber,
        List<SectorVariant> variants,
        int tiedVariantCount)
    {
        SectorNumber = sectorNumber;
        Variants = variants;
        TiedVariantCount = tiedVariantCount;
    }

    /// <summary>Номер неоднозначного сектора.</summary>
    internal int SectorNumber { get; }

    /// <summary>Все версии слота (ссылка на живой список ReceptionSlot).</summary>
    internal List<SectorVariant> Variants { get; }

    /// <summary>
    /// Число вариантов в начале списка, имеющих такой же счётчик,
    /// как наиболее подтверждённый вариант.
    /// </summary>
    internal int TiedVariantCount { get; }
}
