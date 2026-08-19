using System.IO;
using System.Text;
using DataShield.Codec.Ecc;
using DataShield.Codec.Packets;
using DataShield.Codec.Reporting;

namespace DataShield.Codec;

// ─────────────────────────────────────────────────────────────────────────────
//  Кодер: файл → 75-байтные пакеты (заголовки + секторы данных)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Кодирует файл в поток пакетов DataShield.
///
/// Стратегия заголовков: первый и последний пакеты — заголовки,
/// промежуточные равномерно распределены. Общее количество заголовков
/// ≤3% от числа data-томов, но не менее 3 (начало/середина/конец).
/// </summary>
public sealed class FileEncoder
{
    // Процент избыточности ECC (0 = без ECC, ≥1 = min 1 ECC-том)
    private readonly int _eccPercent;

    // Процент избыточности заголовков (min 3 копии)
    private readonly int _headerPercent;

    // RS-адаптер для вычисления ECC-томов
    private readonly RsCodecAdapter _rs = new();

    /// <summary>Текущий процент избыточности ECC.</summary>
    public int EccPercent => _eccPercent;

    /// <summary>Текущий процент избыточности заголовков.</summary>
    public int HeaderPercent => _headerPercent;

    /// <summary>
    /// Создать кодер с заданным процентом избыточности.
    /// </summary>
    /// <param name="eccPercent">0 — без ECC, иначе M = max(1, ⌈N·pct/100⌉).</param>
    /// <param name="headerPercent">Процент заголовков (0-100), по умолчанию 3. Минимум 3 копии.</param>
    public FileEncoder(int eccPercent = 10, int headerPercent = 3)
    {
        if (eccPercent < 0)
            throw new ArgumentOutOfRangeException(nameof(eccPercent));
        if (headerPercent < 0)
            throw new ArgumentOutOfRangeException(nameof(headerPercent));
        _eccPercent = eccPercent;
        _headerPercent = headerPercent;
    }

    /// <summary>
    /// M = max(1, ⌈N · eccPercent / 100⌉) при eccPercent ≥ 1, иначе 0.
    /// </summary>
    public static int ComputeEccCount(int dataCount, int eccPercent)
    {
        if (eccPercent <= 0 || dataCount <= 0) return 0;
        var m = (int)(((long)dataCount * eccPercent + 99) / 100);
        return Math.Max(1, m);
    }

    /// <summary>
    /// Количество копий заголовка при проценте по умолчанию (3%).
    /// </summary>
    public static int ComputeHeaderCount(int totalCount) =>
        ComputeHeaderCount(totalCount, DefaultHeaderPercent);

    /// <summary>Процент заголовков по умолчанию (3%).</summary>
    public const int DefaultHeaderPercent = 3;

    /// <summary>
    /// Количество копий заголовка: max(3, ⌈totalCount · headerPercent / 100⌉).
    /// </summary>
    public static int ComputeHeaderCount(int totalCount, int headerPercent)
    {
        if (headerPercent < 0) headerPercent = 0;
        if (totalCount <= 0) return 3;
        var pct = (totalCount * headerPercent + 99) / 100;
        return Math.Max(3, pct);
    }

    /// <summary>
    /// Кодировать файл в список 75-байтных пакетов.
    /// </summary>
    public List<byte[]> Encode(ReadOnlySpan<byte> content, string fileName) =>
        Encode(content, fileName, progress: null, default);

