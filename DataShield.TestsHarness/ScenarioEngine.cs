using DataShield.Codec;
using DataShield.Codec.Packets;

namespace DataShield.TestsHarness;

// ─────────────────────────────────────────────────────────────────────────────
//  Движок сценариев стендов: план итерации и диспатч повреждений
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Тип сценария итерации стенда.</summary>
public enum ScenarioKind
{
    /// <summary>Одиночный файл с комбинированными повреждениями.</summary>
    SingleDamaged,

    /// <summary>Многофайловый перемешанный повреждённый поток.</summary>
    MultiShuffled,

    /// <summary>Многофайловый поток: чистая последовательная склейка FEC-потоков.</summary>
    MultiConcat,
}

/// <summary>Файл сценария: содержимое и параметры кодирования.</summary>
/// <param name="Content">Байты файла.</param>
/// <param name="Name">Имя файла (уходит в заголовок пакета).</param>
/// <param name="EccPercent">Избыточность ECC, %.</param>
/// <param name="HeaderPercent">Избыточность заголовков, %.</param>
/// <param name="Scheme">Схема хеширования секторов.</param>
/// <param name="SectorLimit">Порог применимости схемы VirtualIndex.</param>
public sealed record ScenarioFile(
    byte[] Content, string Name, int EccPercent, int HeaderPercent,
    SectorScheme Scheme, int SectorLimit);

/// <summary>План итерации стенда.</summary>
/// <param name="Kind">Тип сценария.</param>
/// <param name="Stress">Разыгранный стресс: повреждения сверх бюджета / подделка-победитель.</param>
/// <param name="Format">Формат кусков одиночного файла (многофайловым сценариям не используется).</param>
/// <param name="Files">Файлы итерации (одиночному сценарию соответствует один).</param>
public sealed record ScenarioPlan(
    ScenarioKind Kind, bool Stress, OutputFormat Format,
    IReadOnlyList<ScenarioFile> Files)
{
    /// <summary>Многофайловый сценарий.</summary>
    public bool IsMulti => Kind != ScenarioKind.SingleDamaged;
}

/// <summary>Настройки распределения параметров сценариев стенда.</summary>
/// <param name="MinEccPercent">Минимальная избыточность ECC, %.</param>
/// <param name="MaxEccPercent">Максимальная избыточность ECC, % (эксклюзивно).</param>
/// <param name="MinHeaderPercent">Минимальная избыточность заголовков, %.</param>
/// <param name="MaxHeaderPercent">Максимальная избыточность заголовков, % (эксклюзивно).</param>
/// <param name="WarnChancePercent">Доля стресс-итераций (сверхбюджетные повреждения), %.</param>
/// <param name="MultiChancePercent">Доля многофайловых итераций (из не-стрессовых), %.</param>
/// <param name="MultiConcatPercent">Доля конкатенации среди многофайловых итераций, %.</param>
/// <param name="EmptyChancePercent">Доля пустых файлов, %.</param>
/// <param name="VirtualIndexChancePercent">Доля схемы VirtualIndex (с откатом на Classic), %.</param>
/// <param name="MinFileSize">Минимальный размер файла, байт.</param>
/// <param name="MaxSizeLog2">log2 максимального размера файла.</param>
public sealed record ScenarioOptions(
    int MinEccPercent = 1,
    int MaxEccPercent = 200,
    int MinHeaderPercent = 1,
    int MaxHeaderPercent = 10,
    int WarnChancePercent = 15,
    int MultiChancePercent = 10,
    int MultiConcatPercent = 40,
    int EmptyChancePercent = 3,
    int VirtualIndexChancePercent = 12,
    int MinFileSize = 1,
    int MaxSizeLog2 = 18);

/// <summary>
/// Движок сценариев: разыгрывает план итерации (режим, файлы, параметры
/// кодирования) и применяет повреждения по плану. Единая точка контроля
/// распределений для стендов DataShield (кодек-стенд и CLI-стенд).
/// </summary>
public static class ScenarioEngine
{
    /// <summary>Настройки по умолчанию (распределения кодек-стенда).</summary>
    public static ScenarioOptions DefaultOptions { get; } = new();

    /// <summary>Доля стресс-итераций с повреждением сверх бюджета (против коллизии-победителя).</summary>
    private const int StressOverkillPercent = 70;

