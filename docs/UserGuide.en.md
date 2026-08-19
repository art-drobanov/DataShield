# DataShield User Guide

## What DataShield Is

DataShield protects small files for transfer over unreliable channels: radio, text chats, e-mail, damaged media. A file is encoded into a stream of short self-contained packets (75 bytes = 100 Base64 characters), and the receiver reconstructs it even when some packets are lost, duplicated, or buried in noise. The codec works both ways: encoding adds redundancy, decoding accumulates and reassembles the original file.

## Requirements

- .NET 10 (Desktop Runtime for the GUI; the console version needs only the plain .NET runtime) or a self-contained build of the application.
- Operating system: Windows, Linux, macOS (the GUI is built on Avalonia).
- The algorithms need no network and run fully locally.

## Preparing a File for Transfer (Encoding)

1. Choose a file. Limits: size up to 4 MB (more precisely, up to (65,535 − M) · 64 bytes with ECC; the size field allows up to 16,777,215 bytes, but the volume limit kicks in earlier); long names are automatically shortened to 14 characters with the extension preserved (e.g., `documents.tar.gz` → `docume~.tar.gz`).
2. Set the ECC redundancy, % — how many redundant packets to add to the data packets. The default is 10%: recovery is guaranteed while no more than 10% of sectors are lost. A higher percentage means more reliability but a longer stream.
3. Optionally enable "Virtual-index sector scheme (up to 512 volumes)": the sector packet carries no number field — its place is taken by a longer hash, raising the strength margin (up to 79 bits versus 72). The mode applies to files of at most 512 volumes (N+M); above that the encoder refuses with a clear message. The same option also applies to reception.
4. Choose the output format:
   - **Text (Base64)** — one packet per line; fits chats, e-mails, text files;
   - **Binary** — raw 75-byte packets; more compact, for file-based channels.
5. Press "Encode". Progress shows the phases: data preparation → ECC computation → packet building. The result is a stream ready to copy; you may copy it whole or in parts.

The encoder automatically inserts several header copies (start, end, and evenly across the stream, ~3%), so the file's properties appear in the stream even under heavy losses.

## Receiving and Recovering (Decoding)

1. Paste the received Base64 text or load a binary file; you may feed the stream in parts and in any order — the scanner finds packets in noise and retains everything not yet recognized.
2. While scanning, the UI shows: found files (headers), each file's sector map (received/missing volumes), confirmation and collision counters.
3. Press "Assemble". If the data suffices (all sectors present, or losses covered by ECC), the file is restored and verified by SHA-256 — the result is guaranteed bit-perfect. If the data is insufficient, the decoder refuses honestly: collect more packets and try again.

Good to know:

- Packets may arrive in any order and with repeats — duplicate copies merely raise the confirmation counters.
- A stream may contain several files interleaved — each is assembled by its own header.
- Sectors that arrive before their file's header are not lost: the decoder rebinds them once the header appears.
- Reception is hybrid: with the virtual-index option enabled, the decoder also accepts such files (up to 512 volumes); regular files read as always.

## Console Version

Besides the graphical interface, the solution ships console applications `DataShield.Console.RU` and `DataShield.Console.EN` (Russian or English output) — the same encoding and recovery from the command line:

```powershell
dotnet run --project DataShield.Console.RU -c Release -- encode photo.jpg --ecc 20
dotnet run --project DataShield.Console.RU -c Release -- decode photo.jpg.DataShield.txt --all --use-name
```

By default encoding writes the stream next to the source file: `name.DataShield.txt` (Base64 text) or `name.DataShield.bin` (binary, `--format bin`). When decoding, the format is detected by extension (.txt / .bin); the result is saved with the `.DataShield.*` suffix stripped (or with `.out` inserted when there is no suffix, so the input is never overwritten).

encode options: `--format text|bin`, `--ecc` (redundancy, %, default 10), `--header` (header copies, %), `--scheme classic|virtual`, `--output` (result path), `--quiet` (no progress). decode options: `--scheme`, `--all` (restore every file in the stream, not just the most complete one), `--use-name` (save under the original name from the header), `--output`, `--quiet`. Short forms (`-e 20`, `-o out`) and the `--key=value` spelling are accepted; the `help` command prints usage. A summary follows on completion: size, SHA-256, data/ECC volumes, header copies, scheme. Exit codes: 0 — success, 1 — error, 2 — bad arguments, 130 — interrupted with Ctrl+C (the operation finishes cleanly instead of being killed).

## Recovery Guarantees

- Up to M sectors lost (M ≈ N · ECC%): the file is restored by the Reed–Solomon code.
- All packets present: the file is assembled directly.
- Forged packets (foreign data with a valid hash is practically impossible): when versions look equally plausible, the decoder searches through combinations and accepts only the one whose SHA-256 matches the header.
- Integrity control: SHA-256 in the header; a result without a matching hash is never produced.

## Demonstration and Self-Check

The bundled console bench (`DataShield.Demo`) continuously generates random files, damages them randomly, and verifies recovery. Indicators: **PASS** — restored bit-perfect; **WARN** — the damage deliberately exceeds the safety margin, a refusal is expected; **FAIL** — a codec failure (does not occur in normal operation). Keeping the bench open is a convenient way to watch stability and speed.

The console version has its own bench, `DataShield.Console.Demo`: it continuously runs both localized consoles as child processes, encodes and damages random files, and verifies bit-exact roundtrips and exit codes.

## Quick Reference

| Parameter     | Value                                          |
| ------------- | ---------------------------------------------- |
| Packet        | 75 bytes = 100 Base64 characters               |
| Payload       | 64 bytes per packet                            |
| Default ECC   | 10% (recovers up to 10% losses)                |
| Header copies | ~3%, minimum 3 (start/middle/end)              |
| Sector scheme | Classic (default) or VirtualIndex (opt-in, up to 512 volumes) |
| Maximum file  | ~4 MB without ECC, less with ECC               |
| File name     | ASCII, up to 14 characters (longer — with `~`) |
| Verification  | SHA-256 + truncated SHA-256 per packet         |
