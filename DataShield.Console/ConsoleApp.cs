using System.Text;
using DataShield.Codec;
using DataShield.Codec.Packets;
using DataShield.Codec.Reporting;
using DataShield.Codec.StreamProcessor;
using DataShield.Interfaces;

namespace DataShield.Console;

// ─────────────────────────────────────────────────────────────────────────────
//  Консольное приложение: разбор аргументов, команды encode/decode
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Консольное приложение DataShield. Точка входа приложения-локализации:
/// <c>new ConsoleApp(RuStrings.Instance).Run(args)</c> — реализация
/// <see cref="IConsoleStrings"/> живёт в локализованной консоли
/// (DataShield.Console.RU / DataShield.Console.EN).
/// </summary>
public sealed class ConsoleApp(IConsoleStrings strings)
{
    /// <summary>Строка «продукт — версия — копирайт» для шапки и заголовка окна.</summary>
    private string VersionLine { get; set; } =
        $"DataShield v1.0 {BuildInfo.BuildLabel}   Copyright (c) 2026 Artem Drobanov, Vladislav Utyumov";

    private const int ExitOk = 0;
    private const int ExitError = 1;
    private const int ExitUsage = 2;
    // 130 — конвенция MIT-spec/cmd: 128 + SIGINT(2), процесс прерван пользователем
    private const int ExitCanceled = 130;