    /// <summary>Попыток переигрывания до одноформатного результата (смешанные куски редки).</summary>
    private const int UniformAttempts = 32;

    /// <summary>
    /// Разыграть план итерации: режим (стресс/многофайловость — взаимоисключающие),
    /// файлы с содержимым и параметрами кодирования, формат кусков одиночного
    /// файла, схема секторов (VirtualIndex с откатом на Classic за порогом).
    /// </summary>
    /// <param name="n">Номер итерации (для имён файлов).</param>
    /// <param name="rng">Генератор случайных чисел.</param>
    /// <param name="options">Настройки распределений (по умолчанию — кодек-стенд).</param>
    public static ScenarioPlan RollPlan(int n, Random rng, ScenarioOptions? options = null)
    {
        var o = options ?? DefaultOptions;

        var roll = rng.Next(100);
        var stress = roll < o.WarnChancePercent;
        var multi = !stress && roll < o.WarnChancePercent + o.MultiChancePercent;

        if (!multi)
        {
            var size = NextSize(rng, o);
            var eccPercent = o.MinEccPercent + rng.Next(o.MaxEccPercent);
            var headerPercent = o.MinHeaderPercent + rng.Next(o.MaxHeaderPercent);
            var format = rng.Next(2) == 0 ? OutputFormat.Base64 : OutputFormat.Binary;
            var scheme = RollScheme(size, eccPercent, rng, o);
            var sectorLimit = RollSectorLimit(scheme, size, eccPercent, rng, o);

            return new ScenarioPlan(
                ScenarioKind.SingleDamaged, stress, format,
                [
                    new ScenarioFile(
                        RandomInput.Bytes(size, rng), $"demo-{n:D6}.dat",
                        eccPercent, headerPercent, scheme, sectorLimit),
                ]);
        }

        var fileCount = rng.Next(2) == 0 ? 2 : 3;
        var files = new List<ScenarioFile>(fileCount);
        for (var f = 0; f < fileCount; f++)
        {
            var content = RandomInput.Bytes(NextSize(rng, o), rng);
            var eccPercent = o.MinEccPercent + rng.Next(o.MaxEccPercent);
            var headerPercent = o.MinHeaderPercent + rng.Next(o.MaxHeaderPercent);
            var scheme = RollScheme(content.Length, eccPercent, rng, o);

            files.Add(new ScenarioFile(
                content, $"demo-{n:D6}-{(char)('A' + f)}.dat",
                eccPercent, headerPercent, scheme,
                RollSectorLimit(scheme, content.Length, eccPercent, rng, o)));
        }

        var kind = rng.Next(100) < o.MultiConcatPercent
            ? ScenarioKind.MultiConcat
            : ScenarioKind.MultiShuffled;
        return new ScenarioPlan(kind, Stress: false, OutputFormat.Binary, files);
    }

    /// <summary>
    /// Применить повреждения по плану: одиночный файл — комбинированные
    /// повреждения в бюджете либо сверх бюджета (стресс); многофайловый —
    /// перемешанный повреждённый поток либо чистая последовательная склейка
    /// FEC-потоков.
    /// </summary>
    /// <param name="plan">План итерации.</param>
    /// <param name="encoded">Пакеты и статистика каждого файла плана.</param>
    /// <param name="rng">Генератор случайных чисел.</param>
    public static DamageResult ApplyDamage(
        ScenarioPlan plan,
        IReadOnlyList<(IReadOnlyList<byte[]> Packets, EncodeStats Stats)> encoded,
        Random rng)
    {
        if (plan.Kind == ScenarioKind.MultiConcat)
            return DamageEngine.ConcatMultiFile(encoded, rng);

        if (plan.Kind == ScenarioKind.MultiShuffled)
            return DamageEngine.ApplyMultiFile(
                encoded, rng, schemes: plan.Files.Select(f => f.Scheme).ToList());

        var overkill = 0;
        var collisionKill = false;
        if (plan.Stress)
        {
            if (rng.Next(100) < StressOverkillPercent)
                overkill = 1 + rng.Next(2);
            else
                collisionKill = true;
        }

        var (packets, stats) = encoded[0];
        return DamageEngine.Apply(
            packets, stats, plan.Format, rng, overkill, collisionKill,
            plan.Files[0].Scheme);
    }

