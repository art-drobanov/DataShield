using DataShield.Codec.Packets;
using DataShield.Codec.Reporting;

namespace DataShield.Codec;

// ─────────────────────────────────────────────────────────────────────────────
//  Единый контракт кодека DataShield
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Единый контракт кодека DataShield: кодирование данных в FEC-поток
/// (заголовки + data/ECC-секторы) и восстановление данных из повреждённого
/// потока. Контракт ориентирован на потоки (<see cref="Stream"/>); перегрузки
/// под коллекции — на реализациях; файловые операции выделены в отдельный
/// класс <see cref="DataShieldCodecFiles"/>.
///
/// Библиотечные реализации: <see cref="DataShieldCodec"/> (сборка
/// DataShield.Codec — первичное API глубокой интеграции) и
/// <c>DataShieldConsole</c> (сборка DataShield.Console — консольная
/// специфика с отчётом прогресса в консоль).
///
/// Все операции потокобезопасны; экземпляры, удерживающие данные
/// пользователя, освобождаются через <see cref="IDisposable"/>.
/// </summary>
public interface IDataShieldCodec : IDisposable
{
    /// <summary>Процент избыточности ECC (0 = без ECC).</summary>
    int EccPercent { get; }

    /// <summary>Процент заголовков в потоке (минимум 3 копии).</summary>
    int HeaderPercent { get; }

    /// <summary>Схема хеширования секторов данных.</summary>
    SectorScheme SectorScheme { get; }

    /// <summary>Порог применимости схемы VirtualIndex по числу томов (N+M).</summary>
    int VirtualIndexSectorLimit { get; }

    // ── Кодирование ─────────────────────────────────────────────────────────

    /// <summary>
    /// Кодировать содержимое потока в двоичный массив FEC-потока
    /// (75-байтные пакеты подряд, формат Binary).
    /// </summary>
    /// <param name="content">Входной поток с содержимым файла.</param>
    /// <param name="fileName">Имя файла для заголовка.</param>
    /// <param name="progress">Приёмник прогресса (глобальная шкала 0..100).</param>
    /// <param name="ct">Токен отмены.</param>
    byte[] EncodeToBytes(
        Stream content, string fileName,
        IProgress<CodecProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Кодировать содержимое потока в массив 75-байтных пакетов.
    /// </summary>
    byte[][] EncodeToPackets(
        Stream content, string fileName,
        IProgress<CodecProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Кодировать содержимое потока с выгрузкой пакетов по ссылке в
    /// готовую коллекцию (список/коллекция вызывающего).
    /// </summary>
    /// <returns>Число выгруженных пакетов.</returns>
    int EncodeInto(
        Stream content, string fileName,
        ICollection<byte[]> output,
        IProgress<CodecProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Кодировать содержимое потока в Base64-текст (по пакету на строку,
    /// с декоративным обрамлением метаданных).
    /// </summary>
    string EncodeToText(
        Stream content, string fileName,
        IProgress<CodecProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Кодировать содержимое потока и записать FEC-поток заданного формата
    /// в выходной поток (поток не закрывается).
    /// </summary>
    /// <returns>Статистика кодирования (SHA-256, счётчики томов и пакетов).</returns>
    EncodeStats Encode(
        Stream content, Stream output, OutputFormat format, string fileName,
        IProgress<CodecProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Кодировать содержимое потока и вернуть пакеты вместе со статистикой.
    /// </summary>
    (List<byte[]> Packets, EncodeStats Stats) EncodePacketsWithStats(
        Stream content, string fileName,
        IProgress<CodecProgress>? progress, CancellationToken ct);

    // ── Декодирование: накопительный приём ──────────────────────────────────

    /// <summary>
    /// Добавить поток к накопленному приёму без сборки (многоходовой приём:
    /// произвольное число вызовов с любыми форматами; порядок прихода
    /// произволен, данные между вызовами накапливаются). Сборка —
    /// <see cref="DecodeAll(IProgress{CodecProgress}, CancellationToken)"/>.
    /// </summary>
    void Scan(
        Stream input, OutputFormat format,
        IProgress<CodecProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Добавить Base64-текст (перечисление строк, мусор вне алфавита
    /// отбрасывается) к накопленному приёму без сборки.
    /// </summary>
    void ScanText(
        IEnumerable<string> lines,
        IProgress<CodecProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Собрать все файлы накопленного приёма (без нового сканирования):
    /// слоты, упорядоченные по числу принятых секторов по убыванию.
    /// </summary>
    List<DecodeResult> DecodeAll(
        IProgress<CodecProgress>? progress, CancellationToken ct);

    // ── Декодирование: одноходовые операции ─────────────────────────────────

    /// <summary>
    /// Декодировать FEC-поток заданного формата и вернуть содержимое файла
    /// с наибольшим числом принятых секторов; null — восстановление
    /// невозможно (повреждения превысили избыточность) либо поток не
    /// содержит распознаваемых пакетов. Прежний накопленный приём
    /// сбрасывается.
    /// </summary>
    /// <param name="input">Входной поток FEC-данных (не закрывается).</param>
    /// <param name="format">Формат входного потока.</param>
    /// <param name="progress">Приёмник прогресса (глобальная шкала 0..100).</param>
    /// <param name="ct">Токен отмены.</param>
    byte[]? Decode(
        Stream input, OutputFormat format,
        IProgress<CodecProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Декодировать Base64-текст (перечисление строк, мусор вне алфавита
    /// отбрасывается) и вернуть содержимое лучшего файла; null — неудача.
    /// Прежний накопленный приём сбрасывается.
    /// </summary>
    byte[]? DecodeText(
        IEnumerable<string> lines,
        IProgress<CodecProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Декодировать FEC-поток заданного формата и вернуть результаты по
    /// всем обнаруженным файлам (слотам), упорядоченные по числу принятых
    /// секторов по убыванию. Прежний накопленный приём сбрасывается.
    /// </summary>
    List<DecodeResult> DecodeAll(
        Stream input, OutputFormat format,
        IProgress<CodecProgress>? progress, CancellationToken ct);
}
