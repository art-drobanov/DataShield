using DataShield.Codec.Packets;

namespace DataShield.TestsHarness;

// ─────────────────────────────────────────────────────────────────────────────
//  Повреждения на уровне пакетов (общее ядро для текста, бинарного формата
//  и многофайловых потоков)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Повреждения списка 75-байтных пакетов: перестановка, дублирование, порча,
/// выпадение, обрезка и коллизии версий (подделки с корректным хешем).
/// Заголовки повреждаются только целыми копиями (обрезка гарантирует
/// не менее двух оставшихся); секторы — в пределах бюджета ECC.
/// </summary>
public static class PacketDamage
{
    /// <summary>Индексы пакетов, являющихся корректными секторами (не заголовками).</summary>
    public static List<int> SectorIndices(IReadOnlyList<byte[]> packets, byte[] headerHash)
    {
        var result = new List<int>();
        for (var i = 0; i < packets.Count; i++)
        {
            if (PacketProbe.IsHeader(packets[i])) continue;
            if (!PacketProbe.IsValidSector(packets[i], headerHash)) continue;
            result.Add(i);
        }
        return result;
    }

    /// <summary>
    /// Дублирование случайных пакетов — повторный приход.
    /// </summary>
    public static void DuplicateRandom(List<byte[]> packets, int count, Random rng)
    {
        for (var i = 0; i < count && packets.Count > 0; i++)
            packets.Insert(rng.Next(packets.Count + 1), packets[rng.Next(packets.Count)]);
    }

    /// <summary>
    /// Порча до <paramref name="maxCount"/> корректных секторов: переворот
    /// битов ломает хеш, пакет становится шумом (стирание). Возвращает число испорченных.
    /// </summary>
    public static int CorruptSectors(
        List<byte[]> packets, int maxCount, byte[] headerHash, Random rng)
    {
        var candidates = SectorIndices(packets, headerHash);
        RandomInput.Shuffle(candidates, rng);

        var corrupted = Math.Min(maxCount, candidates.Count);
        for (var n = 0; n < corrupted; n++)
        {
            // Копия: оригинальные пакеты кодера не мутируются
            var packet = packets[candidates[n]].ToArray();
            var flips = 1 + rng.Next(8);
            for (var f = 0; f < flips; f++)
                packet[rng.Next(packet.Length)] ^= (byte)(1 + rng.Next(255));
            packets[candidates[n]] = packet;
        }

        return corrupted;
    }

    /// <summary>Удаление до <paramref name="maxCount"/> корректных секторов.</summary>
    public static int RemoveSectors(
        List<byte[]> packets, int maxCount, byte[] headerHash, Random rng)
    {
        var candidates = SectorIndices(packets, headerHash);
        RandomInput.Shuffle(candidates, rng);

        var selected = candidates
            .Take(Math.Min(maxCount, candidates.Count))
            .OrderByDescending(index => index)
            .ToList();

        foreach (var index in selected)
            packets.RemoveAt(index);

        return selected.Count;
    }

    /// <summary>
    /// Обрезка хвоста потока. Потеря корректных секторов не превышает
    /// <paramref name="maxSectorLoss"/>, в оставшейся части остаётся
    /// не менее двух копий заголовка. Возвращает фактические потери.
    /// </summary>
    public static int TruncateTail(
        List<byte[]> packets, int maxSectorLoss, byte[] headerHash, Random rng)
    {
        if (packets.Count < 4) return 0;

        var desired = 1 + rng.Next(maxSectorLoss);
        var maxDrop = Math.Min(desired, packets.Count / 2);

        for (var drop = maxDrop; drop > 0; drop--)
        {
            var lost = 0;
            for (var i = packets.Count - drop; i < packets.Count; i++)
                if (!PacketProbe.IsHeader(packets[i])) lost++;

            if (lost > maxSectorLoss) continue;

            var headersKept = 0;
            for (var i = 0; i < packets.Count - drop; i++)
                if (PacketProbe.IsHeader(packets[i]))
                    headersKept++;

            if (headersKept < 2) continue;

            packets.RemoveRange(packets.Count - drop, drop);
            return lost;
        }

        return 0;
    }

