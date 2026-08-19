using System.Text;
using DataShield.Codec;
using DataShield.Codec.Packets;

namespace DataShield.TestsHarness;

// ─────────────────────────────────────────────────────────────────────────────
//  Генератор комбинированных повреждений DataShield-потока
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Результат применения повреждений к кодированному потоку.</summary>
/// <param name="Mask">Битовая маска применённых повреждений (<see cref="DamageBits"/>).</param>
/// <param name="LostSectors">Число потерянных секторов (консервативная оценка).</param>
/// <param name="DamageCount">Число применённых операций повреждения.</param>
/// <param name="Chunks">Куски потока для последовательного сканирования.</param>
/// <param name="ChunkFormats">Формат каждого куска (длина равна <paramref name="Chunks"/>).</param>
public sealed record DamageResult(
    uint Mask,
    int LostSectors,
    int DamageCount,
    IReadOnlyList<byte[]> Chunks,
    IReadOnlyList<OutputFormat> ChunkFormats);

/// <summary>
/// Комбинированные случайные повреждения кодированного потока DataShield.
///
    /// За одну итерацию по возможности применяются все типы повреждений из
    /// арсенала юнит-тестов: перестановка, дублирование, мусор, фрагменты
    /// реальных пакетов, порча, выпадение, обрезка секторов, коллизии версий
    /// (включая равновероятные — с поиском), тихая порча (хеш-валидная
    /// подделка заменяет оригинал — подбор подмножества томов), шум и
    /// рассинхронизация, декорация Base64, приём в два прохода и куски
    /// разных форматов.
///
/// Гарантии:
/// <list type="bullet">
///   <item>Обычный режим: потери секторов укладываются в ECC-бюджет,
///         заголовков остаётся ≥2 — восстановление гарантировано.</item>
///   <item>Режим <paramref name="overkillExtra"/> &gt; 0: повреждения сверх
///         бюджета — корректный исход это отказ сборки (или точное
///         восстановление, если уцелели все data-сектора).</item>
    ///   <item>Режим <paramref name="collisionKill"/>: подделка побеждает по
    ///         подтверждениям — корректный исход это отказ сборки либо
    ///         восстановление подбором подмножества томов (коллизионный том
    ///         исключается и восстанавливается RS в пределах бюджета ECC).</item>
/// </list>
/// </summary>
public static class DamageEngine
{
    // ── Огибающая стадии подбора подмножества томов (стадия 3 сборки) ──────
    //
    // Тихая порча восстановима только перебором подмножества: уровень 1
    // перебирает одиночные исключения всех одноверсионных томов — до (N+M)/2
    // RS-декодов. Сценарий держится в лёгком режиме повреждений (небольшой
    // файл, мало потерь): иначе попытки упираются в лимит времени стадии,
    // сборка честно отказывает — и корректный отказ ломает ожидание
    // (точное восстановление) нормальной итерации стенда.

    /// <summary>Максимальное суммарное число томов файла (N+M) для тихой порчи.</summary>
    private const int SubsetStageVolumeLimit = 4096;

    /// <summary>Максимальные пакетные потери до тихой порчи (лёгкий режим).</summary>
    private const int LightPacketLosses = 4;

    /// <summary>
    /// Запас на потери бинарного рендера после уровня пакетов:
    /// рассинхронизация, текстовый мусор, фрагмент, разрез двух проходов.
    /// </summary>
    private const int RenderLossHeadroom = 4;
    /// <summary>
    /// Применить комбинированные повреждения к кодированному потоку одного файла
    /// и вернуть готовые к сканированию куски в заданном формате.
    /// </summary>
    /// <param name="packets">Пакеты кодированного файла.</param>
    /// <param name="stats">Статистика кодирования (бюджет ECC).</param>
    /// <param name="format">Основной формат кусков.</param>
    /// <param name="rng">Генератор случайных чисел.</param>
    /// <param name="overkillExtra">Повреждений сверх бюджета (ожидаемый отказ).</param>
    /// <param name="collisionKill">Подделка сектора побеждает (ожидаемый отказ).</param>
    public static DamageResult Apply(
        IReadOnlyList<byte[]> packets, EncodeStats stats, OutputFormat format,
        Random rng, int overkillExtra = 0, bool collisionKill = false)
    {
            var h5 = PacketProbe.HeaderHash(PacketProbe.FindHeader(packets));
        uint mask = 0;
        var ops = 0;
        var lost = 0;

        var damaged = DamagePackets(
            packets, stats.EccCount, stats.DataCount + stats.EccCount, h5, rng,
            overkillExtra, collisionKill, ref mask, ref lost, ref ops,
            out var budget);

        return format == OutputFormat.Binary
            ? RenderBinary(damaged, rng, ref budget, ref mask, ref lost, ref ops)
            : RenderText(damaged, rng, ref mask, ref lost, ref ops);
    }

