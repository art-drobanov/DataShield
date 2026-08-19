using System.Buffers.Binary;
using System.Diagnostics;
using DataShield.Codec.Ecc;
using DataShield.Codec.Packets;
using DataShield.Codec.Reporting;
using DataShield.Codec.StreamProcessor.Subsets;
using DataShield.Codec.StreamProcessor.Versions;

namespace DataShield.Codec.StreamProcessor;

// ─────────────────────────────────────────────────────────────────────────────
//  Слот приёма одного файла (элементарный накопитель)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Состояние приёма одного файла: известный заголовок, статистика приёма
/// заголовков и принятые версии секторов данных.
/// </summary>
public sealed class ReceptionSlot
{
    private readonly byte[] _headerBytes;

    /*
     * Ключ — номер сектора.
     *
     * Значение — версии payload, отсортированные по убыванию
     * ConfirmationCount. При одинаковом счётчике сохраняется текущий порядок,
     * который может изменяться эвристической прокруткой.
     */
    private readonly SortedDictionary<int, List<SectorVariant>> _sectors = new();

    private readonly SectorVersionSearchOptions _searchOptions;

    /// <summary>Заголовок файла, по которому создан слот.</summary>
    public HeaderContent Header { get; }

    /// <summary>Хеш заголовка (H5, 24 байта) — сид для хеша секторов данных.</summary>
    internal byte[] HeaderHash { get; }

    /// <summary>Сколько копий этого заголовка принято из потока.</summary>
    public int HeaderReceptionCount { get; private set; }

    /// <summary>N — число data-томов файла.</summary>
    public int DataVolumeCount => Header.DataVolumeCount;

    /// <summary>M — число ECC-томов файла.</summary>
    public int EccCount => Header.EccCount;

    /// <summary>N+M — общее число томов файла.</summary>
    public int TotalVolumeCount => Header.TotalVolumeCount;

    /// <summary>Количество номеров секторов, для которых есть хотя бы одна версия.</summary>
    public int ReceivedSectorCount => _sectors.Count;

    /// <summary>Общее количество принятых копий секторов с учётом повторений.</summary>
    public long ReceivedSectorCopyCount
    {
        get
        {
            long count = 0;

            foreach (var versions in _sectors.Values)
                foreach (var version in versions)
                    count += version.ConfirmationCount;

            return count;
        }
    }

    /// <summary>
    /// Количество номеров секторов, для которых обнаружено больше одной
    /// различающейся версии payload.
    /// </summary>
    public int CollisionSectorCount
    {
        get
        {
            var count = 0;

            foreach (var versions in _sectors.Values)
                if (versions.Count > 1)
                    count++;

            return count;
        }
    }

    /// <summary>Покрытие приёма: доля принятых номеров секторов, % от N+M.</summary>
    public double Coverage =>
        TotalVolumeCount == 0
            ? 0.0
            : _sectors.Count * 100.0 / TotalVolumeCount;

    /// <summary>
    /// Создать слот с настройками поиска версий по умолчанию
    /// (<see cref="SectorVersionSearchOptions.Default"/>).
    /// </summary>
    /// <param name="headerBytes">Сериализованный заголовок (51 байт).</param>
    /// <param name="header">Распарсенный заголовок.</param>
    /// <param name="headerHash">Хеш заголовка (поле H5, 24 байта).</param>
    internal ReceptionSlot(
        byte[] headerBytes,
        HeaderContent header,
        byte[] headerHash)
        : this(
            headerBytes,
            header,
            headerHash,
            SectorVersionSearchOptions.Default)
    {
    }

