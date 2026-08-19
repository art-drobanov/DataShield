using System.Text;
using DataShield.Codec.Ecc;
using DataShield.Codec.IO;
using DataShield.Codec.Packets;
using DataShield.Codec.Reporting;
using DataShield.Codec.StreamFilter;
using DataShield.Codec.StreamProcessor;
using DataShield.Codec.StreamProcessor.Versions;
using DataShield.Codec.StreamScanner;
using DataShield.Interfaces;

namespace DataShield.Codec;

// ─────────────────────────────────────────────────────────────────────────────
//  Декодер: поток → фильтр → сканер → накопитель приёма
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Декодер потока DataShield, построенный на потоковой конвейерной модели:
/// источник байт → фильтр Base64 (txt-режим) → побайтовый сканер окон →
/// накопитель приёма.
///
/// Заголовок опознаётся автономной проверкой усечённого SHA-256 (H5 =
/// Trunc24(SHA-256(H1–H4))), сектор данных привязывается к заголовку сидом H5
/// (D3 = Trunc9(SHA-256(H5 ‖ D1 ‖ D2))). Порядок прихода
/// произволен: секторы, просканированные до появления соответствующего
/// заголовка, добираются адресным повторным сканированием удержанных данных
/// (см. <see cref="SlidingWindowScanner.RequestRescan"/>); сканеры обоих
/// форматов сохраняют удержанные данные между вызовами Scan.
///
/// Поддерживаются два формата входного потока:
/// <list type="bullet">
///   <item><b>Base64</b> — текст (фильтр отбрасывает мусор вне алфавита).</item>
///   <item><b>Binary</b> — сырые 75-байтные пакеты (фильтр исключён из цепочки).</item>
/// </list>
///
/// Декодер удерживает пользовательские данные (слоты приёма и хвосты
/// сканеров): операции сериализуются внутренней блокировкой, освобождение —
/// через <see cref="Dispose"/> (вайп), сброс между операциями — через
/// <see cref="Clear"/>.
/// </summary>
public sealed class StreamDecoder : StreamCodec
{
    // Накопитель приёма (слоты заголовков и секторов)
    private readonly DataShield.Codec.StreamProcessor.StreamProcessor _processor;

    private readonly SectorVersionSearchOptions _searchOptions;

    // Сканеры обоих форматов: удержанные данные живут между вызовами Scan
    private SlidingWindowScanner? _txtScanner;
    private SlidingWindowScanner? _binScanner;

    // Сканер, к выходу которого подключён накопитель в текущем вызове Scan
    private SlidingWindowScanner? _activeScanner;

    /// <summary>Все обнаруженные слоты приёма (снимок).</summary>
    public IReadOnlyList<ReceptionSlot> Slots => _processor.Slots;

    /// <summary>Количество уникальных файлов (слотов) в потоке.</summary>
    public int FileCount => _processor.FileCount;

    /// <summary>
    /// Создать декодер с настройками поиска версий секторов
    /// (по умолчанию — <see cref="SectorVersionSearchOptions.Default"/>).
    /// </summary>
    /// <param name="searchOptions">Настройки обхода коллизий версий.</param>
    /// <param name="sectorScheme">
    /// Схема хеширования секторов. Classic — приём только классических
    /// пакетов; VirtualIndex — гибридный приём: классические секторы
    /// проверяются первыми, затем перебирается виртуальный индекс (файлы
    /// обеих схем читаются одновременно).
    /// </param>
    /// <param name="virtualIndexSectorLimit">
    /// Порог схемы VirtualIndex: слоты с N+M выше порога проверяются
    /// только по классической схеме.
    /// </param>
    public StreamDecoder(
        SectorVersionSearchOptions? searchOptions = null,
        SectorScheme sectorScheme = SectorScheme.Classic,
        int virtualIndexSectorLimit = VirtualIndexHasher.DefaultSectorLimit)
        : base(sectorScheme, virtualIndexSectorLimit)
    {
        _searchOptions =
            searchOptions ??
            SectorVersionSearchOptions.Default;

        _searchOptions.Validate();

        _processor = new DataShield.Codec.StreamProcessor.StreamProcessor(
            _searchOptions,
            sectorScheme: sectorScheme,
            virtualIndexSectorLimit: virtualIndexSectorLimit);
        _processor.HeaderAccepted += OnHeaderAccepted;
    }

