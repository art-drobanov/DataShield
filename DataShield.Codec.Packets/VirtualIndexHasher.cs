using DataShield.Codec.Reporting;

namespace DataShield.Codec.Packets;

// ─────────────────────────────────────────────────────────────────────────────
//  Секторы с виртуальным индексом (см. SectorScheme.VirtualIndex)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Примитивы схемы секторов с виртуальным индексом.
///
/// <b>Пакет</b> (75 байт): payload(64) ‖ Trunc11(SHA-256(H5 ‖ payload ‖ idx LE)).
/// Номер сектора в пакете отсутствует: индекс восстанавливается на приёме
/// перебором K = sectorCount кандидатов до совпадения усечённого хеша.
///
/// Стойкость подмены на файл — 88 − log₂K бит (у Classic — 72 бита на пакет
/// независимо от K); при K ≤ 512 схема даёт ≥ 79 бит. Плата за это — до K
/// хешей на пакет при проверке, поэтому применение схемы ограничено порогом
/// <see cref="DefaultSectorLimit"/>; длинный перебор выполняется параллельно.
/// </summary>
public static class VirtualIndexHasher
{
    /// <summary>
    /// Потолок применимости схемы: 512 томов (N+M). При K = 512 стойкость —
    /// 79 бит (против 72 у Classic), перебор на приёме остаётся дешёвым.
    /// Файлы с большим числом секторов кодировать схемой VirtualIndex
    /// запрещено — без молчаливого даунгрейда до Classic. Одновременно это
    /// жёсткий верхний предел параметра virtualIndexSectorLimit в
    /// конструкторах кодера и декодера.
    /// </summary>
    public const int DefaultSectorLimit = 512;

    /// <summary>Размер усечённого хеша: 11 байт (88 бит).</summary>
    public const int HashSize = 11;

    /// <summary>Смещение хеша в пакете: сразу после payload.</summary>
    public const int HashOffset = PacketFormat.PayloadSize; // 64

    // Вход хеша: H5(24) ‖ payload(64) ‖ idxLE(2) = 90 байт. Индекс — в хвосте:
    // кандидаты при переборе различаются последними байтами входа.

    // Параметры перебора. Фаза 1 — последовательный префикс в порядке
    // курсора: поток в порядке следования принимает сектор за O(1) и не
    // платит за параллелизм. Фаза 2 — параллельный обход остатка блоками
    // (ThreadPool); включается только при большом остатке, где накладные
    // расходы окупаются.

    /// <summary>Шагов последовательного префикса перебора (фаза 1).</summary>
    private const int SequentialSteps = 64;

    /// <summary>Минимальный остаток перебора для включения фазы 2.</summary>
    private const int ParallelThreshold = 256;

    /// <summary>Размер блока параллельного перебора, шагов.</summary>
    private const int ParallelChunk = 128;

    /// <summary>Trunc11(SHA-256(H5 ‖ payload ‖ idx LE)) в буфер из 11 байт.</summary>
    public static void ComputeHashInto(
        ReadOnlySpan<byte> payload,
        int sectorIndex,
        ReadOnlySpan<byte> headerHash,
        Span<byte> hash)
    {
        if (payload.Length != PacketFormat.PayloadSize)
            throw new ArgumentException(
                CodecErrors.ExpectedPayloadBytes(payload.Length, PacketFormat.PayloadSize),
                nameof(payload));

        if (headerHash.Length != PacketFormat.HeaderHashSize)
            throw new ArgumentException(
                CodecErrors.ExpectedHeaderHashBytes(headerHash.Length, PacketFormat.HeaderHashSize),
                nameof(headerHash));

        if (hash.Length != HashSize)
            throw new ArgumentException(
                CodecErrors.ExpectedBufferBytes(hash.Length, HashSize),
                nameof(hash));

        if ((uint)sectorIndex > PacketFormat.MaxDataVolumes)
            throw new ArgumentOutOfRangeException(
                nameof(sectorIndex),
                CodecErrors.SectorIndexOutOfRange(sectorIndex, PacketFormat.MaxDataVolumes));

        Span<byte> input = stackalloc byte[
            PacketFormat.HeaderHashSize + PacketFormat.PayloadSize +
            PacketFormat.SectorNumberSize]; // 90
        Span<byte> full = stackalloc byte[32];

        headerHash.CopyTo(input);
        payload.CopyTo(input[PacketFormat.HeaderHashSize..]);
        input[^2] = (byte)(sectorIndex & 0xFF);
        input[^1] = (byte)((sectorIndex >> 8) & 0xFF);

        Sha256Compact.HashData(input, full);
        full[..HashSize].CopyTo(hash);
    }

    /// <summary>
    /// Собрать пакет схемы VirtualIndex (75 байт): payload ‖ Trunc11(…).
    /// </summary>
    public static void BuildPacketInto(
        ReadOnlySpan<byte> payload,
        int sectorIndex,
        ReadOnlySpan<byte> headerHash,
        Span<byte> packet)
    {
        if (packet.Length != PacketFormat.PacketSize)
            throw new ArgumentException(
                CodecErrors.ExpectedBufferBytes(packet.Length, PacketFormat.PacketSize),
                nameof(packet));

        payload.CopyTo(packet);
        ComputeHashInto(
            payload,
            sectorIndex,
            headerHash,
            packet[HashOffset..]);
    }

