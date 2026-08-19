# DataShield

**English** | [Русский](README.ru.md)

**DataShield** is a forward error correction (FEC) codec for transferring small files over
unreliable channels: text pastes, messengers, email bodies, radio links, damaged storage
media.

A file is turned into a stream of small self-contained packets — 75 bytes, exactly
100 Base64 characters in text mode, one line = one packet. The receiver reassembles the
file from an arbitrarily damaged, reordered, duplicated and multi-file stream — the result
is guaranteed bit-for-bit identical to the original (verified against the SHA-256 stored
in the header).

Limits: files up to ~4 MB ((65,535 − M) · 64 bytes with ECC); ASCII names up to
14 characters (longer ones are shortened with `~`). Fully offline, no network.

## Features

- **Losses** — erasure Reed–Solomon code over GF(2^16): at 10% ECC the file survives the
  loss of up to 10% of sectors (the percentage is configurable).
- **Arbitrary order** — orderless accumulative reception; a sector carries only its number.
- **Duplicates** — repeated packet copies do no harm and raise confirmation counters.
- **Noise** — packets are recognized by a sliding-window scan; no byte is treated as
  garbage until it fails a truncated SHA-256 check.
- **Multi-file streams** — each sector is cryptographically bound to its own header
  through a hash; files never get mixed up.
- **Forgeries and collisions** — payload versions with confirmation counters and
  combination search at assembly; a result with a mismatching SHA-256 is never produced.
- **Silent corruption** — a sector is formally valid yet the file will not assemble:
  a volume-subset search recovers the suspicious volumes from the remaining ones and ECC.
- **Two sector schemes** — Classic (numbered sectors) and an optional VirtualIndex scheme
  where the number field is replaced by a longer hash (79 vs 72 bits of collision margin),
  for streams of up to 512 volumes.
- **Two output formats** — text (Base64, for chats and email) and binary (raw packets).

## Applications

| Application                   | Purpose                                                                 |
| ----------------------------- | ----------------------------------------------------------------------- |
| `DataShield.GUI`              | Cross-platform Avalonia GUI: encode/decode, sector map, RU/EN interface |
| `DataShield.Console.RU`/`.EN` | Localized console CLI                                                   |
| `DataShield.Demo`             | Endless stability/performance bench (PASS/WARN/FAIL)                    |
| `DataShield.Console.Demo`     | CLI contract bench: spawns both consoles, verifies roundtrips and codes |
| `DataShield.CollisionBench`   | Collision-strength bench for the two sector schemes                     |

## Quick Start

Requires the .NET 10 SDK.

```powershell
dotnet build DataShield.slnx -c Release
dotnet test DataShield.slnx                    # 543 tests
dotnet run --project DataShield.GUI -c Release
```

Console — a file into a FEC stream and back:

```powershell
dotnet run --project DataShield.Console.EN -c Release -- encode photo.jpg --ecc 20
dotnet run --project DataShield.Console.EN -c Release -- decode photo.jpg.DataShield.txt --all --use-name
```

Encoding writes `name.DataShield.txt` (Base64, default) or `name.DataShield.bin`
(`--format bin`) next to the source; decoding detects the format by extension and strips
the suffix for the restored file.

## Command-Line Interface

Commands: `help`, `encode <file>`, `decode <file>`. Options accept long and short forms
(`--ecc 20` / `-e 20`) and the `--key=value` spelling:

| Option           | Applies to | Meaning                                                            |
| ---------------- | ---------- | ------------------------------------------------------------------ |
| `--format`, `-f` | encode     | `text` (Base64, default) or `bin`                                  |
| `--ecc`, `-e`    | encode     | ECC redundancy, %; 0..1000 (default 10)                            |
| `--header`, `-h` | encode     | header copies, %; 0..50 (default ~3)                               |
| `--scheme`, `-s` | both       | `classic` (default) or `virtual`                                   |
| `--output`, `-o` | both       | result path                                                        |
| `--quiet`, `-q`  | both       | suppress the progress line                                         |
| `--all`, `-a`    | decode     | restore every file of the stream, not just the most complete one   |
| `--use-name`, `-n` | decode   | save under the original name from the header                       |

Exit codes: `0` — success, `1` — error, `2` — bad usage, `130` — interrupted with Ctrl+C
(the current phase finishes cleanly).

## Repository Layout

- `DataShield.Codec.*` — the codec pipeline: wire format (`Packets`), IO sources and
  writers, Base64 stream filter, sliding-window scanner, reception core
  (`StreamProcessor`), RS ECC adapter, reporting/localization; `DataShield.Codec` is the
  facade over it all.
- Applications and benches: `DataShield.GUI`, `DataShield.Console` (+ `.RU`/`.EN`),
  `DataShield.Demo`, `DataShield.Console.Demo`, `DataShield.CollisionBench`.
- Tests: `DataShield.*.Tests` ×8 (unit, per module) + `DataShield.Tests` (integration) —
  543 in total; `DataShield.TestsHarness` is the damage engine shared by tests and
  benches.
- `docs/` — full documentation; `refs-src/` — unmodified RS/SHA reference sources
  (never edited).

## Documentation

| Document                                                 | Contents                                        |
| -------------------------------------------------------- | ----------------------------------------------- |
| [`UserGuide`](docs/UserGuide.en.md)                      | GUI and console usage, recovery guarantees      |
| [`AlgorithmGuide`](docs/AlgorithmGuide.en.md)            | encoding, reception, and assembly algorithms    |
| [`DeveloperGuide`](docs/DeveloperGuide.en.md)            | architecture, module walkthrough, tests, build  |
| [`EncoderGuide`](docs/EncoderGuide.en.md)                | encoder and wire format in depth                |
| [`DecoderGuide`](docs/DecoderGuide.en.md)                | decoder in depth                                |
| [`AssemblyGuide`](docs/AssemblyGuide.en.md)              | assembly: RS recovery, version search           |
| [`CollisionBench`](docs/CollisionBench.en.md)            | collision-strength measurements                 |

Every document also has a Russian version (`*.ru.md`).

## License

[Apache License 2.0](LICENSE).
