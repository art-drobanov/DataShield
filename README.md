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
- **Two output formats** — text (Base64, for chats and email) and binary (raw packets).

## License

[Apache License 2.0](LICENSE).
