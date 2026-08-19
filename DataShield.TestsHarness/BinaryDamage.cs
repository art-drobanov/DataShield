using System.Text;
using DataShield.Codec.Packets;

namespace DataShield.TestsHarness;

// ─────────────────────────────────────────────────────────────────────────────
//  Повреждения бинарного потока (75-байтные пакеты подряд)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Повреждения бинарного представления потока DataShield.
///
/// Операции, чувствительные к выравниванию (порча/удаление/обрезка),
/// применяются к выровненному потоку. Шумовые вставки планируются на
/// выровненном потоке (смещения — границы реальных пакетов), а применяются
/// одним пакетом по убыванию смещения: вставка в более позднюю позицию
/// не сдвигает более ранние границы, поэтому ни один пакет не разрезается
/// «выровненной» вставкой.
/// </summary>
public static class BinaryDamage
{
    private static readonly byte[] TextGapPayload =
        Encoding.UTF8.GetBytes("мусор между пакетами #DataShield#");

    /// <summary>
    /// Порча до <paramref name="maxCount"/> корректных секторов: переворот всех
    /// байтов пакета ломает хеш — пакет становится стиранием.
    /// Поток должен быть выровнен по границам пакетов.
    /// </summary>
    public static int CorruptPackets(byte[] stream, int maxCount, byte[] headerHash, Random rng)
    {
        var indices = CollectSectorIndices(stream, headerHash);
        RandomInput.Shuffle(indices, rng);

        var corrupted = Math.Min(maxCount, indices.Count);
        for (var n = 0; n < corrupted; n++)
        {
            var offset = indices[n] * PacketFormat.PacketSize;
            for (var i = 0; i < PacketFormat.PacketSize; i++)
                stream[offset + i] ^= 0xFF;
        }

        return corrupted;
    }

    /// <summary>
    /// Вырезание до <paramref name="maxCount"/> целых пакетов-секторов (дыры).
    /// Возвращает поток без выбранных пакетов; их число — в <paramref name="removed"/>.
    /// </summary>
    public static byte[] RemovePackets(
        byte[] stream, int maxCount, byte[] headerHash, Random rng, out int removed)
    {
        var indices = CollectSectorIndices(stream, headerHash);
        RandomInput.Shuffle(indices, rng);

        var dropSet = new HashSet<int>(indices.Take(Math.Min(maxCount, indices.Count)));
        removed = dropSet.Count;
        if (removed == 0) return stream;

        var packetCount = stream.Length / PacketFormat.PacketSize;
        var result = new byte[stream.Length - removed * PacketFormat.PacketSize];
        var writeOffset = 0;

        for (var index = 0; index < packetCount; index++)
        {
            if (dropSet.Contains(index)) continue;
            stream.AsSpan(index * PacketFormat.PacketSize, PacketFormat.PacketSize)
                .CopyTo(result.AsSpan(writeOffset));
            writeOffset += PacketFormat.PacketSize;
        }

        return result;
    }

    /// <summary>
    /// Обрезка хвоста потока. Потеря корректных секторов не превышает
    /// <paramref name="maxSectorLoss"/>, в оставшейся части остаётся
    /// не менее двух копий заголовка. Возвращает фактические потери.
    /// </summary>
    public static byte[] TruncateTail(
        byte[] stream, int maxSectorLoss, byte[] headerHash, Random rng, out int lostSectors)
    {
        lostSectors = 0;
        var packetCount = stream.Length / PacketFormat.PacketSize;
        if (packetCount < 4) return stream;

        var desired = 1 + rng.Next(maxSectorLoss);
        var maxDrop = Math.Min(desired, packetCount / 2);

        for (var drop = maxDrop; drop > 0; drop--)
        {
            var lost = 0;
            for (var i = packetCount - drop; i < packetCount; i++)
                if (IsSectorAt(stream, i, headerHash)) lost++;

            if (lost > maxSectorLoss) continue;

            var headersKept = 0;
            for (var i = 0; i < packetCount - drop; i++)
                if (PacketProbe.IsHeader(stream.AsSpan(
                        i * PacketFormat.PacketSize, PacketFormat.PacketSize)))
                    headersKept++;

            if (headersKept < 2) continue;

            lostSectors = lost;
            var newLength = (packetCount - drop) * PacketFormat.PacketSize;
            var result = new byte[newLength];
            stream.AsSpan(0, newLength).CopyTo(result);
            return result;
        }

        return stream;
    }

    /// <summary>
    /// Спланировать рассинхронизацию: посторонние байты внутри потока.
    /// При <paramref name="lossy"/> = true позиция выбирается внутри
    /// случайного пакета-сектора — пакет, разрезанный вставкой, теряется
    /// (одно стирание), заголовки не затрагиваются. Иначе вставка
    /// выполняется строго на границе реальных пакетов.
    /// Смещение вычисляется в координатах переданного (выровненного) потока.
    /// </summary>
    public static (int Offset, byte[] Payload)? PlanDesync(
        byte[] stream, Random rng, bool lossy)
    {
        var offset = ChooseOffset(stream, lossy, rng);
        return offset < 0
            ? null
            : (offset, RandomInput.Bytes(1 + rng.Next(7), rng));
    }

