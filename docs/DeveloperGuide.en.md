# DataShield Alpha-XVI — Developer Guide

Repository layout, project graph, build, test suite, vendored references and extension points.

| | |
|---|---|
| Project | DataShield Alpha-XVI |
| Document | `docs/DeveloperGuide.en.md` (English); Russian mirror: `docs/DeveloperGuide.ru.md` |
| Audience | Contributors to the codebase; engineers porting the codec (e.g. to C++); CI/CD maintainers packaging or building the solution |
| Scope | Solution structure and dependency graph, `refs-src` pinned references, build commands, offline NuGet workflow, the test suite and damage harness, demo/bench applications, coding conventions, extension points, versioning |
| Out of scope | Algorithms ([AlgorithmGuide](AlgorithmGuide.en.md)), usage ([UserGuide](UserGuide.en.md)), codec APIs ([EncoderGuide](EncoderGuide.en.md), [DecoderGuide](DecoderGuide.en.md), [AssemblyGuide](AssemblyGuide.en.md)) |

## 1. Conventions used in this document

- "The solution" is `DataShield.slnx` at the repository root (the XML solution format); all 28 projects target **net10.0**.
- `refs-src` — the directory of pinned reference source files (§4).
- Product version at the time of writing: **3.0** (`AssemblyVersion 3.0.0.0`).

## 2. Repository layout

| Entry | Content |
|---|---|
| `DataShield.slnx` | The solution; folders `/Demo/` and `/Tests/` group the non-shipping projects. |
| `DataShield.*/` (production) | 9 libraries + 4 applications — see §3. |
| `DataShield.*.Tests/`, `DataShield.TestsHarness/` | Test projects and the shared damage-simulation library (§6). |
| `DataShield.CollisionBench/`, `DataShield.Demo/`, `DataShield.Console.Demo/`, `RsRaid16Demo/`, `ShaCompactDemo/` | Stands and benches (§7). |
| `refs-src/` | Pinned reference implementations with `.sha256` sidecars (§4). |
| `docs/` | This documentation set. |
| `nuget.config`, `packages/`, `switch_to_local_packages.cmd`, `switch_to_online_packages.cmd`, `download_local_packages.cmd` | NuGet configuration and the offline-feed workflow (§5). |
| `clean.cmd` | Removes `bin/`, `obj/`, `.vs/`, logs and transient artifacts. |
| `LICENSE` | Apache License 2.0 text (project metadata declares `MIT` as the package license expression). |
| `timestamp.txt` | Local build bookkeeping. |

## 3. The project graph

### 3.1 Production libraries

| Project | Depends on | Content |
|---|---|---|
| `DataShield.Interfaces` | — | Pipeline contracts: `IDataSource`, `IDataProcessor`, `DataProcessorBase`, `DataReadyHandler`, plus `BuildInfo`. |
| `DataShield.Codec.Reporting` | — | `CodecProgress` and `CodecStrings` — localized phase names and report labels (English/Russian). |
| `DataShield.Codec.Packets` | Reporting | Wire format: `PacketFormat`, `HeaderContent`, `PacketHasher`, `SignaturePacket`, `SignatureMode`, `SectorScheme`, `FileNameCodec`, `VirtualIndexHasher`; links the vendored SHA implementations. |
| `DataShield.Codec.Ecc` | Reporting | `RsCodecAdapter` — Reed–Solomon erasure coding over GF(2¹⁶); links vendored `GF16` + `RsRaid16`. |
| `DataShield.Codec.IO` | Interfaces | Sources (`ByteArraySource`, `StreamSource`, `FileSource`) and writers (`ByteListWriter`, `StreamDataWriter`, `FileDataWriter`, `PreallocatedBufferWriter`). |
| `DataShield.Codec.StreamFilter` | Interfaces, Reporting | `ByteRangeFilter` — the Base64 garbage filter. |
| `DataShield.Codec.StreamScanner` | Interfaces | `SlidingWindowScanner`, `WindowHandler`. |
| `DataShield.Codec.StreamProcessor` | Interfaces, Packets, Reporting, Ecc | `StreamProcessor`, `ReceptionSlot`, `Versions/` and `Subsets/` assembly machinery; links vendored `Mt19937`. |
| `DataShield.Codec` | all of the above | The facade assembly: `StreamEncoder`, `StreamDecoder`, `StreamCodec`, `DataShieldCodec`, `PacketIO`, `CodecReport`, `EncodeStats`, `DecodeResult`, `SecretScopeGuard`. |

