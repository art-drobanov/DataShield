using System.Buffers.Binary;
using DataShield.Codec.Reporting;

namespace DataShield.Codec.Ecc;

/// <summary>
/// Адаптер стирающего кода Рида–Соломона над GF(2¹⁶) для 64-байтных томов.
///
/// Поле GF(2¹⁶) реализовано в <see cref="RsRaid16"/> (не изменяется).
/// Один символ поля = 2 байта (UInt16, little-endian), поэтому 64-байтный
/// payload раскладывается на 32 символа, обрабатываемых независимо.
///
/// Кодер: K data-томов → M избыточных (ECC). Все K+M значений образуют
/// кодовое слово, для которого выполняется K+M линейных уравнений над полем.
///
/// Декодер: по карте стираний (какие тома потеряны) и доступным томам
/// восстанавливает пропущенные data-тома. Условие восстановления:
/// число стёртых data-томов ≤ числу доступных ECC-томов.
///
/// Ограничение: K + M ≤ 65535 (размер поля GF(2¹⁶)).
/// </summary>
public sealed class RsCodecAdapter
{
    // ─────────────────────────────────────────────────────────────────────────
    //  КОДИРОВАНИЕ
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Кодировать K data-томов → M ECC-томов.
    ///
    /// Каждый ECC-том — линейная комбинация data-томов над GF(2¹⁶).
    /// Вычисления выполняются символ за символом (по 2 байта):
    /// для каждой из 32 позиций собирается столбец из K data-символов,
    /// <see cref="RsRaidBase.Process(int[])"/> достраивает M ECC-символов в хвосте
    /// того же массива, после чего они записываются в соответствующие
    /// ECC-тома.
    /// </summary>
    /// <param name="dataSectors">K data-томов одинаковой длины (кратной 2).</param>
    /// <param name="eccCount">Требуемое число ECC-томов (M).</param>
    /// <returns>M ECC-томов; пустой массив, если M ≤ 0 или K = 0.</returns>
    public IReadOnlyList<byte[]> Encode(IReadOnlyList<byte[]> dataSectors, int eccCount) =>
        Encode(dataSectors, eccCount, progress: null, default);

