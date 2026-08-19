namespace DataShield.Codec;

// ─────────────────────────────────────────────────────────────────────────────
//  Сводная статистика операции кодирования
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Метаданные выполненного кодирования: размер и хеш исходного файла,
/// состав томов и количество пакетов выходного потока.
/// </summary>
/// <param name="FileSize">Размер исходного файла в байтах.</param>
/// <param name="Sha256">SHA-256 содержимого исходного файла.</param>
/// <param name="DataCount">Число data-секторов (томов данных).</param>
/// <param name="EccCount">Число ECC-томов избыточности.</param>
/// <param name="TotalPackets">Общее число пакетов в потоке, включая копии заголовка.</param>
/// <param name="HeaderCopies">Число копий заголовочного пакета в потоке.</param>
public record EncodeStats(
    uint FileSize, byte[] Sha256, int DataCount, int EccCount,
    int TotalPackets, int HeaderCopies);