    /// <summary>
    /// Проверить пакет схемы VirtualIndex: перебор индексов 0..K−1 в порядке
    /// «от курсора вперёд, затем с нуля» до первого совпадения Trunc11.
    /// Первый совпавший индекс считается номером сектора (вероятность
    /// многозначности — (K−1)·2⁻⁸⁸, при K = 4096 ≈ 10⁻²³).
    ///
    /// Перебор двухфазный: первые <see cref="SequentialSteps"/> шагов —
    /// последовательно (поток в порядке следования принимает за O(1)),
    /// остаток при объёме от <see cref="ParallelThreshold"/> кандидатов —
    /// параллельно блоками по <see cref="ParallelChunk"/> шагов. Каждый блок
    /// фиксирует своё первое совпадение; побеждает совпадение с наименьшим
    /// номером шага — итог совпадает с чисто последовательным обходом
    /// (детерминизм).
    /// </summary>
    /// <param name="packet">Кандидат: полный пакет 75 байт.</param>
    /// <param name="headerHash">H5 заголовка файла, 24 байта.</param>
    /// <param name="sectorCount">Число секторов файла K.</param>
    /// <param name="startCursor">Ожидаемый следующий индекс (курсор прихода).</param>
    /// <param name="sectorIndex">Найденный номер сектора при успехе.</param>
    public static bool TryVerifyPacket(
        ReadOnlySpan<byte> packet,
        ReadOnlySpan<byte> headerHash,
        int sectorCount,
        int startCursor,
        out int sectorIndex)
    {
        sectorIndex = 0;

        if (packet.Length != PacketFormat.PacketSize ||
            headerHash.Length != PacketFormat.HeaderHashSize ||
            sectorCount <= 0 || sectorCount > PacketFormat.MaxDataVolumes)
        {
            return false;
        }

        var cursor = (uint)Math.Clamp(startCursor, 0, sectorCount - 1);
        var payload = packet[..PacketFormat.PayloadSize];
        var actual = packet[HashOffset..];
        Span<byte> expected = stackalloc byte[HashSize];

        // ── Фаза 1: последовательный префикс в порядке курсора ──────────────
        var prefix = Math.Min(sectorCount, SequentialSteps);
        for (var step = 0; step < prefix; step++)
        {
            var idx = (int)((cursor + (uint)step) % (uint)sectorCount);
            ComputeHashInto(payload, idx, headerHash, expected);
            if (expected.SequenceEqual(actual))
            {
                sectorIndex = idx;
                return true;
            }
        }

        if (sectorCount <= prefix)
            return false;

        // ── Фаза 2а: короткий остаток — досчитать последовательно ──────────
        var remaining = sectorCount - prefix;
        if (remaining < ParallelThreshold)
        {
            for (var step = prefix; step < sectorCount; step++)
            {
                var idx = (int)((cursor + (uint)step) % (uint)sectorCount);
                ComputeHashInto(payload, idx, headerHash, expected);
                if (expected.SequenceEqual(actual))
                {
                    sectorIndex = idx;
                    return true;
                }
            }

            return false;
        }

        // ── Фаза 2б: длинный остаток — параллельный обход блоками ──────────
        // Span нельзя захватить в лямбде: входы копируются в массивы (64+24+11 байт).
        var payloadCopy = payload.ToArray();
        var hashSeed = headerHash.ToArray();
        var actualCopy = actual.ToArray();

        var chunkCount = (remaining + ParallelChunk - 1) / ParallelChunk;
        var chunkMatch = new int[chunkCount];

        Parallel.For(0, chunkCount, chunk =>
        {
            var from = prefix + chunk * ParallelChunk;
            var to = Math.Min(from + ParallelChunk, sectorCount);

            // Буфер на блок: ComputeHashInto работает со Span, для лямбды
            // нужен управляемый массив.
            var probe = new byte[HashSize];

            for (var step = from; step < to; step++)
            {
                var idx = (int)((cursor + (uint)step) % (uint)sectorCount);
                ComputeHashInto(payloadCopy, idx, hashSeed, probe);
                if (probe.AsSpan().SequenceEqual(actualCopy))
                {
                    chunkMatch[chunk] = step;
                    return;
                }
            }

            chunkMatch[chunk] = -1;
        });

        // Победитель — совпадение с наименьшим шагом: результат тот же,
        // что у последовательного обхода от курсора.
        var bestStep = -1;
        for (var chunk = 0; chunk < chunkCount; chunk++)
        {
            if (chunkMatch[chunk] >= 0 &&
                (bestStep < 0 || chunkMatch[chunk] < bestStep))
            {
                bestStep = chunkMatch[chunk];
            }
        }

        if (bestStep < 0)
            return false;

        sectorIndex = (int)((cursor + (uint)bestStep) % (uint)sectorCount);
        return true;
    }
}