The layering rule is one-directional: format and math at the bottom (`Packets`, `Ecc`), plumbing in the middle (`IO`, `Filter`, `Scanner`, `Processor`), orchestration at the top (`Codec`). Nothing below `Codec` references it.

### 3.2 Applications

| Project | Kind | Content |
|---|---|---|
| `DataShield.GUI` | WinExe | Avalonia 11.3.20 desktop application (Fluent theme, Inter fonts): `MainWindow.axaml`, `MainViewModel`. References only `DataShield.Codec`. |
| `DataShield.Console` | library | The shared console engine: `ConsoleApp`, `DataShieldConsole`, `ConsoleProgress`, `IConsoleStrings`. |
| `DataShield.Console.EN` / `DataShield.Console.RU` | Exe | Thin entry points (`Program.cs` + `Strings.cs`) supplying the localized strings for the shared engine. |

## 4. `refs-src`: pinned reference implementations

Seven files live under `refs-src/`, each accompanied by a `.sha256` sidecar:

| File | Linked into | Role |
|---|---|---|
| `Sha256Compact.cs`, `Sha512Compact.cs` | `DataShield.Codec.Packets` | Allocation-free plain SHA-256/SHA-512 used for every "on the wire" digest. |
| `Sha256Secure.cs`, `Sha512Secure.cs` | `DataShield.Codec.Packets` | Secret-scoped (keyed) variants: `AsyncLocal<SecretScope>` plumbing; empty/absent secret degrades to Compact. |
| `GF16.cs`, `RsRaid16.cs` | `DataShield.Codec.Ecc` | GF(2¹⁶) arithmetic and the RS erasure codec. |
| `Mt19937.cs` | `DataShield.Codec.StreamProcessor` | Mersenne Twister for the deterministic subset-planner shuffle. |

These are compiled into their projects via `<Compile Include="..\refs-src\…">` links. Their header comment states the contract plainly: *"reference implementations: the source for a C++ port — do not edit"*. The `.sha256` sidecars (and, for `Mt19937`, a hash marker inside the file itself) pin the exact content, so an accidental modification is detectable. If you need different behavior, wrap or configure around them — do not fork them in place.

## 5. Building

Requirements: .NET SDK 10 (`net10.0` targets). Everyday commands:

```text
dotnet build DataShield.slnx                 # everything
dotnet test DataShield.slnx                  # full test suite (§6)
dotnet run --project DataShield.GUI
dotnet run --project DataShield.Console.EN -- <args>
dotnet run --project DataShield.Console.RU -- <args>
```

> [!NOTE]
> A full build may print benign warnings: **CS1668** (the classic VC98 `LIB` environment-path notice from the MSVC toolchain resolution) and **AVLN3001** (an Avalonia analyzer notice). Neither affects output correctness.

Offline NuGet workflow (for air-gapped machines): `switch_to_local_packages.cmd` rewrites `nuget.config` to use only the local flat feed `packages/` (backing up the original to `nuget.online.config`), and opportunistically syncs the dependency closure from the machine-wide NuGet cache; `download_local_packages.cmd` fills the feed; `switch_to_online_packages.cmd` restores the online configuration (currently `nuget.org`). `clean.cmd` wipes `bin`/`obj`/`.vs` and transient files when a truly fresh build is needed.

## 6. The test suite

### 6.1 Projects and scale

Nine xUnit projects (xunit 2.9.3, `Microsoft.NET.Test.Sdk` 17.14.1, `xunit.runner.visualstudio` 3.1.4, `coverlet.collector` 6.0.4):

