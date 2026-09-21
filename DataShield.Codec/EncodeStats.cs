namespace DataShield.Codec;

// ─────────────────────────────────────────────────────────────────────────────
//  Сводная статистика операции кодирования
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Метаданные выполненного кодирования: размер и хеши исходного файла,
/// состав томов и количество пакетов выходного потока.
/// </summary>
/// <param name="FileSize">Размер исходного файла в байтах.</param>
/// <param name="Sha256">SHA-256 содержимого исходного файла (значение «на проводе», в заголовке H3).</param>
/// <param name="DataCount">Число data-секторов (томов данных).</param>
/// <param name="EccCount">Число ECC-томов избыточности.</param>
/// <param name="TotalPackets">Общее число пакетов в потоке, включая копии заголовка.</param>
/// <param name="HeaderCopies">Число копий заголовочного пакета в потоке.</param>
/// <param name="Sha512">SHA-512 содержимого (значение «на проводе») при включённых сигнатурных пакетах, иначе null.</param>
/// <param name="SignatureCopies">Число сигнатурных пакетов SHA-512 в потоке.</param>
/// <param name="RealSha256">Реальный SHA-256 (без секрета), пересчитанный по потоку в ОЗУ — для отчёта.</param>
/// <param name="RealSha512">Реальный SHA-512 (без секрета) при включённых сигнатурных пакетах, иначе null.</param>
public record EncodeStats(
    uint FileSize, byte[] Sha256, int DataCount, int EccCount,
    int TotalPackets, int HeaderCopies,
    byte[]? Sha512 = null, int SignatureCopies = 0,
    byte[]? RealSha256 = null, byte[]? RealSha512 = null)
{
    /// <summary>Включены ли сигнатурные пакеты SHA-512 (есть сигнатура потока).</summary>
    public bool SignaturePackets512 => Sha512 is not null;
}
