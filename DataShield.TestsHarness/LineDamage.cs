using DataShield.Codec.Packets;

namespace DataShield.TestsHarness;

// ─────────────────────────────────────────────────────────────────────────────
//  Повреждения Base64-потока (одна строка = один пакет)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Повреждения текстового (Base64) представления потока DataShield.
/// Все операции соответствуют сценариям юнит-тестов кодека.
/// </summary>
public static class LineDamage
{
    private static readonly string[] JunkTexts =
    {
        "мусор между пакетами",
        "=== НАЧАЛО ФАЙЛА ===",
        "garbage line !!! ~~~",
        "----------------------------------------",
        "@@@@@@@@@@@@@@@@@@@@@@@@@@",
    };

    /// <summary>Случайная перестановка строк — порядок прихода пакетов.</summary>
    public static void Shuffle(List<string> lines, Random rng) =>
        RandomInput.Shuffle(lines, rng);

    /// <summary>Дублирование случайных строк — повторный приход пакетов.</summary>
    public static void DuplicateRandom(List<string> lines, int count, Random rng)
    {
        for (var i = 0; i < count && lines.Count > 0; i++)
            lines.Insert(rng.Next(lines.Count + 1), lines[rng.Next(lines.Count)]);
    }

    /// <summary>Вставка мусорных строк: случайный текст и пакеты с битым хешем.</summary>
    public static void InsertJunk(List<string> lines, int count, Random rng)
    {
        for (var i = 0; i < count; i++)
        {
            var junk = rng.Next(2) == 0
                ? Convert.ToBase64String(RandomInput.Bytes(PacketFormat.PacketSize, rng))
                : JunkTexts[rng.Next(JunkTexts.Length)];
            lines.Insert(rng.Next(lines.Count + 1), junk);
        }
    }

    /// <summary>
    /// Вставка фрагментов реальных пакетов: обрезанные Base64-строки
    /// (10..99 символов). Правдоподобный мусор, сбивающий скользящее окно.
    /// </summary>
    public static void InsertFragmentLines(
        List<string> lines, IReadOnlyList<byte[]> sourcePackets, int count, Random rng)
    {
        for (var i = 0; i < count && sourcePackets.Count > 0; i++)
        {
            var packet = sourcePackets[rng.Next(sourcePackets.Count)];
            var full = Convert.ToBase64String(packet);
            var cut = 10 + rng.Next(full.Length - 10);
            lines.Insert(rng.Next(lines.Count + 1), full[..cut]);
        }
    }

    /// <summary>
    /// Порча до <paramref name="maxCount"/> корректных секторов: переворот битов
    /// ломает хеш, и пакет становится стиранием. Возвращает число испорченных.
    /// </summary>
    public static int CorruptSectors(List<string> lines, int maxCount, byte[] headerHash, Random rng)
    {
        var candidates = CollectSectorIndices(lines, headerHash);
        RandomInput.Shuffle(candidates, rng);

        var corrupted = 0;
        for (var n = 0; n < candidates.Count && corrupted < maxCount; n++)
        {
            var index = candidates[n];
            if (!PacketProbe.TryGetPacket(lines[index], out var packet)) continue;

            for (var f = 0; f < 1 + rng.Next(8); f++)
                packet[rng.Next(packet.Length)] ^= (byte)(1 + rng.Next(255));

            lines[index] = Convert.ToBase64String(packet);
            corrupted++;
        }

        return corrupted;
    }

    /// <summary>Удаление до <paramref name="maxCount"/> корректных секторов.</summary>
    public static int RemoveSectors(List<string> lines, int maxCount, byte[] headerHash, Random rng)
    {
        var candidates = CollectSectorIndices(lines, headerHash);
        RandomInput.Shuffle(candidates, rng);

        var selected = candidates
            .Take(Math.Min(maxCount, candidates.Count))
            .OrderByDescending(index => index)
            .ToList();

        foreach (var index in selected)
            lines.RemoveAt(index);

        return selected.Count;
    }

    /// <summary>
    /// Коллизия версий: в поток подмешивается подделка сектора (корректный хеш,
    /// искажённый payload). Верная версия дублируется так, чтобы превосходить
    /// подделку по числу подтверждений — сборка выбирает её без перебора.
    /// </summary>
    public static bool InjectCollision(List<string> lines, byte[] headerHash, Random rng)
    {
        var candidates = CollectSectorIndices(lines, headerHash);
        if (candidates.Count == 0) return false;

        var index = candidates[rng.Next(candidates.Count)];
        if (!PacketProbe.TryGetPacket(lines[index], out var packet)) return false;

        var correct = lines[index];
        var forged = PacketProbe.ForgeSectorVariant(packet, headerHash, rng);

        lines.Insert(rng.Next(lines.Count + 1), Convert.ToBase64String(forged));
        lines.Insert(rng.Next(lines.Count + 1), correct);
        lines.Insert(rng.Next(lines.Count + 1), correct);

        return true;
    }

    /// <summary>Декорация Base64-строк пробелами и паддингом '='.</summary>
    public static void Decorate(List<string> lines, Random rng)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length < 20 || rng.NextDouble() >= 0.5) continue;

            var cut = 1 + rng.Next(line.Length - 2);
            lines[i] = "  " + line[..cut] + " = " + line[cut..] + " =";
        }
    }

    // ── Служебные ───────────────────────────────────────────────────────────

    private static List<int> CollectSectorIndices(List<string> lines, byte[] headerHash)
    {
        var result = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!PacketProbe.TryGetPacket(lines[i], out var packet)) continue;
            if (PacketProbe.IsHeader(packet)) continue;
            if (!PacketProbe.IsValidSector(packet, headerHash)) continue;
            result.Add(i);
        }
        return result;
    }
}
