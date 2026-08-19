using System.IO;
using DataShield.Codec.Ecc;
using DataShield.Codec.Packets;
using DataShield.Codec.Reporting;

namespace DataShield.Codec;

// ─────────────────────────────────────────────────────────────────────────────
//  Базовый класс кодека: общая конфигурация и контракт освобождения
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Базовый класс кодера и декодера DataShield. Содержит общую конфигурацию
/// (схема хеширования секторов, порог применимости VirtualIndex), общий
/// RS-адаптер, самотест SHA-256, чтение входного потока и контракт
/// <see cref="IDisposable"/> для реализаций, удерживающих данные.
///
/// <see cref="RsCodecAdapter"/> не содержит состояния (RS-матрица создаётся
/// на каждый вызов), поэтому один экземпляр безопасно используется
/// параллельными операциями. <see cref="Gate"/> сериализует операции
/// наследников с внутренним состоянием (конвейер декодера).
/// </summary>
public abstract class StreamCodec : IDisposable
{
    /// <summary>RS-адаптер для ECC-кодирования и восстановления.</summary>
    protected readonly RsCodecAdapter Rs = new();

    /// <summary>
    /// Блокировка операций с состоянием. Наследники без состояния
    /// (кодер) могут её не использовать.
    /// </summary>
    protected readonly object Gate = new();

    // Схема хеширования секторов данных
    private readonly SectorScheme _sectorScheme;

    // Порог применимости схемы VirtualIndex: максимум N+M томов
    private readonly int _virtualIndexSectorLimit;

    private int _disposed;

    /// <summary>Схема хеширования секторов данных.</summary>
    public SectorScheme SectorScheme => _sectorScheme;

    /// <summary>Порог применимости схемы VirtualIndex по числу томов (N+M).</summary>
    public int VirtualIndexSectorLimit => _virtualIndexSectorLimit;

    /// <summary>Экземпляр освобождён вызовом <see cref="Dispose"/>.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Создать кодек с заданной схемой секторов и порогом VirtualIndex.
    /// </summary>
    /// <param name="sectorScheme">Схема хеширования секторов данных.</param>
    /// <param name="virtualIndexSectorLimit">
    /// Порог схемы VirtualIndex: максимум N+M томов
    /// (1..<see cref="VirtualIndexHasher.DefaultSectorLimit"/>).
    /// </param>
    protected StreamCodec(
        SectorScheme sectorScheme,
        int virtualIndexSectorLimit = VirtualIndexHasher.DefaultSectorLimit)
    {
        if (!Enum.IsDefined(sectorScheme))
            throw new ArgumentOutOfRangeException(nameof(sectorScheme));
        if (virtualIndexSectorLimit is < 1 or > VirtualIndexHasher.DefaultSectorLimit)
            throw new ArgumentOutOfRangeException(
                nameof(virtualIndexSectorLimit),
                CodecErrors.VirtualIndexLimitRange(VirtualIndexHasher.DefaultSectorLimit));

        _sectorScheme = sectorScheme;
        _virtualIndexSectorLimit = virtualIndexSectorLimit;
    }

    /// <summary>
    /// Проверить самотест SHA-256: реализация хеша должна быть верифицирована
    /// перед каждой операцией кодека.
    /// </summary>
    protected static void VerifySha256()
    {
        if (!Sha256Compact.Test())
            throw new InvalidOperationException(CodecErrors.Sha256SelfTestFailed);
    }

    /// <summary>
    /// Прочитать поток целиком в массив с проверкой предельного размера
    /// <see cref="PacketFormat.MaxFileSizeField"/> (3-байтное поле заголовка).
    /// Работает и с непозиционируемыми потоками.
    /// </summary>
    protected static byte[] ReadStreamContent(Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
            throw new ArgumentException(CodecErrors.StreamNotReadable, nameof(content));

        if (content.CanSeek)
        {
            var remaining = content.Length - content.Position;
            if (remaining > PacketFormat.MaxFileSizeField)
                throw new InvalidOperationException(
                    CodecErrors.StreamSizeExceedsMax(remaining, PacketFormat.MaxFileSizeField));

            var buffer = new byte[(int)remaining];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = content.Read(buffer, read, buffer.Length - read);
                if (n <= 0) break;
                read += n;
            }

            if (read < buffer.Length) Array.Resize(ref buffer, read);
            return buffer;
        }

        // Непозиционируемый поток: копия через промежуточный буфер
        using var ms = new MemoryStream();
        content.CopyTo(ms);
        var result = ms.ToArray();
        if (result.Length > PacketFormat.MaxFileSizeField)
            throw new InvalidOperationException(
                CodecErrors.StreamSizeExceedsMax(result.Length, PacketFormat.MaxFileSizeField));
        return result;
    }

    /// <summary>Выбросить <see cref="ObjectDisposedException"/>, если экземпляр освобождён.</summary>
    protected void ThrowIfDisposed()
    {
        if (IsDisposed)
            throw new ObjectDisposedException(GetType().Name);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Освобождение удерживаемых данных. Наследники, кеширующие данные
    /// пользователя, очищают их здесь под <see cref="Gate"/>.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
    }
}
