using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DataShield.Codec;
using DataShield.Codec.Packets;
using DataShield.Codec.Reporting;
using DataShield.Codec.StreamProcessor;

namespace DataShield.Gui;

/// <summary>
/// ViewModel главного окна. Управляет состоянием UI, запускает
/// кодирование/декодирование в фоновой задаче с прогрессом и отменой.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    // Ширина колонки меток в блоке результата и MAP-отчёте.
    private const int ResultLabelWidth = 17;
    private const int MapLabelWidth = 10;

    private WorkMode _mode = WorkMode.Encode;
    private UiLanguage _language = LanguageManager.Current;
    private string _inputPath = "";
    private string _outputPath = "";
    private int _eccPercent = 10;
    private int _headerPercent = FileEncoder.DefaultHeaderPercent;
    private bool _isRunning;
    private int _progressPercent;
    private string _phaseLabel = "";
    private string _statusMessage = "";
    private bool _statusIsError;
    private bool[]? _validityMap;
    private bool[]? _collisionMap;
    private int _mapDataCount;
    private CancellationTokenSource? _cts;
    private bool _outputUserSet;
    private bool _useSlotName = true;
    private OutputFormat _selectedFormat = OutputFormat.Base64;

    // ── Свойства для привязок ───────────────────────────────────────────────

    /// <summary>Текущий режим работы (кодирование/декодирование).</summary>
    public WorkMode Mode
    {
        get => _mode;
        set { if (SetField(ref _mode, value)) OnModeChanged(); }
    }

    /// <summary>
    /// Язык интерфейса. Смена языка применяется глобально и пересоздаёт
    /// главное окно; сгенерированные ранее строки результата сбрасываются.
    /// </summary>
    public UiLanguage Language
    {
        get => _language;
        set
        {
            if (!SetField(ref _language, value))
                return;

            LanguageManager.Apply(value);

            // Строки результата и статуса были построены на прежнем языке.
            ResultLines.Clear();
            SetStatus("");
        }
    }

    /// <summary>Простаивает ли приложение (разрешает смену языка).</summary>
    public bool IsIdle => !IsRunning;

    /// <summary>Путь к входному файлу (исходник или FEC-поток).</summary>
    public string InputPath
    {
        get => _inputPath;
        set
        {
            if (SetField(ref _inputPath, value))
            {
                _outputUserSet = false;
                UpdateDefaultOutput();
            }
        }
    }

    /// <summary>Путь к выходному файлу (пустой = путь по умолчанию).</summary>
    public string OutputPath
    {
        get => _outputPath;
        set { _outputUserSet = true; SetField(ref _outputPath, value); }
    }

    /// <summary>Выбранный формат вывода FEC-потока.</summary>
    public OutputFormat SelectedFormat
    {
        get => _selectedFormat;
        set
        {
            if (SetField(ref _selectedFormat, value))
                UpdateDefaultOutput();
        }
    }

    /// <summary>Использовать имя файла из заголовка слота для выхода (декодирование).</summary>
    public bool UseSlotName
    {
        get => _useSlotName;
        set => SetField(ref _useSlotName, value);
    }

    /// <summary>Процент избыточности ECC (0 = без ECC).</summary>
    public int EccPercent
    {
        get => _eccPercent;
        set => SetField(ref _eccPercent, value);
    }

    /// <summary>Процент заголовков в потоке (минимум 3 копии).</summary>
    public int HeaderPercent
    {
        get => _headerPercent;
        set => SetField(ref _headerPercent, value);
    }

    /// <summary>Выполняется ли операция (блокирует повторный запуск).</summary>
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (SetField(ref _isRunning, value))
            {
                _runCommand.RaiseCanExecuteChanged();
                _cancelCommand.RaiseCanExecuteChanged();
                OnChanged(nameof(IsIdle));
            }
        }
    }

    /// <summary>Глобальный прогресс операции, 0..100.</summary>
    public int ProgressPercent
    {
        get => _progressPercent;
        set => SetField(ref _progressPercent, value);
    }

    /// <summary>Название текущей фазы операции.</summary>
    public string PhaseLabel
    {
        get => _phaseLabel;
        set => SetField(ref _phaseLabel, value);
    }

    /// <summary>Строка статуса (итог операции или сообщение об ошибке).</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    /// <summary>Признак ошибки в StatusMessage (влияет на цвет строки статуса).</summary>
    public bool StatusIsError
    {
        get => _statusIsError;
        set => SetField(ref _statusIsError, value);
    }

    /// <summary>Карта валидности секторов для мини-карты (true = принят).</summary>
    public bool[]? ValidityMap
    {
        get => _validityMap;
        set => SetField(ref _validityMap, value);
    }

    /// <summary>Карта коллизий версий для мини-карты (true = несколько версий payload).</summary>
    public bool[]? CollisionMap
    {
        get => _collisionMap;
        set => SetField(ref _collisionMap, value);
    }

    /// <summary>Число data-томов (N) для раскраски мини-карты.</summary>
    public int MapDataCount
    {
        get => _mapDataCount;
        set => SetField(ref _mapDataCount, value);
    }

    /// <summary>Текстовые строки результата (многострочный блок).</summary>
    public ObservableCollection<string> ResultLines { get; } = new();

    // ── Команды ────────────────────────────────────────────────────────────

    /// <summary>Команда запуска кодирования.</summary>
    public ICommand RunCommand { get; }

    /// <summary>Команда отмены выполняемой операции.</summary>
    public ICommand CancelCommand { get; }

    // Команды с доступностью, зависящей от IsRunning: при её смене
    // принудительно пересчитывается CanExecute кнопок.
    private readonly RelayCommand _runCommand;
    private readonly RelayCommand _cancelCommand;

    /// <summary>Создать ViewModel и команды запуска/отмены.</summary>
    public MainViewModel()
    {
        RunCommand = _runCommand = new RelayCommand(ExecuteRun, _ => !IsRunning);
        CancelCommand = _cancelCommand = new RelayCommand(ExecuteCancel, _ => IsRunning);
    }

    // ── Запуск кодирования/декодирования ───────────────────────────────────

    private async void ExecuteRun(object? _)
    {
        if (IsRunning) return;
        if (string.IsNullOrWhiteSpace(InputPath))
        {
            SetStatus(UiStrings.StatusNoInput, isError: true);
            return;
        }

        ResultLines.Clear();
        ValidityMap = null;
        CollisionMap = null;
        MapDataCount = 0;
        ProgressPercent = 0;
        PhaseLabel = UiStrings.PhasePreparing;

        _cts = new CancellationTokenSource();
        IsRunning = true;

        try
        {
            if (Mode == WorkMode.Encode)
                await RunEncodeAsync(_cts.Token);
            else
                await RunDecodeAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            SetStatus(UiStrings.StatusCancelled);
        }
        catch (Exception ex)
        {
            SetStatus(string.Format(UiStrings.StatusErrorFormat, ex.Message), isError: true);
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
            if (ProgressPercent < 100 && string.IsNullOrEmpty(StatusMessage))
                SetStatus(UiStrings.StatusCompleted);
        }
    }

    private void ExecuteCancel(object? _)
    {
        _cts?.Cancel();
        SetStatus(UiStrings.StatusCancelling);
    }

    // ── Кодирование ────────────────────────────────────────────────────────

    private async Task RunEncodeAsync(CancellationToken ct)
    {
        SetStatus(string.Format(UiStrings.StatusEncodingFormat, Path.GetFileName(InputPath)));

        var data = await Task.Run(() => File.ReadAllBytes(InputPath), ct);
        var encoder = new FileEncoder(EccPercent, HeaderPercent);

        var progress = new Progress<CodecProgress>(p =>
        {
            ProgressPercent = p.Percent;
            PhaseLabel = p.Phase;
        });

        var (packets, stats) = await Task.Run(() =>
            encoder.EncodeWithStats(data, Path.GetFileName(InputPath), progress, ct), ct);

        var outputPath = string.IsNullOrWhiteSpace(OutputPath)
            ? OutputFormatConfig.GetDefaultOutputPath(InputPath, SelectedFormat)
            : OutputPath;

        await Task.Run(() =>
            PacketIO.WriteFile(outputPath, packets, SelectedFormat,
                Path.GetFileName(InputPath), stats.Sha256, stats.FileSize), ct);

        var overheadPct = stats.TotalPackets * PacketFormat.PacketSize * 100.0
                          / Math.Max(1, stats.FileSize);

        ResultLines.Clear();
        ResultLines.Add(Field(UiStrings.ROutputFormat, $"{SelectedFormat}"));
        ResultLines.Add(Field(UiStrings.RSourceSize, $"{stats.FileSize:N0} {UiStrings.BytesUnit}"));
        ResultLines.Add(Field(UiStrings.RSha256, Hex(stats.Sha256)));
        ResultLines.Add(Field(UiStrings.RDataVolumes, $"{stats.DataCount:N0}"));
        ResultLines.Add(Field(UiStrings.REccVolumes, $"{stats.EccCount:N0}"));
        ResultLines.Add(Field(UiStrings.RTotalPackets, $"{stats.TotalPackets:N0}"));
        ResultLines.Add(Field(UiStrings.RHeaderCopies, $"{stats.HeaderCopies:N0}"));
        ResultLines.Add(Field(UiStrings.ROverhead, $"~ {overheadPct:F1}%"));
        ResultLines.Add(Field(UiStrings.ROutputFile, outputPath));

        // Карта валидности: при кодировании все N+M томов «целы»
        var totalCount = stats.DataCount + stats.EccCount;
        ValidityMap = Enumerable.Repeat(true, totalCount).ToArray();
        MapDataCount = stats.DataCount;

        SetStatus(string.Format(UiStrings.EncodeDoneFormat,
            $"{stats.FileSize:N0}", $"{stats.TotalPackets:N0}"));
    }

    // ── Декодирование ──────────────────────────────────────────────────────

    private async Task RunDecodeAsync(CancellationToken ct)
    {
        SetStatus(string.Format(UiStrings.StatusDecodingFormat, Path.GetFileName(InputPath)));

        var detectedFormat = OutputFormatConfig.DetectFormat(InputPath);
        var decoder = new FileDecoder();

        var scanProgress = new Progress<CodecProgress>(p =>
        {
            ProgressPercent = p.Percent;
            PhaseLabel = p.Phase;
        });

        await Task.Run(() => PacketIO.ScanFile(decoder, InputPath, scanProgress, ct), ct);

        if (decoder.FileCount == 0)
        {
            SetStatus(UiStrings.NoHeadersError, isError: true);
            return;
        }

        // Выбираем файл с наибольшим числом принятых секторов
        ReceptionSlot? best = null;
        foreach (var s in decoder.Slots)
            if (best is null || s.ReceivedSectorCount > best.ReceivedSectorCount)
                best = s;

        if (best is null)
        {
            SetStatus(UiStrings.NoDataError, isError: true);
            return;
        }

        // Карта валидности для отображения
        ValidityMap = best.BuildValidityMap();
        CollisionMap = BuildCollisionFlags(best);
        MapDataCount = best.DataVolumeCount;

        var outputPath = string.IsNullOrWhiteSpace(OutputPath)
            ? OutputFormatConfig.GetDefaultDecodeOutputPath(InputPath)
            : OutputPath;

        if (UseSlotName && !string.IsNullOrWhiteSpace(best.Header.FileName))
        {
            var slotName = best.Header.FileName;
            foreach (var c in Path.GetInvalidFileNameChars())
                slotName = slotName.Replace(c, '_');
            var outputDir = Path.GetDirectoryName(outputPath);
            outputPath = string.IsNullOrEmpty(outputDir)
                ? slotName
                : Path.Combine(outputDir, slotName);

            _outputPath = outputPath;
            OnChanged(nameof(OutputPath));
        }

        // Сборка
        PhaseLabel = UiStrings.PhasePreparing;
        ProgressPercent = 0;

        var asmProgress = new Progress<CodecProgress>(p =>
        {
            ProgressPercent = p.Percent;
            PhaseLabel = p.Phase;
        });

        var content = await Task.Run(() =>
            decoder.TryAssemble(best.Header, asmProgress, ct), ct);

        if (content is null)
        {
            var hint = best.EccCount > 0
                ? string.Format(UiStrings.FailWithEccFormat, best.EccCount)
                : UiStrings.FailNoEcc;
            SetStatus(hint, isError: true);

            ResultLines.Clear();
            ResultLines.Add(Field(UiStrings.RInputFormat, $"{detectedFormat}"));
            ResultLines.Add(Field(UiStrings.RFileName, best.Header.FileName));
            ResultLines.Add(Field(UiStrings.RSize, $"{best.Header.FileSize:N0} {UiStrings.BytesUnit}"));
            ResultLines.Add(Field(UiStrings.RSha256, Hex(best.Header.Sha256)));
            ResultLines.Add(Field(UiStrings.RHeaders,
                string.Format(UiStrings.HeadersCopiesReceivedFormat, best.HeaderReceptionCount)));
            ResultLines.Add(Field(UiStrings.RDataVolumes, $"{best.DataVolumeCount:N0}"));
            ResultLines.Add(Field(UiStrings.REccVolumes, $"{best.EccCount:N0}"));
            ResultLines.Add(SectorsLine(best));

            return;
        }

        await Task.Run(() => File.WriteAllBytes(outputPath, content), ct);

        // Запись карты валидности в файл
        var mapPath = BuildMapPath(outputPath);
        await Task.Run(() => WriteValidityMapFile(mapPath, best), ct);

        ResultLines.Clear();
        ResultLines.Add(Field(UiStrings.RInputFormat, $"{detectedFormat}"));
        ResultLines.Add(Field(UiStrings.RFileName, best.Header.FileName));
        ResultLines.Add(Field(UiStrings.RSize, $"{content.Length:N0} {UiStrings.BytesUnit}"));
        ResultLines.Add(Field(UiStrings.RSha256, Hex(best.Header.Sha256)));
        ResultLines.Add(Field(UiStrings.RHeaders,
            string.Format(UiStrings.HeadersCopiesReceivedFormat, best.HeaderReceptionCount)));
        ResultLines.Add(Field(UiStrings.RDataVolumes, $"{best.DataVolumeCount:N0}"));
        ResultLines.Add(Field(UiStrings.REccVolumes, $"{best.EccCount:N0}"));
        ResultLines.Add(SectorsLine(best));
        ResultLines.Add(Field(UiStrings.ROutputFile, outputPath));
        ResultLines.Add(Field(UiStrings.RMapFile, mapPath));

        SetStatus(string.Format(UiStrings.DecodeDoneFormat, $"{content.Length:N0}", outputPath));
    }

    // ── Вспомогательные методы ─────────────────────────────────────────────

    private void SetStatus(string message, bool isError = false)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }

    private void OnModeChanged()
    {
        UpdateDefaultOutput();
    }

    /// <summary>
    /// Принудительно пересчитать выходной путь по умолчанию.
    /// Вызывается после выбора файла через диалог, даже если InputPath не изменился.
    /// </summary>
    public void RefreshDefaultOutput()
    {
        _outputUserSet = false;
        UpdateDefaultOutput();
    }

    private void UpdateDefaultOutput()
    {
        if (_outputUserSet) return;
        if (string.IsNullOrWhiteSpace(InputPath)) return;
        _outputPath = Mode == WorkMode.Encode
            ? OutputFormatConfig.GetDefaultOutputPath(InputPath, SelectedFormat)
            : OutputFormatConfig.GetDefaultDecodeOutputPath(InputPath);
        OnChanged(nameof(OutputPath));
    }

    private static string BuildMapPath(string output)
    {
        var dir = Path.GetDirectoryName(output);
        var name = Path.GetFileName(output);
        var mapName = $"{name}.MAP.txt";
        return string.IsNullOrEmpty(dir) ? mapName : Path.Combine(dir, mapName);
    }

    /// <summary>Строка результата «Метка : значение» с выравниванием меток.</summary>
    private static string Field(string label, string value) =>
        label.PadRight(ResultLabelWidth) + " : " + value;

    /// <summary>Строка «Секторы : X / Y (покрытие Z%)».</summary>
    private static string SectorsLine(ReceptionSlot slot) =>
        Field(UiStrings.RSectors,
            $"{slot.ReceivedSectorCount:N0} / {slot.TotalVolumeCount:N0} " +
            $"({UiStrings.RCoverage} {slot.Coverage:F2}%)");

    /// <summary>
    /// Карта коллизий для мини-карты: true для номеров секторов,
    /// у которых принято более одной различающейся версии payload.
    /// </summary>
    private static bool[]? BuildCollisionFlags(ReceptionSlot slot)
    {
        var collisions = slot.BuildCollisionMap();
        if (collisions.Count == 0) return null;

        var flags = new bool[slot.TotalVolumeCount];
        foreach (var sectorNumber in collisions.Keys)
            if (sectorNumber >= 0 && sectorNumber < flags.Length)
                flags[sectorNumber] = true;

        return flags;
    }

    /// <summary>Байты в hex-строку нижнего регистра.</summary>
    private static string Hex(byte[] bytes) =>
        BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

    /// <summary>
    /// Записать MAP-отчёт: сводку приёма и построчную карту валидности
    /// секторов на языке интерфейса.
    /// </summary>
    private static void WriteValidityMapFile(string path, ReceptionSlot slot)
    {
        const int ColumnsPerRow = 64;

        var map = slot.BuildValidityMap();
        var collisions = BuildCollisionFlags(slot);
        var total = map.Length;
        var present = 0;
        for (var i = 0; i < total; i++) if (map[i]) present++;
        var missing = total - present;
        var coverage = total == 0 ? 0.0 : present * 100.0 / total;

        using var sw = new StreamWriter(path, false, System.Text.Encoding.UTF8);

        sw.WriteLine("         ═══════════════════════════════════════════════════════════════");
        sw.WriteLine($"         {UiStrings.MapReportTitle}");
        sw.WriteLine("         ═══════════════════════════════════════════════════════════════");
        sw.WriteLine(MapField(UiStrings.RFileName, slot.Header.FileName));
        sw.WriteLine(MapField(UiStrings.RSize, $"{slot.Header.FileSize:N0} {UiStrings.BytesUnit}"));
        sw.WriteLine(MapField(UiStrings.RSha256, Hex(slot.Header.Sha256)));
        sw.WriteLine(MapField(UiStrings.MapHeadersCount, $"{slot.HeaderReceptionCount}"));
        sw.WriteLine(MapField("N (data)", $"{slot.DataVolumeCount}"));
        sw.WriteLine(MapField("M (ECC)", $"{slot.EccCount}"));
        sw.WriteLine(MapField(UiStrings.MapTotal, $"{total}"));
        sw.WriteLine(MapField(UiStrings.MapPresent, $"{present}"));
        sw.WriteLine(MapField(UiStrings.MapMissing, $"{missing}"));
        sw.WriteLine(MapField(UiStrings.MapCoverage, $"{coverage:F2}%"));
        sw.WriteLine("         ───────────────────────────────────────────────────────────────");
        sw.WriteLine($"         {UiStrings.MapLegend}");
        sw.WriteLine("         ───────────────────────────────────────────────────────────────");
        sw.WriteLine();

        if (total == 0)
        {
            sw.WriteLine("  " + UiStrings.MapEmpty);
            return;
        }

        var idxWidth = Math.Max(6, total.ToString().Length);

        for (var row = 0; row * ColumnsPerRow < total; row++)
        {
            var start = row * ColumnsPerRow;
            var end = Math.Min(start + ColumnsPerRow, total);

            sw.Write(start.ToString().PadLeft(idxWidth));
            sw.Write(" │");

            for (var i = start; i < end; i++)
                sw.Write(!map[i] ? '░'
                    : collisions is not null && i < collisions.Length && collisions[i] ? '▓'
                    : '█');

            sw.Write(new string('─', ColumnsPerRow - (end - start)));

            var rowPresent = 0;
            for (var i = start; i < end; i++) if (map[i]) rowPresent++;
            sw.WriteLine($"│ {rowPresent}/{end - start}");
        }
    }

    /// <summary>Строка MAP-отчёта «Метка : значение» с выравниванием меток.</summary>
    private static string MapField(string label, string value) =>
        "         " + label.PadRight(MapLabelWidth) + " : " + value;

    // ── INotifyPropertyChanged ────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
