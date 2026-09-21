# DataShield Alpha-XVI — Decoder Guide

Reference for the receiving side: feeding damaged text or binary streams, scanning, multi-file reception, assembly entry points, reports and state management.

| | |
|---|---|
| Project | DataShield Alpha-XVI |
| Document | `docs/DecoderGuide.en.md` (English); Russian mirror: `docs/DecoderGuide.ru.md` |
| Audience | Application developers embedding the decoder (console, GUI, service); engineers integrating DataShield streams into pipelines |
| Scope | `StreamDecoder` and its pipeline: input formats and `Scan` overloads, the Base64 filter, the sliding-window scanner, late-header rebinding, the reception processor boundary, assembly entry points, the facade (`DataShieldCodec`) decode operations, progress and cancellation, state and memory hygiene, report files |
| Out of scope | Format and verification fundamentals ([AlgorithmGuide](AlgorithmGuide.en.md)), deep assembly mechanics ([AssemblyGuide](AssemblyGuide.en.md)), encoding ([EncoderGuide](EncoderGuide.en.md)), CLI and GUI ([UserGuide](UserGuide.en.md)) |

Reference implementation: `DataShield.Codec` (`StreamDecoder.cs`, `StreamCodec.cs`, `DataShieldCodec.cs`, `PacketIO.cs`, `CodecReport.cs`, `DecodeResult.cs`), `DataShield.Codec.StreamFilter`, `DataShield.Codec.StreamScanner`, `DataShield.Codec.IO` and `DataShield.Interfaces`.

## 1. Conventions used in this document

- A **packet** is the 75-byte atomic unit — 100 Base64 characters on the wire ([AlgorithmGuide §4](AlgorithmGuide.en.md)).
- `H5` — the 24-byte header hash; it seeds all sector checks of that file.
- A **slot** is the per-header reception state (versions, collisions, counters); **assembly** turns a slot into file bytes.
- "txt" input means arbitrary text containing Base64 material; "binary" input means raw concatenated 75-byte packets.
- Progress values live on a global 0..100 scale; phase names come from `CodecStrings` (English by default, Russian via `CodecStrings.Language = CodecLanguage.Russian`).
- `N`, `M`, `T = N + M` — data volume count, ECC volume count, total volumes.

## 2. The decoding pipeline at a glance

Decoding is a chain of pull-driven, event-delivered stages:

```text
source (bytes)  →  [txt only] Base64 filter  →  sliding-window scanner  →  reception processor  →  slots
```

| Stage | Class | What it does |
|---|---|---|
| Source | `ByteArraySource`, `StreamSource`, `FileSource` (`DataShield.Codec.IO`) | Pumps bytes in a background task; delivers buffer-sized chunks through `DataReady`. |
| Filter | `ByteRangeFilter` (`DataShield.Codec.StreamFilter`) | txt mode only: drops every byte outside the Base64 alphabet. |
| Scanner | `SlidingWindowScanner` (`DataShield.Codec.StreamScanner`) | Slides a window (100 Base64 chars or 75 raw bytes) byte-by-byte; emits verified packets. |
| Processor | `StreamProcessor` (`DataShield.Codec.StreamProcessor`) | Classifies packets (header / signature / sector), fills slots. |
| Assembly | `ReceptionSlot.TryAssemble` | Reconstructs files — the subject of the [AssemblyGuide](AssemblyGuide.en.md). |

Everything the decoder keeps between `Scan` calls (scanner tails, slots) is user data: it is wiped by `Clear()` and `Dispose()`.

## 3. Quick start

Cumulative reception with the core class:

```csharp
using var decoder = new StreamDecoder();

decoder.Scan(File.ReadLines("mangled_chat_export.txt"));   // accumulate…
decoder.Scan(moreLines);                                    // …across calls

foreach (var slot in decoder.Slots)                         // one slot per distinct header
{
    try
    {
        var content = decoder.TryAssemble(slot.Header);
        if (content is not null)
            File.WriteAllBytes(slot.Header.FileName, content);
    }
    catch (SignatureVerificationException)
    {
        // Exact(SHA-256) passed but no accepted signature confirms it — see §10
    }
}
```

One-shot decoding through the facade:

```csharp
using var codec = new DataShieldCodec(signaturePackets512: true);

byte[]? single = codec.Decode(
    File.OpenRead("stream.DataShield.bin"), OutputFormat.Binary);

List<DecodeResult> all = codec.DecodeAll(
    File.OpenRead("stream.DataShield.txt"), OutputFormat.Base64);

PacketIO.ScanFile(decoder, "stream.DataShield.txt");  // format auto-detected by extension
```

## 4. Constructing a decoder

