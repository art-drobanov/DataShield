using DataShield.Codec.Packets;
using DataShield.Codec.StreamProcessor.Versions;
using DataShield.Interfaces;

namespace DataShield.Codec.StreamProcessor;

// ─────────────────────────────────────────────────────────────────────────────
//  Накопитель приёма с подробным API оценки содержимого
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Накапливает данные, полученные от сканера потока, и предоставляет
/// подробное API для оценки содержимого: накопленные заголовки и их
/// подтверждённость копиями, ожидаемое количество секторов в потоке,
/// количество полученных секторов, ожидаемые количества секторов данных
/// и ECC, карта секторов, карта коллизий с указанием кратности.
///
/// Классификация пакетов: заголовок опознаётся автономной проверкой усечённого
/// SHA-256 (H5 = Trunc24(SHA-256(H1–H4))); сектор данных привязывается к
/// заголовку сидом H5 (D3 = Trunc9(SHA-256(H5 ‖ D1 ‖ D2))). Вход накапливается
/// потокобезопасно; чтение состояния возможно параллельно с приёмом (снимки
/// под блокировкой).
/// </summary>
public sealed class StreamProcessor : DataProcessorBase, IDisposable
{
    private readonly SectorVersionSearchOptions _searchOptions;
    private readonly object _state = new();

    // Схема хеширования секторов (см. SectorScheme)
    private readonly SectorScheme _sectorScheme;

    // Порог применимости схемы VirtualIndex: максимум N+M томов на слот
    private readonly int _virtualIndexSectorLimit;

    // Секрет хеширования операции (null/пустая — без секрета)
    private readonly string? _hashSecret;

    // Режим работы с сигнатурными пакетами SHA-512 (см. SignatureMode)
    private readonly SignatureMode _signatureMode;

    // Уникальные сигнатуры SHA-512 из принятых сигнатурных пакетов (копии
    // не накапливаются — смысла нет). В режиме Base не заполняется.
    private readonly List<byte[]> _signatures = new();

    // Счётчик принятых сигнатурных пакетов (с учётом копий)
    private int _signaturePacketCount;

    // Слоты приёма (аккумуляторы заголовков и секторов)
    private readonly List<ReceptionSlot> _slots = new();

    // Неполный пакет на границе входных кусков
    private readonly byte[] _pending = new byte[PacketFormat.PacketSize];
    private int _pendingFill;

    /// <summary>Схема хеширования секторов данных.</summary>
    public SectorScheme SectorScheme => _sectorScheme;

    /// <summary>Порог применимости схемы VirtualIndex по числу томов (N+M).</summary>
    public int VirtualIndexSectorLimit => _virtualIndexSectorLimit;

    /// <summary>Режим работы с сигнатурными пакетами SHA-512.</summary>
    public SignatureMode SignatureMode => _signatureMode;

    /// <summary>Принято сигнатурных пакетов SHA-512 (с учётом копий).</summary>
    public int SignaturePacketCount
    {
        get { lock (_state) return _signaturePacketCount; }
    }

    /// <summary>Уникальные принятые сигнатуры SHA-512 (снимок).</summary>
    public IReadOnlyList<byte[]> AcceptedSignatures
    {
        get { lock (_state) return _signatures.ToArray(); }
    }

    /// <summary>
    /// Активна ли расширенная проверка SHA-512: Force — всегда; Auto — при
    /// наличии хотя бы одной принятой сигнатуры; Base — никогда.
    /// </summary>
    public bool SignatureCheckActive
    {
        get
        {
            lock (_state)
            {
                return _signatureMode switch
                {
                    SignatureMode.Base => false,
                    SignatureMode.Force => true,
                    _ => _signatures.Count > 0,
                };
            }
        }
    }

    /// <summary>
    /// Сигнатуры для расширенной проверки сборки: снимок уникальных
    /// сигнатур, если проверка активна (<see cref="SignatureCheckActive"/>),
    /// иначе null (проверка отключена).
    /// </summary>
    public IReadOnlyList<byte[]>? SignaturesForCheck()
    {
        lock (_state)
        {
            if (_signatureMode == SignatureMode.Base) return null;
            if (_signatureMode == SignatureMode.Auto && _signatures.Count == 0)
                return null;
            return _signatures.ToArray();
        }
    }