    /// <summary>
    /// Повреждения с кусками единого формата: смешанные txt+bin куски
    /// переигрываются (нужно потоковым CLI-приёмникам; в остальном
    /// эквивалентно <see cref="ApplyDamage"/>).
    /// </summary>
    public static DamageResult ApplyDamageUniform(
        ScenarioPlan plan,
        IReadOnlyList<(IReadOnlyList<byte[]> Packets, EncodeStats Stats)> encoded,
        Random rng)
    {
        for (var attempt = 0; ; attempt++)
        {
            var damage = ApplyDamage(plan, encoded, rng);
            if (CodecVariants.IsUniform(damage) || attempt >= UniformAttempts)
                return damage;
        }
    }

    /// <summary>
    /// Итоговый стресс итерации: разыгранный стресс либо коллизии версий —
    /// адверсариальный случай, когда декодер может не суметь однозначно
    /// выбрать правильную версию даже в бюджете.
    /// </summary>
    public static bool IsStressOutcome(ScenarioPlan plan, DamageResult damage) =>
        plan.Stress || (damage.Mask & DamageBits.Collision) != 0;

    /// <summary>
    /// Ожидание декодирования выполнено: обычный режим — точное
    /// восстановление; стресс — чистый отказ (null) либо точное
    /// восстановление.
    /// </summary>
    public static bool MeetsExpectation(
        ScenarioPlan plan, DamageResult damage, byte[]? restored, byte[] content)
    {
        var exact = restored is not null &&
                    restored.AsSpan().SequenceEqual(content);
        return IsStressOutcome(plan, damage)
            ? restored is null || exact
            : exact;
    }

    // ── Разыгрывание параметров файла ───────────────────────────────────────

    /// <summary>
    /// Случайный размер файла: лог-равномерный в диапазоне настроек,
    /// иногда — пустой файл.
    /// </summary>
    private static int NextSize(Random rng, ScenarioOptions o)
    {
        if (rng.Next(100) < o.EmptyChancePercent) return 0;

        var size = (int)Math.Round(Math.Pow(2.0, rng.NextDouble() * o.MaxSizeLog2));
        return Math.Clamp(size, o.MinFileSize, 1 << o.MaxSizeLog2);
    }

    /// <summary>Оценка N+M до кодирования (порог применимости схемы VirtualIndex).</summary>
    private static int EstimateTotalVolumes(int size, int eccPercent)
    {
        var dataCount = Math.Max(1,
            (size + PacketFormat.PayloadSize - 1) / PacketFormat.PayloadSize);
        return dataCount + StreamEncoder.ComputeEccCount(dataCount, eccPercent);
    }

    /// <summary>
    /// Схема секторов итерации: иногда VirtualIndex с откатом на Classic для
    /// файлов длиннее порога (схема B применима максимум к 512 томам N+M —
    /// кодер без даунгрейда отказывает, поэтому длинные файлы кодируются A).
    /// </summary>
    private static SectorScheme RollScheme(int size, int eccPercent, Random rng, ScenarioOptions o) =>
        rng.Next(100) < o.VirtualIndexChancePercent &&
        EstimateTotalVolumes(size, eccPercent) <= VirtualIndexHasher.DefaultSectorLimit
            ? SectorScheme.VirtualIndex
            : SectorScheme.Classic;

    /// <summary>
    /// Порог применимости VirtualIndex: по умолчанию 512; для схемы B иногда
    /// плотнее — оценка N+M плюс небольшой запас (порог всегда покрывает файл,
    /// кодер без даунгрейда не отказывает).
    /// </summary>
    private static int RollSectorLimit(
        SectorScheme scheme, int size, int eccPercent, Random rng, ScenarioOptions o)
    {
        if (scheme != SectorScheme.VirtualIndex)
            return VirtualIndexHasher.DefaultSectorLimit;

        return rng.Next(2) == 0
            ? VirtualIndexHasher.DefaultSectorLimit
            : Math.Min(VirtualIndexHasher.DefaultSectorLimit,
                EstimateTotalVolumes(size, eccPercent) + 1 + rng.Next(32));
    }
}