    /// <summary>
    /// Создать слот приёма с заданными настройками поиска версий.
    /// </summary>
    /// <param name="headerBytes">Сериализованный заголовок (51 байт).</param>
    /// <param name="header">Распарсенный заголовок.</param>
    /// <param name="headerHash">Хеш заголовка (поле H5, 24 байта).</param>
    /// <param name="searchOptions">Настройки обхода коллизий версий.</param>
    public ReceptionSlot(
        byte[] headerBytes,
        HeaderContent header,
        byte[] headerHash,
        SectorVersionSearchOptions searchOptions)
    {
        searchOptions.Validate();

        _headerBytes = headerBytes;
        _searchOptions = searchOptions;

        Header = header;
        HeaderHash = headerHash;
        HeaderReceptionCount = 1;
    }

    /// <summary>Побайтовое сравнение сериализованного заголовка с этим слотом.</summary>
    public bool HeaderMatches(ReadOnlySpan<byte> bytes) =>
        bytes.SequenceEqual(_headerBytes);

    /// <summary>Учесть очередную копию заголовка (с насыщением на int.MaxValue).</summary>
    internal void IncrementHeaderCount()
    {
        if (HeaderReceptionCount < int.MaxValue)
            HeaderReceptionCount++;
    }

    /// <summary>
    /// Получить снимок всех версий указанного сектора в текущем порядке
    /// предпочтительности.
    /// </summary>
    public IReadOnlyList<SectorVersionInfo> GetSectorVersions(int sectorNum)
    {
        if (!_sectors.TryGetValue(sectorNum, out var versions))
            return Array.Empty<SectorVersionInfo>();

        var result = new SectorVersionInfo[versions.Count];

        for (var i = 0; i < versions.Count; i++)
        {
            result[i] = new SectorVersionInfo(
                versions[i].Payload.ToArray(),
                versions[i].ConfirmationCount);
        }

        return result;
    }

    /// <summary>
    /// Добавить принятую копию сектора.
    ///
    /// Если payload полностью совпадает с существующей версией, её счётчик
    /// увеличивается. Если payload отличается, создаётся новая версия.
    /// После этого список остаётся отсортированным по убыванию счётчика.
    /// </summary>
    /// <returns>
    /// true — сектор принят; false — номер вне диапазона или неверная длина.
    /// </returns>
    public bool AddSector(int sectorNum, byte[] payload)
    {
        if (sectorNum < 0 || sectorNum >= TotalVolumeCount)
            return false;

        if (payload.Length != PacketFormat.PayloadSize)
            return false;

        if (!_sectors.TryGetValue(sectorNum, out var versions))
        {
            versions = new List<SectorVariant>();
            _sectors.Add(sectorNum, versions);
        }

        for (var i = 0; i < versions.Count; i++)
        {
            var version = versions[i];

            if (!payload.AsSpan().SequenceEqual(version.Payload))
                continue;

            if (version.ConfirmationCount < int.MaxValue)
                version.ConfirmationCount++;

            /*
             * Поднимаем получившую дополнительное подтверждение версию.
             * Через версии с тем же счётчиком не переставляем: это сохраняет
             * текущий порядок равновероятных элементов, в том числе после
             * эвристической прокрутки.
             */
            while (i > 0 &&
                   versions[i - 1].ConfirmationCount <
                   version.ConfirmationCount)
            {
                versions[i] = versions[i - 1];
                i--;
                versions[i] = version;
            }

            return true;
        }

        // Все существующие элементы имеют счётчик >= 1, поэтому новая версия
        // добавляется в конец и сортировка не нарушается.
        versions.Add(new SectorVariant(payload));
        return true;
    }

    /// <summary>
    /// Карта валидности: true для номеров секторов, у которых есть
    /// хотя бы одна принятая версия. Длина = TotalVolumeCount.
    /// </summary>
    public bool[] BuildValidityMap()
    {
        var map = new bool[TotalVolumeCount];

        foreach (var idx in _sectors.Keys)
        {
            if (idx >= 0 && idx < map.Length)
                map[idx] = true;
        }

        return map;
    }

