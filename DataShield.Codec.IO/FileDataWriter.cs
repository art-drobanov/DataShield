using System.IO;
using DataShield.Interfaces;

namespace DataShield.Codec.IO;

// ─────────────────────────────────────────────────────────────────────────────
//  Приёмник в файл
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Приёмник данных в файл: открывает поток записи и работает как
/// <see cref="StreamDataWriter"/>. Файл закрывается при освобождении приёмника.
/// </summary>
public sealed class FileDataWriter : WriterBase, IDisposable
{
    private readonly FileStream _file;
    private readonly object _sync = new();

    /// <summary>Открыть файл как приёмник данных.</summary>
    /// <param name="path">Путь к файлу.</param>
    /// <param name="append">true — дописывать в конец, false — перезаписать.</param>
    public FileDataWriter(string path, bool append = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Путь к файлу не задан.", nameof(path));

        _file = new FileStream(
            path,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.Read);
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> data)
    {
        lock (_sync)
        {
            _file.Write(data);
        }
    }

    /// <summary>Закрыть файл.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            _file.Flush();
            _file.Dispose();
        }
    }
}