    /// <summary>
    /// Многофайловый поток: файлы кодируются отдельно, повреждаются в пределах
    /// своих бюджетов, перемешиваются в общих текстовых кусках (порядок
    /// прихода пакетов разных файлов произволен).
    /// </summary>
    /// <param name="files">Пакеты и статистика каждого файла.</param>
    /// <param name="rng">Генератор случайных чисел.</param>
    /// <param name="overkillFileIndex">Индекс файла со сверхбюджетным
    /// повреждением (ожидаемый отказ только для него), или -1.</param>
    public static DamageResult ApplyMultiFile(
        IReadOnlyList<(IReadOnlyList<byte[]> Packets, EncodeStats Stats)> files,
        Random rng, int overkillFileIndex = -1)
    {
        uint mask = DamageBits.MultiFile;
        var ops = 1;
        var lost = 0;

        var lines = new List<string>();
        var sources = new List<byte[]>();

        for (var f = 0; f < files.Count; f++)
        {
            var (packets, stats) = files[f];
        var h5 = PacketProbe.HeaderHash(PacketProbe.FindHeader(packets));
            var overkill = f == overkillFileIndex ? 1 + rng.Next(2) : 0;

            var damaged = DamagePackets(
                packets, stats.EccCount, stats.DataCount + stats.EccCount, h5, rng,
                overkill, collisionKill: false, ref mask, ref lost, ref ops,
                out _);

            sources.AddRange(damaged);
            lines.AddRange(damaged.Select(p => Convert.ToBase64String(p)));
        }

        // Глобальный порядок: пакеты всех файлов вперемешку
        RandomInput.Shuffle(lines, rng);
        mask |= DamageBits.Shuffle;
        ops++;

        LineDamage.DuplicateRandom(lines, 1 + rng.Next(3), rng);
        mask |= DamageBits.Duplicate;
        ops++;

        LineDamage.InsertJunk(lines, 1 + rng.Next(3), rng);
        mask |= DamageBits.Junk;
        ops++;

        if (Roll(rng, 0.5))
        {
            LineDamage.InsertFragmentLines(lines, sources, 1 + rng.Next(3), rng);
            mask |= DamageBits.Fragment;
            ops++;
        }

        LineDamage.Decorate(lines, rng);
        mask |= DamageBits.Decorate;
        ops++;

        // Приём в два прохода
        if (Roll(rng, 0.3) && lines.Count >= 2)
        {
            var half = lines.Count / 2;
            mask |= DamageBits.TwoPass;
            ops++;
            return new DamageResult(
                mask, lost, ops,
                new[] { ToTextChunk(lines.Take(half)), ToTextChunk(lines.Skip(half)) },
                new[] { OutputFormat.Base64, OutputFormat.Base64 });
        }

        return new DamageResult(
            mask, lost, ops,
            new[] { ToTextChunk(lines) },
            new[] { OutputFormat.Base64 });
    }

    // ── Уровень пакетов: перестановка, дубли, порча, выпадения, коллизии ────

