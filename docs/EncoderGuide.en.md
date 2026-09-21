# DataShield Alpha-XVI — Encoder Guide

Reference for the sending side: configuration, volume arithmetic, packet construction, stream layout, output formats, statistics and reports.

| | |
|---|---|
| Project | DataShield Alpha-XVI |
| Document | `docs/EncoderGuide.en.md` (English); Russian mirror: `docs/EncoderGuide.ru.md` |
| Audience | Application developers embedding the encoder; operators choosing redundancy and format settings |
| Scope | `StreamEncoder` construction and parameters, encoding operations (core and facade), volume and header arithmetic, packet building, `ArrangePackets` layout, output formats and file naming (`OutputFormatConfig`, `PacketIO`), `EncodeStats`, the encoding report, progress phases, memory hygiene |
| Out of scope | Format fundamentals ([AlgorithmGuide](AlgorithmGuide.en.md)), decoding ([DecoderGuide](DecoderGuide.en.md)), assembly ([AssemblyGuide](AssemblyGuide.en.md)), CLI and GUI ([UserGuide](UserGuide.en.md)) |

Reference implementation: `DataShield.Codec` (`StreamEncoder.cs`, `EncodeStats.cs`, `PacketIO.cs`, `CodecReport.cs`, `DataShieldCodec.cs`), `DataShield.Codec.Packets` (`OutputFormat.cs`, `OutputFormatConfig.cs`).

## 1. Conventions used in this document

- `N` — data volumes, `M` — ECC volumes, `T = N + M` — total volumes; each volume is carried by exactly one sector packet ([AlgorithmGuide §8](AlgorithmGuide.en.md)).
- `H` — the number of header copies in the stream; a **signature packet** follows every header copy when signatures are enabled.
- Wire hashes are the values stored in packets (keyed by the hashing secret when one is set); **real** hashes are plain re-computations for reports.
- Sizes: `B` = bytes, `MiB` = 1,048,576 B; one payload is 64 B; one packet is 75 B = 100 Base64 characters.

## 2. What one encoding produces

An encoding run turns `content + fileName` into an ordered list of 75-byte packets:

```text
[header (+signature)]  sector₀ … sectorᵢ [header (+signature)] sectorᵢ₊₁ … … [header (+signature)]
```

- Every data volume and every ECC volume becomes one sector packet (Classic or VirtualIndex scheme).
- The stream **opens** with a header packet and **closes** with a header packet; further header copies are spread evenly through the body (§7).
- With `signaturePackets512` enabled, each header copy is immediately followed by one signature packet (`S1 ‖ Trunc11(SHA-512(S1))`).

The decoder side does not depend on this ordering — it reorders anything — but the layout maximizes survival for the typical damage pattern (truncation from either end, middle dropouts).

## 3. Constructing an encoder

```csharp
public StreamEncoder(
    int eccPercent = 10,
    int headerPercent = 3,
    SectorScheme sectorScheme = SectorScheme.Classic,
    int virtualIndexSectorLimit = VirtualIndexHasher.DefaultSectorLimit,
    bool signaturePackets512 = false,
    string? hashSecret = null)
```