    /// <summary>
    /// Карта валидности в текстовом виде: '█' — принят, '▓' — принят
    /// с коллизией версий, '░' — пропущен.
    /// </summary>
    public string FormatValidityMap()
    {
        var map = BuildValidityMap();

        if (map.Length == 0)
            return "(пусто)";

        var chars = new char[map.Length];

        for (var i = 0; i < map.Length; i++)
        {
            chars[i] = !map[i] ? '░'
                : _sectors[i].Count > 1 ? '▓'
                : '█';
        }

        return new string(chars);
    }

    /// <summary>
    /// Карта коллизий с указанием кратности: номер сектора → число различных
    /// версий payload. Включаются только секторы с более чем одной версией.
    /// </summary>
    public IReadOnlyDictionary<int, int> BuildCollisionMap()
    {
        var map = new Dictionary<int, int>();

        foreach (var (sectorNumber, versions) in _sectors)
            if (versions.Count > 1)
                map.Add(sectorNumber, versions.Count);

        return map;
    }

    // ── Сборка файла с обходом коллизий ─────────────────────────────────────

    /// <summary>
    /// Собрать файл из наиболее подтверждённых версий секторов.
    /// При коллизиях (равновероятные версии) выполняется поиск: полный
    /// перебор или эвристическая прокрутка (см. <see cref="SectorVersionSearchOptions"/>).
    /// </summary>
    /// <returns>Содержимое файла при успехе, иначе null.</returns>
    public byte[]? TryAssemble(RsCodecAdapter? rs = null) =>
        TryAssemble(rs, progress: null, default);

    /// <inheritdoc cref="TryAssemble(RsCodecAdapter?)"/>
    /// <param name="rs">RS-адаптер для восстановления пропущенных томов (может быть null).</param>
    /// <param name="progress">Приёмник прогресса (глобальная шкала 0..100).</param>
    /// <param name="ct">Токен отмены.</param>
    public byte[]? TryAssemble(
        RsCodecAdapter? rs,
        IProgress<CodecProgress>? progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var selected = BuildPreferredSelection();
        var choicePoints = BuildChoicePoints();

        byte[]? result;

        /*
         * Если равновероятных версий нет, сохраняем обычное поведение:
         * сначала прямая сборка, затем RS.
         */
        if (choicePoints.Count == 0)
        {
            result = TryAssembleSelected(
                selected,
                rs,
                progress,
                ct);
        }
        else
        {
            var combinationCount = SectorCombinationMath.CountCombinations(
                TiedCounts(choicePoints));
            var timer = Stopwatch.StartNew();

            progress?.Report(
                CodecProgress.Create(
                    0,
                    string.Format(
                        CodecStrings.SectorVersionSearchFormat,
                        FormatCount(combinationCount))));
            /*
             * Исходное состояние: из каждого слота берётся первый,
             * наиболее предпочтительный вариант.
             */
            var firstAttemptStarted = timer.ElapsedTicks;

            result = TryAssembleSelected(
                selected,
                rs,
                progress: null,
                ct);

            var firstAttemptTicks = Math.Max(
                1,
                timer.ElapsedTicks - firstAttemptStarted);

            if (result is null)
            {
                /*
                 * Решение о полном переборе принимается по двум условиям:
                 *
                 * 1. комбинаций не больше жёсткого лимита;
                 * 2. оценка времени (первая попытка × число комбинаций)
                 *    укладывается в бюджет.
                 */
                var estimatedSeconds =
                    firstAttemptTicks /
                    (double)Stopwatch.Frequency *
                    combinationCount;

                result = SectorCombinationMath.ShouldUseExhaustiveSearch(
                        combinationCount,
                        estimatedSeconds,
                        _searchOptions)
                    ? TryExhaustiveSearch(
                        selected,
                        choicePoints,
                        combinationCount,
                        rs,
                        progress,
                        ct,
                        timer)
                    : TryRotationSearch(
                        choicePoints,
                        rs,
                        progress,
                        ct,
                        timer);
            }
        }

        /*
         * Стадия 3: подбор подмножества томов. Запускается, когда прямая
         * сборка, RS по полной карте и перебор версий не дали результата:
         * подозрительные тома сознательно исключаются и восстанавливаются
         * RS по оставшимся (арбитр — финальный SHA-256).
         */
        if (result is null)
            result = TryVolumeSubsetSearch(rs, progress, ct);

        if (result is not null)
        {
            progress?.Report(
                CodecProgress.Create(100, CodecStrings.AssemblyFinished));
        }

        return result;
    }