    // ── Текстовый вход (Base64) ─────────────────────────────────────────────

    /// <summary>
    /// Сканировать поток Base64-строк: не-Base64 символы (переводы строк,
    /// пробелы, мусор) отбрасываются фильтром конвейера.
    /// </summary>
    public void Scan(IEnumerable<string> lines) =>
        Scan(lines, progress: null, default);

    /// <inheritdoc cref="Scan(IEnumerable{string})"/>
    /// <param name="lines">Строки Base64-текста входного потока.</param>
    /// <param name="progress">Приёмник прогресса (глобальная шкала 0..100).</param>
    /// <param name="ct">Токен отмены.</param>
    public void Scan(IEnumerable<string> lines, IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ct.ThrowIfCancellationRequested();

        lock (Gate)
        {
            ThrowIfDisposed();

            var bytes = Encoding.UTF8.GetBytes(string.Concat(lines));
            if (bytes.Length < PacketFormat.Base64Size) return;

            RunPipeline(
                text: true,
                source: new ByteArraySource(bytes),
                totalBytes: bytes.Length,
                progress, ct);
        }
    }

    // ── Двоичный вход (сырые пакеты) ────────────────────────────────────────

    /// <summary>
    /// Сканировать поток двоичных 75-байтных пакетов. Сканирование —
    /// скользящим окном с шагом 1 байт (допускает шум/рассинхронизацию
    /// между пакетами).
    /// </summary>
    public void Scan(byte[] data) => Scan(data, progress: null, default);

    /// <inheritdoc cref="Scan(byte[])"/>
    /// <param name="data">Сырые байты входного потока (75-байтные пакеты).</param>
    /// <param name="progress">Приёмник прогресса (глобальная шкала 0..100).</param>
    /// <param name="ct">Токен отмены.</param>
    public void Scan(byte[] data, IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(data);
        ct.ThrowIfCancellationRequested();

        lock (Gate)
        {
            ThrowIfDisposed();

            if (data.Length < PacketFormat.PacketSize) return;

            RunPipeline(
                text: false,
                source: new ByteArraySource(data),
                totalBytes: data.Length,
                progress, ct);
        }
    }

    // ── Потоковый вход (Stream) ─────────────────────────────────────────────

    /// <summary>
    /// Сканировать FEC-поток заданного формата, читая его из <see cref="Stream"/>
    /// (в том числе из памяти — <see cref="MemoryStream"/>). Формат указывается
    /// явно: у потока нет расширения для автоопределения.
    /// </summary>
    public void Scan(Stream input, OutputFormat format) =>
        Scan(input, format, progress: null, default);

    /// <inheritdoc cref="Scan(Stream, OutputFormat)"/>
    /// <param name="input">Входной поток FEC-данных (не закрывается).</param>
    /// <param name="format">Формат входного потока.</param>
    /// <param name="progress">Приёмник прогресса (глобальная шкала 0..100).</param>
    /// <param name="ct">Токен отмены.</param>
    public void Scan(
        Stream input, OutputFormat format,
        IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ct.ThrowIfCancellationRequested();

        lock (Gate)
        {
            ThrowIfDisposed();

            var totalBytes = input.CanSeek
                ? input.Length - input.Position
                : 0;

            RunPipeline(
                format != OutputFormat.Binary,
                new StreamSource(input),
                totalBytes,
                progress, ct);
        }
    }

    // ── Сборка файла ────────────────────────────────────────────────────────

    /// <summary>
    /// Попытаться собрать файл по заголовку.
    /// Заголовок сериализуется в байты и сравнивается со слотами побайтово.
    /// </summary>
    public byte[]? TryAssemble(HeaderContent header) => TryAssemble(header, progress: null, default);

    /// <inheritdoc cref="TryAssemble(HeaderContent)"/>
    /// <param name="header">Заголовок искомого файла.</param>
    /// <param name="progress">Приёмник прогресса.</param>
    /// <param name="ct">Токен отмены.</param>
    public byte[]? TryAssemble(HeaderContent header, IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(header);

        lock (Gate)
        {
            ThrowIfDisposed();

            var hb = header.ToBytes();
            foreach (var slot in _processor.Slots)
            {
                if (slot.HeaderMatches(hb))
                    return slot.TryAssemble(Rs, progress, ct);
            }
            return null;
        }
    }

    // ── Сброс и освобождение ────────────────────────────────────────────────