    /// <summary>
    /// Создать накопитель с настройками поиска версий секторов
    /// (по умолчанию — <see cref="SectorVersionSearchOptions.Default"/>).
    /// </summary>
    /// <param name="searchOptions">Настройки обхода коллизий версий.</param>
    /// <param name="bufferSize">Порог выдачи принятых пакетов, байт.</param>
    /// <param name="sectorScheme">
    /// Схема хеширования секторов. Classic — приём только классических
    /// пакетов; VirtualIndex — гибридный приём: сначала классическая
    /// проверка, затем перебор виртуального индекса (файлы обеих схем
    /// читаются одновременно).
    /// </param>
    /// <param name="virtualIndexSectorLimit">
    /// Порог схемы VirtualIndex: слоты с N+M выше порога проверяются
    /// только по классической схеме.
    /// </param>
    /// <param name="signatureMode">
    /// Режим работы с сигнатурными пакетами SHA-512: Base — детект без
    /// влияния на сборку; Auto (по умолчанию) — расширенная проверка при
    /// приёме хотя бы одного сигнатурного пакета; Force — проверка всегда.
    /// </param>
    /// <param name="hashSecret">
    /// Секрет хеширования операции: должен совпадать с секретом
    /// кодирования, иначе заголовки и секторы не опознаются.
    /// null/пустая строка — обычный режим без секрета.
    /// </param>
    public StreamProcessor(
        SectorVersionSearchOptions? searchOptions = null,
        int bufferSize = 1200,
        SectorScheme sectorScheme = SectorScheme.Classic,
        int virtualIndexSectorLimit = VirtualIndexHasher.DefaultSectorLimit,
        SignatureMode signatureMode = SignatureMode.Auto,
        string? hashSecret = null)
        : base(bufferSize)
    {
        if (!Enum.IsDefined(sectorScheme))
            throw new ArgumentOutOfRangeException(nameof(sectorScheme));
        if (virtualIndexSectorLimit is < 1 or > VirtualIndexHasher.DefaultSectorLimit)
            throw new ArgumentOutOfRangeException(
                nameof(virtualIndexSectorLimit),
                $"Порог должен быть в диапазоне 1..{VirtualIndexHasher.DefaultSectorLimit}.");
        if (!Enum.IsDefined(signatureMode))
            throw new ArgumentOutOfRangeException(nameof(signatureMode));

        _searchOptions =
            searchOptions ??
            SectorVersionSearchOptions.Default;

        _searchOptions.Validate();
        _sectorScheme = sectorScheme;
        _virtualIndexSectorLimit = virtualIndexSectorLimit;
        _signatureMode = signatureMode;
        _hashSecret = hashSecret;
    }

    /// <summary>Все накопленные слоты приёма (снимок).</summary>
    public IReadOnlyList<ReceptionSlot> Slots
    {
        get { lock (_state) return _slots.ToArray(); }
    }

    /// <summary>Количество уникальных файлов (слотов) в потоке.</summary>
    public int FileCount
    {
        get { lock (_state) return _slots.Count; }
    }

    /// <summary>Суммарное количество принятых номеров секторов по всем слотам.</summary>
    public int TotalReceivedSectorCount
    {
        get
        {
            lock (_state)
            {
                var count = 0;
                foreach (var slot in _slots) count += slot.ReceivedSectorCount;
                return count;
            }
        }
    }

    /// <summary>Суммарное количество принятых копий секторов по всем слотам.</summary>
    public long TotalReceivedSectorCopyCount
    {
        get
        {
            long count = 0;
            lock (_state)
            {
                foreach (var slot in _slots) count += slot.ReceivedSectorCopyCount;
                return count;
            }
        }
    }

    /// <summary>Суммарное количество секторов с коллизиями версий по всем слотам.</summary>
    public int TotalCollisionSectorCount
    {
        get
        {
            lock (_state)
            {
                var count = 0;
                foreach (var slot in _slots) count += slot.CollisionSectorCount;
                return count;
            }
        }
    }

    /// <summary>
    /// Принят новый заголовок (создан слот). Даёт вышестоящим модулям
    /// команду на адресную перепривязку удержанных данных. Вызывается вне
    /// блокировки состояния.
    /// </summary>
    public event Action<HeaderContent, byte[]>? HeaderAccepted;

    // ── Сброс и освобождение ────────────────────────────────────────────────