| Parameter | Default | Meaning |
|---|---|---|
| `eccPercent` | **10** | 0 — no RS redundancy; ≥ 1 — `M = max(1, round(N·pct/100))`, at least one ECC volume. Negative values are rejected. |
| `headerPercent` | **3** | Header copies as a percentage of `T`; always at least 3 (begin/middle/end). |
| `sectorScheme` | **Classic** | `VirtualIndex` drops the stored sector index and spends the bytes on an 11-byte hash tag ([AlgorithmGuide §5.3](AlgorithmGuide.en.md)). |
| `virtualIndexSectorLimit` | **512** | Files with `T` above the limit are **refused** under VirtualIndex — there is no silent downgrade to Classic (the operator's security expectations would silently change). |
| `signaturePackets512` | **off** | Emit a SHA-512 signature packet after every header copy; enables the decoder's Exact(SHA-256) & Any(SHA-512) verification. |
| `hashSecret` | **none** | Keys all wire hashes for this instance's operations; the decoder must be configured with the same secret. |

Two hard format limits surface as `InvalidOperationException` at encode time: file size above 16,777,215 B (the 3-byte `H2` field), and `T` above 65,535 (`MaxDataVolumes`). Since `T ≥ N = ⌈size/64⌉`, the practical payload ceiling is ≈ 4.0 MiB with zero ECC, less with redundancy.

## 4. Encoding operations

Core class (`StreamEncoder`, stateless between calls — parallel encodes on one instance are safe):

| Operation | Returns |
|---|---|
| `Encode(content, fileName)` (+progress/ct) | `List<byte[]>` — raw 75-byte packets. |
| `EncodeToText(content, fileName)` | Base64 text, one 100-char line per packet (no decorative framing — that is `PacketIO`'s file-level concern, §8). |
| `EncodeWithStats(content, fileName)` | `(packets, EncodeStats)` — packets plus the operation summary (§9). |
| Stream inputs | Every `Stream` overload reads the content fully first (`ReadStreamContent`, size-checked, works on non-seekable streams) — SHA-256 and RS need the complete buffer anyway. |

Facade (`DataShieldCodec`) adds the collection conveniences:

| Facade operation | Behavior |
|---|---|
| `EncodeToPackets(content, fileName)` | packets, from bytes / enumeration / stream. |
| `EncodeToText(content, fileName)` | Base64 text (via `PacketIO`, **with** decorative framing when written by `Encode`). |
| `EncodeToBytes(content, fileName)` | binary concatenation (75 B × packets). |
| `EncodeInto(content, fileName, output)` | appends packets into a caller-owned collection; returns the count. |
| `Encode(content-stream, output-stream, format, fileName)` | writes the FEC stream in the requested format and returns `EncodeStats`. |

## 5. Volume and header arithmetic

All formulas are public static members of `StreamEncoder` — usable to preview sizing without encoding:

| Quantity | Formula | Notes |
|---|---|---|
| `N` | `max(1, ⌈fileSize / 64⌉)` | an empty file still occupies one volume (zeros) |
| `M` | `max(1, round(N · eccPercent / 100))` when `eccPercent ≥ 1`, else 0 | rounding is half-up: `(N·pct + 50) / 100` |
| `T` | `N + M ≤ 65,535` | hard format limit |
| `H` | `max(3, round(T · headerPercent / 100))` | half-up, minimum 3 |
| middle headers | `H − 2` | first and last are fixed |
| insertion interval | `max(1, T / (middle + 1))` sectors | even spread |

Worked example — a 128 B file, defaults (`eccPercent 10`, `headerPercent 3`, signatures on):

| Step | Value |
|---|---|
| `N` | `⌈128/64⌉ = 2` |
| `M` | `max(1, round(0.2)) = 1` |
| `T` | `3` |
| `H` | `max(3, round(0.09)) = 3` → no middle headers (begin + end only) |
| Packets | `2 data + 1 ECC + 3 headers + 3 signatures = 9` |

A 1 MiB file: `N = 16,384`, `M = 1,638`, `T = 18,022`, `H = 541` → `19,104` sector packets plus `541` headers (+ `541` signatures when enabled) — an expansion of ≈ 1.82× in binary bytes.

## 6. Packet construction

Three builders (all `internal`, exercised through `Encode`):

| Builder | Output |
|---|---|
| `BuildHeaderPacket` | `H1..H4 (51 B) ‖ H5 = Trunc24(SHA-256(H1..H4))` — the packet that identifies the file. |
| `BuildDataSector` | `D1 = seqNum LE(2) ‖ D2 = payload(64) ‖ D3 = Trunc9(SHA-256(H5 ‖ D1 ‖ D2))` — the Classic scheme. |
| `BuildVirtualIndexSector` | `payload(64) ‖ Trunc11(SHA-256(H5 ‖ payload ‖ idx LE))` — the VirtualIndex scheme; the index exists only inside the hash input. |

The header is built once, its `H5` computed once, and every sector is hashed under that `H5` — which is why fragments of different files cannot intermix silently downstream.

The last data volume is zero-padded to 64 B; the true size travels in `H2` and the final SHA-256 gate ignores the padding.

## 7. Stream layout: `ArrangePackets`

The placement algorithm:

1. Emit the header packet (+ signature if enabled) — the stream's first packet.
2. If `H ≤ 2`: append all sector packets.
3. Otherwise: append sector packets, and after every `interval`-th sector insert a header copy (+ signature) while middle-header budget lasts. The interval is `max(1, T / (middle + 1))`, so copies land at roughly even positions; sector order itself stays sequential (friendly to the receiver's VirtualIndex cursor).
4. Emit the header packet (+ signature) — the stream's last packet.

Consequences worth internalizing:

- Any single truncation of the stream keeps at least one header reachable from the surviving side; a header always sits at an extreme of every large surviving fragment's neighborhood.
- Signature copies always travel adjacent to header copies, so the pair survives or dies together.
- Sector packets themselves are order-independent for the decoder; the sequential order is an optimization, not a requirement.

## 8. Output formats and file naming

`OutputFormat` selects the container:

| Format | Extension | Content |
|---|---|---|
| `Base64` | `.DataShield.txt` | UTF-8 text; one packet per line; optional decorative first/last lines (see below). |
| `Binary` | `OutputFormat.Binary` → `.DataShield.bin` | raw 75-byte packets, concatenated, no separators. |

`PacketIO.WriteBase64Text` adds **decorative framing** when the source file name is supplied — exactly 100 characters each, so they never disturb scanning:

```text
>[REPORT.BIN    ][0000640][SHA-256:9f86d0…8]
i1QyN3Lz…  (100 Base64 chars, one packet)
…
<[REPORT.BIN    ][0000640][SHA-256:9f86d0…8]
```

The bracket fields reuse the header's packed name (14 chars, `FileNameCodec.Pack`), the size as 7 zero-padded decimal digits, and the hex SHA-256. The lines are human convenience only — the decoder ignores everything outside the Base64 alphabet, including these lines.

Naming helpers (`OutputFormatConfig`):

| Helper | Rule |
|---|---|
| `GetDefaultOutputPath(input, format)` | `input + ".DataShield.txt"/".DataShield.bin"`. |
| `DetectFormat(path)` | `.txt` → Base64, `.bin` → Binary, anything else → Base64. |
| `StripFecSuffix(path)` | Drops a trailing `.DataShield.*` suffix (`data.DataShield.bin` → `data`). |
| `GetDefaultDecodeOutputPath(path)` | Strips the FEC suffix; if none was present, inserts `.out` before the extension (`stream.txt` → `stream.out.txt`) so decode output never overwrites the input. |

## 9. `EncodeStats`: the operation summary

`EncodeWithStats` returns the metadata of what was just produced:

| Field | Meaning |
|---|---|
| `FileSize` | Original size in bytes. |
| `Sha256` / `Sha512` | **Wire** hashes — the values stored in the header/signature packets; keyed when a secret is configured. |
| `RealSha256` / `RealSha512` | **Real** (secret-free) hashes recomputed in RAM — fit for reports and logs; what a third party would compute. |
| `DataCount` / `EccCount` | `N` and `M`. |
| `TotalPackets` | Everything in the stream, headers and signatures included. |
| `HeaderCopies` / `SignatureCopies` | Copy counts, derived from the stream shape: extras = `TotalPackets − N − M`; with signatures enabled the extras split evenly (each header is followed by exactly one signature), otherwise all extras are headers. |
| `SignaturePackets512` | Convenience flag: signatures are present. |

The wire/real distinction exists so that secret-mode operations can still produce meaningful reports without ever printing a keyed value (§10).

## 10. The encoding report

`CodecReport.WriteEncoding(path, stats, fileName)` writes `<output>.report.txt` (localized via `CodecStrings.Language`; the same routine serves GUI and console):

```text
         ════════════════════════════════════════════════════════════════
         DataShield : Encoding report
         ════════════════════════════════════════════════════════════════
         File name  : report.bin
         Size       : 640 bytes
         SHA-256    : 9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08
         SHA-512    : 2b0c4e…(64 hex)
                    : …(64 hex, aligned continuation)
         N (data)   : 10
         M (ECC)    : 1
         Headers    : 3
         Signatures : 3
         Packets    : 17
```

Rules: SHA-512 prints as two aligned lines only when signatures are enabled; a hash line that had to fall back to the wire value (no real hash available) is marked with a trailing `*` — under a secret that value is keyed and must not be treated as a plain digest.

## 11. Progress phases

Encoding reports on the global 0..100 scale in three phases (names from `CodecStrings`, throttled to whole percentages by `ProgressThrottle`):

| Range | Phase |
|---|---|
| 0..10 | "Preparing data" — hashing, slicing into payloads |
| 10..75 | "ECC encoding" — the RS pass (internally scaled by `ScaledProgress`) |
| 75..100 | "Building packets" — per-sector packet construction |
| 100 | "Done" |

Cancellation is checked at phase ticks; the token reaches the RS kernel as well.

## 12. Memory hygiene and concurrency

- After a successful `Encode`, all intermediate buffers are wiped: data payloads, ECC payloads and the serialized header (`Array.Clear`). Only the returned packets (which must survive — they are the product) and the caller's content remain.
- `StreamEncoder` keeps no per-operation state (configuration only), so parallel `Encode` calls on one instance are safe; the `DataShieldCodec` facade additionally serializes everything behind its own gate.
- The hashing self-test (`VerifySha256`) runs before every operation.

## 13. Document map

| Document | Content |
|---|---|
| [AlgorithmGuide](AlgorithmGuide.en.md) | Wire format and algorithms; the format-level view of everything built here. |
| [EncoderGuide](EncoderGuide.en.md) (this file) | Encoder usage, layout, formats, statistics, reports. |
| [DecoderGuide](DecoderGuide.en.md) | Feeding the produced streams back in. |
| [AssemblyGuide](AssemblyGuide.en.md) | How the receiver arbitrates damage in what the encoder produced. |
| [CollisionBench](CollisionBench.en.md) | Why Classic and VirtualIndex have the strengths they have. |
| [DeveloperGuide](DeveloperGuide.en.md) | Project layout, build, tests. |
| [UserGuide](UserGuide.en.md) | End-to-end usage from the console application and the GUI. |

Russian mirrors of every document carry the `ru` suffix (`EncoderGuide.ru.md`, …).