    /// <summary>
    /// Сбросить накопленный приём (слоты, секторы, удержанные хвосты
    /// сканеров) — вайп данных пользователя. Декодер остаётся пригодным
    /// для новых вызовов <c>Scan</c> с прежней конфигурацией.
    /// </summary>
    public void Clear()
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            ResetState();
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;

        lock (Gate)
        {
            _processor.Detach();
            _processor.HeaderAccepted -= OnHeaderAccepted;
            ResetState();
        }
    }

    // Отключить конвейер и вайпнуть удержанные данные; сканеры отбрасываются.
    private void ResetState()
    {
        _processor.Detach();
        _processor.Clear();

        _txtScanner?.Detach();
        _binScanner?.Detach();
        _txtScanner?.Clear();
        _binScanner?.Clear();

        _txtScanner = null;
        _binScanner = null;
        _activeScanner = null;
    }

    // ── Конвейер ────────────────────────────────────────────────────────────

    /// <summary>
    /// Прогнать источник через конвейер до конца данных. Сканер сохраняет
    /// удержанные данные для адресной перепривязки последующими вызовами.
    /// </summary>
    private void RunPipeline(
        bool text, IDataSource source, long totalBytes,
        IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        VerifySha256();

        var scanner = text ? GetTxtScanner() : GetBinScanner();
        ByteRangeFilter? filter = null;
        _activeScanner = scanner;

        if (text)
        {
            filter = ByteRangeFilter.CreateBase64();
            filter.Attach(source);
            scanner.Detach();
            scanner.Attach(filter);
        }
        else
        {
            scanner.Detach();
            scanner.Attach(source);
        }

        _processor.Detach();
        _processor.Attach(scanner);

        // Прогресс: до первого заголовка — «поиск заголовков», затем —
        // «приём секторов»; процент — доля потреблённых байт источника
        void ReportProgress(long consumed)
        {
            ct.ThrowIfCancellationRequested();
            if (progress is null || totalBytes <= 0) return;

            var phase = _processor.FileCount == 0
                ? CodecStrings.HeaderSearch
                : CodecStrings.SectorSearch;

            var pct = (int)(consumed * 100 / totalBytes);
            progress.Report(CodecProgress.Create(pct, phase));
        }

        scanner.ConsumedAdvanced += ReportProgress;
        try
        {
            source.Start();
            source.Completion.Wait(ct);

            // Сброс хвостов цепочки по порядку: фильтр → сканер → накопитель.
            filter?.Complete();
            scanner.Complete();
            _processor.Complete();
        }
        finally
        {
            scanner.ConsumedAdvanced -= ReportProgress;
            scanner.Detach();
            filter?.Detach();
        }

        progress?.Report(CodecProgress.Create(100, CodecStrings.Done));
    }

    // Ленивые сканеры форматов: создаются при первом использовании своего
    // формата и сохраняют удержанные данные до Clear/Dispose.

    /// <summary>Сканер txt-режима (окно 100 Base64-символов → пакет).</summary>
    private SlidingWindowScanner GetTxtScanner() =>
        _txtScanner ??= new SlidingWindowScanner(
            PacketFormat.Base64Size, TxtWindow);

    /// <summary>Сканер binary-режима (окно 75 сырых байт).</summary>
    private SlidingWindowScanner GetBinScanner() =>
        _binScanner ??= new SlidingWindowScanner(
            PacketFormat.PacketSize, BinWindow);

    // ── Обработка окон ──────────────────────────────────────────────────────

    /// <summary>
    /// Окно txt-режима: 100 Base64-символов → пакет. Неуспех декода или
    /// нераспознанный пакет — сдвиг окна на 1 байт.
    /// </summary>
    private int TxtWindow(ReadOnlySpan<byte> window, out byte[]? emitted)
    {
        emitted = null;

        var packet = new byte[PacketFormat.PacketSize];
        if (!TryDecodePacket(window, packet)) return 1;
        if (!_processor.Recognizes(packet)) return 1;

        emitted = packet;
        return PacketFormat.Base64Size;
    }

    /// <summary>
    /// Окно binary-режима: сырые 75 байт. Нераспознанный пакет — сдвиг на 1 байт.
    /// </summary>
    private int BinWindow(ReadOnlySpan<byte> window, out byte[]? emitted)
    {
        emitted = null;

        if (!_processor.Recognizes(window)) return 1;

        emitted = window.ToArray();
        return PacketFormat.PacketSize;
    }

    /// <summary>
    /// Принят новый заголовок: просим оба сканера адресно перепривязать
    /// удержанные данные (секторы, пришедшие раньше заголовка, добирают свой
    /// слот). Повторный проход проверяет секторы только по этому заголовку,
    /// поэтому подтверждения остальных слотов не затрагиваются. Выход
    /// перепривязываемого сканера направляется в накопитель.
    /// </summary>
    /// <summary>
    /// Новый заголовок принят накопителем: перепривязать удержанные данные
    /// обоих сканеров (текстового и бинарного) под хеш нового заголовка.
    /// </summary>
    private void OnHeaderAccepted(HeaderContent header, byte[] headerHash)
    {
        var total = header.TotalVolumeCount;

        RequestRebind(_txtScanner, text: true, headerHash, total);
        RequestRebind(_binScanner, text: false, headerHash, total);
    }

    /// <summary>
    /// Запросить адресное повторное сканирование удержанных данным потока
    /// под новый заголовок (секторы, пришедшие раньше своего заголовка).
    /// </summary>
    private void RequestRebind(
        SlidingWindowScanner? scanner, bool text, byte[] headerHash, int total)
    {
        if (scanner is null) return;

        if (ReferenceEquals(scanner, _activeScanner))
        {
            // Накопитель уже подключён к сканеру; запрос выполняется
            // немедленно либо откладывается до конца текущего прохода.
            scanner.RequestRescan(
                (window, out emitted) => RebindWindow(
                    window, headerHash, total, text,
                    SectorScheme, VirtualIndexSectorLimit, out emitted));
            return;
        }

        // Сканер другого формата: подключить накопитель на время перепривязки.
        // Вызовы Scan последовательны, поэтому посторонний сканер простаивает
        // и запрос выполняется синхронно; Complete сбрасывает хвост выдачи.
        var active = _activeScanner;
        _processor.Detach();
        _processor.Attach(scanner);

        scanner.RequestRescan(
            (window, out emitted) => RebindWindow(
                window, headerHash, total, text,
                SectorScheme, VirtualIndexSectorLimit, out emitted));
        scanner.Complete();

        _processor.Detach();
        if (active is not null)
            _processor.Attach(active);
    }

    /// <summary>
    /// Окно адресной перепривязки: принять пакет, если он является сектором
    /// данных опоздавшего заголовка — классическим (диапазон номера + хеш
    /// с сидом H5) либо, при включённой схеме VirtualIndex и N+M в пределах
    /// порога, сектором с виртуальным индексом (перебор от нуля: удержанные
    /// данные идут в порядке потока). Окончательная классификация пакета —
    /// за накопителем приёма.
    /// </summary>
    private static int RebindWindow(
        ReadOnlySpan<byte> window, byte[] headerHash, int total, bool text,
        SectorScheme sectorScheme, int virtualIndexLimit, out byte[]? emitted)
    {
        emitted = null;

        byte[] packet;
        if (text)
        {
            packet = new byte[PacketFormat.PacketSize];
            if (!TryDecodePacket(window, packet)) return 1;
        }
        else
        {
            packet = window.ToArray();
        }

        var sectorNum = packet[0] | (packet[1] << 8);
        if (sectorNum >= 0 && sectorNum < total &&
            PacketHasher.VerifySectorPacket(packet, headerHash))
        {
            emitted = packet;
            return text ? PacketFormat.Base64Size : PacketFormat.PacketSize;
        }

        if (sectorScheme == SectorScheme.VirtualIndex &&
            total <= virtualIndexLimit &&
            VirtualIndexHasher.TryVerifyPacket(packet, headerHash, total, 0, out _))
        {
            emitted = packet;
            return text ? PacketFormat.Base64Size : PacketFormat.PacketSize;
        }

        return 1;
    }

    /// <summary>
    /// Декодировать окно из 100 Base64-байт в 75 байт.
    /// Возвращает false при ошибке декодирования.
    /// </summary>
    private static bool TryDecodePacket(ReadOnlySpan<byte> window, byte[] packet)
    {
        Span<char> chars = stackalloc char[PacketFormat.Base64Size];
        for (var i = 0; i < window.Length; i++)
            chars[i] = (char)window[i];

        return Convert.TryFromBase64Chars(
            chars, packet, out var written)
            && written == PacketFormat.PacketSize;
    }
}