    /// <summary>
    /// Спланировать текстовый мусор между пакетами (бинарный поток
    /// с текстовыми вставками). Семантика <paramref name="lossy"/> —
    /// как у <see cref="PlanDesync"/>.
    /// </summary>
    public static (int Offset, byte[] Payload)? PlanTextGap(
        byte[] stream, Random rng, bool lossy)
    {
        var offset = ChooseOffset(stream, lossy, rng);
        return offset < 0 ? null : (offset, TextGapPayload.ToArray());
    }

    /// <summary>
    /// Спланировать вставку фрагмента реального сектора (1..70 байт).
    /// При <paramref name="lossy"/> = true позиция внутри пакета-сектора
    /// (разрезанный пакет теряется — одно стирание, заголовки не затрагиваются),
    /// иначе — на границе пакетов.
    /// </summary>
    public static (int Offset, byte[] Payload)? PlanFragment(
        byte[] stream, Random rng, bool lossy)
    {
        var packetCount = stream.Length / PacketFormat.PacketSize;

        var sources = new List<int>();
        for (var i = 0; i < packetCount; i++)
            if (!PacketProbe.IsHeader(stream.AsSpan(
                    i * PacketFormat.PacketSize, PacketFormat.PacketSize)))
                sources.Add(i);

        if (sources.Count == 0) return null;

        var source = sources[rng.Next(sources.Count)] * PacketFormat.PacketSize;
        var length = 1 + rng.Next(PacketFormat.PacketSize - 1);
        var start = rng.Next(PacketFormat.PacketSize - length + 1);
        var payload = stream[
            (source + start)..(source + start + length)]
            .ToArray();

        var offset = ChooseOffset(stream, lossy, rng);
        return offset < 0 ? null : (offset, payload);
    }

    /// <summary>Спланировать шум в начале потока.</summary>
    public static (int Offset, byte[] Payload) PlanPrefixNoise(Random rng) =>
        (0, RandomInput.Bytes(1 + rng.Next(64), rng));

    /// <summary>Спланировать шум в конце потока.</summary>
    public static (int Offset, byte[] Payload) PlanSuffixNoise(
        byte[] stream, Random rng) =>
        (stream.Length, RandomInput.Bytes(1 + rng.Next(64), rng));

    /// <summary>
    /// Применить запланированные вставки к потоку в порядке убывания
    /// смещения. Вставка в более позднюю позицию не меняет более ранние
    /// смещения, поэтому все запланированные позиции остаются корректными.
    /// </summary>
    public static byte[] ApplyPlanned(
        byte[] stream, IReadOnlyList<(int Offset, byte[] Payload)> plan)
    {
        var ordered = plan
            .OrderByDescending(insertion => insertion.Offset)
            .ToList();

        foreach (var (offset, payload) in ordered)
            stream = Insert(stream, payload, offset);

        return stream;
    }

    // ── Служебные ───────────────────────────────────────────────────────────

    /// <summary>
    /// Смещение lossy-вставки: строго внутри случайного пакета, не
    /// являющегося заголовком. Заголовки ECC-бюджетом не защищены,
    /// поэтому разрезать их повреждения не должны: целые заголовки
    /// может удалить только <see cref="TruncateTail"/>, гарантирующий
    /// не менее двух оставшихся копий.
    /// Возвращает -1, если кандидатов нет.
    /// </summary>
    private static int ChooseLossyOffset(byte[] stream, Random rng)
    {
        var packetCount = stream.Length / PacketFormat.PacketSize;

        var candidates = new List<int>();
        for (var i = 0; i < packetCount; i++)
        {
            if (!PacketProbe.IsHeader(stream.AsSpan(
                    i * PacketFormat.PacketSize, PacketFormat.PacketSize)))
                candidates.Add(i);
        }

        if (candidates.Count == 0) return -1;

        var packet = candidates[rng.Next(candidates.Count)];
        return packet * PacketFormat.PacketSize +
               rng.Next(PacketFormat.PacketSize);
    }

    private static int ChooseOffset(byte[] stream, bool lossy, Random rng)
    {
        if (lossy) return ChooseLossyOffset(stream, rng);

        var packetCount = stream.Length / PacketFormat.PacketSize;
        if (packetCount >= 2)
            return (1 + rng.Next(packetCount - 1)) * PacketFormat.PacketSize;
        return -1;
    }

    private static bool IsSectorAt(byte[] stream, int packetIndex, byte[] headerHash) =>
        PacketProbe.IsValidSector(stream.AsSpan(
            packetIndex * PacketFormat.PacketSize, PacketFormat.PacketSize), headerHash);

    private static List<int> CollectSectorIndices(byte[] stream, byte[] headerHash)
    {
        var result = new List<int>();
        var packetCount = stream.Length / PacketFormat.PacketSize;
        for (var i = 0; i < packetCount; i++)
            if (IsSectorAt(stream, i, headerHash))
                result.Add(i);
        return result;
    }

    private static byte[] Insert(byte[] stream, byte[] payload, int offset)
    {
        var result = new byte[stream.Length + payload.Length];
        stream.AsSpan(0, offset).CopyTo(result);
        payload.CopyTo(result, offset);
        stream.AsSpan(offset).CopyTo(result.AsSpan(offset + payload.Length));
        return result;
    }
}
