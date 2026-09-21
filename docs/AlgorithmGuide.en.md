# DataShield Alpha-XVI — Algorithm Guide

Reference documentation of the wire format and the encoding/decoding algorithms.

| | |
|---|---|
| Project | DataShield Alpha-XVI |
| Document | `docs/AlgorithmGuide.en.md` (English); Russian mirror: `docs/AlgorithmGuide.ru.md` |
| Audience | Engineers who need to understand, review, port or independently implement the codec; advanced operators who want to know what the tool actually does |
| Scope | Packet formats, the hashing system, volume arithmetic, Reed–Solomon redundancy, the encoding pipeline, the decoding pipeline, verification rules, worked examples, parameter reference |
| Out of scope | Command-line and GUI usage ([UserGuide](UserGuide.en.md)), stream text formatting options ([EncoderGuide](EncoderGuide.en.md)), scanner internals in depth ([DecoderGuide](DecoderGuide.en.md)), stage-by-stage assembly mechanics ([AssemblyGuide](AssemblyGuide.en.md)), project layout and build ([DeveloperGuide](DeveloperGuide.en.md)) |

## 1. Conventions used in this document

- A **byte range** `a..b` is inclusive: bytes at offsets `a` through `b`, that is `b − a + 1` bytes.
- `X ‖ Y` means concatenation of byte strings: first the bytes of `X`, then the bytes of `Y`.
- `TruncN(X)` means **the first N bytes** of a longer digest. All underlying digests here are full-size; only their wire representation is shortened.
- Multi-byte integers on the wire are **little-endian** (LE) unless stated otherwise.
- `SHA-256(data)` and `SHA-512(data)` denote the standard algorithms. Where a hashing secret is configured, the actual computation is the *keyed* variant described in [§6](#6-the-hashing-system); until then read them as plain hashes.
- Sizes: `B` = bytes, `MiB` = 1,048,576 bytes.
- "2⁻ⁿ" is the probability that a random value passes an n-bit check.

## 2. The problem being solved

DataShield moves a **file** across a channel it does not control. Typical channels: a chat message, a pasted note, an e-mail body, a ticket comment. Such channels regularly:

- **truncate** the text (message length limits, manual copy of "the important part");
- **drop fragments** from the middle (copy-paste slips, editor mishaps);
- **duplicate** fragments (forwarded quotes, repeated pastes);
- **reorder** fragments (manual reassembly by a human);
- **inject foreign text** around and between the useful parts (greetings, signatures, other people's messages);
- **corrupt** short pieces (autocorrect, encoding mishaps).

The tool on the receiving side gets an arbitrary pile of text and must recover the file — bit-exact — from whatever survived.

Two properties are required:

1. **Durability.** Loss of a controlled fraction of the transmission must not lose the file.
2. **Integrity.** The receiver must be able to *prove* that the recovered bytes are exactly the original bytes — including the case where pieces of two different transmissions got mixed together.

What the format deliberately does **not** provide:

> [!IMPORTANT]
> **This is not encryption.** Payloads travel as plain bytes (packed into text). The hashing secret (§6) makes forged packets computationally infeasible for parties without the secret, but anyone can read the content. For confidentiality, encrypt the file before encoding it; DataShield will faithfully carry the ciphertext.

## 3. Design principles

The format is built from a small number of decisions, applied consistently:

1. **One self-contained unit: the packet.** The file is broken into fixed 75-byte packets, each converted to exactly 100 characters of Base64. A packet is either fully intact and verifiable, or it is garbage; there are no partially useful packets. This makes the channel's job trivial: anything that delivers whole 100-character fragments delivers usable data.
2. **Three independent redundancy layers.**
   - *Duplication of metadata*: several copies of the header packet and (optionally) the signature packet are spread through the stream, so knowing *what* to reconstruct never depends on a single fragment.
   - *Erasure coding*: Reed–Solomon parity volumes restore a controlled fraction of lost payload packets (§9).
   - *Cryptographic binding*: every packet is bound to its file by a truncated hash seeded with the file's header hash, so fragments of different files cannot silently mix (§5, §6).
3. **Verification before trust.** No packet is accepted on syntax alone. A header must satisfy a 192-bit internal check; a sector must satisfy a hash bound to the header of *that specific file*; the final assembled file must reproduce the SHA-256 stored in the header. Nothing reaches the output unverified.
4. **Deterministic, bounded recovery.** All search stages on the decoder side have explicit limits (combination counts, attempt counts, wall-clock budgets). The decoder either succeeds within the limits or reports failure; it never spins without bound.
5. **Plain engineering primitives.** SHA-256/SHA-512, truncated digests, Reed–Solomon over GF(2¹⁶), Base64. No custom ciphers, no novelty cryptography.

## 4. The packet: unit of transmission

| Property | Value |
|---|---|
| Binary size | 75 B |
| Text size | exactly 100 Base64 characters, no `=` padding (75 is divisible by 3) |
| Kinds | header, sector (two schemes), signature — see §5 |
| Fragmentation | never: a packet is atomic; the receiver looks for whole 100-character fragments |

Encoding direction: bytes → Base64 (`A–Z a–z 0–9 + /`). The receiving-side text filter extracts Base64 material from arbitrary surrounding text, ignoring whitespace and stray padding characters; the scanner then looks for 75-byte windows whose internal checks pass (§11.1). How packets are joined into the output text (line breaks, framing) is an output-format option described in the [EncoderGuide](EncoderGuide.en.md); the decoder does not depend on it.

## 5. Packet formats

All packets are exactly 75 bytes. Offsets are from the start of the packet.

### 5.1 Header packet

Carries everything needed to identify and verify the file. Several copies are placed in the stream (§10.2).

| Field | Range | Size | Encoding | Meaning |
|---|---|---|---|---|
| H1 — file name | 0..13 | 14 B | ASCII, space-padded on the right | packed file name; packing rules in §7 |
| H2 — file size | 14..16 | 3 B | unsigned LE (low 3 bytes of a 32-bit value) | exact size of the file content, in bytes |
| H3 — content hash | 17..48 | 32 B | raw | SHA-256 of the complete file content |
| H4 — parity count | 49..50 | 2 B | UInt16 LE | M: number of Reed–Solomon parity volumes |
| H5 — header hash | 51..74 | 24 B | raw | `Trunc24(SHA-256(H1 ‖ H2 ‖ H3 ‖ H4))` |

The 51 bytes H1..H4 are the *header content*; H5 is its 24-byte (192-bit) truncated digest.

**Autonomous verification.** A 75-byte window is a valid header packet if and only if the bytes at 51..74 equal `Trunc24(SHA-256(bytes 0..50))`. No external context is needed, so the scanner can recognize headers in a stream it has never seen before. The chance that a random 75-byte blob passes is 2⁻¹⁹².

Derived quantities (computed by the decoder from the header):

- Data volume count `N = max(1, ceil(H2 / 64))` — the file is carried in N payload volumes of 64 B each.
- Total volume count `N + M` — payload volumes plus parity volumes; sector indices run `0 .. N+M−1`.

### 5.2 Sector packet — Classic scheme (default)

Carries one 64-byte payload for one volume index of one file.

| Field | Range | Size | Encoding | Meaning |
|---|---|---|---|---|
| D1 — volume index | 0..1 | 2 B | UInt16 LE | index of the volume: `0..N−1` data volumes, `N..N+M−1` parity volumes |
| D2 — payload | 2..65 | 64 B | raw | file bytes (data volume) or parity bytes (parity volume); the final data volume is zero-padded to 64 B |
| D3 — sector hash | 66..74 | 9 B | raw | `Trunc9(SHA-256(H5 ‖ D1 ‖ D2))` |

D3 is seeded with **H5 of this file's header**. Consequences:

- A sector packet cannot be verified without knowing the header — deliberately. Noise and foreign packets are structurally invisible to a receiver that has not yet seen the header; conversely, once the header is known, every sector is bound to that exact file (H3 inside H5 pins the file's identity).
- Given the header, a random 75-byte blob passes the D3 check with probability 2⁻⁷² (72 bits).

### 5.3 Sector packet — VirtualIndex scheme (optional)

Same purpose, different binding of the index.

| Field | Range | Size | Encoding | Meaning |
|---|---|---|---|---|
| V2 — payload | 0..63 | 64 B | raw | payload, same as D2 |
| V3 — position tag | 64..74 | 11 B | raw | `Trunc11(SHA-256(H5 ‖ payload ‖ index_LE))`, where `index_LE` is the volume index as a little-endian byte string |

The index is **not stored** in the packet. It is an input of the tag instead: the receiver determines the index by testing candidate indices until the tag matches (§11.3). Both schemes carry exactly 64 payload bytes per packet — Classic spends 2 B on the index and 9 B on the tag, VirtualIndex spends 0 B on the index and 11 B on the tag; the totals balance at 75 B.

Why two schemes:

- **Classic** scales to the full volume range (up to 65,535 volumes) and verifies in one hash computation per packet.
- **VirtualIndex** is stronger per packet (88-bit tag) and frees the 2 index bytes, but the receiver must recover the index by search. The search is limited by `VirtualIndexSectorLimit` (default **512** total volumes): files with more volumes are simply **not encoded** in this scheme — the encoder refuses instead of silently downgrading. On the receiving side, index recovery applies to slots within the same limit; the scheme is selected explicitly (Classic by default) and must match on both ends.

False-positive strength of the tag, given a known header: a random blob matches *some* index among `K = N + M` candidates with probability `K · 2⁻⁸⁸ = 2⁻⁸⁸⁺ˡᵒᵍ₂ᴷ`, i.e. **88 − log₂K bits**; at the limit K = 512 that is ≥ 79 bits — still stronger than Classic's 72 bits.

### 5.4 Signature packet (optional)

Independent proof of the complete file content, produced with SHA-512.

| Field | Range | Size | Encoding | Meaning |
|---|---|---|---|---|
| S1 — content signature | 0..63 | 64 B | raw | `SHA-512(complete file content)` |
| S2 — signature tag | 64..74 | 11 B | raw | `Trunc11(SHA-512(S1))` |

S2 makes the packet **autonomous** (verifiable without any header, chance of a random pass 2⁻⁸⁸), so signature packets are recognized anywhere in the stream, exactly like headers. S1 itself is checked later, against the assembled file. One signature copy is placed immediately after every header copy (§10.2).

### 5.5 Verification strength summary

| Check | Bits | Context needed | Random blob passes with |
|---|---|---|---|
| Header H5 | 192 | none | 2⁻¹⁹² |
| Signature S2 | 88 | none | 2⁻⁸⁸ |
| Classic sector D3 | 72 | header of the same file | 2⁻⁷² |
| VirtualIndex tag V3 | 88 − log₂K | header of the same file | ≥ 2⁻⁷⁹ at K = 512 |
| Final content check | 256 | header H3 | 2⁻²⁵⁶ |
| Final signature check | 512 | any signature packet S1 | 2⁻⁵¹² |

## 6. The hashing system

### 6.1 Two levels: Compact and Secure

Every SHA-256/SHA-512 computation in the codec goes through a single pair of primitives:

- **Compact** — plain SHA, byte-for-byte the standard algorithm.
- **Secure** — *keyed by a secret*. The secret (a string) is UTF-8-encoded and fed as a **prefix to every hashed message**: `digest = SHA(secret_utf8 ‖ message)`. Without a secret set, Secure is bit-identical to Compact.

This is a deliberate, conservative design: prefix-keyed hashing in the HMAC spirit, but constructed from unmodified SHA so that the no-secret path is exactly the standard algorithm. All wire values (H3, H5, D3, V3, S1, S2) are computed through the Secure primitive, so **one switch keys the entire wire format**.

The secret is held in an `AsyncLocal` secret scope: it is set for the duration of an encode or decode operation and is naturally confined to that asynchronous flow. A guard structure (`SecretScopeGuard`) installs the secret at operation start and restores the previous scope on dispose. Two rules matter:

- An **empty secret means "no secret"**: installing an empty string does not erase a secret set by an outer scope.
- The secret applies symmetrically: **the same secret must be supplied on the decoding side**, or every hash check fails (the stream becomes unverifiable — and is rejected — rather than accepted with weakened checks).

### 6.2 What is hashed, and where it shows up

| Value | Primitive | Input | Where it lives |
|---|---|---|---|
| H3 | SHA-256 (Secure) | complete file content | header bytes 17..48 |
| H5 | SHA-256 (Secure), truncated to 24 B | H1 ‖ H2 ‖ H3 ‖ H4 | header bytes 51..74 |
| D3 | SHA-256 (Secure), truncated to 9 B | H5 ‖ D1 ‖ D2 | Classic sector bytes 66..74 |
| V3 | SHA-256 (Secure), truncated to 11 B | H5 ‖ payload ‖ index_LE | VirtualIndex sector bytes 64..74 |
| S1 | SHA-512 (Secure) | complete file content | signature bytes 0..63 |
| S2 | SHA-512 (Secure), truncated to 11 B | S1 | signature bytes 64..74 |

> [!NOTE]
> Values shown *to the user* in reports and statistics (the "real" SHA-256/SHA-512 of the file) are always computed **without** the secret, so a report remains comparable across machines and sessions. The secret affects only what travels on the wire and what internal checks accept.

### 6.3 Why truncation is safe here

Truncated digests weaken the *collision resistance* of a hash, but the wire checks are **second-preimage-style** gates against accidental noise and unkeyed forgery: an attacker (without the secret) must produce a message whose keyed digest shares the stored prefix. Against a 192-bit header gate or a 72–88-bit sector gate, the practical answer is that random text simply never passes; the final 256/512-bit full-size checks (H3, S1) are what actually certify the output file. Truncation exists to fit the checks into the fixed 75-byte packet alongside a useful 64-byte payload.

## 7. Packing the file name (field H1)

The header has 14 bytes for a name. Real names do not always fit, so the name is *packed*, not stored verbatim; the exact original name is recovered from H3 (see below).

Rules of `FileNameCodec.Pack`:

1. The name is split at the **first** dot. Everything from that dot on is the *extension* and is kept **whole** — compound extensions like `.tar.gz` survive as one unit. If there is no dot, or the dot is the first character, the whole name is the base and there is no extension.
2. The *base* (the part before the extension) gets the remaining byte budget: `budget = 14 − extension_length`.
3. If the base fits the budget, it is stored as-is. If not, it is cut to `budget − 1` bytes and a tilde `~` is appended as a truncation marker (no digits are attached — the marker says only "this was cut").
4. If the budget is under 2 bytes (1 character + the marker), the name is **not representable** and encoding is refused with an error — a mangled 0–1 character hint is considered worse than a clear failure.
5. The result is right-padded with spaces to exactly 14 bytes and must be ASCII-encodable.

The packed name is a *display hint*: it lets a human recognize the file from any surviving header copy. The **authoritative** name comes from the sender side out-of-band or from the receiver's own knowledge, and identity of the content is always settled by H3: the decoder verifies the assembled content against the header's SHA-256, so a matching H3 certifies the bytes that the (possibly truncated-looking) name refers to.

## 8. Volume arithmetic and limits

Notation: file size `S` bytes, parity percentage `p` (0–100), header percentage `q` (0–100).

| Quantity | Formula | Notes |
|---|---|---|
| Data volumes `N` | `max(1, ceil(S / 64))` | the last data volume is zero-padded to 64 B |
| Parity volumes `M` | `max(1, round(N · p / 100))` when `p ≥ 1`; `0` when `p = 0` | rounding half-up; even 1 % buys at least one parity volume |
| Total volumes `T` | `N + M` | sector indices `0..T−1`; parity occupies `N..T−1` |
| Header copies `H` | `max(3, round(T · q / 100))` | rounding half-up; at least 3 copies: begin, middle, end |

Hard limits, all enforced with explicit errors:

| Limit | Value | Origin |
|---|---|---|
| File size field | 16,777,215 B (≈16.8 MB) | H2 is 3 bytes |
| Total volumes `T` | 65,535 | sector index field and RS codeword length (§9) |
| Parity count `M` | 65,535 | H4 is UInt16 |
| VirtualIndex total volumes | `VirtualIndexSectorLimit`, default 512 | index search cost (§5.3) |

> [!NOTE]
> The binding constraint for large files is the volume limit, not the size field: `T ≤ 65,535` means at most `65,535 · 64 ≈ 4.0 MiB` of data even with zero parity. The 3-byte size field simply never gets a chance to overflow.

## 9. The Reed–Solomon layer

Purpose: tolerate the *loss* of whole sector packets, up to a controlled fraction, without any retransmission.

Model:

- A **volume** is one 64-byte slot of the codeword; on the wire it is carried by one sector packet (Classic or VirtualIndex). Data volumes hold file bytes; parity volumes hold computed redundancy.
- The code operates over **GF(2¹⁶)**: one symbol = 2 bytes, read little-endian. Each 64-byte volume is **32 symbols**.
- For each of the 32 symbol positions independently, the values at that position across all `T = N + M` volumes form one Reed–Solomon codeword of length `T` with `N` information symbols and `M` parity symbols. Equivalently: parity volume `j` is computed so that the whole stack of volumes forms a systematic RS code column-group by column-group.
- Decoding is **erasure decoding**: the decoder knows *which* volumes are missing (it has verified packets for the others), not *how* they were corrupted. RS erasure decoding recovers any `e ≤ M` missing volumes of a codeword from the remaining `T − e ≥ N` volumes.

Properties and rules that follow:

- **Restoration condition.** A set of lost data volumes is recoverable exactly when `erased data volumes ≤ usable parity volumes` (parity volumes that are themselves present). At `p = 10 %` the stream survives the loss of any 10 % of its payload packets — any 10 %, not just a contiguous tail.
- **Length limit.** `N + M ≤ 65,535` — the RS length ceiling over GF(2¹⁶), matching the volume limit of §8.
- **Erasures, not errors.** Corrupted-in-place packets do not exist from the decoder's point of view: a packet whose hash check fails is discarded and becomes an erasure. This is why per-packet verification (§5) and RS fit together so tightly.
- **Cost.** One decode attempt costs roughly an `O(E²·N)` setup plus 32 passes of `O(E·N)` work plus hashing, where `E` is the number of erased volumes. The subset-search stage caps `E` at 32 (`MaxErasedDataVolumes`, calibrated by benchmark) so that even at the maximum legal `T = 65,535` dozens of attempts still fit inside the 30-second time budget (§11.4).
- **Implementation.** The arithmetic lives in the reference `RsRaid16` sources wrapped by `RsCodecAdapter`; the codec proper never touches GF math directly.

## 10. The encoding pipeline

Input: file content (bytes), file name, parameters (`eccPercent`, `headerPercent`, `sectorScheme`, `virtualIndexSectorLimit`, `signaturePackets512`, `hashSecret`).

Steps of `StreamEncoder.Encode`, in order:

1. **Install the secret scope.** A `SecretScopeGuard` is created: if `hashSecret` is non-empty, all hash computations inside the operation are keyed (§6.1); on dispose the previous scope is restored.
2. **Validate size.** `S ≤ 16,777,215` or fail.
3. **Pack the name** into 14 bytes (§7); fail if not representable.
4. **Hash the content**: `sha256 = SHA-256-Secure(content)` — this becomes H3.
5. **Compute dimensions**: `N`, `M`, `T` (§8); check `T ≤ 65,535`; if the scheme is VirtualIndex, check `T ≤ 512` (no silent downgrade).
6. **Split into payloads**: `N` buffers of 64 B; the last one takes the tail of the file and is **zero-padded** to 64 B. (The true length is H2; the decoder trims the padding after assembly.)
7. **Compute parity**: `Rs.Encode(payloads, M)` → `M` parity buffers of 64 B. This is the heaviest phase; progress reporting maps it to the 10..75 % band of the global scale.
8. **Build the header**: 51 content bytes (H1 name, H2 size, H3 hash, H4 parity count) + `H5 = Trunc24(SHA-256-Secure(H1..H4))` → the 75-byte header packet.
9. **Build the signature packet** (if enabled): `S1 = SHA-512-Secure(content)`, `S2 = Trunc11(SHA-512-Secure(S1))`. Computed inside the secret scope, like everything else.
10. **Build sector packets** for all `T` volumes (data first, parity after), Classic or VirtualIndex per the selected scheme (progress band 75..100 %).
11. **Arrange the stream** — see §10.2.
12. **Wipe intermediates**: the plaintext payload buffers, the parity buffers and the raw header bytes are zeroed (`Array.Clear`) before the method returns. The returned packets themselves are, by design, the transmission.

### 10.1 What the encoder produces

A flat list of 75-byte packets:

```
packetCount = T + H                     (without signatures)
packetCount = T + 2·H                   (with signatures: one per header copy)
```

The caller converts each packet to its 100-character Base64 fragment and joins them according to the output format (line width, framing — see [EncoderGuide](EncoderGuide.en.md)). The decoder is indifferent to that formatting.

### 10.2 Stream layout: `ArrangePackets`

Placement of header copies follows one rule: **the stream starts with a header, ends with a header, and carries evenly spaced copies in between.**

1. Emit `header (+ signature if enabled)`.
2. If `H ≤ 2`: emit all sector packets. (In practice `H ≥ 3` always, because of the minimum; the branch exists for completeness.)
3. Otherwise, with `middle = H − 2` intermediate copies and `interval = max(1, T div (middle + 1))`: walk the sector packets in index order and, after every `interval`-th sector — while intermediate copies remain — emit `header (+ signature)`.
4. Emit the final `header (+ signature)`.

Two consequences worth stating explicitly:

- **Every header copy is followed immediately by one signature copy** (when signatures are on). Header and signature travel as a bound pair, so any contiguous window of the stream that contains one header copy also contains the file's full identity (H2/H3/H4) *and* its SHA-512 proof.
- **Uniform exposure.** With `H ≥ 3` the copies sit at the beginning, the end, and evenly inside the body. A contiguous excerpt of roughly `2·interval + 2` packets taken *anywhere* in the stream is guaranteed to contain at least one header (and signature) copy; the interval arithmetic of step 3 is what guarantees the middle copies cannot all fall outside a long enough window.

## 11. The decoding pipeline

Input: arbitrary text (or a stream read from it) plus options: expected `sectorScheme`, `signatureMode`, optional `hashSecret`, search limits.

### 11.1 Text filter and packet scanner

- The **filter** strips the input down to Base64 material: whitespace, punctuation and stray `=` padding are ignored; the remaining alphabet characters are decoded in a sliding fashion. The decoder never assumes clean input.
- The **scanner** walks a 75-byte window over the decoded byte flow and classifies each window, **in this order**:
  1. **Header?** — check H5 (autonomous, 192 bits). On success, a *reception slot* is created (or found) for this header; the packet's bytes are the slot's identity.
  2. **Signature?** — check S2 (autonomous, 88 bits). On success, S1 (64 bytes) joins the slot's set of collected signatures. Multiple copies of the same signature collapse into one entry — the set holds distinct values, so re-sends and duplicated fragments do not accumulate.
  3. **Sector?** — for every *known* slot (each slot's H5 is the seed), check the sector per the scheme: Classic verifies `Trunc9(SHA-256(H5‖D1‖D2))` against D3 and reads the index from D1; VirtualIndex computes `Trunc11(SHA-256(H5‖payload‖idx))` for candidate indices (§11.3) and accepts the packet at the matching index. A window that passes for some slot is filed into that slot at that index.

  The order matters: autonomous checks come first because they need no context; sector checks are last because they are per-file. A signature packet that also happens to parse as a sector of some file is harmless — it can only be accepted if it passes that file's own hash check, and a genuine signature has no reason to collide with one (and if it did, the final 256-bit check would catch it).

### 11.2 Reception slots

A **slot** is the decoder's state for one header: the header bytes, per-index payload storage, collision bookkeeping, and the signature set. Slots are keyed by the header content, so:

- duplicated header copies land in the same slot;
- **several different files can be received from one stream simultaneously** — each recognized header gets its own slot, and assembly runs per slot;
- each slot tracks how many sector *copies* arrived (for statistics) separately from how many distinct verified *variants* exist per index.

### 11.3 VirtualIndex recovery

For a VirtualIndex slot with `K = T ≤ 512`:

- **Sequential arrival (the common case).** The slot keeps a cursor — the next expected index. For each incoming packet it first tests the tag at the cursor's index: one hash computation, O(1) per packet when the stream arrives in order.
- **Out-of-order or random access.** The tag is tested against candidate indices; the search over `0..K−1` is parallelized. At most `K · 2⁻⁸⁸⁺ˡᵒᵍ₂ᴷ` false acceptance per foreign packet (§5.3).

### 11.4 Assembly stages

When a slot has received material, the decoder attempts to assemble the file. Conceptually the attempts escalate (mechanics and tuning are covered in the [AssemblyGuide](AssemblyGuide.en.md)):

1. **Direct assembly.** Every data index has exactly one verified payload: concatenate in index order, trim to `H2` bytes (removing the zero padding of the last volume), hash, compare with H3.
2. **RS restoration.** Some data volumes are missing, no index is ambiguous, and `missing ≤ usable parity`: rebuild the missing payloads by RS erasure decoding (§9), then proceed as in stage 1.
3. **Version search.** Some indices hold several *distinct* verified payloads apiece (a **collision slot** — the same index reached by packets from damaged/duplicated transmissions, or by an unkeyed forger). Each candidate per index is a *version*; the decoder enumerates combinations of versions. Strategy: exhaustive enumeration while the combination count stays within `MaxExhaustiveCombinations` (default 100,000), then heuristic sampling within `MaxHeuristicAttempts` (default 100,000), all under a wall-clock `TimeBudget` (default 30 s). Combinatorics and step ordering are centralized in a pure-math helper (counting, GCD/LCM interval arithmetic, strategy selection).
4. **Volume subset search.** When collisions persist or too many volumes are missing, the decoder changes the question: *which volumes should be declared erased* so that RS can restore them? A planner enumerates **exclusion masks**:
   - the *base* mask excludes every collision-involved volume — ambiguous data is restored from parity rather than guessed;
   - *level 1* additionally excludes one non-collision volume, choosing by suspicion (less-confirmed volumes first; ties broken by a deterministic pseudo-random order seeded from H5, so runs are reproducible);
   - *levels 2 and deeper* exclude more volumes from a shortlist, up to `MaxExtraExclusionLevel`;
   - a mask is *feasible* when the erased data volumes (missing + excluded) number at most the usable parity and at most `MaxErasedDataVolumes = 32`;
   - masks that would erase no data volume are skipped (nothing to restore).
   Each feasible mask costs one RS attempt (§9, cost note) plus verification; limits: at most 100,000 attempts and 30 s total (`VolumeSubsetSearchOptions`).

Every path funnels into the same **final verification**:

- **Exact(SHA-256):** the assembled content's SHA-256 must equal H3, byte for byte. This is non-negotiable; nothing is ever written out on a mismatch.
- **Any(SHA-512)** (extended check, mode-dependent — see §11.5): the assembled content's SHA-512 must match at least one signature in the slot's set.

The signature snapshot used by these checks is taken once per assembly attempt and threaded through all stages, so every stage verdict is evaluated against the same, stable view of the received signatures.

### 11.5 Signature modes

| Mode | Behavior of the decoder |
|---|---|
| `Base` | Signature packets are detected and collected, but the extended check is **not performed**; the file is accepted on Exact(SHA-256) alone. |
| `Auto` (default) | The extended check is active **if at least one signature packet was received** for the file. A stream that simply carries no signatures decodes normally; a stream that carries signatures must honor them. |
| `Force` | The extended check is active **always**. If no collected signature matches the assembled content — including the case where no signature packet arrived at all — decoding fails with `SignatureVerificationException`, even though SHA-256 passed. |

Reading of the modes: `Base` is compatibility with signature-less traffic; `Auto` follows the evidence in the stream; `Force` is the paranoid setting for channels where the sender's intent to sign must be enforced.

## 12. Receiving several files from one stream

Nothing in the scanner assumes a single file. Each distinct header found in the text produces its own reception slot (§11.2); sectors bind to slots through their H5 seed, signatures through their autonomy; assembly and verification run per slot independently. A pasted concatenation of two transmissions therefore yields both files, each verified against its own H3 (and its own signatures, per mode). The decoder also copes with the degenerate case of one file's fragments scattered through foreign text: noise is simply never classified (§11.1), and missing packets are the RS layer's problem (§9).

## 13. Security properties and boundaries

What the design guarantees, stated plainly:

- **Integrity of output.** A file is written only after Exact(SHA-256) against the header, with the extended SHA-512 check applied per the signature mode. Assembling wrong bytes that pass a 256-bit gate requires finding a second preimage of the (possibly keyed) hash.
- **Unforgeability of packets (with a secret).** With `hashSecret` set, every wire check is keyed. A party without the secret cannot craft header, sector or signature packets that the decoder would accept, short of breaking SHA-256/SHA-512 prefix-keyed hashing. Without a secret, protection is against accidental noise and mixing only.
- **Cross-file isolation.** Sectors are bound to their file's H5; fragments of different files do not verify against each other's slots, and even a deliberate collision attempt must survive the per-packet gate and then the final 256/512-bit gates.
- **No confidentiality.** Stated once more, because it matters: payloads are plaintext inside the packets. Encrypt first if secrecy is required.
- **Hygiene.** The secret lives in an async-local scope for the duration of the operation (not in static mutable state beyond the scope mechanism); the encoder zeroes its plaintext payload buffers, parity buffers and raw header bytes before returning. Signature packets are built inside the secret guard, so the keyed/unkeyed behavior is identical on both ends of the wire.

## 14. Worked examples

### 14.1 A 128-byte file, default settings, signatures on

Parameters: `eccPercent = 10`, `headerPercent = 3`, Classic scheme, `signaturePackets512 = on`.

Arithmetic:

| Step | Computation | Result |
|---|---|---|
| Data volumes | `ceil(128 / 64)` | `N = 2` |
| Parity volumes | `max(1, round(2 · 10 / 100)) = max(1, 0)` | `M = 1` |
| Total volumes | `2 + 1` | `T = 3` |
| Header copies | `max(3, round(3 · 3 / 100)) = max(3, 0)` | `H = 3` |
| Interval | `max(1, 3 div (1 + 1)) = max(1, 1)` | `1` |
| Packets | `T + 2·H = 3 + 6` | `9` |

The emitted stream, in order (`Hdr` = header+signature pair spelled out):

| # | Packet |
|---|---|
| 1 | Header |
| 2 | Signature |
| 3 | Sector 0 (data, file bytes 0..63) |
| 4 | Header |
| 5 | Signature |
| 6 | Sector 1 (data, file bytes 64..127) |
| 7 | Sector 2 (parity) |
| 8 | Header |
| 9 | Signature |

Wire cost: `9 · 75 = 675` binary bytes, `9 · 100 = 900` Base64 characters for 128 bytes of payload — an expansion of ≈ 5.3× in binary terms (≈ 7.0× counting characters). Small files are dominated by the fixed metadata: three header copies and three signature copies outweigh the two payload packets. That is the price of the begin/middle/end identification rule, and it fades quickly as files grow (next example).

Damage tolerance of this stream: any 3 packets can be lost outright and the file still assembles directly or via RS (one parity volume restores any one missing data volume); even with both data volumes lost, the parity alone is not enough (`2 > 1`), but *any* single surviving data sector plus the parity completes the file.

### 14.2 A 1 MiB file, default settings

`S = 1,048,576` B, `eccPercent = 10`, `headerPercent = 3`.

| Step | Computation | Result |
|---|---|---|
| Data volumes | `1,048,576 / 64` | `N = 16,384` |
| Parity volumes | `round(16,384 · 10 / 100)` | `M = 1,638` |
| Total volumes | | `T = 18,022` |
| Header copies | `round(18,022 · 3 / 100)` | `H = 541` |
| Packets with signatures | `18,022 + 2 · 541` | `19,104` |
| Wire text | `19,104 · 100` | `1,910,400` characters ≈ 1.91 MB |

Expansion ≈ **1.82×** in characters (1.37× in binary bytes). Breakdown: payload packing alone costs 100 characters per 64 bytes (1.56×); 10 % parity brings it to ≈ 1.72×; headers and signatures add ≈ 0.10×. The stream tolerates the loss of any 1,638 payload packets (≈ 8.6 % of the stream) with direct RS restoration, plus whatever the search stages recover from heavier damage.

### 14.3 The ceiling

The volume cap (§8) makes ≈ 4.0 MiB the largest encodable file (65,535 data volumes, zero parity — `eccPercent = 0`). Larger inputs are rejected with an explicit error, not truncated.

## 15. Parameter reference

### 15.1 Encoder (defaults in bold)

| Parameter | Default | Meaning |
|---|---|---|
| `eccPercent` | **10** | parity as a percentage of `N`; 0 disables RS entirely |
| `headerPercent` | **3** | header copies as a percentage of `T`; minimum 3 copies regardless |
| `sectorScheme` | **Classic** | `Classic` or `VirtualIndex` |
| `virtualIndexSectorLimit` | **512** (`VirtualIndexHasher.DefaultSectorLimit`) | max `T` for the VirtualIndex scheme; larger files are refused |
| `signaturePackets512` | **off** | emit a signature packet after every header copy |
| `hashSecret` | **none** | keyed hashing (§6); must match on decode |

### 15.2 Decoder search limits (defaults)

| Option | Default | Stage | Meaning |
|---|---|---|---|
| `MaxExhaustiveCombinations` | 100,000 | version search | cap on full enumeration of version combinations |
| `MaxHeuristicAttempts` | 100,000 | version search | cap on sampled combinations after enumeration is abandoned |
| `TimeBudget` | 30 s | version search | wall-clock budget for the stage |
| `MaxAttempts` | 100,000 | subset search | cap on exclusion-mask attempts |
| `TimeBudget` | 30 s | subset search | wall-clock budget for the stage |
| `MaxErasedDataVolumes` | 32 | subset search | cap `E` per attempt; calibrated so the worst legal case (`T = 65,535`) still allows dozens of attempts in budget |
| `SignatureMode` | `Auto` | final check | `Base` / `Auto` / `Force` (§11.5) |

### 15.3 Format constants

| Constant | Value |
|---|---|
| Packet size | 75 B |
| Base64 size | 100 chars, no padding |
| Payload per packet | 64 B |
| Header content / hash | 51 B / 24 B |
| Sector index (Classic) / hash | 2 B / 9 B |
| VirtualIndex tag | 11 B |
| Signature / tag | 64 B / 11 B |
| File name field | 14 B ASCII |
| Max file size field | 16,777,215 B |
| Max total volumes | 65,535 |
| VirtualIndex limit | 512 volumes |

## 16. Glossary

| Term | Meaning |
|---|---|
| Packet | The 75-byte atomic transmission unit; 100 Base64 characters on the wire. |
| Volume | One 64-byte slot of the RS codeword: data volumes carry file bytes, parity volumes carry RS redundancy. Carried 1:1 by sector packets; "volume index" = "sector number". |
| Sector packet | The packet kind that carries one volume. |
| Header | The packet kind carrying file identity: name, size, SHA-256, parity count, and its own 192-bit hash H5. |
| Signature packet | Optional packet kind carrying SHA-512 of the content (S1) with an autonomous 88-bit tag (S2). |
| Classic scheme | Sector binding by explicit 2-byte index + 9-byte H5-seeded hash. |
| VirtualIndex scheme | Sector binding by an 11-byte tag that includes the index as a hash input; index recovered by the receiver; limited to 512 volumes. |
| Truncation (`TruncN`) | First N bytes of a digest, used to fit checks into the fixed packet size. |
| Secret scope | The async-local setting that keys all Secure hash computations for one operation. |
| Erasure | A volume known to be missing (its packet absent or failed verification) — the input condition of RS decoding. |
| Collision slot | An index for which several distinct verified payloads have been received; resolved by version search or exclusion. |
| Version | One candidate payload for one index in a collision slot. |
| Exclusion mask | A declared set of volumes to treat as erased so that RS restores them; enumerated by the subset planner. |
| Reception slot | Decoder-side state for one header: payloads, collisions, signatures, assembly. |
| Exact(SHA-256) / Any(SHA-512) | The final gates: content hash must equal H3 exactly; content SHA-512 must match at least one collected signature (mode-dependent). |
| `H1..H5`, `D1..D3`, `S1..S2` | Field names inside header, sector and signature packets (§5). |

## 17. Document map

| Document | Content |
|---|---|
| [AlgorithmGuide](AlgorithmGuide.en.md) (this file) | Wire format and algorithms. |
| [EncoderGuide](EncoderGuide.en.md) | Using the encoder: input modes, output text formats, statistics, reports. |
| [DecoderGuide](DecoderGuide.en.md) | Using the decoder: scanning noisy text, multi-file reception, reports, exit conditions. |
| [AssemblyGuide](AssemblyGuide.en.md) | Deep dive into the assembly stages: version search, subset planning, limits and tuning. |
| [CollisionBench](CollisionBench.en.md) | Benchmark methodology and measured resilience under damage patterns. |
| [DeveloperGuide](DeveloperGuide.en.md) | Project layout, build, test suite, extension points. |
| [UserGuide](UserGuide.en.md) | End-to-end usage from the console application and the GUI. |

Russian mirrors of every document carry the `ru` suffix (`AlgorithmGuide.ru.md`, …).