    /// <summary>
    /// Тихая порча: подделка сектора (корректный хеш, искажённые данные)
    /// заменяет оригинальный пакет на его месте — версия остаётся единственной,
    /// точки ветвления нет, приёмник не видит подвоха. Прямая сборка даёт
    /// неверный файл, SHA-256 не сходится; спасает только подбор подмножества
    /// томов (исключение тома на уровне 1 + восстановление RS).
    /// </summary>
    public static bool CorruptSilently(
        List<byte[]> packets, byte[] headerHash, Random rng)
    {
        var candidates = SectorIndices(packets, headerHash);
        if (candidates.Count == 0) return false;

        var index = candidates[rng.Next(candidates.Count)];
        packets[index] = PacketProbe.ForgeSectorVariant(packets[index], headerHash, rng);
        return true;
    }

    /// <summary>
    /// Коллизия с подтверждением: подделка сектора (корректный хеш,
    /// искажённые данные) плюс две дополнительные копии верного сектора —
    /// сборка выбирает верную версию по счётчику подтверждений.
    /// </summary>
    public static bool InjectCollisionPromote(
        List<byte[]> packets, byte[] headerHash, Random rng)
    {
        var forged = PickForgeable(packets, headerHash, rng, out var correct);
        if (forged is null) return false;

        packets.Insert(rng.Next(packets.Count + 1), forged);
        packets.Insert(rng.Next(packets.Count + 1), correct);
        packets.Insert(rng.Next(packets.Count + 1), correct);
        return true;
    }

    /// <summary>
    /// Коллизия с равными счётчиками: подделки вставляются по столько же
    /// копий, сколько есть верных, — разрешение требует поиска версий
    /// (полный перебор или эвристическая прокрутка в TryAssemble).
    /// </summary>
    public static bool InjectCollisionTie(
        List<byte[]> packets, byte[] headerHash, Random rng)
    {
        var forged = PickForgeable(packets, headerHash, rng, out var correct);
        if (forged is null) return false;

        var correctCount = CountCopies(packets, correct);
        var forgeryCount = 1 + rng.Next(2); // итоговая связка 2-3 версий

        for (var f = 0; f < forgeryCount; f++)
        {
            var variant = f == 0
                ? forged
                : PacketProbe.ForgeSectorVariant(correct, headerHash, rng);
            for (var c = 0; c < correctCount; c++)
                packets.Insert(rng.Next(packets.Count + 1), variant);
        }

        return true;
    }

    /// <summary>
    /// Победа подделки: копий подделки больше, чем копий верного сектора,
    /// поэтому выборка берёт её, SHA-256 не сходится и корректный рапорт —
    /// отказ сборки. Спасает подбор подмножества томов: коллизионный том
    /// исключается целиком и восстанавливается RS, если позволяет бюджет ECC.
    /// </summary>
    public static bool InjectCollisionKill(
        List<byte[]> packets, byte[] headerHash, Random rng)
    {
        var forged = PickForgeable(packets, headerHash, rng, out var correct);
        if (forged is null) return false;

        var copies = CountCopies(packets, correct) + 1;
        for (var c = 0; c < copies; c++)
            packets.Insert(rng.Next(packets.Count + 1), forged);

        return true;
    }

    // ── Служебные ───────────────────────────────────────────────────────────

    private static byte[]? PickForgeable(
        List<byte[]> packets, byte[] headerHash, Random rng, out byte[] correct)
    {
        var candidates = SectorIndices(packets, headerHash);
        if (candidates.Count == 0)
        {
            correct = Array.Empty<byte>();
            return null;
        }

        correct = packets[candidates[rng.Next(candidates.Count)]];
        return PacketProbe.ForgeSectorVariant(correct, headerHash, rng);
    }

    private static int CountCopies(List<byte[]> packets, byte[] packet)
    {
        var count = 0;
        foreach (var candidate in packets)
            if (candidate.AsSpan().SequenceEqual(packet))
                count++;
        return count;
    }
}
