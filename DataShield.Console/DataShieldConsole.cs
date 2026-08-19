using System.IO;
using DataShield.Codec;
using DataShield.Codec.Packets;
using DataShield.Codec.Reporting;
using DataShield.Codec.StreamProcessor;
using DataShield.Codec.StreamProcessor.Versions;

namespace DataShield.Console;

// ─────────────────────────────────────────────────────────────────────────────
//  Консольная реализация единого контракта кодека
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Консольная реализация единого контракта <see cref="IDataShieldCodec"/>:
/// агрегирует библиотечный <see cref="DataShieldCodec"/> и автоматически
/// добавляет к каждой операции отчёт прогресса в консоль (перезаписываемая
/// строка «фаза + проценты», см. <see cref="ConsoleProgress"/>); флаг
/// quiet отключает вывод. Внешний приёмник прогресса, если задан,
/// подключается параллельно консольному.
///
/// Точка интеграции консольных сборок: DataShield.Console.RU и
/// DataShield.Console.EN работают с кодеком через этот класс и контракт.
/// </summary>
public sealed class DataShieldConsole : IDataShieldCodec
{
    private readonly DataShieldCodec _codec;
    private readonly bool _quiet;

    /// <summary>
    /// Создать консольный кодек.
    /// </summary>
    /// <param name="quiet">Отключить вывод прогресса в консоль.</param>
    /// <param name="eccPercent">0 — без ECC, иначе M = max(1, round(N·pct/100)).</param>
    /// <param name="headerPercent">Процент заголовков (минимум 3 копии).</param>
    /// <param name="sectorScheme">Схема хеширования секторов данных.</param>
    /// <param name="virtualIndexSectorLimit">Порог схемы VirtualIndex (N+M).</param>
    /// <param name="searchOptions">Настройки обхода коллизий версий декодера.</param>
    public DataShieldConsole(
        bool quiet = false,
        int eccPercent = 10,
        int headerPercent = StreamEncoder.DefaultHeaderPercent,
        SectorScheme sectorScheme = SectorScheme.Classic,
        int virtualIndexSectorLimit = VirtualIndexHasher.DefaultSectorLimit,
        SectorVersionSearchOptions? searchOptions = null)
    {
        _quiet = quiet;
        _codec = new DataShieldCodec(
            eccPercent, headerPercent, sectorScheme, virtualIndexSectorLimit, searchOptions);
    }

    /// <inheritdoc/>
    public int EccPercent => _codec.EccPercent;

    /// <inheritdoc/>
    public int HeaderPercent => _codec.HeaderPercent;

    /// <inheritdoc/>
    public SectorScheme SectorScheme => _codec.SectorScheme;

    /// <inheritdoc/>
    public int VirtualIndexSectorLimit => _codec.VirtualIndexSectorLimit;

    // ── Кодирование ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public byte[] EncodeToBytes(
        Stream content, string fileName,
        IProgress<CodecProgress>? progress, CancellationToken ct) =>
        Run(p => _codec.EncodeToBytes(content, fileName, p, ct), progress);

    /// <inheritdoc/>
    public byte[][] EncodeToPackets(
        Stream content, string fileName,
        IProgress<CodecProgress>? progress, CancellationToken ct) =>
        Run(p => _codec.EncodeToPackets(content, fileName, p, ct), progress);

    /// <inheritdoc/>
    public int EncodeInto(
        Stream content, string fileName,
        ICollection<byte[]> output,
        IProgress<CodecProgress>? progress, CancellationToken ct) =>
        Run(p => _codec.EncodeInto(content, fileName, output, p, ct), progress);

    /// <inheritdoc/>
    public string EncodeToText(
        Stream content, string fileName,
        IProgress<CodecProgress>? progress, CancellationToken ct) =>
        Run(p => _codec.EncodeToText(content, fileName, p, ct), progress);

    /// <inheritdoc/>
    public EncodeStats Encode(
        Stream content, Stream output, OutputFormat format, string fileName,
        IProgress<CodecProgress>? progress, CancellationToken ct) =>
        Run(p => _codec.Encode(content, output, format, fileName, p, ct), progress);

    /// <inheritdoc/>
    public (List<byte[]> Packets, EncodeStats Stats) EncodePacketsWithStats(
        Stream content, string fileName,
        IProgress<CodecProgress>? progress, CancellationToken ct) =>
        Run(p => _codec.EncodePacketsWithStats(content, fileName, p, ct), progress);

    // ── Декодирование: накопительный приём ──────────────────────────────────

    /// <inheritdoc/>
    public void Scan(
        Stream input, OutputFormat format,
        IProgress<CodecProgress>? progress, CancellationToken ct) =>
        RunVoid(p => _codec.Scan(input, format, p, ct), progress);

    /// <inheritdoc/>
    public void ScanText(
        IEnumerable<string> lines,
        IProgress<CodecProgress>? progress, CancellationToken ct) =>
        RunVoid(p => _codec.ScanText(lines, p, ct), progress);

    /// <inheritdoc/>
    public List<DecodeResult> DecodeAll(
        IProgress<CodecProgress>? progress, CancellationToken ct) =>
        Run(p => _codec.DecodeAll(p, ct), progress);

    // ── Декодирование: одноходовые операции ─────────────────────────────────

    /// <inheritdoc/>
    public byte[]? Decode(
        Stream input, OutputFormat format,
        IProgress<CodecProgress>? progress, CancellationToken ct) =>
        Run(p => _codec.Decode(input, format, p, ct), progress);

    /// <inheritdoc/>
    public byte[]? DecodeText(
        IEnumerable<string> lines,
        IProgress<CodecProgress>? progress, CancellationToken ct) =>
        Run(p => _codec.DecodeText(lines, p, ct), progress);

    /// <inheritdoc/>
    public List<DecodeResult> DecodeAll(
        Stream input, OutputFormat format,
        IProgress<CodecProgress>? progress, CancellationToken ct) =>
        Run(p => _codec.DecodeAll(input, format, p, ct), progress);

    /// <summary>Экземпляр освобождён вызовом <see cref="Dispose"/>.</summary>
    public bool IsDisposed => _codec.IsDisposed;

    /// <inheritdoc/>
    public void Dispose() => _codec.Dispose();

    // ── Консольный прогресс ─────────────────────────────────────────────────

    // Выполнить операцию с консольным прогрессом (quiet отключает вывод);
    // внешний приёмник подключается параллельно. По завершении строка
    // прогресса затирается.
    private T Run<T>(Func<IProgress<CodecProgress>?, T> op, IProgress<CodecProgress>? progress)
    {
        ConsoleProgress? console = _quiet ? null : new ConsoleProgress(quiet: false);
        var chain = console is null
            ? progress
            : progress is null ? console : new CompositeProgress(console, progress);

        try
        {
            return op(chain);
        }
        finally
        {
            console?.Done();
        }
    }

    // Void-вариант Run для операций накопительного приёма.
    private void RunVoid(Action<IProgress<CodecProgress>?> op, IProgress<CodecProgress>? progress)
    {
        Run<object?>(p =>
        {
            op(p);
            return null;
        }, progress);
    }
}