    /// <inheritdoc cref="Encode(IReadOnlyList{byte[]}, int)"/>
    /// <param name="dataSectors">K data-томов одинаковой длины (кратной 2).</param>
    /// <param name="eccCount">Требуемое число ECC-томов (M).</param>
    /// <param name="progress">Приёмник прогресса (локальная шкала 0..100).</param>
    /// <param name="ct">Токен отмены.</param>
    public IReadOnlyList<byte[]> Encode(
        IReadOnlyList<byte[]> dataSectors, int eccCount,
        IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        var k = dataSectors.Count;
        var m = eccCount;

        // Граничный случай: нечего кодировать
        if (k == 0 || m <= 0) return [];

        // Проверка предела поля: K + M не может превышать 65535
        if (k + m > GF16.GFSizeConst)
            throw new InvalidOperationException(
                $"K + M = {k + m} превышает предел GF(16) = {GF16.GFSizeConst}.");

        // Длина payload — берётся от первого data-тома;
        // все тома должны быть одинаковой длины (проверяется ниже косвенно
        // через UniformSpan — если длины разные, ReadUInt16 выйдет за границу).
        var payloadSize = dataSectors[0].Length;

        // Число GF-символов в одном томе = payload / 2
        var symbolsPerSector = payloadSize / 2;

        // Инициализация RS-матрицы для кодирования (карта валидности не нужна)
        var rs = new RsRaid16();
        if (!rs.Init(k, m, validBlockMap: null))
            throw new InvalidOperationException("RsRaid16 init failed for encoding.");

        // Результирующие ECC-тома
        var ecc = new byte[m][];
        for (var i = 0; i < m; i++) ecc[i] = new byte[payloadSize];

        // Рабочий столбец: первые K элементов — data, следующие M — будет
        // заполнено ECC-символами после Process.
        var column = new int[k + m];

        var lastPct = -1;

        // Обходим все 32 позиции символов независимо
        for (var s = 0; s < symbolsPerSector; s++)
        {
            ProgressThrottle.Tick(progress, ref lastPct, s, symbolsPerSector, CodecStrings.EccEncoding, ct);

            // Байтовое смещение текущего символа в каждом томе
            var byteOff = s * 2;

            // Собираем K data-символов из всех data-томов
            for (var i = 0; i < k; i++)
                column[i] = BinaryPrimitives.ReadUInt16LittleEndian(
                    dataSectors[i].AsSpan(byteOff, 2));

            // Process достраивает M ECC-символов в column[k..k+m)
            rs.Process(column);

            // Записываем вычисленные ECC-символы в соответствующие ECC-тома
            for (var j = 0; j < m; j++)
                BinaryPrimitives.WriteUInt16LittleEndian(
                    ecc[j].AsSpan(byteOff, 2), (ushort)column[k + j]);
        }

        progress?.Report(CodecProgress.Create(100, CodecStrings.EccEncoding));

        return ecc;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  ДЕКОДИРОВАНИЕ
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Восстановить пропущенные data-тома по ECC.
    ///
    /// На вход подаётся массив из K+M слотов: первые K — data, остальные M —
    /// ECC. Карта <paramref name="validityMap"/> отмечает, какие слоты
    /// действительно приняты (true), а какие стёрты (false).
    ///
    /// Алгоритм:
    /// <list type="bullet">
    ///   <item>Если все data на месте — возвращаем их как есть (passthrough).</item>
    ///   <item>Если число стёртых data &gt; доступных ECC — восстановление
    ///         невозможно, возвращаем null.</item>
    ///   <item>Иначе — инициализируем RS с картой стираний и обращаем
    ///         матрицу системы, восстанавливая потерянные символы.</item>
    /// </list>
    /// </summary>
    /// <param name="sectors">Массив K+M слотов; стёртые могут быть null.</param>
    /// <param name="validityMap">Карта наличия длиной K+M.</param>
    /// <param name="dataCount">K — число data-томов.</param>
    /// <returns>K восстановленных data-томов, либо null при неудаче.</returns>
    public IReadOnlyList<byte[]>? Decode(
        IReadOnlyList<byte[]?> sectors, bool[] validityMap, int dataCount) =>
        Decode(sectors, validityMap, dataCount, progress: null, default);

    /// <inheritdoc cref="Decode(IReadOnlyList{byte[]}, bool[], int)"/>
    /// <param name="sectors">Массив K+M слотов; стёртые могут быть null.</param>
    /// <param name="validityMap">Карта наличия длиной K+M.</param>
    /// <param name="dataCount">K — число data-томов.</param>
    /// <param name="progress">Приёмник прогресса (локальная шкала 0..100).</param>
    /// <param name="ct">Токен отмены.</param>
    public IReadOnlyList<byte[]>? Decode(
        IReadOnlyList<byte[]?> sectors, bool[] validityMap, int dataCount,
        IProgress<CodecProgress>? progress, CancellationToken ct)
    {
        var total = sectors.Count;

        // Базовые проверки целостности аргументов
        if (total == 0 || validityMap.Length != total) return null;

        var k = dataCount;
        if (k <= 0 || k >= total) return null;
        var m = total - k; // Число ECC-томов

        // ── Определение единого размера payload среди валидных томов ────────
        var payloadSize = -1;
        for (var i = 0; i < total; i++)
        {
            if (!validityMap[i]) continue;
            var buf = sectors[i];
            if (buf is null) return null; // Карта говорит «валиден», но данных нет
            if (payloadSize < 0) payloadSize = buf.Length;     // первый замер
            else if (buf.Length != payloadSize) return null;   // неодинаковая длина
        }
        // Если ни одного валидного тома нет или длина нечётная — отказ
        if (payloadSize < 0 || (payloadSize & 1) != 0) return null;

        // ── Подсчёт стираний и доступных ECC ────────────────────────────────
        var erasedData = 0;
        for (var i = 0; i < k; i++) if (!validityMap[i]) erasedData++;

        var eccAvail = 0;
        for (var i = 0; i < m; i++) if (validityMap[k + i]) eccAvail++;

        // Случай 1: потерь data нет — возвращаем как есть
        if (erasedData == 0)
        {
            var passthrough = new byte[k][];
            for (var i = 0; i < k; i++) passthrough[i] = sectors[i]!;
            return passthrough;
        }

        // Случай 2: стёрто больше, чем ECC может покрыть — восстановить нельзя
        if (erasedData > eccAvail) return null;

        // Случай 3: инициализируем RS-матрицу с картой стираний
        var rs = new RsRaid16();
        if (!rs.Init(k, m, validityMap)) return null;

        // ── Подготовка буфера восстановления ────────────────────────────────
        // Валидные data-тома клонируем, стёртые — заполняем нулями
        // (значения будут перезаписаны после Process).
        var recovered = new byte[k][];
        for (var i = 0; i < k; i++)
            recovered[i] = validityMap[i]
                ? (byte[])sectors[i]!.Clone()
                : new byte[payloadSize];

        var symbolsPerSector = payloadSize / 2;
        var column = new int[k + m];
        var lastPct = -1;

        // ── Покомпонентное восстановление ───────────────────────────────────
        for (var s = 0; s < symbolsPerSector; s++)
        {
            ProgressThrottle.Tick(progress, ref lastPct, s, symbolsPerSector, CodecStrings.RsRecovery, ct);

            var byteOff = s * 2;

            // Загружаем data-символы (стёртые — ноль)
            for (var i = 0; i < k; i++)
                column[i] = validityMap[i]
                    ? BinaryPrimitives.ReadUInt16LittleEndian(sectors[i]!.AsSpan(byteOff, 2))
                    : 0;

            // Загружаем ECC-символы (стёртые — ноль)
            for (var j = 0; j < m; j++)
                column[k + j] = validityMap[k + j]
                    ? BinaryPrimitives.ReadUInt16LittleEndian(sectors[k + j]!.AsSpan(byteOff, 2))
                    : 0;

            // Process решает систему и восстанавливает стёртые символы
            rs.Process(column);

            // Записываем восстановленные data-символы обратно в буфер
            for (var i = 0; i < k; i++)
            {
                if (validityMap[i]) continue; // Пропускаем целые тома
                BinaryPrimitives.WriteUInt16LittleEndian(
                    recovered[i].AsSpan(byteOff, 2), (ushort)column[i]);
            }
        }

        progress?.Report(CodecProgress.Create(100, CodecStrings.RsRecovery));

        return recovered;
    }
}