    /// <inheritdoc cref="Encode(ReadOnlySpan{byte}, string)"/>
    /// <param name="content">Содержимое файла.</param>
    /// <param name="fileName">Имя файла для заголовка.</param>
    /// <param name="progress">Приёмник прогресса (глобальная шкала 0..100).</param>
    /// <param name="ct">Токен отмены.</param>
    public List<byte[]> Encode(
        ReadOnlySpan<byte> content, string fileName,
        IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        var fileSize = (uint)content.Length;
        if (fileSize > PacketFormat.MaxFileSizeField)
            throw new InvalidOperationException(
                $"Размер файла ({fileSize:N0}) превышает максимум " +
                $"{PacketFormat.MaxFileSizeField:N0} байт (3-байтное поле).");

        // ── Упаковка имени файла в поле H1 (14 байт) ─────────────────────────
        var nameForHeader = FileNameCodec.Pack(Path.GetFileName(fileName));

        ct.ThrowIfCancellationRequested();

        // ── Границы фаз прогресса ───────────────────────────────────────────
        const int PhaseDataEnd = 10;     // Подготовка данных: 0..10
        const int PhaseEccEnd = 75;      // ECC-кодирование: 10..75
        const int PhasePacketsEnd = 100; // Формирование пакетов: 75..100

        var lastPct = -1;

        // ── SHA-256 содержимого ──────────────────────────────────────────────
        ProgressThrottle.Tick(progress, ref lastPct, 0, 100, CodecStrings.DataPreparation, ct);
        var sha256 = Sha256Compact.HashData(content);

        // ── Размерности ──────────────────────────────────────────────────────
        var dataCount = Math.Max(1,
            (int)((fileSize + (uint)PacketFormat.PayloadSize - 1) / PacketFormat.PayloadSize));
        var eccCount = ComputeEccCount(dataCount, _eccPercent);
        var totalCount = dataCount + eccCount;

        if (totalCount > PacketFormat.MaxDataVolumes)
            throw new InvalidOperationException(
                $"N+M = {totalCount:N0} превышает предел {PacketFormat.MaxDataVolumes:N0} томов (GF(2¹⁶)). " +
                $"Уменьшите размер файла или процент избыточности.");

        // ── Data payloads (64 байта, последний добивается нулями) ────────────
        var dataPayloads = new byte[dataCount][];
        for (var i = 0; i < dataCount; i++)
        {
            dataPayloads[i] = new byte[PacketFormat.PayloadSize];
            var off = i * PacketFormat.PayloadSize;
            var len = Math.Min(PacketFormat.PayloadSize, content.Length - off);
            if (len > 0) content.Slice(off, len).CopyTo(dataPayloads[i]);
        }

        ProgressThrottle.Tick(progress, ref lastPct, PhaseDataEnd, 100, CodecStrings.DataPreparation, ct);

        // ── ECC payloads ─────────────────────────────────────────────────────
        byte[][] eccPayloads = [];
        if (eccCount > 0)
        {
            var eccProgress = new ScaledProgress(progress, CodecStrings.EccEncoding, PhaseDataEnd, PhaseEccEnd);
            var rsResult = _rs.Encode(dataPayloads, eccCount, eccProgress, ct);
            eccPayloads = new byte[eccCount][];
            for (var i = 0; i < eccCount; i++)
                eccPayloads[i] = rsResult[i];
        }

        lastPct = PhaseEccEnd - 1;

        // ── Заголовок и его хеш (H5) ─────────────────────────────────────────
        var header = new HeaderContent
        {
            FileName = nameForHeader,
            FileSize = fileSize,
            Sha256 = sha256,
            EccCount = (ushort)eccCount,
        };
        var headerBytes = header.ToBytes();
        var headerHash = PacketHasher.ComputeHeaderHash(headerBytes);
        var headerPacket = BuildHeaderPacket(headerBytes, headerHash);

        // ── Секторы данных и ECC ─────────────────────────────────────────────
        var allPayloads = new byte[totalCount][];
        for (var i = 0; i < dataCount; i++) allPayloads[i] = dataPayloads[i];
        for (var i = 0; i < eccCount; i++) allPayloads[dataCount + i] = eccPayloads[i];

        var dataSectors = new byte[totalCount][];
        for (var i = 0; i < totalCount; i++)
        {
            ProgressThrottle.Tick(progress, ref lastPct,
                PhaseEccEnd + (i + 1) * (PhasePacketsEnd - PhaseEccEnd) / totalCount, 100,
                CodecStrings.PacketBuilding, ct);
            dataSectors[i] = BuildDataSector(i, allPayloads[i], headerHash);
        }

        // ── Расположение пакетов для передачи ────────────────────────────────
        var headerCount = ComputeHeaderCount(totalCount, _headerPercent);
        var packets = ArrangePackets(headerPacket, dataSectors, headerCount);

        progress?.Report(CodecProgress.Create(100, CodecStrings.Done));

        // ── Вайп промежуточных буферов ───────────────────────────────────────
        foreach (var p in dataPayloads) Array.Clear(p);
        foreach (var p in eccPayloads) Array.Clear(p);
        Array.Clear(headerBytes);

        return packets;
    }