    /// <summary>
    /// Полный декартов перебор всех равновероятных версий.
    /// Нулевая комбинация уже была проверена вызывающим методом.
    /// </summary>
    private byte[]? TryExhaustiveSearch(
        byte[][] selected,
        List<ChoicePoint> choicePoints,
        long combinationCount,
        RsCodecAdapter? rs,
        IProgress<CodecProgress>? progress,
        CancellationToken ct,
        Stopwatch timer)
    {
        var indexes = new int[choicePoints.Count];
        var moduli = TiedCounts(choicePoints);
        var lastProgress = -1;

        ReportSearchProgress(
            progress,
            1,
            combinationCount,
            ref lastProgress,
            CodecStrings.ExhaustiveSearch);

        for (long completed = 1;
             completed < combinationCount;
             completed++)
        {
            ct.ThrowIfCancellationRequested();

            if (timer.Elapsed >= _searchOptions.TimeBudget)
                return null;

            if (!SectorCombinationMath.AdvanceIndexes(indexes, moduli))
                return null;

            ApplyIndexes(selected, indexes, choicePoints);

            var result = TryAssembleSelected(
                selected,
                rs,
                progress: null,
                ct);

            ReportSearchProgress(
                progress,
                completed + 1,
                combinationCount,
                ref lastProgress,
                CodecStrings.ExhaustiveSearch);

            if (result is not null)
                return result;
        }

        return null;
    }

    /// <summary>
    /// Эвристический поиск.
    ///
    /// После каждой неудачной попытки во всех неоднозначных слотах первый
    /// равновероятный вариант переносится в конец равновероятной части.
    ///
    /// Пример:
    /// A/B/C + X/Y даст последовательность:
    /// A+X, B+Y, C+X, A+Y, B+X, C+Y.
    /// </summary>
    private byte[]? TryRotationSearch(
        List<ChoicePoint> choicePoints,
        RsCodecAdapter? rs,
        IProgress<CodecProgress>? progress,
        CancellationToken ct,
        Stopwatch timer)
    {
        /*
         * Через НОК определяем количество уникальных состояний синхронной
         * прокрутки. Значение ограничивается MaxHeuristicAttempts.
         *
         * Исходное состояние уже проверено.
         */
        var stateCount = SectorCombinationMath.CountRotationStates(
            TiedCounts(choicePoints),
            _searchOptions.MaxHeuristicAttempts);
        var lastProgress = -1;

        ReportSearchProgress(
            progress,
            1,
            stateCount,
            ref lastProgress,
            CodecStrings.HeuristicRotation);

        for (long state = 1; state < stateCount; state++)
        {
            ct.ThrowIfCancellationRequested();

            /*
             * Предыдущая попытка была неудачна — прокручиваем каждый
             * равновероятный список на один элемент.
             */
            RotateTiedVariants(choicePoints);

            if (timer.Elapsed >= _searchOptions.TimeBudget)
                return null;

            var selected = BuildPreferredSelection();

            var result = TryAssembleSelected(
                selected,
                rs,
                progress: null,
                ct);

            ReportSearchProgress(
                progress,
                state + 1,
                stateCount,
                ref lastProgress,
                CodecStrings.HeuristicRotation);

            if (result is not null)
                return result;
        }

        return null;
    }

    // ── Стадия 3: подбор подмножества томов ─────────────────────────────────