```csharp
public StreamDecoder(
    SectorVersionSearchOptions? searchOptions = null,
    SectorScheme sectorScheme = SectorScheme.Classic,
    int virtualIndexSectorLimit = VirtualIndexHasher.DefaultSectorLimit,
    SignatureMode signatureMode = SignatureMode.Auto,
    string? hashSecret = null)
```

| Parameter | Default | Meaning |
|---|---|---|
| `searchOptions` | `SectorVersionSearchOptions.Default` | Assembly search limits (version search, subset search) — catalogued in the [AssemblyGuide](AssemblyGuide.en.md). |
| `sectorScheme` | `Classic` | `Classic` — receive only classic sector packets. `VirtualIndex` — hybrid reception: classic packets are checked first, then the virtual index is swept; files of both schemes are read simultaneously. |
| `virtualIndexSectorLimit` | `512` | Slots with `T` above the limit are verified classically only (the sweep would be too broad and too weak — see [CollisionBench](CollisionBench.en.md)). Range 1..512. |
| `signatureMode` | `Auto` | `Base` — detect signature packets, ignore them; `Auto` — extended check active once ≥ 1 signature packet arrives; `Force` — always active (missing signatures then fail assembly). |
| `hashSecret` | `null` | Keyed hashing scope for the whole operation; must equal the encoder's secret, otherwise headers simply never verify. Empty string = no secret. |

