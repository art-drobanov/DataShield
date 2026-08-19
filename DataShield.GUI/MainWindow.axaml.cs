using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DataShield.Codec;
using DataShield.Codec.Packets;

namespace DataShield.Gui;

/// <summary>
/// Главное окно приложения: диалоги выбора файлов и привязка
/// <see cref="MainViewModel"/> к разметке.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    /// <summary>
    /// Создать окно для заданной ViewModel (единая ViewModel приложения
    /// передаётся заново при пересоздании окна после смены языка).
    /// </summary>
    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        Title = $"DataShield v{ver?.Major ?? 1}.{ver?.Minor ?? 0} Alpha-XII   Copyright (c) 2026 Artem Drobanov, Vladislav Utyumov";
        DataContext = _vm;
    }

    /// <summary>
    /// Принудительное применение языка по клику радиокнопки. Клик по уже
    /// отмеченной кнопке не меняет IsChecked, поэтому TwoWay-привязка
    /// молчит и переключение невозможно, когда визуальное состояние и
    /// ViewModel разошлись — здесь язык применяется всегда.
    /// </summary>
    private void LanguageRadio_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: UiLanguage language })
            _vm.Language = language;
    }

    /// <summary>Выбор входного файла через диалог (фильтр зависит от режима).</summary>
    private async void BrowseInput_Click(object? sender, RoutedEventArgs e)
    {
        var isEncode = _vm.Mode == WorkMode.Encode;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = isEncode
                ? UiStrings.InputDialogEncodeTitle
                : UiStrings.InputDialogDecodeTitle,
            AllowMultiple = false,
            FileTypeFilter = ParseWpfFilter(isEncode
                ? UiStrings.FilterAllFiles
                : UiStrings.FilterDecodeStream),
        });

        if (files.Count == 0) return;

        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        _vm.InputPath = path;
        _vm.SelectedFormat = OutputFormatConfig.DetectFormat(path);
        _vm.RefreshDefaultOutput();
    }

    /// <summary>Выбор выходного файла через диалог сохранения.</summary>
    private async void BrowseOutput_Click(object? sender, RoutedEventArgs e)
    {
        var isEncode = _vm.Mode == WorkMode.Encode;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = UiStrings.OutputDialogTitle,
            FileTypeChoices = ParseWpfFilter(isEncode
                ? UiStrings.FilterEncodeOutput
                : UiStrings.FilterAllFiles),
            SuggestedFileName = string.IsNullOrWhiteSpace(_vm.OutputPath)
                ? (isEncode
                    ? UiStrings.DefaultOutputName
                    : UiStrings.DefaultDecodeName)
                : Path.GetFileName(_vm.OutputPath),
        });

        if (file is null) return;

        var path = file.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            _vm.OutputPath = path;
    }

    /// <summary>
    /// Разобрать WPF-строку фильтра ("Метка|*.ext;*.ext|Метка|*.*")
    /// в список типов файлов StorageProvider. Сохраняет локализованные
    /// названия фильтров из ресурсов.
    /// </summary>
    private static IReadOnlyList<FilePickerFileType> ParseWpfFilter(string filter)
    {
        var parts = filter.Split('|');
        var result = new List<FilePickerFileType>();

        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            var patterns = parts[i + 1]
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToArray();

            if (patterns.Length > 0)
                result.Add(new FilePickerFileType(parts[i]) { Patterns = patterns });
        }

        return result;
    }
}