    /// <summary>
    /// Стадия 3: подбор подмножества томов с RS-восстановлением исключённых.
    ///
    /// Покрывает повреждения, невидимые для предыдущих стадий: сектор (data
    /// или ECC) испорчен, но номер цел и усечённый хеш D3 сошёлся, версия
    /// одна — точки ветвления нет, а RS по полной карте принимает испорченный
    /// том за истину.
    ///
    /// Все коллизионные слоты исключаются (RS восстанавливает их — версии не
    /// перебираются), затем по уровням перебираются дополнительные исключения
    /// подозрительных томов. Какая-то маска оставит валидные data и ECC —
    /// декодер восстановит истину, финальный SHA-256 подтвердит. Лимиты
    /// попыток и времени — <see cref="SectorVersionSearchOptions.SubsetSearch"/>.
    /// </summary>
    private byte[]? TryVolumeSubsetSearch(
        RsCodecAdapter? rs,
        IProgress<CodecProgress>? progress,
        CancellationToken ct)
    {
        if (rs is null || Header.EccCount == 0)
            return null;

        var options = _searchOptions.SubsetSearch;

        var volumes = BuildVolumeReceptions();
        var seed = BinaryPrimitives.ReadUInt32LittleEndian(HeaderHash);

        var plan = SubsetMaskPlanner.Plan(
            volumes,
            DataVolumeCount,
            Header.EccCount,
            options,
            seed);

        if (plan.AttemptUpperBound <= 0)
            return null;

        progress?.Report(
            CodecProgress.Create(
                0,
                string.Format(
                    CodecStrings.VolumeSubsetSearchFormat,
                    0,
                    FormatCount(plan.AttemptUpperBound))));

        var selected = BuildPreferredSelection();
        var timer = Stopwatch.StartNew();
        var lastPercentage = -1;
        long attempted = 0;

        foreach (var exclusions in plan.Exclusions)
        {
            ct.ThrowIfCancellationRequested();

            if (attempted >= options.MaxAttempts)
                break;

            if (timer.Elapsed >= options.TimeBudget)
                break;

            attempted++;

            var result = TryAssembleWithEcc(
                rs,
                selected,
                progress: null,
                ct,
                exclusions);

            ReportSearchProgress(
                progress,
                attempted,
                plan.AttemptUpperBound,
                ref lastPercentage,
                CodecStrings.VolumeSubsetSearch);

            if (result is not null)
                return result;
        }

        return null;
    }

    /// <summary>
    /// Снимок приёма всех томов для планировщика подмножеств: присутствие,
    /// кратность коллизии и подтверждения головы каждого номера.
    /// </summary>
    private VolumeReception[] BuildVolumeReceptions()
    {
        var volumes = new VolumeReception[TotalVolumeCount];

        foreach (var (sectorNumber, versions) in _sectors)
        {
            if (sectorNumber < 0 ||
                sectorNumber >= volumes.Length ||
                versions.Count == 0)
            {
                continue;
            }

            volumes[sectorNumber] = new VolumeReception(
                Present: true,
                VariantCount: versions.Count,
                HeadConfirmationCount: versions[0].ConfirmationCount);
        }

        return volumes;
    }

    /// <summary>
    /// Сформировать текущий предпочтительный набор: первый элемент
    /// каждого списка версий.
    /// </summary>
    private byte[][] BuildPreferredSelection()
    {
        var selected = new byte[TotalVolumeCount][];

        foreach (var (sectorNumber, versions) in _sectors)
        {
            if (sectorNumber < 0 ||
                sectorNumber >= selected.Length ||
                versions.Count == 0)
            {
                continue;
            }

            selected[sectorNumber] = versions[0].Payload;
        }

        return selected;
    }

    /// <summary>
    /// Найти слоты с несколькими равновероятными наиболее подтверждёнными
    /// версиями.
    /// </summary>
    private List<ChoicePoint> BuildChoicePoints()
    {
        var result = new List<ChoicePoint>();

        foreach (var (sectorNumber, versions) in _sectors)
        {
            if (versions.Count < 2)
                continue;

            var bestCount = versions[0].ConfirmationCount;
            var tiedCount = 1;

            while (tiedCount < versions.Count &&
                   versions[tiedCount].ConfirmationCount == bestCount)
            {
                tiedCount++;
            }

            if (tiedCount > 1)
            {
                result.Add(
                    new ChoicePoint(
                        sectorNumber,
                        versions,
                        tiedCount));
            }
        }

        return result;
    }

