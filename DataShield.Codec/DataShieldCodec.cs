using System.IO;
using DataShield.Codec.Packets;
using DataShield.Codec.Reporting;
using DataShield.Codec.StreamProcessor;
using DataShield.Codec.StreamProcessor.Versions;

namespace DataShield.Codec;

// ─────────────────────────────────────────────────────────────────────────────
//  Фасад кодека: единый контракт поверх кодера и декодера
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Библиотечная реализация единого контракта <see cref="IDataShieldCodec"/>:
/// агрегирует <see cref="StreamEncoder"/> и <see cref="StreamDecoder"/>
/// (агрегация вместо наследования — кодер и декодер не имеют общего
/// поведения сверх конфигурации <see cref="StreamCodec"/>).
///
/// Ядро ориентировано на потоки; перегрузки под коллекции (вход — массив
/// или перечисление байт, выгрузка по ссылке в коллекцию, возврат массива
/// байт) добавлены здесь; файловые операции — <see cref="DataShieldCodecFiles"/>.
///
/// Все операции сериализуются внутренней блокировкой — экземпляр безопасен
/// для параллельного использования из нескольких потоков. Декодер
/// удерживает данные пользователя между вызовами: перед каждым
/// декодированием накопленный приём сбрасывается (вайп), окончательное
/// освобождение — <see cref="Dispose"/>.
/// </summary>
public sealed class DataShieldCodec : IDataShieldCodec
{
    private readonly StreamEncoder _encoder;
    private readonly StreamDecoder _decoder;
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>
    /// Создать кодек с заданной конфигурацией (общей для кодера и декодера).
    /// </summary>
    /// <param name="eccPercent">0 — без ECC, иначе M = max(1, round(N·pct/100)).</param>
    /// <param name="headerPercent">Процент заголовков (минимум 3 копии).</param>
    /// <param name="sectorScheme">Схема хеширования секторов данных.</param>
    /// <param name="virtualIndexSectorLimit">Порог схемы VirtualIndex (N+M).</param>
    /// <param name="searchOptions">Настройки обхода коллизий версий декодера.</param>
    public DataShieldCodec(
        int eccPercent = 10,
        int headerPercent = StreamEncoder.DefaultHeaderPercent,
        SectorScheme sectorScheme = SectorScheme.Classic,
        int virtualIndexSectorLimit = VirtualIndexHasher.DefaultSectorLimit,
        SectorVersionSearchOptions? searchOptions = null)
    {
        _encoder = new StreamEncoder(eccPercent, headerPercent, sectorScheme, virtualIndexSectorLimit);
        _decoder = new StreamDecoder(searchOptions, sectorScheme, virtualIndexSectorLimit);
    }

    /// <inheritdoc/>
    public int EccPercent => _encoder.EccPercent;

    /// <inheritdoc/>
    public int HeaderPercent => _encoder.HeaderPercent;

    /// <inheritdoc/>
    public SectorScheme SectorScheme => _encoder.SectorScheme;

    /// <inheritdoc/>
    public int VirtualIndexSectorLimit => _encoder.VirtualIndexSectorLimit;

    // ── Кодирование: вход Stream ────────────────────────────────────────────

    /// <inheritdoc/>
    public byte[] EncodeToBytes(
        Stream content, string fileName,
        IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        var (packets, _) = EncodePacketsWithStats(content, fileName, progress, ct);
        return PacketIO.WriteBinaryBytes(packets);
    }

    /// <inheritdoc/>
    public byte[][] EncodeToPackets(
        Stream content, string fileName,
        IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        var (packets, _) = EncodePacketsWithStats(content, fileName, progress, ct);
        return packets.ToArray();
    }