    /// <summary>
    /// Сбросить накопленный приём: слоты (заголовки, секторы, версии) и
    /// неполный пакет на границе кусков — вайп данных пользователя.
    /// Накопитель остаётся пригодным для нового приёма с прежней
    /// конфигурацией.
    /// </summary>
    public void Clear()
    {
        lock (_state)
        {
            _slots.Clear();
            foreach (var signature in _signatures) Array.Clear(signature, 0, signature.Length);
            _signatures.Clear();
            _signaturePacketCount = 0;
            _pendingFill = 0;
            Array.Clear(_pending, 0, _pending.Length);
        }
    }

    /// <summary>Отключиться от источника и вайпнуть накопленный приём.</summary>
    public void Dispose()
    {
        Detach();
        Clear();
    }

    /// <summary>
    /// Предикат распознавания пакета без побочных эффектов: автономный хеш
    /// заголовка, автономный тег сигнатурного пакета SHA-512 либо хеш
    /// сектора под один из известных заголовков. Используется делегатом
    /// окна сканера для решения о продвижении.
    /// </summary>
    /// <param name="packet">Кандидат-пакет (75 байт).</param>
    /// <returns>true — пакет будет принят накопителем.</returns>
    public bool Recognizes(ReadOnlySpan<byte> packet)
    {
        if (packet.Length != PacketFormat.PacketSize) return false;

        if (IsHeaderPacket(packet)) return true;
        if (SignaturePacket.Verify(packet, _hashSecret)) return true;

        lock (_state)
        {
            foreach (var slot in _slots)
                if (SectorMatches(packet, slot))
                    return true;
            return false;
        }
    }

    /// <inheritdoc/>
    protected override void ProcessChunk(byte[] chunk)
    {
        List<ReceptionSlot>? newSlots = null;

        lock (_state)
        {
            var pos = 0;
            while (true)
            {
                var need = PacketFormat.PacketSize - _pendingFill;
                var take = Math.Min(need, chunk.Length - pos);

                if (take > 0)
                {
                    Buffer.BlockCopy(chunk, pos, _pending, _pendingFill, take);
                    _pendingFill += take;
                    pos += take;
                }

                if (_pendingFill < PacketFormat.PacketSize) break;

                var packet = (byte[])_pending.Clone();
                if (AcceptPacket(packet, out var newSlot))
                    EmitPacket(packet);

                if (newSlot is not null)
                    (newSlots ??= new List<ReceptionSlot>()).Add(newSlot);

                _pendingFill = 0;
            }
        }

        if (newSlots is not null)
            foreach (var slot in newSlots)
                HeaderAccepted?.Invoke(slot.Header, slot.HeaderHash);
    }

    /// <summary>
    /// Маршрутизация принятого пакета: заголовок попадает в соответствующий
    /// слот (существующий или новый), сигнатурный пакет — в накопитель
    /// сигнатур (Base — только счётчик), сектор — во все слоты, под которые
    /// он подходит. Возвращает true, если пакет принят хотя бы одним путём.
    /// </summary>
    private bool AcceptPacket(byte[] packet, out ReceptionSlot? newSlot)
    {
        if (IsHeaderPacket(packet))
        {
            newSlot = AcceptHeader(packet);
            return true;
        }

        newSlot = null;

        var accepted = false;

        // Сигнатурный пакет не привязан к файлу и не влияет на дальнейший
        // парсинг: его блоб дополнительно проверяется путём секторов ниже,
        // ложная коллизия неопасна (критерий «любой из» на финале).
        if (SignaturePacket.Verify(packet, _hashSecret))
        {
            AcceptSignature(packet);
            accepted = true;
        }

        foreach (var slot in _slots)
            if (AcceptSectorForSlot(packet, slot))
                accepted = true;

        return accepted;
    }

    /// <summary>
    /// Принять сигнатурный пакет: счётчик копий всегда; уникальная
    /// сигнатура S1 сохраняется в режимах Auto/Force (копии не накапливаются).
    /// </summary>
    private void AcceptSignature(byte[] packet)
    {
        _signaturePacketCount++;

        if (_signatureMode == SignatureMode.Base)
            return;

        var signature = packet.AsSpan(0, PacketFormat.SignatureSize);

        foreach (var existing in _signatures)
            if (signature.SequenceEqual(existing))
                return;

        _signatures.Add(signature.ToArray());
    }

