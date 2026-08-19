namespace DataShield.Codec.Packets;

// ─────────────────────────────────────────────────────────────────────────────
//  Константы формата пакета
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Формат пакета DataShield (75 байт → 100 Base64 символов, без паддинга).
///
/// <b>Пакет заголовка потока</b> (75 байт):
/// <code>
///   H1 [0..13]   FileName     (14 байт, ASCII, space-padded)
///   H2 [14..16]  FileSize     (3 байта, LE — младшие байты UInt32)
///   H3 [17..48]  SHA-256      (32 байта — хеш содержимого файла)
///   H4 [49..50]  EccCount     (2 байта, UInt16 LE)
///   H5 [51..74]  HeaderHash   (24 байта — Trunc24(SHA-256(H1–H2–H3–H4)))
/// </code>
///
/// <b>Сектор данных</b> (75 байт):
/// <code>
///   D1 [0..1]    SeqNum       (2 байта, LE)
///   D2 [2..65]   Payload      (64 байта)
///   D3 [66..74]  SectorHash   (9 байт — Trunc9(SHA-256(H5 ‖ D1 ‖ D2)))
/// </code>
///
/// Хеш заголовка (H5) проверяется автономно — это позволяет классифицировать
/// пакеты при сканировании (ложное срабатывание 2⁻¹⁹²). Хеш сектора (D3)
/// привязан к H5: без заголовка проверка невозможна, а сектор другого файла
/// не проходит проверку даже при совпадении номера.
/// </summary>
public static class PacketFormat
{
    /// <summary>Полный размер пакета: 75 байт.</summary>
    public const int PacketSize = 75;

    /// <summary>Размер payload данных: 64 байта (кратно 2 для GF(16)).</summary>
    public const int PayloadSize = 64;

    // ── Сектор данных ────────────────────────────────────────────────────────

    /// <summary>D1: размер поля номера сектора: 2 байта (LE).</summary>
    public const int SectorNumberSize = 2;

    /// <summary>Содержимое сектора без хеша: D1 + D2 = 66 байт.</summary>
    public const int SectorContentSize = SectorNumberSize + PayloadSize; // 66

    /// <summary>D3: размер хеша сектора: 9 байт.</summary>
    public const int SectorHashSize = 9;

    /// <summary>Смещение хеша сектора в пакете: 66.</summary>
    public const int SectorHashOffset = SectorContentSize; // 66

    // ── Поля заголовка ───────────────────────────────────────────────────────

    /// <summary>H1: имя файла (упакованное, см. <see cref="FileNameCodec"/>), ASCII, space-padded.</summary>
    public const int FileNameSize = 14;

    /// <summary>H2: размер файла в байтах, 3 байта LE.</summary>
    public const int FileSizeBytes = 3;

    /// <summary>H3: SHA-256 содержимого файла.</summary>
    public const int Sha256Size = 32;

    /// <summary>H4: количество ECC-томов, UInt16 LE.</summary>
    public const int EccCountBytes = 2;

    /// <summary>Содержимое заголовка без хеша: H1 + H2 + H3 + H4 = 51 байт.</summary>
    public const int HeaderContentSize =
        FileNameSize + FileSizeBytes + Sha256Size + EccCountBytes; // 51

    /// <summary>H5: размер хеша заголовка: 24 байта (192 бита).</summary>
    public const int HeaderHashSize = 24;

    /// <summary>Смещение хеша заголовка в пакете: 51.</summary>
    public const int HeaderHashOffset = HeaderContentSize; // 51

    // ── Смещения полей заголовка в пакете ────────────────────────────────────

    /// <summary>H1: смещение имени файла = 0.</summary>
    public const int FileNameOffset = 0;

    /// <summary>H2: смещение размера файла = 14.</summary>
    public const int FileSizeOffset = FileNameOffset + FileNameSize;    // 14

    /// <summary>H3: смещение SHA-256 = 17.</summary>
    public const int Sha256Offset = FileSizeOffset + FileSizeBytes;     // 17

    /// <summary>H4: смещение EccCount = 49.</summary>
    public const int EccCountOffset = Sha256Offset + Sha256Size;        // 49

    // ── Производные константы ────────────────────────────────────────────────

    /// <summary>Размер пакета в Base64 символах: 100.</summary>
    public const int Base64Size = (PacketSize * 4) / 3;

    /// <summary>Абсолютный максимум значения FileSize в 3 байтах.</summary>
    public const uint MaxFileSizeField = (1U << 24) - 1; // 16 777 215

    /// <summary>
    /// Максимум томов данных: 65 535. Ограничен одновременно нумерацией
    /// секторов UInt16 (D1) и размером поля GF(2¹⁶) стирающего кода
    /// (см. DataShield.Codec.Ecc).
    /// </summary>
    public const int MaxDataVolumes = 65535;
}