| Project | Fact/Theory methods | Focus |
|---|---|---|
| `DataShield.Codec.Ecc.Tests` | 14 | RS erasure coding, geometry edge cases. |
| `DataShield.Codec.IO.Tests` | 18 | Sources and writers, delivery contract. |
| `DataShield.Codec.Packets.Tests` | 48 | Wire format, hashes, name packing, VirtualIndex hasher. |
| `DataShield.Codec.Reporting.Tests` | 3 | Localization strings. |
| `DataShield.Codec.StreamFilter.Tests` | 8 | Byte-range filtering. |
| `DataShield.Codec.StreamScanner.Tests` | 11 | Sliding window, rescan semantics. |
| `DataShield.Codec.StreamProcessor.Tests` | 95 | Slots, versions, collisions, subset planning, assembly stages. |
| `DataShield.Interfaces.Tests` | 6 | Pipeline base classes. |
| `DataShield.Tests` | 145 | End-to-end: `CodecTests`, `SignatureTests`, `VirtualIndexCodecTests`, `StreamEncoderTests`, `StreamDecoderTests`, `DataShieldCodecTests`, `PacketIOTests`, `CodecReportTests`, `EncoderProgressPhasesTests`. |

That is 348 fact/theory methods; theory expansion brings a full run to 561 executed test cases at the time of writing.

### 6.2 The damage harness (`DataShield.TestsHarness`)

The end-to-end tests do not feed pristine streams to the decoder — they feed damaged ones, reproducibly. The harness provides:

| Component | Role |
|---|---|
| `RandomInput` | Seeded random file content of controlled sizes. |
| `DamageBits`, `BinaryDamage`, `LineDamage`, `PacketDamage` | Damage primitives: bit flips, byte-level damage of raw streams, line-level damage of text (truncation, deletion, duplication, reordering, injection), packet-level surgery. |
| `PacketProbe` | Controlled single-packet corruption (used, e.g., by the CollisionBench corrupt scenario). |
| `DamageEngine`, `ScenarioEngine` | Compose primitives into scenario runs with budgets ("within ECC budget" vs "beyond"). |
| `CodecVariants` | The matrix of codec configurations (schemes, signature modes, secrets, ECC/header percents) that scenarios iterate. |

### 6.3 Concurrency of tests

`xunit.runner.json` sets `"parallelizeTestCollections": false`. Reason: parts of the code under test use **process-global state** — `CodecStrings.Language` (localization) and the `AsyncLocal` secret scope interact badly with parallel collections that expect different settings. Tests that change the language restore it in `finally`. Keep this contract when adding tests.

## 7. Stands and benches (the `/Demo/` folder)

| Project | What it does continuously |
|---|---|
| `DataShield.CollisionBench` | The collision-resistance measurement campaign — modes `selftest`/`collide`/`full`/`perf`/`all` ([CollisionBench guide](CollisionBench.en.md)). |
| `DataShield.Demo` | Endless stability stand: random files (single- and multi-file streams, both sector schemes, random ECC percents, optional signatures/secrets/signature modes), damage within and beyond the ECC budget, then decode verification. PASS = bit-exact recovery; WARN = correct refusal (over-damage, wrong secret, forced signatures missing); FAIL = broken expectation (stops the stand). Every operation goes through the uniform `IDataShieldCodec` contract — randomly via the library type (`Api = A`) or via `DataShieldConsole` (`Api = C`); instances are disposed per operation; the PRNG seed rotates on schedule (default: hourly). |
| `DataShield.Console.Demo` | Endless *user-contract* stand: launches the real `DataShield.Console.EN` / `DataShield.Console.RU` executables as child processes and verifies return codes, the banner, help output, localized error texts, bit-exact encode→decode roundtrips and damaged-stream decoding across locales, option spellings and flag combinations. |
| `RsRaid16Demo` | RS stability stand: random volume geometry (2,048..4,096 volumes), erasure counts strictly below correction capability, block-by-block verification of recovery. The `bench [--csv]` argument runs the calibration grid instead — the measurements behind the decoder's default time/E budgets. |
| `ShaCompactDemo` | Differential stress: random buffers (1 B..1 MiB) hashed by `Sha256Compact`/`Sha512Compact` and by the .NET BCL implementations, compared; the first mismatch aborts. |