    /// <summary>
    /// Комбинированное повреждение списка пакетов. Все потери секторов
    /// (кроме overkill) укладываются в ECC-бюджет. Возвращает повреждённую
    /// копию списка; остаток бюджета — в out.
    /// </summary>
    private static List<byte[]> DamagePackets(
        IReadOnlyList<byte[]> packets, int eccBudget, int totalVolumes, byte[] h5,
        Random rng, int overkillExtra, bool collisionKill,
        ref uint mask, ref int lost, ref int ops, out int budget)
    {
        var list = packets.ToList();
        budget = eccBudget;

        // Порядок прихода произволен — перестановка всегда
        RandomInput.Shuffle(list, rng);
        mask |= DamageBits.Shuffle;
        ops++;

        // Повторный приход — дубли всегда
        PacketDamage.DuplicateRandom(list, 1 + rng.Next(3), rng);
        mask |= DamageBits.Duplicate;
        ops++;

        // Порча в пределах бюджета
        if (budget > 0)
        {
            var corrupted = PacketDamage.CorruptSectors(
                list, Math.Min(budget, 1 + rng.Next(3)), h5, rng);
            if (corrupted > 0)
            {
                budget -= corrupted;
                lost += corrupted;
                mask |= DamageBits.Corrupt;
                ops++;
            }
        }

        // Выпадение в пределах бюджета
        if (budget > 0)
        {
            var removed = PacketDamage.RemoveSectors(
                list, Math.Min(budget, 1 + rng.Next(2)), h5, rng);
            if (removed > 0)
            {
                budget -= removed;
                lost += removed;
                mask |= DamageBits.Remove;
                ops++;
            }
        }

        // Обрезка хвоста
        if (budget > 0 && Roll(rng, 0.35))
        {
            var tailLost = PacketDamage.TruncateTail(list, budget, h5, rng);
            if (tailLost > 0)
            {
                budget -= tailLost;
                lost += tailLost;
                mask |= DamageBits.Truncate;
                ops++;
            }
        }

        // Тихая порча: хеш-валидная подделка заменяет оригинал — точка
        // ветвления отсутствует, спасает только подбор подмножества томов
        // (стадия 3 сборки). Применяется последней среди пакетных потерь и
        // только в лёгком режиме: суммарные стёртые тома (потери + исключение
        // подделки + потери рендера) должны оставаться малыми, чтобы перебор
        // уровня 1 успевал в лимит времени и лимит стёртых томов стадии.
        if (budget > 0 && overkillExtra == 0 && !collisionKill &&
            totalVolumes <= SubsetStageVolumeLimit &&
            eccBudget - budget <= LightPacketLosses &&
            Roll(rng, 0.35))
        {
            if (PacketDamage.CorruptSilently(list, h5, rng))
            {
                budget--;
                lost++;
                mask |= DamageBits.SilentCorruption;
                ops++;
            }
        }

        // Повреждения сверх бюджета — ожидаемый отказ сборки
        if (overkillExtra > 0)
        {
            var extra = PacketDamage.RemoveSectors(list, overkillExtra, h5, rng);
            if (extra == 0)
                extra = PacketDamage.CorruptSectors(list, overkillExtra, h5, rng);
            lost += extra;
            mask |= DamageBits.Overkill;
            ops++;
        }

        // Коллизии версий
        if (collisionKill)
        {
            if (PacketDamage.InjectCollisionKill(list, h5, rng))
            {
                mask |= DamageBits.CollisionKill;
                ops++;
            }
        }
        else if (overkillExtra == 0 && Roll(rng, 0.45))
        {
            var injected = Roll(rng, 0.4)
                ? PacketDamage.InjectCollisionTie(list, h5, rng)
                : PacketDamage.InjectCollisionPromote(list, h5, rng);
            if (injected)
            {
                mask |= DamageBits.Collision;
                ops++;
            }
        }

        return list;
    }

    // ── Рендер: текст (Base64) ──────────────────────────────────────────────

    private static DamageResult RenderText(
        List<byte[]> packets, Random rng, ref uint mask, ref int lost, ref int ops)
    {
        // Куски в разных форматах: половина пакетов текстом, половина бинарно
        if (Roll(rng, 0.2) && packets.Count >= 2)
            return BuildMixedChunks(packets, rng, ref mask, ref lost, ref ops);

        var lines = packets.Select(p => Convert.ToBase64String(p)).ToList();

        LineDamage.InsertJunk(lines, 1 + rng.Next(3), rng);
        mask |= DamageBits.Junk;
        ops++;

        if (Roll(rng, 0.6))
        {
            LineDamage.InsertFragmentLines(lines, packets, 1 + rng.Next(3), rng);
            mask |= DamageBits.Fragment;
            ops++;
        }

        LineDamage.Decorate(lines, rng);
        mask |= DamageBits.Decorate;
        ops++;

        if (Roll(rng, 0.375) && lines.Count >= 2)
        {
            var half = lines.Count / 2;
            mask |= DamageBits.TwoPass;
            ops++;
            return new DamageResult(
                mask, lost, ops,
                new[] { ToTextChunk(lines.Take(half)), ToTextChunk(lines.Skip(half)) },
                new[] { OutputFormat.Base64, OutputFormat.Base64 });
        }

        return new DamageResult(
            mask, lost, ops,
            new[] { ToTextChunk(lines) },
            new[] { OutputFormat.Base64 });
    }

    // ── Рендер: бинарный поток ──────────────────────────────────────────────