    /// <inheritdoc/>
    public int EncodeInto(
        Stream content, string fileName,
        ICollection<byte[]> output,
        IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);
        var (packets, _) = EncodePacketsWithStats(content, fileName, progress, ct);
        foreach (var p in packets) output.Add(p);
        return packets.Count;
    }

    /// <inheritdoc/>
    public string EncodeToText(
        Stream content, string fileName,
        IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        var (packets, stats) = EncodePacketsWithStats(content, fileName, progress, ct);
        return PacketIO.WriteBase64Text(packets, fileName, stats.Sha256, stats.FileSize);
    }

    /// <inheritdoc/>
    public EncodeStats Encode(
        Stream content, Stream output, OutputFormat format, string fileName,
        IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);

        var (packets, stats) = EncodePacketsWithStats(content, fileName, progress, ct);
        PacketIO.WriteFile(output, packets, format, fileName, stats.Sha256, stats.FileSize);
        return stats;
    }

    /// <inheritdoc/>
    public (List<byte[]> Packets, EncodeStats Stats) EncodePacketsWithStats(
        Stream content, string fileName,
        IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _encoder.EncodeWithStats(content, fileName, progress, ct);
        }
    }

    // ── Кодирование: вход — коллекции ───────────────────────────────────────

    /// <inheritdoc cref="EncodeToPackets(Stream, string, IProgress{CodecProgress}, CancellationToken)"/>
    public byte[][] EncodeToPackets(
        byte[] content, string fileName,
        IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);

        lock (_gate)
        {
            ThrowIfDisposed();
            return _encoder.Encode(content, fileName, progress, ct).ToArray();
        }
    }

    /// <inheritdoc cref="EncodeToPackets(Stream, string, IProgress{CodecProgress}, CancellationToken)"/>
    public byte[][] EncodeToPackets(
        IEnumerable<byte> content, string fileName,
        IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        var buffer = Materialize(content);
        return EncodeToPackets(buffer, fileName, progress, ct);
    }

    /// <inheritdoc cref="EncodeToBytes(Stream, string, IProgress{CodecProgress}, CancellationToken)"/>
    public byte[] EncodeToBytes(
        byte[] content, string fileName,
        IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        var packets = EncodeToPackets(content, fileName, progress, ct);
        return PacketIO.WriteBinaryBytes(packets);
    }

    /// <inheritdoc cref="EncodeToText(Stream, string, IProgress{CodecProgress}, CancellationToken)"/>
    public string EncodeToText(
        byte[] content, string fileName,
        IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        var (packets, stats) = EncodePacketsWithStats(content, fileName, progress, ct);
        return PacketIO.WriteBase64Text(packets, fileName, stats.Sha256, stats.FileSize);
    }

    /// <inheritdoc cref="EncodeInto(Stream, string, ICollection{byte[]}, IProgress{CodecProgress}, CancellationToken)"/>
    public int EncodeInto(
        byte[] content, string fileName,
        ICollection<byte[]> output,
        IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);

        var packets = EncodeToPackets(content, fileName, progress, ct);
        foreach (var p in packets) output.Add(p);
        return packets.Length;
    }

    /// <inheritdoc cref="EncodeInto(Stream, string, ICollection{byte[]}, IProgress{CodecProgress}, CancellationToken)"/>
    public int EncodeInto(
        IEnumerable<byte> content, string fileName,
        ICollection<byte[]> output,
        IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        var buffer = Materialize(content);
        return EncodeInto(buffer, fileName, output, progress, ct);
    }

    /// <inheritdoc cref="EncodePacketsWithStats(Stream, string, IProgress{CodecProgress}, CancellationToken)"/>
    public (List<byte[]> Packets, EncodeStats Stats) EncodePacketsWithStats(
        byte[] content, string fileName,
        IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);

        lock (_gate)
        {
            ThrowIfDisposed();
            return _encoder.EncodeWithStats(content, fileName, progress, ct);
        }
    }

    // ── Декодирование: накопительный приём ──────────────────────────────────

    /// <inheritdoc/>
    public void Scan(
        Stream input, OutputFormat format,
        IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            PacketIO.ScanStream(_decoder, input, format, progress, ct);
        }
    }

    /// <inheritdoc/>
    public void ScanText(
        IEnumerable<string> lines,
        IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _decoder.Scan(lines, progress, ct);
        }
    }

    /// <inheritdoc/>
    public List<DecodeResult> DecodeAll(
        IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return AssembleAll(progress, ct);
        }
    }

    // ── Декодирование: одноходовые операции ─────────────────────────────────

    /// <inheritdoc/>
    public byte[]? Decode(
        Stream input, OutputFormat format,
        IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        var results = DecodeAll(input, format, progress, ct);
        return results.Count > 0 ? results[0].Content : null;
    }

    /// <inheritdoc/>
    public byte[]? DecodeText(
        IEnumerable<string> lines,
        IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _decoder.Clear();
            _decoder.Scan(lines, progress, ct);
            return AssembleBest(progress, ct);
        }
    }

    /// <inheritdoc/>
    public List<DecodeResult> DecodeAll(
        Stream input, OutputFormat format,
        IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _decoder.Clear();
            PacketIO.ScanStream(_decoder, input, format, progress, ct);
            return AssembleAll(progress, ct);
        }
    }

    /// <inheritdoc cref="Decode(Stream, OutputFormat, IProgress{CodecProgress}, CancellationToken)"/>
    public byte[]? Decode(
        byte[] input, OutputFormat format,
        IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        using var stream = new MemoryStream(input, writable: false);
        return Decode(stream, format, progress, ct);
    }

    // ── Освобождение ────────────────────────────────────────────────────────

    /// <summary>Экземпляр освобождён вызовом <see cref="Dispose"/>.</summary>
    public bool IsDisposed => _disposed;

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _decoder.Dispose();
        _encoder.Dispose();
    }

    // ── Служебные ───────────────────────────────────────────────────────────

    /// <summary>Гвард освобождения: операции после Dispose недопустимы.</summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DataShieldCodec));
    }

    // Собрать лучший файл после завершённого сканирования.
    private byte[]? AssembleBest(IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        foreach (var slot in OrderedSlots())
            return _decoder.TryAssemble(slot.Header, progress, ct);
        return null;
    }

    // Собрать все файлы после завершённого сканирования.
    private List<DecodeResult> AssembleAll(IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        var results = new List<DecodeResult>(_decoder.FileCount);
        foreach (var slot in OrderedSlots())
            results.Add(new DecodeResult(slot, _decoder.TryAssemble(slot.Header, progress, ct)));
        return results;
    }

    // Слоты по убыванию числа принятых секторов.
    private IReadOnlyList<ReceptionSlot> OrderedSlots()
    {
        var slots = _decoder.Slots;
        var ordered = new List<ReceptionSlot>(slots);
        ordered.Sort(static (a, b) => b.ReceivedSectorCount.CompareTo(a.ReceivedSectorCount));
        return ordered;
    }

    // Материализация перечисления байт в массив (без копии для byte[]).
    private static byte[] Materialize(IEnumerable<byte> content) =>
        content as byte[] ?? content.ToArray();
}