    // ── Потоковый вход (Stream) ─────────────────────────────────────────────

    /// <summary>
    /// Кодировать содержимое потока в список 75-байтных пакетов.
    /// Поток читается целиком (ограничение <see cref="PacketFormat.MaxFileSizeField"/>
    /// и требование полного содержимого для SHA-256 и RS-кодирования).
    /// </summary>
    public List<byte[]> Encode(Stream content, string fileName) =>
        Encode(ReadStreamContent(content), fileName, progress: null, default);

    /// <inheritdoc cref="Encode(Stream, string)"/>
    /// <param name="content">Входной поток с содержимым файла.</param>
    /// <param name="fileName">Имя файла для заголовка.</param>
    /// <param name="progress">Приёмник прогресса (глобальная шкала 0..100).</param>
    /// <param name="ct">Токен отмены.</param>
    public List<byte[]> Encode(
        Stream content, string fileName,
        IProgress<CodecProgress>? progress, CancellationToken ct) =>
        Encode(ReadStreamContent(content), fileName, progress, ct);

    /// <summary>Кодировать содержимое потока в текст (Base64, по пакету на строку).</summary>
    public string EncodeToText(Stream content, string fileName) =>
        EncodeToText(ReadStreamContent(content), fileName);

    /// <summary>
    /// Кодировать содержимое потока и вернуть статистику (<see cref="EncodeStats"/>).
    /// </summary>
    public (List<byte[]> packets, EncodeStats stats) EncodeWithStats(
        Stream content, string fileName) =>
        EncodeWithStats(ReadStreamContent(content), fileName);

    /// <inheritdoc cref="EncodeWithStats(Stream, string)"/>
    /// <param name="content">Входной поток с содержимым файла.</param>
    /// <param name="fileName">Имя файла для заголовка.</param>
    /// <param name="progress">Приёмник прогресса (глобальная шкала 0..100).</param>
    /// <param name="ct">Токен отмены.</param>
    public (List<byte[]> packets, EncodeStats stats) EncodeWithStats(
        Stream content, string fileName,
        IProgress<CodecProgress>? progress, CancellationToken ct) =>
        EncodeWithStats(ReadStreamContent(content), fileName, progress, ct);

    /// <summary>Кодировать файл в текст (Base64, по пакету на строку).</summary>
    public string EncodeToText(ReadOnlySpan<byte> content, string fileName)
    {
        var packets = Encode(content, fileName);
        var sb = new StringBuilder(packets.Count * (PacketFormat.Base64Size + 2));
        foreach (var p in packets)
            sb.Append(Convert.ToBase64String(p)).Append('\n');
        return sb.ToString();
    }

    /// <summary>Кодировать и вернуть статистику (<see cref="EncodeStats"/>).</summary>
    public (List<byte[]> packets, EncodeStats stats) EncodeWithStats(
        ReadOnlySpan<byte> content, string fileName) =>
        EncodeWithStats(content, fileName, progress: null, default);

    /// <inheritdoc cref="EncodeWithStats(ReadOnlySpan{byte}, string)"/>
    /// <param name="content">Содержимое файла.</param>
    /// <param name="fileName">Имя файла для заголовка.</param>
    /// <param name="progress">Приёмник прогресса (глобальная шкала 0..100).</param>
    /// <param name="ct">Токен отмены.</param>
    public (List<byte[]> packets, EncodeStats stats) EncodeWithStats(
        ReadOnlySpan<byte> content, string fileName,
        IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        var packets = Encode(content, fileName, progress, ct);

        var fileSize = (uint)content.Length;
        var sha256 = Sha256Compact.HashData(content);
        var dataCount = Math.Max(1,
            (int)((fileSize + (uint)PacketFormat.PayloadSize - 1) / PacketFormat.PayloadSize));
        var eccCount = ComputeEccCount(dataCount, _eccPercent);

        var headerCopies = packets.Count - dataCount - eccCount;

        return (packets, new EncodeStats(
            fileSize, sha256, dataCount, eccCount, packets.Count, headerCopies));
    }

    // ── Служебные методы ────────────────────────────────────────────────────