    /// <summary>
    /// Собрать количества равновероятных вариантов из точек ветвления.
    /// </summary>
    private static int[] TiedCounts(IReadOnlyList<ChoicePoint> choicePoints)
    {
        var result = new int[choicePoints.Count];

        for (var i = 0; i < choicePoints.Count; i++)
            result[i] = choicePoints[i].TiedVariantCount;

        return result;
    }

    /// <summary>
    /// Подставить выбранные версии неоднозначных слотов
    /// в набор для очередной попытки сборки.
    /// </summary>
    private static void ApplyIndexes(
        byte[][] selected,
        IReadOnlyList<int> indexes,
        IReadOnlyList<ChoicePoint> choicePoints)
    {
        for (var i = 0; i < choicePoints.Count; i++)
        {
            var point = choicePoints[i];

            selected[point.SectorNumber] =
                point.Variants[indexes[i]].Payload;
        }
    }

    /// <summary>
    /// Циклически сдвинуть только равновероятную начальную часть списка.
    ///
    /// Элементы с меньшим счётчиком не затрагиваются, поэтому сортировка
    /// по убыванию ConfirmationCount остаётся корректной.
    /// </summary>
    private static void RotateTiedVariants(
        IReadOnlyList<ChoicePoint> choicePoints)
    {
        foreach (var point in choicePoints)
        {
            if (point.TiedVariantCount < 2)
                continue;

            var first = point.Variants[0];

            point.Variants.RemoveAt(0);
            point.Variants.Insert(
                point.TiedVariantCount - 1,
                first);
        }
    }

    /// <summary>
    /// Число в строку; long.MaxValue заменяется заглушкой переполнения.
    /// </summary>
    private static string FormatCount(long count) =>
        count == long.MaxValue
            ? CodecStrings.MoreThanLongMax
            : count.ToString();

    /// <summary>
    /// Сообщить прогресс перебора/прокрутки с троттлингом по целому
    /// проценту (не более 99 до завершения операции).
    /// </summary>
    private static void ReportSearchProgress(
        IProgress<CodecProgress>? progress,
        long completed,
        long total,
        ref int lastPercentage,
        string stage)
    {
        if (progress is null)
            return;

        var percentage = total <= 0
            ? 0
            : (int)Math.Min(
                99,
                completed / (double)total * 100.0);

        if (percentage == lastPercentage)
            return;

        lastPercentage = percentage;

        progress.Report(
            CodecProgress.Create(
                percentage,
                $"{stage}: {completed} / {FormatCount(total)}"));
    }

    // ── Одна попытка сборки для выбранной комбинации ────────────────────────

    /// <summary>
    /// Одна попытка сборки для выбранной комбинации: прямая сборка,
    /// при неудаче — RS-восстановление (если задан адаптер).
    /// </summary>
    private byte[]? TryAssembleSelected(
        byte[][] selected,
        RsCodecAdapter? rs,
        IProgress<CodecProgress>? progress,
        CancellationToken ct)
    {
        var direct = TryAssembleDirect(selected);

        if (direct is not null)
            return direct;

        if (rs is null)
            return null;

        return TryAssembleWithEcc(
            rs,
            selected,
            progress,
            ct);
    }

    /// <summary>Прямая сборка без ECC: все N data-тома должны быть приняты.</summary>
    internal byte[]? TryAssembleDirect() =>
        TryAssembleDirect(BuildPreferredSelection());