    /// <summary>
    /// Принять пакет-заголовок: совпадающий существующий слот — только
    /// счётчик копий; новый заголовок — создание слота. Возвращает новый
    /// слот либо null, если слот уже существовал.
    /// </summary>
    private ReceptionSlot? AcceptHeader(byte[] packet)
    {
        var headerSpan = packet.AsSpan(0, PacketFormat.HeaderContentSize);

        foreach (var slot in _slots)
        {
            if (slot.HeaderMatches(headerSpan))
            {
                slot.IncrementHeaderCount();
                return null;
            }
        }

        var headerBytes = headerSpan.ToArray();
        var header = HeaderContent.ReadFrom(headerBytes);
        var headerHash = packet[PacketFormat.HeaderHashOffset..].ToArray();

        var newSlot = new ReceptionSlot(headerBytes, header, headerHash, _searchOptions, _hashSecret);
        _slots.Add(newSlot);
        return newSlot;
    }

    /// <summary>Пакет является заголовком (верификация автономного хеша заголовка).</summary>
    private bool IsHeaderPacket(ReadOnlySpan<byte> packet) =>
        PacketHasher.VerifyHeaderPacket(packet, _hashSecret);

    /// <summary>
    /// Чистый предикат (без побочных эффектов): пакет является сектором
    /// данного слота по классической схеме либо — при включённой схеме
    /// VirtualIndex и N+M в пределах порога — по виртуальному индексу.
    /// </summary>
    private bool SectorMatches(ReadOnlySpan<byte> packet, ReceptionSlot slot)
    {
        if (SectorMatchesClassic(packet, slot))
            return true;

        return _sectorScheme == SectorScheme.VirtualIndex &&
            slot.TotalVolumeCount <= _virtualIndexSectorLimit &&
            VirtualIndexHasher.TryVerifyPacket(
                packet,
                slot.HeaderHash,
                slot.TotalVolumeCount,
                slot.VirtualIndexCursor,
                out _,
                _hashSecret);
    }

    /// <summary>
    /// Классическая схема адресации: номер сектора — в первых двух байтах
    /// пакета (D1), проверка — хеш сектора под хеш заголовка слота.
    /// </summary>
    private bool SectorMatchesClassic(
        ReadOnlySpan<byte> packet, ReceptionSlot slot)
    {
        var sectorNum = packet[0] | (packet[1] << 8);
        if (sectorNum < 0 || sectorNum >= slot.TotalVolumeCount)
            return false;

        return PacketHasher.VerifySectorPacket(packet, slot.HeaderHash, _hashSecret);
    }

    /// <summary>
    /// Принять пакет-сектор в конкретный слот: классическая схема либо
    /// (при включённой схеме VirtualIndex в пределах порога) восстановление
    /// индекса перебором от курсора. Мутирует слот.
    /// </summary>
    private bool AcceptSectorForSlot(byte[] packet, ReceptionSlot slot)
    {
        // Классическая схема: номер сектора в D1, payload со смещением 2.
        // Проверяется первой — старые (Classic) потоки читаются и при
        // включённой схеме VirtualIndex (гибридный приём).
        if (SectorMatchesClassic(packet, slot))
        {
            var sectorNum = packet[0] | (packet[1] << 8);
            var payload = packet[PacketFormat.SectorNumberSize..
                (PacketFormat.SectorNumberSize + PacketFormat.PayloadSize)].ToArray();

            return slot.AddSector(sectorNum, payload);
        }

        // Схема VirtualIndex: индекс восстанавливается перебором от курсора
        // прихода, payload — с начала пакета.
        if (_sectorScheme != SectorScheme.VirtualIndex ||
            slot.TotalVolumeCount > _virtualIndexSectorLimit)
        {
            return false;
        }

        if (!VirtualIndexHasher.TryVerifyPacket(
                packet,
                slot.HeaderHash,
                slot.TotalVolumeCount,
                slot.VirtualIndexCursor,
                out var index,
                _hashSecret))
        {
            return false;
        }

        var virtualPayload = packet[..PacketFormat.PayloadSize].ToArray();
        var accepted = slot.AddSector(index, virtualPayload);

        if (accepted)
            slot.AdvanceVirtualIndexCursor(index);

        return accepted;
    }
}
