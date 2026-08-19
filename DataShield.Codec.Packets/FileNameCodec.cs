using System.Text;

namespace DataShield.Codec.Packets;

// ─────────────────────────────────────────────────────────────────────────────
//  Упаковка имени файла в поле H1 (14 байт)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Упаковка имени файла в 14-байтное поле H1.
///
/// Имя разбивается по <b>первой</b> точке: расширение (включая составные,
/// вида «.tar.gz») сохраняется целиком, квантуется база. При фактическом
/// усечении в конец базы добавляется «~» (без цифры). Если на базу остаётся
/// меньше 2 байт (1 символ + маркер) — имя не представимо, выдаётся отказ.
///
/// Крайние случаи: нет точки или точка первая — всё имя считается базой;
/// расширение длиннее 12 байт не оставляет базе минимального бюджета.
/// Полное имя восстанавливается по SHA-256 из заголовка (H3).
/// </summary>
public static class FileNameCodec
{
    /// <summary>Маркер усечения базы имени.</summary>
    public const char TruncationMarker = '~';

    /// <summary>
    /// Упаковать имя файла в ≤14 ASCII-символов.
    /// Бросает <see cref="InvalidOperationException"/>, если имя не представимо.
    /// </summary>
    public static string Pack(string fileName)
    {
        var name = fileName ?? "";

        var dotIndex = name.IndexOf('.');

        // Нет точки или точка первая: всё имя — база, расширения нет.
        var hasExt = dotIndex > 0;
        var nameBytes = Encoding.ASCII.GetBytes(hasExt ? name[..dotIndex] : name);
        var extBytes = Encoding.ASCII.GetBytes(hasExt ? name[dotIndex..] : "");

        var budget = PacketFormat.FileNameSize - extBytes.Length;

        if (nameBytes.Length > budget)
        {
            if (budget < 2)
                throw new InvalidOperationException(
                    $"Имя файла «{fileName}» не представимо в поле " +
                    $"{PacketFormat.FileNameSize} байт: расширение слишком длинное.");

            nameBytes = nameBytes[..(budget - 1)]
                .Append((byte)TruncationMarker)
                .ToArray();
        }

        var packed = string.Concat(
            Encoding.ASCII.GetString(nameBytes),
            Encoding.ASCII.GetString(extBytes));

        if (packed.Length > PacketFormat.FileNameSize)
            throw new InvalidOperationException(
                $"Имя файла «{fileName}» не представимо в поле " +
                $"{PacketFormat.FileNameSize} байт.");

        return packed;
    }
}