The stands are not part of `dotnet test`; they are long-running instruments meant for soak testing and calibration.

## 8. Coding conventions of the codebase

- **Language**: code identifiers and the public API are English; XML documentation comments are Russian. Keep new comments consistent with the file you are editing.
- **Project settings**: `Nullable` and `ImplicitUsings` enabled everywhere; versions synchronized (`Version 3.0`, `AssemblyVersion 3.0.0.0`, `Product DataShield`, `Company Artem Drobanov`).
- **Visibility discipline**: `internal` by default; `public` only for what applications genuinely consume (codec facade, slot observables, options types). Test access uses `InternalsVisibleTo` where needed.
- **Determinism**: anything with randomness takes an explicit seed (`Mt19937`, `Random` mixes); planning code is pure and reproducible (see `SubsetMaskPlanner`, `SectorCombinationMath`).
- **Defensive lifecycle**: user-data holders implement `Clear()` (wipe, reusable) and `Dispose()` (wipe, final); failed verification wipes buffers; counters saturate instead of overflowing.
- **Concurrency**: state machines serialize on a `Gate`/`SyncRoot`; the RS adapter is stateless and safe to share.
- **Pure math isolation**: combinatorics and probability live in static classes of pure functions (`SectorCombinationMath`, `CollisionMath`) so they are unit-testable in isolation.

## 9. Extension points

| Want to… | Touch |
|---|---|
| Tune recovery limits | `SectorVersionSearchOptions` / `VolumeSubsetSearchOptions` (no code changes needed — they are constructor parameters; see [AssemblyGuide §17](AssemblyGuide.en.md)). |
| Add an output container format | `OutputFormat` enum + `PacketIO` writers/readers + `OutputFormatConfig.DetectFormat`. The decoder pipeline is format-agnostic above the filter/scanner choice. |
| Localize to another language | `CodecStrings` (codec phases/reports), `Strings.cs` per console app, GUI resource files. Note the global-state caveats of §6.3. |
| Add a hashing secret consumer | `SecretScopeGuard` + the `Sha*Secure` classes — nothing else needs to know secrets exist. |
| Change the wire format | `PacketFormat` + `PacketHasher` + `StreamProcessor` classification + the docs. Treat as a format revision: the header carries no version field, so compatibility is deliberate by construction (autonomous checks only). |
| Port to C++ | Start from `refs-src` (that is its declared purpose), then port `Packets` → `Ecc` → pipeline → codec orchestration, in that order. |

## 10. Versioning and metadata

All projects carry synchronized assembly metadata (`Version 3.0`, copyright "© 2026 Artem Drobanov"; the demo/bench stands additionally credit Vladislav Utyumov in their banners). Package metadata declares `PackageLicenseExpression: MIT`; the repository root ships the Apache-2.0 `LICENSE` text. `BuildInfo.BuildLabel` (Interfaces) stamps build labels into banner lines of the stands.

## 11. Document map

| Document | Content |
|---|---|
| [AlgorithmGuide](AlgorithmGuide.en.md) | Wire format and algorithms. |
| [EncoderGuide](EncoderGuide.en.md) | Encoder usage. |
| [DecoderGuide](DecoderGuide.en.md) | Decoder usage. |
| [AssemblyGuide](AssemblyGuide.en.md) | Assembly stages in depth. |
| [CollisionBench](CollisionBench.en.md) | Collision benchmark methodology. |
| [DeveloperGuide](DeveloperGuide.en.md) (this file) | Layout, build, tests, references, conventions. |
| [UserGuide](UserGuide.en.md) | End-to-end usage from the console application and the GUI. |

Russian mirrors of every document carry the `ru` suffix (`DeveloperGuide.ru.md`, …).