    /// <summary>Выполнить команду и вернуть код завершения.</summary>
    public int Run(string[] args)
    {
        CodecStrings.Language = strings.CodecLanguage;
        System.Console.OutputEncoding = Encoding.UTF8;

        var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        VersionLine =
            $"DataShield v{ver?.Major ?? 1}.{ver?.Minor ?? 0} {BuildInfo.BuildLabel}   Copyright (c) 2026 Artem Drobanov, Vladislav Utyumov";
        if (!System.Console.IsOutputRedirected)
            System.Console.Title = VersionLine;

        using var cts = new CancellationTokenSource();
        // Ctrl+C не убивает процесс мгновенно: отменяем токен, чтобы кодек
        // корректно завершил текущую фазу и освободил ресурсы
        System.Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            return RunCore(args, cts.Token);
        }
        catch (CliException ex)
        {
            System.Console.Error.WriteLine(ex.Message);
            System.Console.Error.WriteLine(strings.UsageHint);
            return ExitUsage;
        }
        catch (OperationCanceledException)
        {
            System.Console.Error.WriteLine(strings.CanceledByUser);
            return ExitCanceled;
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine(string.Format(strings.Culture, strings.ErrorFormat, ex.Message));
            return ExitError;
        }
    }

    /// <summary>
    /// Диспетчер команд: help — справка, encode/decode — рабочие команды,
    /// прочее — ошибка использования. Аргументы после команды разбираются
    /// в позиционные и опции (<see cref="ParseArguments"/>).
    /// </summary>
    private int RunCore(string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return ExitUsage;
        }

        var command = args[0].ToLowerInvariant();

        if (command is "help" or "--help" or "/?")
        {
            PrintHelp();
            return ExitOk;
        }

        var (positional, options) = ParseArguments(args[1..]);

        return command switch
        {
            "encode" => Encode(positional, options, ct),
            "decode" => Decode(positional, options, ct),
            _ => throw new CliException(
                string.Format(strings.Culture, strings.UnknownCommandFormat, command)),
        };
    }

    // ── encode ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Команда encode: один входной файл → поток в выбранном формате.
    /// Опции: --format text|bin, --ecc 0..100, --header 0..100,
    /// --scheme classic|virtual, --output, --quiet. После кодирования
    /// печатает сводку (SHA-256, тома, избыточность).
    /// </summary>
    private int Encode(
        List<string> positional, Dictionary<string, string> options, CancellationToken ct)
    {
        if (positional.Count != 1)
            throw new CliException(strings.EncodeSingleInputError);

        var inputPath = positional[0];
        if (!File.Exists(inputPath))
            throw new CliException(
                string.Format(strings.Culture, strings.EncodeFileNotFoundFormat, inputPath));

        var format = ParseFormat(GetString(options, "format", "text"));
        var eccPercent = GetInt(options, "ecc", 10, 0, 1000);
        var headerPercent = GetInt(options, "header", StreamEncoder.DefaultHeaderPercent, 0, 50);
        var scheme = ParseScheme(GetString(options, "scheme", "classic"));
        var quiet = options.ContainsKey("quiet");

        var outputPath = GetString(options, "output", OutputFormatConfig.GetDefaultOutputPath(inputPath, format));

        using IDataShieldCodec codec = new DataShieldConsole(quiet, eccPercent, headerPercent, scheme);
        var stats = DataShieldCodecFiles.EncodeFile(codec, inputPath, outputPath, format, ct: ct);

        PrintBanner();
        var labelWidth = new[]
        {
            strings.FileLabel, strings.SizeLabel, strings.Sha256Label, strings.VolumesLabel,
            strings.PacketsLabel, strings.SchemeLabel, strings.OutputLabel,
        }.Max(label => label.Length) + 1;
        Field(strings.FileLabel, inputPath, labelWidth);
        Field(strings.SizeLabel, string.Format(strings.Culture, strings.SizeFormat, stats.FileSize), labelWidth);
        Field(strings.Sha256Label, Hex(stats.Sha256), labelWidth);
        // Избыточность ECC: доля ECC-томов от data-томов
        var eccRedundancyPct = stats.DataCount > 0
            ? stats.EccCount * 100.0 / stats.DataCount
            : 0.0;
        // Избыточность заголовков: копии относительно числа томов потока
        // (TotalPackets включает сами заголовки и занижает долю)
        var totalVolumes = stats.DataCount + stats.EccCount;
        var headerRedundancyPct = totalVolumes > 0
            ? stats.HeaderCopies * 100.0 / totalVolumes
            : 0.0;

        Field(strings.VolumesLabel,
            string.Format(strings.Culture, strings.VolumesFormat, eccRedundancyPct, stats.DataCount, stats.EccCount),
            labelWidth);
        Field(strings.PacketsLabel,
            string.Format(strings.Culture, strings.PacketsFormat, headerRedundancyPct, stats.TotalPackets, stats.HeaderCopies),
            labelWidth);
        Field(strings.SchemeLabel, SchemeName(scheme), labelWidth);
        Field(strings.OutputLabel, $"{outputPath} ({FormatName(format)})", labelWidth);
        return ExitOk;
    }

    // ── decode ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Команда decode: входной поток (формат определяется по расширению)
    /// → восстановление. Без --all обрабатывается только первый слот
    /// (наиболее полный), --use-name сохраняет под именем из заголовка.
    /// Возвращает ExitError, если ни один файл не восстановлен.
    /// </summary>
    private int Decode(
        List<string> positional, Dictionary<string, string> options, CancellationToken ct)
    {
        if (positional.Count != 1)
            throw new CliException(strings.DecodeSingleInputError);

        var inputPath = positional[0];
        if (!File.Exists(inputPath))
            throw new CliException(
                string.Format(strings.Culture, strings.DecodeFileNotFoundFormat, inputPath));

        var scheme = ParseScheme(GetString(options, "scheme", "classic"));
        var restoreAll = options.ContainsKey("all");
        var useSlotName = options.ContainsKey("use-name");
        var quiet = options.ContainsKey("quiet");
        var explicitOutput = GetString(options, "output", "");

        var format = OutputFormatConfig.DetectFormat(inputPath);

        using IDataShieldCodec codec = new DataShieldConsole(quiet, sectorScheme: scheme);
        var results = DataShieldCodecFiles.DecodeFile(codec, inputPath, ct: ct);

        if (results.Count == 0)
        {
            System.Console.Error.WriteLine(strings.NoHeadersError);
            return ExitError;
        }

        var targets = restoreAll ? results : results.Take(1).ToList();

        // Ширина колонки под-полей слота: SHA-256 выравнивается под метку
        // строки восстановления (длина префикса её форматной строки)
        var restoredPrefix = strings.RestoredLineFormat[
            ..strings.RestoredLineFormat.IndexOf('{')];
        var subWidth = Math.Max(strings.Sha256Label.Length, restoredPrefix.Length);

        PrintBanner();
        var labelWidth = new[]
        {
            strings.InputLabel, strings.FilesLabel, strings.SchemeLabel,
        }.Max(label => label.Length) + 1;
        Field(strings.InputLabel, $"{inputPath} ({FormatName(format)})", labelWidth);
        Field(strings.FilesLabel,
            string.Format(strings.Culture, strings.FilesInStreamFormat, results.Count, targets.Count), labelWidth);
        Field(strings.SchemeLabel, SchemeName(scheme), labelWidth);

        var restored = 0;

        for (var i = 0; i < targets.Count; i++)
        {
            var result = targets[i];
            var slot = result.Slot;

            System.Console.WriteLine();
            System.Console.WriteLine(string.Format(
                strings.Culture, strings.SlotHeaderFormat,
                i + 1, slot.Header.FileName, slot.Header.FileSize,
                slot.Coverage, slot.ReceivedSectorCount, slot.TotalVolumeCount,
                slot.HeaderReceptionCount));

            if (slot.CollisionSectorCount > 0)
                System.Console.WriteLine("    " + string.Format(
                    strings.Culture, strings.CollisionsFormat, slot.CollisionSectorCount));

            var content = result.Content;

            if (content is null)
            {
                System.Console.Error.WriteLine("    " + strings.AssemblyFailedError);
                continue;
            }

            var outputPath = ResolveDecodeOutputPath(
                explicitOutput, useSlotName, inputPath, slot, i, targets.Count);

            File.WriteAllBytes(outputPath, content);

            // MAP-отчёт формирует кодек, локализация — CodecStrings.Language
            var mapPath = SectorMapReport.BuildPath(outputPath);
            SectorMapReport.Write(mapPath, slot);

            System.Console.WriteLine("    " + strings.Sha256Label.PadRight(subWidth) + Hex(slot.Header.Sha256));
            System.Console.WriteLine("    " + string.Format(
                strings.Culture, strings.RestoredLineFormat, outputPath, content.Length));

            // Метка карты выравнивается под SHA-256/строку восстановления
            var mapPrefix = strings.MapLineFormat[..strings.MapLineFormat.IndexOf('{')].TrimEnd();
            System.Console.WriteLine("    " + mapPrefix.PadRight(subWidth) + mapPath);
            restored++;
        }

        System.Console.WriteLine();
        System.Console.WriteLine(restored > 0
            ? string.Format(strings.Culture, strings.DoneSummaryFormat, restored, targets.Count)
            : strings.NoFilesRestored);

        return restored > 0 ? ExitOk : ExitError;
    }

    /// <summary>
    /// Выбрать путь восстановленного файла. Приоритет: --use-name
    /// (имя из заголовка, безопасное для файловой системы, каталог берётся
    /// из --output либо из входного файла) → явный --output → путь по
    /// умолчанию. При нескольких слотах добавляется индекс перед расширением.
    /// </summary>
    private string ResolveDecodeOutputPath(
        string explicitOutput,
        bool useSlotName,
        string inputPath,
        ReceptionSlot slot,
        int index,
        int total)
    {
        var headerName = slot.Header.FileName;

        if (useSlotName && !string.IsNullOrWhiteSpace(headerName))
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                headerName = headerName.Replace(c, '_');

            string directory;
            if (explicitOutput.Length > 0)
                directory = Directory.Exists(explicitOutput)
                    ? explicitOutput
                    : Path.GetDirectoryName(Path.GetFullPath(explicitOutput)) ?? "";
            else
                directory = Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? "";

            return string.IsNullOrEmpty(directory)
                ? WithIndex(headerName, index, total)
                : Path.Combine(directory, WithIndex(headerName, index, total));
        }

        if (explicitOutput.Length > 0)
            return WithIndex(explicitOutput, index, total);

        return WithIndex(OutputFormatConfig.GetDefaultDecodeOutputPath(inputPath), index, total);
    }

    /// <summary>
    /// Дописать 1-базированный индекс слота перед расширением
    /// («name.1.ext»), чтобы несколько восстановленных файлов не затирали
    /// друг друга. Единственный файл сохраняется без индекса.
    /// </summary>
    private static string WithIndex(string path, int index, int total)
    {
        if (total <= 1)
            return path;

        var ext = Path.GetExtension(path);
        var stem = ext.Length > 0 ? path[..^ext.Length] : path;
        return $"{stem}.{index + 1}{ext}";
    }

    // ── разбор аргументов ───────────────────────────────────────────────────

    /// <summary>
    /// Разбор аргументов командной строки: всё, что не начинается с «-»,
    /// считается позиционным; опции допускают формы «--key value»,
    /// «--key=value» и короткие псевдонимы (<see cref="CanonicalOption"/>).
    /// Флаговым опциям присваивается значение «true».
    /// </summary>
    private (List<string> Positional, Dictionary<string, string> Options) ParseArguments(
        string[] args)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Length < 2 || arg[0] != '-')
            {
                positional.Add(arg);
                continue;
            }

            string body;
            string? inline = null;

            var eq = arg.IndexOf('=');
            if (eq >= 0)
            {
                body = arg[..eq];
                inline = arg[(eq + 1)..];
            }
            else
            {
                body = arg;
            }

            var key = CanonicalOption(body)
                ?? throw new CliException(
                    string.Format(strings.Culture, strings.UnknownOptionFormat, arg));

            // Значение берётся из «=», из следующего аргумента; иначе опция — флаг
            if (OptionNeedsValue(key))
            {
                string value;
                if (inline is not null)
                    value = inline;
                else if (i + 1 < args.Length)
                    value = args[++i];
                else
                    throw new CliException(
                        string.Format(strings.Culture, strings.OptionValueMissingFormat, arg));

                options[key] = value;
            }
            else
            {
                options[key] = "true";
            }
        }

        return (positional, options);
    }

    /// <summary>Каноническое имя опции по длинному имени или короткому алиасу.</summary>
    private static string? CanonicalOption(string body)
    {
        var name = body.TrimStart('-').ToLowerInvariant();
        return name switch
        {
            "o" or "output" => "output",
            "f" or "format" => "format",
            "e" or "ecc" => "ecc",
            "h" or "header" => "header",
            "s" or "scheme" => "scheme",
            "q" or "quiet" => "quiet",
            "a" or "all" => "all",
            "n" or "use-name" or "usename" => "use-name",
            _ => null,
        };
    }

    /// <summary>Опции, требующие значения (остальные — булевы флаги).</summary>
    private static bool OptionNeedsValue(string key) =>
        key is "output" or "format" or "ecc" or "header" or "scheme";

    /// <summary>Значение строковой опции или значение по умолчанию, если не задана/пуста.</summary>
    private static string GetString(
        Dictionary<string, string> options, string key, string defaultValue) =>
        options.TryGetValue(key, out var value) && value.Length > 0
            ? value
            : defaultValue;

    /// <summary>
    /// Значение целочисленной опции с проверкой синтаксиса и диапазона
    /// [min, max]; ошибки — CliException с кодом использования.
    /// </summary>
    private int GetInt(
        Dictionary<string, string> options, string key, int defaultValue, int min, int max)
    {
        if (!options.TryGetValue(key, out var raw) || raw.Length == 0)
            return defaultValue;

        if (!int.TryParse(raw, out var value))
            throw new CliException(
                string.Format(strings.Culture, strings.IntExpectedFormat, key, raw));

        if (value < min || value > max)
            throw new CliException(
                string.Format(strings.Culture, strings.IntRangeFormat, key, value, min, max));

        return value;
    }

    /// <summary>Разбор значения --format (псевдонимы txt/base64/b64, bin/binary).</summary>
    private OutputFormat ParseFormat(string value) => value.ToLowerInvariant() switch
    {
        "text" or "txt" or "base64" or "b64" => OutputFormat.Base64,
        "bin" or "binary" => OutputFormat.Binary,
        _ => throw new CliException(
            string.Format(strings.Culture, strings.BadFormatValueFormat, value)),
    };

    /// <summary>Разбор значения --scheme (псевдонимы virtualindex/virtual-index).</summary>
    private SectorScheme ParseScheme(string value) => value.ToLowerInvariant() switch
    {
        "classic" => SectorScheme.Classic,
        "virtual" or "virtualindex" or "virtual-index" => SectorScheme.VirtualIndex,
        _ => throw new CliException(
            string.Format(strings.Culture, strings.BadSchemeValueFormat, value)),
    };

    /// <summary>Локализованное имя формата вывода для сводки.</summary>
    private string FormatName(OutputFormat format) =>
        format == OutputFormat.Binary ? strings.BinaryFormatName : strings.Base64FormatName;

    /// <summary>Имя схемы секторов для сводки (без локализации — термины).</summary>
    private static string SchemeName(SectorScheme scheme) =>
        scheme == SectorScheme.VirtualIndex ? "VirtualIndex" : "Classic";

    /// <summary>Байты → нижний регистр hex (SHA-256 принято печатать строчными).</summary>
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>Строка сводки «метка : значение»: метка дополняется до
    /// общей колонки блока (width = длина самой длинной метки + 1).</summary>
    private void Field(string label, string value, int width) =>
        System.Console.WriteLine(label.PadRight(width) + value);

    /// <summary>
    /// Справка по командам и опциям: сперва шапка с рамкой (идентично
    /// процессинг-запускам encode/decode), затем локализованный текст справки.
    /// </summary>
    private void PrintHelp()
    {
        PrintBanner();
        System.Console.WriteLine(strings.HelpText);
    }

    /// <summary>
    /// Шапка команд в стиле консольного RAR: рамка из двойных линий вокруг
    /// строки версии/копирайта (<see cref="VersionLine"/>, из сборки)
    /// (идентично encode и decode).
    /// </summary>
    private void PrintBanner()
    {
        var lines = new[] { VersionLine };
        var width = 0;
        foreach (var line in lines)
            width = Math.Max(width, line.Length);

        const int pad = 2;
        var inner = width + pad * 2;

        System.Console.WriteLine('╔' + new string('═', inner) + '╗');
        foreach (var line in lines)
            System.Console.WriteLine(
                '║' + new string(' ', pad) + line.PadRight(width) + new string(' ', pad) + '║');
        System.Console.WriteLine('╚' + new string('═', inner) + '╝');
        System.Console.WriteLine();
    }
}

/// <summary>
/// Ошибка использования CLI (неизвестная команда/опция, плохое значение):
/// печатается в stderr с подсказкой и завершает процесс кодом ExitUsage.
/// </summary>
internal sealed class CliException(string message) : Exception(message);