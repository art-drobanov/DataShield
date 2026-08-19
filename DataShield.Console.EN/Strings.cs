using System.Globalization;
using DataShield.Codec.Reporting;
using DataShield.Console;

namespace DataShield.Console.En;

// ─────────────────────────────────────────────────────────────────────────────
//  Английская локализация консольного приложения
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Английские строки консольного приложения и настройки локали: язык фаз
/// прогресса кодека и культура форматирования чисел (InvariantCulture).
/// </summary>
internal sealed class EnStrings : IConsoleStrings
{
    /// <summary>Единственный экземпляр английской локализации.</summary>
    public static EnStrings Instance { get; } = new();

    private EnStrings()
    {
    }

    /// <inheritdoc/>
    public CodecLanguage CodecLanguage => CodecLanguage.English;

    /// <inheritdoc/>
    public CultureInfo Culture => CultureInfo.InvariantCulture;

    /// <inheritdoc/>
    public string HelpText => """
        Usage:
          DataShield.Console.EN encode <input> [options]
          DataShield.Console.EN decode <input> [options]

        Commands:
          encode    Encode a file into a FEC stream (75-byte packets, one line per packet)
          decode    Scan a FEC stream and restore file(s)

        encode options:
          -o, --output <path>              Output file (default: input + .DataShield.txt|.bin)
          -f, --format <text|bin>          Format: Base64 text (default) or binary
          -e, --ecc <0-1000>               ECC redundancy, % (default 10)
          -h, --header <0-50>              Header copies, % (default 3)
          -s, --scheme <classic|virtual>   Sector scheme (default classic)
          -q, --quiet                      Disable progress display

        decode options:
          -o, --output <path>              Output file or directory
                                           (default: input name without the .DataShield.* suffix)
          -a, --all                        Restore all files in the stream, not only the best one
          -n, --use-name                   Name output files by stream headers
          -s, --scheme <classic|virtual>   Sector reception scheme (default classic)
          -q, --quiet                      Disable progress display

        The decode input format is detected by extension: .txt — Base64, .bin — binary.
        Packet order is arbitrary; duplicates, noise and losses are tolerated.

        Examples:
          DataShield.Console.EN encode doc.zip -e 20 -f bin
          DataShield.Console.EN decode doc.zip.DataShield.bin -o out -n
        """;

    /// <inheritdoc/>
    public string UsageHint => "Run with --help for usage.";

    /// <inheritdoc/>
    public string UnknownCommandFormat => "Unknown command: {0}";

    /// <inheritdoc/>
    public string UnknownOptionFormat => "Unknown option: {0}";

    /// <inheritdoc/>
    public string OptionValueMissingFormat => "Option {0} requires a value.";

    /// <inheritdoc/>
    public string IntExpectedFormat => "--{0}: expected an integer, got '{1}'.";

    /// <inheritdoc/>
    public string IntRangeFormat => "--{0}: value {1} is out of range {2}..{3}.";

    /// <inheritdoc/>
    public string BadFormatValueFormat => "--format: unknown value '{0}' (available: text, bin).";

    /// <inheritdoc/>
    public string BadSchemeValueFormat => "--scheme: unknown value '{0}' (available: classic, virtual).";

    /// <inheritdoc/>
    public string CanceledByUser => "Canceled by user.";

    /// <inheritdoc/>
    public string ErrorFormat => "Error: {0}";

    /// <inheritdoc/>
    public string EncodeSingleInputError => "encode: specify exactly one input file.";

    /// <inheritdoc/>
    public string DecodeSingleInputError => "decode: specify exactly one input FEC file.";

    /// <inheritdoc/>
    public string EncodeFileNotFoundFormat => "encode: file not found: {0}";

    /// <inheritdoc/>
    public string DecodeFileNotFoundFormat => "decode: file not found: {0}";

    /// <inheritdoc/>
    public string NoHeadersError => "No DataShield headers found in the stream.";

    /// <inheritdoc/>
    public string AssemblyFailedError => "Assembly failed: losses exceed ECC capability.";

    /// <inheritdoc/>
    public string NoFilesRestored => "No files restored.";

    /// <inheritdoc/>
    public string DoneSummaryFormat => "Done: restored {0} of {1} file(s).";

    /// <inheritdoc/>
    public string FileLabel => "File:";

    /// <inheritdoc/>
    public string SizeLabel => "Size:";

    /// <inheritdoc/>
    public string Sha256Label => "SHA-256:";

    /// <inheritdoc/>
    public string VolumesLabel => "Volumes:";

    /// <inheritdoc/>
    public string PacketsLabel => "Packets:";

    /// <inheritdoc/>
    public string SchemeLabel => "Scheme:";

    /// <inheritdoc/>
    public string OutputLabel => "Output:";

    /// <inheritdoc/>
    public string InputLabel => "Input:";

    /// <inheritdoc/>
    public string FilesLabel => "Files:";

    /// <inheritdoc/>
    public string SizeFormat => "{0:N0} B";

    /// <inheritdoc/>
    public string VolumesFormat => "{0:F1}% redundancy ({1} data + {2} ECC)";

    /// <inheritdoc/>
    public string PacketsFormat => "{0:F1}% headers (total packets {1}, headers: {2})";

    /// <inheritdoc/>
    public string FilesInStreamFormat => "{0} in stream, processing: {1}";

    /// <inheritdoc/>
    public string SlotHeaderFormat => "[{0}] {1} — {2:N0} B, coverage {3:F1}% ({4}/{5} volumes, header copies: {6})";

    /// <inheritdoc/>
    public string CollisionsFormat => "Version collisions: {0} sectors";

    /// <inheritdoc/>
    public string RestoredLineFormat => "Restored: {0} ({1:N0} B)";

    /// <inheritdoc/>
    public string MapLineFormat => "Map: {0}";

    /// <inheritdoc/>
    public string Base64FormatName => "Base64 text";

    /// <inheritdoc/>
    public string BinaryFormatName => "binary";
}