    /// <summary>
    /// Собрать буфер из выбранных data-томов (все N должны быть приняты),
    /// обрезать до FileSize и проверить SHA-256.
    /// </summary>
    private byte[]? TryAssembleDirect(byte[][] selected)
    {
        var n = DataVolumeCount;

        for (var i = 0; i < n; i++)
        {
            if (selected[i] is null)
                return null;
        }

        var buffer = new byte[(long)n * PacketFormat.PayloadSize];
        var offset = 0;

        for (var i = 0; i < n; i++)
        {
            selected[i].CopyTo(buffer, offset);
            offset += PacketFormat.PayloadSize;
        }

        return TrimAndVerify(buffer);
    }

    /// <summary>Сборка с RS-восстановлением пропущенных data-томов.</summary>
    internal byte[]? TryAssembleWithEcc(RsCodecAdapter rs) =>
        TryAssembleWithEcc(
            rs,
            BuildPreferredSelection(),
            progress: null,
            default);

    /// <inheritdoc cref="TryAssembleWithEcc(RsCodecAdapter)"/>
    internal byte[]? TryAssembleWithEcc(
        RsCodecAdapter rs,
        IProgress<CodecProgress>? progress,
        CancellationToken ct) =>
        TryAssembleWithEcc(
            rs,
            BuildPreferredSelection(),
            progress,
            ct);

    /// <summary>
    /// RS-восстановление пропущенных data-томов по доступным ECC-томам
    /// с последующей обрезкой и проверкой SHA-256.
    /// </summary>
    private byte[]? TryAssembleWithEcc(
        RsCodecAdapter rs,
        byte[][] selected,
        IProgress<CodecProgress>? progress,
        CancellationToken ct,
        int[]? forcedErasure = null)
    {
        if (Header.EccCount == 0)
            return null;

        var n = DataVolumeCount;
        var total = TotalVolumeCount;

        // Принудительно стёртые тома (стадия подбора подмножества):
        // RS восстанавливает их по оставшимся, payload не используется.
        var excluded = new bool[total];

        if (forcedErasure is not null)
        {
            foreach (var volume in forcedErasure)
            {
                if (volume >= 0 && volume < total)
                    excluded[volume] = true;
            }
        }

        var map = new bool[total];
        var received = new byte[total][];

        var present = 0;

        for (var i = 0; i < total; i++)
        {
            if (excluded[i])
                continue;

            var payload = selected[i];

            if (payload is null ||
                payload.Length != PacketFormat.PayloadSize)
            {
                continue;
            }

            map[i] = true;
            received[i] = payload;
            present++;
        }

        if (present < n)
            return null;

        var rsProgress = progress is null
            ? null
            : new ScaledProgress(
                progress,
                CodecStrings.RsRecovery,
                0,
                100);

        var recovered = rs.Decode(
            received,
            map,
            n,
            rsProgress,
            ct);

        if (recovered is null || recovered.Count != n)
            return null;

        foreach (var chunk in recovered)
        {
            if (chunk is null ||
                chunk.Length != PacketFormat.PayloadSize)
            {
                return null;
            }
        }

        var buffer = new byte[(long)n * PacketFormat.PayloadSize];
        var offset = 0;

        for (var i = 0; i < n; i++)
        {
            recovered[i].CopyTo(buffer, offset);
            offset += PacketFormat.PayloadSize;
        }

        return TrimAndVerify(buffer);
    }

    /// <summary>
    /// Обрезать буфер до размера файла и проверить SHA-256.
    /// При несовпадении хеша буферы очищаются, возвращается null.
    /// </summary>
    private byte[]? TrimAndVerify(byte[] buffer)
    {
        var result = buffer.Length == (int)Header.FileSize
            ? buffer
            : buffer.AsSpan(0, (int)Header.FileSize).ToArray();

        var hash = Sha256Compact.HashData(result);

        if (!hash.AsSpan().SequenceEqual(Header.Sha256))
        {
            Array.Clear(buffer);

            if (!ReferenceEquals(result, buffer))
                Array.Clear(result);

            return null;
        }

        return result;
    }
}