    private static DamageResult RenderBinary(
        List<byte[]> packets, Random rng,
        ref int budget, ref uint mask, ref int lost, ref int ops)
    {
        // Куски в разных форматах: половина пакетов бинарно, половина текстом
        if (Roll(rng, 0.2) && packets.Count >= 2)
            return BuildMixedChunks(packets, rng, ref mask, ref lost, ref ops);

        var stream = PacketIO.WriteBinaryBytes(packets);
        var misaligned = false;

        // Планирование шумовых вставок на выровненном потоке
        var plan = new List<(int Offset, byte[] Payload)>();

        if (Roll(rng, 0.35))
        {
            var lossy = budget >= 1 && Roll(rng, 0.6);
            if (BinaryDamage.PlanDesync(stream, rng, lossy) is { } desync)
            {
                plan.Add(desync);
                if (lossy)
                {
                    budget--;
                    lost++;
                }
                misaligned = true;
                mask |= DamageBits.Desync;
                ops++;
            }
        }

        if (Roll(rng, 0.3))
        {
            var lossy = budget >= 1 && Roll(rng, 0.5);
            if (BinaryDamage.PlanTextGap(stream, rng, lossy) is { } textGap)
            {
                plan.Add(textGap);
                if (lossy)
                {
                    budget--;
                    lost++;
                }
                misaligned = true;
                mask |= DamageBits.TextGap;
                ops++;
            }
        }

        if (Roll(rng, 0.4))
        {
            var lossy = budget >= 1 && Roll(rng, 0.6);
            if (BinaryDamage.PlanFragment(stream, rng, lossy) is { } fragment)
            {
                plan.Add(fragment);
                if (lossy)
                {
                    budget--;
                    lost++;
                }
                // Любая вставка внутри потока сдвигает 75-байтную сетку после
                // себя: разрез TwoPass по k*75 может разрезать реальный пакет
                // даже у не-[lossy] фрагмента.
                misaligned = true;
                mask |= DamageBits.Fragment;
                ops++;
            }
        }

        if (Roll(rng, 0.4))
        {
            plan.Add(BinaryDamage.PlanPrefixNoise(rng));
            misaligned = true;
            mask |= DamageBits.PrefixNoise;
            ops++;
        }

        if (Roll(rng, 0.4))
        {
            plan.Add(BinaryDamage.PlanSuffixNoise(stream, rng));
            mask |= DamageBits.SuffixNoise;
            ops++;
        }

        if (plan.Count > 0)
            stream = BinaryDamage.ApplyPlanned(stream, plan);

        // Приём в два прохода: разрез по границе 75-байтных пакетов.
        // При нарушенном выравнивании пакет на разрезе может потеряться.
        if (Roll(rng, 0.3) && budget >= (misaligned ? 1 : 0))
        {
            var packetCount = stream.Length / PacketFormat.PacketSize;
            if (packetCount >= 2)
            {
                var cut = (1 + rng.Next(packetCount - 1)) * PacketFormat.PacketSize;
                if (misaligned)
                    lost++;

                mask |= DamageBits.TwoPass;
                ops++;
                return new DamageResult(
                    mask, lost, ops,
                    new[] { stream[..cut], stream[cut..] },
                    new[] { OutputFormat.Binary, OutputFormat.Binary });
            }
        }

        return new DamageResult(
            mask, lost, ops,
            new[] { stream },
            new[] { OutputFormat.Binary });
    }

    // ── Куски в разных форматах (txt + bin одного файла) ────────────────────

    /// <summary>
    /// Разделить пакеты на две части и отдать каждую в собственном формате:
    /// одна половина Base64-текстом (мусор, декорация), другая — бинарно
    /// (шум по краям). Дополнительных потерь секторов нет.
    /// </summary>
    private static DamageResult BuildMixedChunks(
        List<byte[]> packets, Random rng, ref uint mask, ref int lost, ref int ops)
    {
        var half = 1 + rng.Next(packets.Count - 1);
        var first = packets.Take(half).ToList();
        var second = packets.Skip(half).ToList();

        // Случайный порядок форматов
        if (rng.Next(2) == 0)
            (first, second) = (second, first);

        // Текстовая часть: строки + мусор + фрагменты + декорация
        var lines = first.Select(p => Convert.ToBase64String(p)).ToList();
        LineDamage.InsertJunk(lines, 1 + rng.Next(3), rng);
        LineDamage.InsertFragmentLines(lines, first, 1 + rng.Next(2), rng);
        LineDamage.Decorate(lines, rng);
        var textChunk = ToTextChunk(lines);

        // Бинарная часть: пакеты + шум по краям
        var binary = PacketIO.WriteBinaryBytes(second);
        var binaryChunk = BinaryDamage.ApplyPlanned(
            binary,
            new[]
            {
                BinaryDamage.PlanPrefixNoise(rng),
                BinaryDamage.PlanSuffixNoise(binary, rng),
            });

        mask |= DamageBits.Junk | DamageBits.Fragment | DamageBits.Decorate |
                DamageBits.PrefixNoise | DamageBits.SuffixNoise |
                DamageBits.TwoPass | DamageBits.MixedIO;
        ops++;

        return new DamageResult(
            mask, lost, ops,
            new[] { textChunk, binaryChunk },
            new[] { OutputFormat.Base64, OutputFormat.Binary });
    }

    // ── Служебные ───────────────────────────────────────────────────────────

    private static bool Roll(Random rng, double probability) =>
        rng.NextDouble() < probability;

    private static byte[] ToTextChunk(IEnumerable<string> lines) =>
        Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
}