    /// <summary>
    /// Прочитать поток целиком в массив с проверкой предельного размера
    /// <see cref="PacketFormat.MaxFileSizeField"/> (3-байтное поле заголовка).
    /// Работает и с непозиционируемыми потоками.
    /// </summary>
    private static byte[] ReadStreamContent(Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
            throw new ArgumentException("Поток не поддерживает чтение.", nameof(content));

        if (content.CanSeek)
        {
            var remaining = content.Length - content.Position;
            if (remaining > PacketFormat.MaxFileSizeField)
                throw new InvalidOperationException(
                    $"Размер потока ({remaining:N0}) превышает максимум " +
                    $"{PacketFormat.MaxFileSizeField:N0} байт (3-байтное поле).");

            var buffer = new byte[(int)remaining];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = content.Read(buffer, read, buffer.Length - read);
                if (n <= 0) break;
                read += n;
            }

            if (read < buffer.Length) Array.Resize(ref buffer, read);
            return buffer;
        }

        // Непозиционируемый поток: копия через промежуточный буфер
        using var ms = new MemoryStream();
        content.CopyTo(ms);
        var result = ms.ToArray();
        if (result.Length > PacketFormat.MaxFileSizeField)
            throw new InvalidOperationException(
                $"Размер потока ({result.Length:N0}) превышает максимум " +
                $"{PacketFormat.MaxFileSizeField:N0} байт (3-байтное поле).");
        return result;
    }

    // ── Расположение пакетов в передаваемом потоке ──────────────────────────

    /// <summary>
    /// Расставить пакеты в выходном потоке: заголовок в начале и конце,
    /// промежуточные копии — равномерно по интервалу.
    /// </summary>
    private static List<byte[]> ArrangePackets(
        byte[] headerPacket, byte[][] dataSectors, int headerCount)
    {
        var totalCount = dataSectors.Length;
        var packets = new List<byte[]>(totalCount + headerCount);

        // Первый пакет потока — заголовок
        packets.Add(headerPacket);

        if (headerCount <= 2)
        {
            // Минимальный случай: только заголовок в начале и в конце
            foreach (var ds in dataSectors) packets.Add(ds);
        }
        else
        {
            // Равномерное распределение промежуточных заголовков
            var middleCount = headerCount - 2; // Без первого и последнего
            var interval = Math.Max(1, totalCount / (middleCount + 1));

            for (var i = 0; i < totalCount; i++)
            {
                packets.Add(dataSectors[i]);
                // Вставляем заголовок на равных интервалах
                if ((i + 1) % interval == 0 && middleCount > 0)
                {
                    packets.Add(headerPacket);
                    middleCount--;
                }
            }
        }

        // Последний пакет потока — заголовок
        packets.Add(headerPacket);

        return packets;
    }

    // ── Построение пакетов ──────────────────────────────────────────────────

    /// <summary>
    /// Пакет заголовка: headerContent(51) + H5(24).
    /// H5 = Trunc24(SHA-256(bytes[0..50])).
    /// </summary>
    internal static byte[] BuildHeaderPacket(byte[] headerBytes, byte[] headerHash)
    {
        var packet = new byte[PacketFormat.PacketSize];
        headerBytes.AsSpan().CopyTo(packet);
        headerHash.AsSpan().CopyTo(packet.AsSpan(PacketFormat.HeaderHashOffset));
        return packet;
    }

    /// <summary>
    /// Сектор данных: seqNum(2) + payload(64) + D3(9).
    /// D3 = Trunc9(SHA-256(headerHash ‖ sector[0..65])).
    /// </summary>
    internal static byte[] BuildDataSector(int sectorNum, byte[] payload, byte[] headerHash)
    {
        var sector = new byte[PacketFormat.PacketSize];

        // D1: Sector number (2 bytes LE)
        sector[0] = (byte)(sectorNum & 0xFF);
        sector[1] = (byte)((sectorNum >> 8) & 0xFF);

        // D2: Payload
        payload.AsSpan().CopyTo(
            sector.AsSpan(PacketFormat.SectorNumberSize, PacketFormat.PayloadSize));

        // D3: хеш с сидом headerHash (поле H5)
        var hash = PacketHasher.ComputeSectorHash(
            sector.AsSpan(0, PacketFormat.SectorContentSize), headerHash);
        hash.AsSpan().CopyTo(sector.AsSpan(PacketFormat.SectorHashOffset));

        return sector;
    }
}