The base class `StreamCodec` validates the scheme enum and the limit range, and shares a stateless `RsCodecAdapter` (the RS matrix is rebuilt per call, so the instance is safe under parallel use — the decoder's own operations are serialized by an internal lock instead).

Before every operation the decoder runs a hashing self-test (`Sha256Secure.Test()`, `Sha512Secure.Test()`) and refuses to operate if either fails — a corrupted crypto environment must not silently produce wrong verdicts.

## 5. Input formats and the `Scan` overloads

`Scan` is **cumulative**: each call adds to the slots and scanner tails; nothing is reset. The decoder distinguishes:

| Overload | Input | Minimum length | Pipeline |
|---|---|---|---|
| `Scan(IEnumerable<string> lines)` | arbitrary text lines | 100 Base64 chars total | UTF-8 concatenation → filter → txt scanner |
| `Scan(byte[] data)` | raw packet bytes (noise/resync tolerated) | 75 bytes | binary scanner, no filter |
| `Scan(Stream input, OutputFormat format)` | stream of either format | — | format decides txt vs binary; seekable streams report length for progress |
| `PacketIO.ScanFile(decoder, path)` | file | — | format auto-detected: `.txt` → Base64, `.bin` → Binary |

Notes:

- Inputs shorter than one packet are silently ignored (nothing to scan).
- The stream is **not** closed by `Scan`; the caller owns it.
- Calls are serialized: a `Scan` in progress blocks other operations of the same instance.

## 6. The plumbing: sources, processors, delivery contract

All stages implement `IDataSource`/`IDataProcessor` from `DataShield.Interfaces`:

- A **source** pumps bytes in a background task into a fixed buffer; when the buffer fills (or at EOF/stop) it raises `DataReady`, handing the consumer a one-shot take delegate; reading resumes after the handler returns. Read errors are captured and surface through `Completion` (and the `Error` property) — the pipeline fails loudly, never silently.
- A **processor** attaches to an upstream source, transforms each chunk under its own lock, and re-emits downstream through its own output buffer. `Complete()` flushes the tail — the decoder calls it strictly in chain order at the end of every `Scan` (filter → scanner → processor), because the final flush is what triggers late rebinding (§9).
- Delivery has two modes: byte mode (`Emit`) and **packet mode** (`EmitPacket`) — packets are never split across delivery boundaries; a batch of packets is handed over when its total size reaches the buffer size.

## 7. The Base64 filter (txt mode)

`ByteRangeFilter.CreateBase64()` builds a 256-entry accept map from five ranges: `A–Z`, `a–z`, `0–9`, `+`, `/`. Everything else — newlines, spaces, quotes, emojis, "-----BEGIN-----" decorations, and `=` padding characters — is dropped before scanning. The surviving stream is pure Base64 material, and the scanner looks for 100-character windows that decode into exactly 75 bytes.

In binary mode the filter is excluded from the chain entirely.

## 8. The sliding-window scanner

`SlidingWindowScanner` is the damage-tolerant core of reception. It owns:

- a **window** of fixed size (100 for txt, 75 for binary);
- the **retained stream** — every byte ever passed through it (this is what makes late rebinding possible);
- `_scanPos` — how far the forward pass has consumed.

The forward pass works per position:

1. copy the window from the retained stream at `_scanPos`;
2. call the handler (§8.1);
3. on recognition, emit the packet and jump by the handler's advance (normally the window size); otherwise slide **one byte**.

The one-byte slide is what survives truncations, deletions and inserted garbage: any intact packet anywhere in the stream is found regardless of alignment. The jump on success is a heuristic acceleration: on a damaged stream it can skip the beginning of a packet overlapping the recognized one — which is exactly why the exhaustive rescan pass exists (§8.2).

### 8.1 The window handler contract

```csharp
public delegate int WindowHandler(ReadOnlySpan<byte> window, out byte[]? emitted);
```

Returns the stream advance in bytes (≥ 1) and, on recognition, a copy of the packet. The window is valid only inside the call. The decoder's handlers:

| Handler | Window | Logic | Advance |
|---|---|---|---|
| `TxtWindow` | 100 chars | decode to 75 bytes (`Convert.TryFromBase64Chars`, exactly 75 written), then `_processor.Recognizes` | 100 on success, else 1 |
| `BinWindow` | 75 bytes | `_processor.Recognizes` directly | 75 on success, else 1 |

`Recognizes` accepts a packet when it passes an autonomous check (header by `H5`, signature by `S2`) or matches a sector of at least one existing slot (classic range + hash, or the VirtualIndex sweep). Final classification — header copy, signature, which slot gets the sector — is the processor's business (§10).

### 8.2 Rescan: the exhaustive safety net

`RequestRescan(handler)` runs a **second, exhaustive pass** over the region the forward pass has already visited — the window is tested at *every* position, no jumps. Two rules make it correct:

- The **bound** is the scanned extent at the moment of the request (all visited windows, including the last one's overhang), not the delivery position: windows beyond the forward pass will be visited by it anyway — now with the new knowledge.
- A request arriving mid-scan is **queued** and drained when the current pass finishes; requests to an idle scanner run immediately.

Re-emitting an already-accepted packet is harmless by design: the slot just increments the confirmation counter (§10), it never duplicates a version.

## 9. Late headers: address rebinding

A sector may be scanned *before* its header arrives (reordered stream, headers placed at the end). The scanner could not classify it then — no `H5` existed to seed the check. The pipeline closes this gap with the retained stream:

1. The processor accepts a **new** header and raises `HeaderAccepted(header, headerHash)`.
2. The decoder requests a rescan on **both** scanners (txt and binary) with a `RebindWindow` handler bound to the new `H5` and `T`.
3. `RebindWindow` decodes (txt) or copies (binary) the window, then accepts the packet if it is a **classic sector of that specific header** (index in range + `D3` hash under the new `H5`) or — with the VirtualIndex scheme enabled and `T` within the limit — a virtual-index sector (sweep from zero: retained data is in stream order).
4. If the rescan targets the scanner of the *other* format (e.g. the header arrived in a txt `Scan`, but a binary scanner holds retained data), the processor is temporarily attached to that scanner for the rescan and re-attached to the active one afterwards; binary and txt `Scan` calls never run concurrently on one decoder.

The rescan only checks sectors against *this* header, so other slots' confirmations are untouched. This is also why consumers that rely on completeness must let the chain's final `Complete()` run — the decoder does it at the end of every `Scan`.

## 10. The reception processor boundary

`StreamProcessor` (detailed in the [AssemblyGuide](AssemblyGuide.en.md)) is where packets land:

- **Header packets** — autonomous `H5` check; byte-identical content increments the slot's copy counter; new content creates a slot (and fires `HeaderAccepted`).
- **Signature packets** — autonomous `S2` check; copies counted always; unique `S1` values collected in `Auto`/`Force` modes.
- **Sector packets** — offered to every slot; each slot's `H5` decides membership, which is what keeps files in one stream from mixing.

The decoder exposes the state read-only: `Slots`, `FileCount`, `SignatureMode`, `SignaturePacketCount`, `SignatureCheckActive`, `AcceptedSignatures`, and per-slot observables (validity map, collision map, coverage — see §13).

## 11. Assembling files

`StreamDecoder.TryAssemble(HeaderContent)` serializes the header, byte-matches it against slots, and delegates to the slot's assembly with the decoder's RS adapter, progress, cancellation token, and — when the extended check is active — the collected unique signatures:

| Outcome | Meaning |
|---|---|
| non-null bytes | File reconstructed and verified (Exact SHA-256, plus Any SHA-512 when active). |
| `null` | Damage exceeded redundancy or limits. |
| `SignatureVerificationException` | SHA-256-exact content was found but no accepted signature confirms it — an integrity alarm, not a decoding failure ([AssemblyGuide §13.3](AssemblyGuide.en.md)). |

The facade wraps this conveniently:

| Facade operation | Behavior |
|---|---|
| `Scan` / `ScanText` | Cumulative feeding (no reset). |
| `Decode(stream, format)` / `DecodeText(lines)` | One-shot: clears, scans, returns the first file's content (or `null`). |
| `DecodeAll(stream, format)` / `DecodeAll(progress, ct)` | One-shot / cumulative respectively; returns `List<DecodeResult>`. |
| `DecodeResult` | `(ReceptionSlot Slot, byte[]? Content)` — full reception detail plus content or `null`. |

Slots are assembled in descending order of `ReceivedSectorCount` (the best-covered file first), so `Decode`'s "first" is the most complete candidate.

## 12. Progress, cancellation, errors

- **Phases.** Before the first header: "Searching for headers"; afterwards: "Searching for sectors"; at the end: "Done" (100 %). Assembly phases (RS recovery, exhaustive search, rotation, subset search, "Assembly finished") come from the slot and share the same 0..100 scale.
- **Percentage.** Share of consumed source bytes (`ConsumedAdvanced` events of the scanner over the total, when known — seekable streams and arrays; otherwise percent-less phase messages).
- **Cancellation.** The token is checked at `Scan` entry, on every progress callback, while waiting for the pipeline (`Completion.Wait(ct)`), and throughout assembly. Cancellation leaves prior reception intact — a later `Scan` can continue the same stream.
- **Errors.** Source I/O failures fail `Completion` (observed as exceptions from `Scan`); the `Error` property carries the cause.

## 13. Reports

`CodecReport` writes a human-readable `.report.txt` next to the output (path = output + `.report.txt`), localized through `CodecStrings.Language`. The decoding report shows the summary (name, size, SHA-256 — recomputed from the assembled content when available — header/signature counts, `N`/`M`, total/present/missing, coverage) and a row-wise validity map, 64 sectors per row:

```text
         ════════════════════════════════════════════════════════════════
         DataShield : Decoding report
         ════════════════════════════════════════════════════════════════
         File name  : REPORT.BIN
         Size       : 640 bytes
         SHA-256    : 9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08
         Headers    : 3
         Signatures : 3
         N (data)   : 10
         M (ECC)    : 1
         Total      : 11
         Present    : 10
         Missing    : 1
         Coverage   : 90.91%
         ────────────────────────────────────────────────────────────────
         Legend: '█' — intact, '▓' — collision, '░' — corrupted/missing
         ────────────────────────────────────────────────────────────────

      0 │█████░█████──────────────────────────────────────────────────│ 10/11
```

Details worth knowing:

- When the extended check is active and content was assembled, the SHA-512 is printed on two aligned lines under the SHA-256.
- Under a hashing secret, "on the wire" hash values are keyed and unfit for display: the report recomputes real hashes from the assembled content in RAM; where only a wire value is available (assembly failed), the line is marked with a trailing `*`.
- `CodecReport.WriteSha256Sidecar(path)` writes a `sha256sum`-compatible `<stream>.sha256` sidecar next to a restored stream file.

## 14. State, thread safety, hygiene

| Concern | Behavior |
|---|---|
| Concurrency | All decoder operations serialize on an internal gate; the RS adapter is stateless and inherently parallel-safe. |
| Retention | The scanner retains the **entire** fed stream (that is what rebinding needs); the processor retains accepted packets. Budget `O(stream)` memory for reception. |
| `Clear()` | Wipes slots and scanner tails; the decoder keeps its configuration and can receive again. |
| `Dispose()` | Detaches the pipeline, wipes everything; further operations throw `ObjectDisposedException`. |
| Self-test | SHA-256/SHA-512 implementations are verified before every operation. |

## 15. Cost model

| Operation | Cost |
|---|---|
| Filter pass | O(n) byte tests |
| txt scan, garbage region | one Base64 decode attempt per byte position (decode fails fast on non-alphabet input — in practice the filter already removed it, so failures are mostly alignment misses) |
| txt scan, valid packet | 1 decode + classification hashes, jump 100 |
| binary scan | classification hashes per position; jump 75 on hit |
| Rescan per new header | one exhaustive pass over the scanned extent (classic: 1 hash per window; VirtualIndex: sweep cost per window) |
| Retained memory | one byte per input byte, per scanner used |
| Assembly | see the [AssemblyGuide](AssemblyGuide.en.md) cost table |

## 16. Document map

| Document | Content |
|---|---|
| [AlgorithmGuide](AlgorithmGuide.en.md) | Wire format and algorithms; packet verification rules. |
| [EncoderGuide](EncoderGuide.en.md) | Producing streams the decoder eats. |
| [DecoderGuide](DecoderGuide.en.md) (this file) | Reception pipeline, scanning, rebinding, assembly entry, reports. |
| [AssemblyGuide](AssemblyGuide.en.md) | Slot internals, version search, subset planning, limits. |
| [CollisionBench](CollisionBench.en.md) | Measured false-accept behavior of sector verification. |
| [DeveloperGuide](DeveloperGuide.en.md) | Project layout, build, tests. |
| [UserGuide](UserGuide.en.md) | End-to-end usage from the console application and the GUI. |

Russian mirrors of every document carry the `ru` suffix (`DecoderGuide.ru.md`, …).
