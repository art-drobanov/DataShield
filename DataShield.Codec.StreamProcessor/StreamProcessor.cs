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
public sealed class StreamProcessor : DataProcessorBase
{
    private readonly SectorVersionSearchOptions _searchOptions;
    private readonly object _state = new();

    // Слоты приёма (аккумуляторы заголовков и секторов)
    private readonly List<ReceptionSlot> _slots = new();

    // Неполный пакет на границе входных кусков
    private readonly byte[] _pending = new byte[PacketFormat.PacketSize];
    private int _pendingFill;

    /// <summary>
    /// Создать накопитель с настройками поиска версий секторов
    /// (по умолчанию — <see cref="SectorVersionSearchOptions.Default"/>).
    /// </summary>
    /// <param name="searchOptions">Настройки обхода коллизий версий.</param>
    /// <param name="bufferSize">Порог выдачи принятых пакетов, байт.</param>
    public StreamProcessor(
        SectorVersionSearchOptions? searchOptions = null,
        int bufferSize = 1200)
        : base(bufferSize)
    {
        _searchOptions =
            searchOptions ??
            SectorVersionSearchOptions.Default;

        _searchOptions.Validate();
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

    /// <summary>
    /// Предикат распознавания пакета без побочных эффектов: автономный хеш
    /// заголовка либо хеш сектора под один из известных заголовков.
    /// Используется делегатом окна сканера для решения о продвижении.
    /// </summary>
    /// <param name="packet">Кандидат-пакет (75 байт).</param>
    /// <returns>true — пакет будет принят накопителем.</returns>
    public bool Recognizes(ReadOnlySpan<byte> packet)
    {
        if (packet.Length != PacketFormat.PacketSize) return false;

        if (IsHeaderPacket(packet)) return true;

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

    private bool AcceptPacket(byte[] packet, out ReceptionSlot? newSlot)
    {
        if (IsHeaderPacket(packet))
        {
            newSlot = AcceptHeader(packet);
            return true;
        }

        newSlot = null;

        var accepted = false;
        foreach (var slot in _slots)
            if (AcceptSectorForSlot(packet, slot))
                accepted = true;

        return accepted;
    }

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

        var newSlot = new ReceptionSlot(headerBytes, header, headerHash, _searchOptions);
        _slots.Add(newSlot);
        return newSlot;
    }

    private static bool IsHeaderPacket(ReadOnlySpan<byte> packet) =>
        PacketHasher.VerifyHeaderPacket(packet);

    private static bool SectorMatches(ReadOnlySpan<byte> packet, ReceptionSlot slot)
    {
        var sectorNum = packet[0] | (packet[1] << 8);
        if (sectorNum < 0 || sectorNum >= slot.TotalVolumeCount)
            return false;

        return PacketHasher.VerifySectorPacket(packet, slot.HeaderHash);
    }

    private static bool AcceptSectorForSlot(byte[] packet, ReceptionSlot slot)
    {
        if (!SectorMatches(packet, slot)) return false;

        var sectorNum = packet[0] | (packet[1] << 8);
        var payload = packet[PacketFormat.SectorNumberSize..
            (PacketFormat.SectorNumberSize + PacketFormat.PayloadSize)].ToArray();

        return slot.AddSector(sectorNum, payload);
    }
}
