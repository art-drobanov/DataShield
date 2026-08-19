using System.IO;
using DataShield.Interfaces;

namespace DataShield.Codec.IO;

// ─────────────────────────────────────────────────────────────────────────────
//  Источник данных на основе файла
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Источник данных на основе файла: открывает поток чтения и работает как
/// <see cref="StreamSource"/>. Файл закрывается при освобождении источника.
/// </summary>
public sealed class FileSource : BufferedSourceBase, IDisposable
{
    private readonly FileStream _file;

    /// <summary>Открыть файл как источник данных.</summary>
    /// <param name="path">Путь к файлу.</param>
    /// <param name="bufferSize">Размер буфера выдачи, байт.</param>
    public FileSource(string path, int bufferSize = 4096)
        : base(bufferSize)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Путь к файлу не задан.", nameof(path));

        _file = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    /// <inheritdoc/>
    protected override int ReadByteCore() => _file.ReadByte();

    /// <summary>Закрыть файл.</summary>
    public void Dispose() => _file.Dispose();
}
