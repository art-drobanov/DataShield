using DataShield.Codec.Reporting;

namespace DataShield.Codec.Packets;

// ─────────────────────────────────────────────────────────────────────────────
//  Сигнатурный пакет SHA-512 (см. задание «Сигнатурные пакеты SHA-512»)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Примитивы сигнатурного пакета SHA-512.
///
/// <b>Пакет</b> (75 байт): S1(64) ‖ S2(11), где S1 = SHA-512 содержимого
/// файла, S2 = Trunc11(SHA-512(S1)). Пакет не привязан явно к файлу:
/// S2 проверяется автономно (ложное срабатывание 2⁻⁸⁸), а подтверждение
/// файла выполняется критерием «любой из» — собранный файл проходит
/// расширенную проверку, если его SHA-512 совпадает хотя бы с одной
/// принятой сигнатурой S1.
///
/// Хеши вычисляются через <c>Sha512Secure</c>: в режиме «с секретом»
/// значения «на проводе» защищены (секрет — байтовый префикс потока).
/// </summary>
public static class SignaturePacket
{
    /// <summary>
    /// Собрать сигнатурный пакет: S1 = SHA-512(content), S2 = Trunc11(SHA-512(S1)).
    /// </summary>
    /// <param name="content">Содержимое файла.</param>
    public static byte[] Build(ReadOnlySpan<byte> content)
    {
        var packet = new byte[PacketFormat.PacketSize];
        BuildInto(content, packet);
        return packet;
    }

    /// <inheritdoc cref="Build(ReadOnlySpan{byte})"/>
    /// <param name="content">Содержимое файла.</param>
    /// <param name="packet">Буфер пакета (75 байт).</param>
    public static void BuildInto(ReadOnlySpan<byte> content, Span<byte> packet)
    {
        if (packet.Length != PacketFormat.PacketSize)
            throw new ArgumentException(
                CodecErrors.ExpectedBufferBytes(packet.Length, PacketFormat.PacketSize),
                nameof(packet));

        Sha512Secure.HashData(
            content,
            packet[..PacketFormat.SignatureSize]);
        WriteTag(packet);
    }

    /// <summary>
    /// Собрать сигнатурный пакет из готовой сигнатуры S1 (без пересчёта
    /// хеша содержимого — используется, когда SHA-512 уже вычислен).
    /// </summary>
    /// <param name="signature">Сигнатура SHA-512 (64 байта).</param>
    public static byte[] BuildFromSignature(ReadOnlySpan<byte> signature)
    {
        var packet = new byte[PacketFormat.PacketSize];
        signature.CopyTo(packet);
        WriteTag(packet);
        return packet;
    }

    /// <summary>
    /// Полный пакет (75 байт) является корректным сигнатурным:
    /// S2 совпадает с Trunc11(SHA-512(S1)).
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> packet)
    {
        if (packet.Length != PacketFormat.PacketSize)
            return false;

        Span<byte> hash = stackalloc byte[PacketFormat.SignatureSize];
        Sha512Secure.HashData(
            packet[..PacketFormat.SignatureSize],
            hash);

        return hash[..PacketFormat.SignatureTagSize].SequenceEqual(
            packet[PacketFormat.SignatureTagOffset..]);
    }

    /// <summary>Копия сигнатуры S1 из пакета (64 байта).</summary>
    public static byte[] ReadSignature(ReadOnlySpan<byte> packet)
    {
        if (packet.Length != PacketFormat.PacketSize)
            throw new ArgumentException(
                CodecErrors.ExpectedBufferBytes(packet.Length, PacketFormat.PacketSize),
                nameof(packet));

        return packet[..PacketFormat.SignatureSize].ToArray();
    }

    // S2 = Trunc11(SHA-512(S1)) в хвост пакета.
    private static void WriteTag(Span<byte> packet)
    {
        Span<byte> hash = stackalloc byte[PacketFormat.SignatureSize];
        Sha512Secure.HashData(
            packet[..PacketFormat.SignatureSize],
            hash);
        hash[..PacketFormat.SignatureTagSize].CopyTo(
            packet[PacketFormat.SignatureTagOffset..]);
    }
}
