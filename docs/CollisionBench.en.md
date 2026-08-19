# DataShield Collision Bench — Engineering Deep-Dive

A deep-dive into the `DataShield.CollisionBench` console harness: the experiment's intent, the probabilistic model, the measurement methodology, fresh results, and the path from conclusions to a production feature — the opt-in virtual-index sector scheme (`SectorScheme.VirtualIndex`). Packet format context lives in `AlgorithmGuide.en.md` (sections 2 and 7); scheme B in production is covered by the `EncoderGuide.*` and `DecoderGuide.*` families; the project map is `DeveloperGuide.en.md`.

## 1. The question

The DataShield format packs every 64-byte volume into a 75-byte packet: 2 bytes of sector number, 64 bytes of payload, 9 bytes of truncated SHA-256. The experiment asks: what if the packet **does not carry the sector number at all**, and the freed 2 bytes go to the hash (truncation 9 → 11 bytes)? The number becomes "virtual": it exists only inside the hash input, and the receiver recovers it by brute force.

The hypothesized win: a hash 2 bytes longer — 16 bits more resistance to false accepts. The hypothesized cost: recognizing a sector requires trying up to K numbers (K = N+M, the file's full volume set), i.e. up to K SHA-256 hashes per packet.

The bench answers four questions by measurement, not intuition:

1. Does the observed false-accept rate agree with the model (A: `2^-8t`, B: `K·2^-8(t+2)`)?
2. Is extrapolating that model to the production truncations of 9/11 bytes sound?
3. What does B cost to receive, in hashes, packets, and seconds — single-threaded and on all cores?
4. What side properties come with it (index substitution, ambiguous accepts, loss of order control)?

Scheme A is the current production scheme; scheme B is the experimental one. The experiment is complete; the outcome is an opt-in deployment of B for files with N+M ≤ 512 (section 8). The bench is kept as the reference implementation and the reproduction tool.

## 2. Schemes A and B

The hash input is identical in both schemes — 90 bytes: `H5(24) ‖ idxLE(2) ‖ payload(64)`, where H5 is Trunc24(SHA-256(H1–H4)) of the owning header. Only the layout of the 75 packet bytes differs.

### 2.1. Scheme A (current, production)

| Offset | Size | Field                              |
| ------:| ----:| ---------------------------------- |
| 0       | 2    | D1 = SeqNum (LE)                   |
| 2       | 64   | D2 = payload                       |
| 66      | 9    | D3 = Trunc9(SHA-256(H5‖D1‖D2))     |

Reception: the number is read from D1; verification costs exactly **1 hash** per packet. The sector is self-contained: it knows its number, and tampering with the number breaks the hash.

### 2.2. Scheme B (experimental; VirtualIndex in production)

| Offset | Size | Field                                   |
| ------:| ----:| --------------------------------------- |
| 0       | 64   | payload                                 |
| 64      | 11   | Trunc11(SHA-256(H5‖idxLE‖payload))      |

The index is **not stored**. Reception enumerates all K indices of the full set (data + ECC); the sector is accepted if at least one index matches. The bench reference caches K prefixes `H5‖idxLE` of 26 bytes each (`SectorSeedCache`), so one attempt = copy prefix + payload + SHA-256:

- valid sector — K/2 hashes on average (search with early exit; the true index is uniform);
- foreign/noise packet — all K hashes.

> **Note.** The production implementation (`VirtualIndexHasher`) uses a different input order — `H5‖payload‖idxLE`, index at the tail: no long prefix copying is needed, and no seed cache exists in production at all. Strength does not depend on the input order (section 8).

### 2.3. Side properties of B

- The sector does not know its number: the position is recovered as the first matching index; range and order control moves to the receiver.
- Swapping two valid sectors is **not detected** by reception: each remains valid under its own index. Scheme A is no stronger here — DataShield reception is order-agnostic by design; the arbiter is the file's SHA-256 (H3) plus RS recovery.
- Ambiguity is possible — several indices with a matching hash. A policy is required: production takes the first in scan order; the probability is ≈ `(K−1)·2⁻⁸⁸`.

## 3. The model

Probability of falsely accepting a "bad" packet (noise, foreign file, corrupted sector):

- **A:** `P = 2^-8t`; at t = 9 → `2^-72` (72 bits);
- **B:** for a single index `p = 2^-8(t+2)`; at least one of K: `P = 1−(1−p)^K ≈ K·p` for `K·p ≪ 1`; at t = 9 → `K·2^-88`.

Effective strength: A = `8t` bits; **B = `8(t+2) − log2(K)` bits**. Equal byte budget: A = 66+t bytes, B = 64+(t+2) = 66+t bytes — the comparison is fair. The gain of B is `16 − log2(K)` bits; parity at K = 65,536.

Technical details:

- for `K·p < 1e-9` the formula `1−(1−p)^K` degenerates in double (`(1−p)` rounds to 1) — the direct product `K·p` is used;
- the confidence interval for the rate is Wilson 95%;
- the per-cell verdict is the z-score `(observed − expected)/σ`, `σ = sqrt(p(1−p)/n)`; the threshold is **|z| ≤ 4**: across a dozen and a half cells a 95% threshold legitimately produces 1–2 false "mismatches" (multiple comparisons).

## 4. Methodology

Four modes: `selftest` (implementation correctness) → `collide` (model check at reduced truncations) → `full` (tail behavior at full truncations) → `perf` (reception cost).

### 4.1. selftest

Bit-exact equivalence of the A replica and the production `PacketHasher` class at t = 9: building, acceptance, identical verdicts on corrupted packets, rejection of a foreign H5. Correctness of B: exactly the true index is found; payload/hash corruption and a foreign H5 are rejected at every index; A and B hash inputs coincide. Seed-cache bounds. Scenario mechanics firing at in-model rates. About 14,400 checks in total.

### 4.2. collide

The real `2^-72 / K·2^-88` is unreachable by direct sampling, so the empirical check runs at reduced truncations with the same byte budget: A = Trunc t, B = Trunc (t+2). Default t = 1: one B event costs ≈ `2^(8·(t+2))` hashes (t = 1 → 2²⁴); t > 1 is exponentially expensive for B — the deep tail is additionally controlled by the full mode.

Scenarios for producing a "bad" packet:

| Scenario    | Input                                                              | Hashes per attempt |
| ----------- | ------------------------------------------------------------------ | ------------------ |
| noise       | random bytes                                                       | A: 1; B: K         |
| foreign file| a valid sector of a foreign H5                                     | A: 2; B: 1+K       |
| corruption  | own sector with 1..4 bit flips at unique positions                 | A: 2; B: 1+K       |

Cells: A × 3 scenarios; B × 3 scenarios × K ∈ {64, 128, 256, 512} (default). Cell stop: ≥ 200 events (with overshoot of up to one batch per worker) or 30 s. Batch size: A — 65,536 attempts; B — `clamp(2,000,000/K, 1, 65,536)` — so that worker reports arrive no rarer than ~50 ms given a per-attempt cost of K hashes. Workers (one per core by default) run on seeded PRNGs: cell seed = `Seed ^ (hB ≪ 24) ^ (K·0x9E37)`, worker seed mixes in the worker number; runs are reproducible by seed.

For B the campaign uses the **full scan** `VerifyAll` (all K indices, all matches counted) — beyond the accept rate this yields two specificity counters: index substitution (first match ≠ true index) and ambiguous accepts (several indices at once).

### 4.3. full

At full truncations (A: 9 bytes = 72 bits; B: 11 bytes = 88 bits at K = 1024) the events themselves are not awaited — two properties are checked instead:

- **zero full matches**: A — 200 million attempts (1 target each), B — 2 million attempts × 1024 targets = 2.1 billion targets;
- **spectrum**: for each noise attempt, the maximum number of matching leading bits M across all targets; model `P(M ≥ m) = targets·2^-m`. Agreement of the spectrum with the model down to the reachable depth `log2(targets)` confirms hash uniformity and the soundness of tail extrapolation.

Counters < 5 in the deep tail are Poisson noise; verified by re-running with seed 777: deviation directions change, no systematic bias. The mode budget is 240 s.

### 4.4. perf

Production truncations (A = 9, B = 11 bytes). Cells: "A valid" (1 hash), "B valid" (search with early exit — the true index is uniform, (K+1)/2 hashes on average), "B foreign" (full scan of K). Threads: 1 and all cores. K ∈ {100, 1,000, 10,000, 65,535}. Sample — 4,096 packets, cycled; warm-up 0.3 s (same JIT path); time checked at least every 16 packets (B) / 4,096 (A). Both packets/s and hashes/s are counted — which makes it visible that throughput tracks the hash count, not the packet count. For B a seed cache (K × 26 bytes) is built, with build time logged.

## 5. Implementation

A net10.0 console application. The single external dependency is the production build of `DataShield.Codec.Packets` (format constants, `Sha256Compact`, `PacketHasher`). No production code was modified; the A replica's correctness is proven by bit-exact equivalence with `PacketHasher` in selftest.

| File                    | Purpose                                                            |
| ----------------------- | ------------------------------------------------------------------ |
| `Program.cs`            | modes, tables, verdicts, banner                                    |
| `BenchOptions.cs`       | command-line parsing, help                                         |
| `CollisionMath.cs`      | model (P_A, P_B), Wilson interval, z-score, formatting             |
| `ClassicScheme.cs`      | parameterizable replica of scheme A (t = 9 — bit-exact production) |
| `ExperimentalScheme.cs` | scheme B: building, full scan VerifyAll, early-exit TryVerify      |
| `SectorSeedCache.cs`    | cache of K `H5‖idxLE` prefixes, 26 bytes each                      |
| `ScenarioRunners.cs`    | campaign engine (batches, stop conditions, seeded PRNGs) + scenarios |
| `Spectrum.cs`           | histogram of maximum leading-bit matches                           |
| `PerfBench.cs`          | reception cost measurements: 1 and N threads, hash counters        |
| `SelfTest.cs`           | bench self-check                                                   |

## 6. Results

Machine: 24 logical cores; Release build; seed 20260906. Absolute numbers depend on the CPU; relative ones do not.

### 6.1. selftest — all groups PASS (14,413 checks)

- the A replica at t = 9 is bit-exact with `PacketHasher` — 8,000 checks;
- scheme B: build and receive with the seed cache — 6,205 checks;
- the seed cache (`H5‖idxLE` prefix, K bounds) — 203 checks;
- scenario mechanics fires at in-model rates — 5 checks.

### 6.2. collide — all 15 cells "MODEL OK"

| Sch | Scenario    | hB | K    | Attempts  | Accepts | Rate     | Expected | Ratio | z    | Verdict    |
| ---- | ----------- | -- | ---- | --------- | ------- | -------- | -------- | ----- | ---- | ---------- |
| A    | noise       | 1  | —    | 1,572,864 | 6,110   | 3.88E-03 | 3.91E-03 | 0.99  | −0.4 | MODEL OK   |
| A    | foreign file| 1  | —    | 1,572,864 | 5,987   | 3.81E-03 | 3.91E-03 | 0.97  | −2.0 | MODEL OK   |
| A    | corruption  | 1  | —    | 1,572,864 | 6,165   | 3.92E-03 | 3.91E-03 | 1.00  | +0.3 | MODEL OK   |
| B    | noise       | 3  | 64   | 11,250,000| 36      | 3.20E-06 | 3.81E-06 | 0.84  | −1.1 | MODEL OK   |
| B    | foreign file| 3  | 64   | 10,968,750| 47      | 4.28E-06 | 3.81E-06 | 1.12  | +0.8 | MODEL OK   |
| B    | corruption  | 3  | 64   | 11,156,250| 48      | 4.30E-06 | 3.81E-06 | 1.13  | +0.8 | MODEL OK   |
| B    | noise       | 3  | 128  | 5,640,625 | 33      | 5.85E-06 | 7.63E-06 | 0.77  | −1.5 | MODEL OK   |
| B    | foreign file| 3  | 128  | 5,578,125 | 48      | 8.61E-06 | 7.63E-06 | 1.13  | +0.8 | MODEL OK   |
| B    | corruption  | 3  | 128  | 5,609,375 | 36      | 6.42E-06 | 7.63E-06 | 0.84  | −1.0 | MODEL OK   |
| B    | noise       | 3  | 256  | 2,820,132 | 57      | 2.02E-05 | 1.53E-05 | 1.32  | +2.1 | MODEL OK   |
| B    | foreign file| 3  | 256  | 2,820,132 | 49      | 1.74E-05 | 1.53E-05 | 1.14  | +0.9 | MODEL OK   |
| B    | corruption  | 3  | 256  | 2,804,508 | 41      | 1.46E-05 | 1.53E-05 | 0.96  | −0.3 | MODEL OK   |
| B    | noise       | 3  | 512  | 1,413,972 | 54      | 3.82E-05 | 3.05E-05 | 1.25  | +1.7 | MODEL OK   |
| B    | foreign file| 3  | 512  | 1,406,160 | 41      | 2.92E-05 | 3.05E-05 | 0.96  | −0.3 | MODEL OK   |
| B    | corruption  | 3  | 512  | 1,410,066 | 40      | 2.84E-05 | 3.05E-05 | 0.93  | −0.5 | MODEL OK   |

- A: observed-to-model ratio 0.97–1.00; B: 0.77–1.32; observed |z| ≤ 2.1 against the threshold of 4. (B cells hit the 30 s budget; A hit the 200-event target within the workers' first batch — hence the identical 1,572,864 attempts.)
- **Index substitution**: 165 of 165 accepts in the "corruption" scenario had the first matching index ≠ the true one. By the model this is ≈(K−1)/K of all accepts: a corrupted packet almost always slips through on a foreign index.
- **Ambiguous accepts** (several indices at once): 0 — negligible expectation at bench truncations.

Calibrating how to read one row: B/noise, K = 512 → 54 accepts in 1,413,972 attempts = 3.82E-05 against the model 512·2⁻²⁴ = 3.05E-05, z = +1.7 — within threshold.

### 6.3. Extrapolation to production truncations

A = Trunc9 = 72 bits always; B = Trunc11:

| K     | B, bits | Gain of B      |
| -----:| -------:| --------------:|
| 64    | 2^-82.0 | +10.0 bits     |
| 128   | 2^-81.0 | +9.0 bits      |
| 256   | 2^-80.0 | +8.0 bits      |
| 512   | 2^-79.0 | +7.0 bits      |
| 1,024 | 2^-78.0 | +6.0 bits      |
| 2,048 | 2^-77.0 | +5.0 bits      |
| 4,096 | 2^-76.0 | +4.0 bits      |
| 8,192 | 2^-75.0 | +3.0 bits      |
| 16,384| 2^-74.0 | +2.0 bits      |
| 32,768| 2^-73.0 | +1.0 bits      |
| 65,535| 2^-72.0 | 0.0 (parity)   |

The same dependence by K deciles of the maximum (value at the decile's upper bound):

| Decile   | K     | Gain, bits |
| -------- | -----:| ----------:|
| 90–100%  | 65536 | 0.00       |
| 80–90%   | 58982 | 0.15       |
| 70–80%   | 52429 | 0.32       |
| 60–70%   | 45875 | 0.51       |
| 50–60%   | 39322 | 0.74       |
| 40–50%   | 32768 | 1.00       |
| 30–40%   | 26214 | 1.32       |
| 20–30%   | 19661 | 1.74       |
| 10–20%   | 13107 | 2.32       |
| 0–10%    | 6554  | 3.32       |

The dependence is logarithmic: near the top of the scale a −10% decile is worth ~0.15 bits; +1 bit only below half the maximum; +3.32 bits below 10%.

### 6.4. full — zero misses, spectra match the model

Run with defaults (`-fa 200M`, `-fb 2M`, `-full-k 1024`, budget 240 s):

| Scheme              | Attempts         | Targets | Time  | Full matches | Max bits | Model log2(targets) |
| ------------------- | ----------------:| -------:| -----:| ------------:| --------:| -------------------:|
| A (Trunc9)          | 200.8 million    | 2.0E+08 | 11.6 s| **0**        | 26       | 27.6                |
| B (Trunc11, K=1024) | 2.03 million × 1024 | 2.1E+09 | 91.8 s | **0**     | 31       | 31.0                |

### 6.5. perf — reception cost

Seed-cache build: K = 100 → 0.2 ms; 1,000 → 0.1 ms; 10,000 → 0.2 MB in 0.9 ms; 65,535 → 1.6 MB in 0.3 ms.

| K     | Case     | Threads | Packets/s | Hashes/s  | µs/packet | ×A      |
| -----:| -------- | -------:| ---------:| ---------:| ---------:| -------:|
| 100   | A valid  | 1       | 2,131,882 | 2,131,882 | 0.47      | 1.0x    |
| 100   | B valid  | 1       | 43,081    | 2,148,591 | 23.21     | 49.5x   |
| 100   | B foreign| 1       | 21,466    | 2,146,651 | 46.58     | 99.3x   |
| 100   | A valid  | 24      | 23,139,852| 23,139,852| 0.04      | 1.0x    |
| 100   | B valid  | 24      | 467,770   | 23,326,112| 2.14      | 49.5x   |
| 100   | B foreign| 24      | 232,641   | 23,264,182| 4.30      | 99.5x   |
| 1,000 | A valid  | 1       | 2,134,653 | 2,134,653 | 0.47      | 1.0x    |
| 1,000 | B valid  | 1       | 4,318     | 2,150,547 | 231.57    | 494.3x  |
| 1,000 | B foreign| 1       | 2,151     | 2,151,110 | 464.88    | 992.3x  |
| 1,000 | A valid  | 24      | 23,029,605| 23,029,605| 0.04      | 1.0x    |
| 1,000 | B valid  | 24      | 46,850    | 23,332,746| 21.34     | 491.6x  |
| 1,000 | B foreign| 24      | 23,362    | 23,362,484| 42.80     | 985.8x  |
| 10,000| A valid  | 1       | 2,122,515 | 2,122,515 | 0.47      | 1.0x    |
| 10,000| B valid  | 1       | 429       | 2,145,537 | 2328.95   | 4943.2x |
| 10,000| B foreign| 1       | 214       | 2,148,285 | 4654.87   | 9880.0x |
| 10,000| A valid  | 24      | 23,050,883| 23,050,883| 0.04      | 1.0x    |
| 10,000| B valid  | 24      | 4,580     | 23,176,920| 218.31    | 5032.2x |
| 10,000| B foreign| 24      | 2,322     | 23,222,986| 430.61    | 9925.9x |
| 65,535| A valid  | 1       | 2,135,717 | 2,135,717 | 0.47      | 1.0x    |
| 65,535| B valid  | 1       | 60        | 2,150,914 | 16636.46  | 35530.8x|
| 65,535| B foreign| 1       | 32        | 2,152,557 | 30445.18  | 65022.3x|
| 65,535| A valid  | 24      | 23,096,314| 23,096,314| 0.04      | 1.0x    |
| 65,535| B valid  | 24      | 644       | 22,894,286| 1551.99   | 35845.2x|
| 65,535| B foreign| 24      | 352       | 23,097,012| 2837.38   | 65533.0x|

Observations:

- **One hash costs ≈ 0.47 µs** (SHA-256 over 90 bytes): A sustains 2.13 million hashes/s per thread; 24 threads — 23.1 million (10.9× scaling).
- B is bound by **hashes**, not packets: 2.15 million hashes/s per thread at any K. A valid packet costs (K+1)/2 hashes on average (measured: 49.9 at K = 100); a foreign one exactly K (measured: 100.0).
- Worst measured case: a foreign packet at K = 65,535 — 30.4 ms/packet on 1 thread, 2.84 ms on 24; A is always 0.47 µs.
- The production range K ≤ 512 is not an explicit row, but interpolates linearly in hashes: a foreign packet at K = 512 ≈ 512 × 0.465 µs ≈ **0.24 ms** per thread (the production search divides that by roughly the core count — section 8).
- The reference seed cache: K × 26 bytes per file (65,535 → 1.6 MB), built in under a millisecond — memory, not time.

## 7. Conclusions

1. Both schemes track the model exactly in every cell (`2^-8t` and `K·2^-8(t+2)`); the full-mode spectra confirm hash uniformity to full depth — the formulas can be trusted without corrections.
2. The gain of B is `16 − log2(K)` bits. It is real only at small K: +10 bits at K = 64, +7 at K = 512, +4 at K = 4096, parity at 65,535.
3. The cost: up to K hashes per packet (4–5 orders of magnitude worse than A in the worst measured case), K × 26 bytes of per-file memory in the reference, loss of the self-contained sector number (range/order control moves to the receiver), an ambiguous-accept policy.
4. As a **universal replacement** for A, scheme B is a dead end: reception cost grows linearly in K while the gain decays logarithmically. As an **option for small files** it is deployed to production (section 8).

## 8. Scheme B in production: SectorScheme.VirtualIndex

The experiment's outcome shipped not as a replacement but as an optional mode for files with few volumes. The original objections were resolved by the decision "the scheme is set on the encoder/decoder, not in the packet":

1. **Nothing to signal the scheme with** — the scheme is not written into the stream at all: it is a per-side setting. No packet format change is required.
2. **Auto-detection and a weak link** — the hybrid "A first, then B" acceptance at the slot level does not weaken A-files: their packets are still checked by A only (2⁻⁷² per packet); the B scan runs only under `SectorScheme.VirtualIndex` and N+M ≤ the limit. One decoder reads files of both schemes simultaneously.
3. **Reception without reading D1** — all three touchpoints were reworked: building (`FileEncoder.BuildVirtualIndexSector`), classification (`StreamProcessor` — A check, then a virtual-index scan from the arrival cursor), rebinding of late headers (`FileDecoder.RebindWindow` — the same pair of checks).
4. **Cache and memory** — the production variant needs no seed cache: the hash input is reordered with the index at the tail.

**Production specification** (differs from the bench reference by the hash input order; strength is equivalent):

```
packet (75 bytes):  payload(64) ‖ Trunc11(SHA-256( H5(24) ‖ payload(64) ‖ idxLE(2) ))
```

- The types `SectorScheme`/`VirtualIndexHasher` live in `DataShield.Codec.Packets`. The applicability ceiling `VirtualIndexHasher.DefaultSectorLimit = 512` volumes (N+M) is both the default value of the `virtualIndexSectorLimit` parameter and the hard bound of its range (1..512) in the `FileEncoder`/`FileDecoder`/`StreamProcessor` constructors. At K = 512 — 79 bits versus 72 for A.
- Under `VirtualIndex` with N+M above the limit the encoder throws `InvalidOperationException` (no silent downgrade to A); the decoder simply does not try B for slots above the limit.
- The arrival cursor on `ReceptionSlot`: the scan starts at the expected next index (cursor → K−1 → 0 → cursor−1, until the first Trunc11 match). For a sequential stream this is O(1) hashes per packet; for a shuffled or foreign one, up to K.
- The scan is two-phase: the first 64 steps sequentially (the O(1) path does not pay for parallelism); the remainder with ≥ 256 candidates — in parallel in blocks of 128 steps (`Parallel.For`, ThreadPool). The match with the lowest step number wins — the result is identical to the sequential scan (determinism). The worst case (a foreign packet, K = 512 ≈ 0.24 ms per thread) is divided by roughly the core count.
- GUI: the checkbox "Virtual-index sector scheme (up to 512 volumes)", off by default; it affects both encoding and reception.
- Ambiguous accepts: the first in scan order wins; the probability is `(K−1)·2⁻⁸⁸ ≈ 2·10⁻²⁴` at K = 512.
- Swapping whole packets between stream positions is not detected by scheme B — nor by scheme A; the final arbiter is the content SHA-256 (H3) plus RS recovery.

Production details: encoder parameters — the `EncoderGuide.*` family; hybrid reception, the cursor, and rebinding — the `DecoderGuide.*` family; the format — `AlgorithmGuide.en.md`; types and tests — `DeveloperGuide.en.md`.

## 9. Running

```
dotnet build DataShield.CollisionBench -c Release
dotnet run --project DataShield.CollisionBench -c Release --no-build -- <mode> [keys]
```

Modes: `selftest` | `collide` (default) | `full` | `perf` | `all`.

| Key                | Default              | Purpose                                |
| ------------------ | -------------------- | -------------------------------------- |
| `-t list`          | 1                    | A truncation in bytes (B = t+2), 1..7  |
| `-k list`          | 64,128,256,512       | set sizes K for collide                |
| `-events N`        | 200                  | target events per collide cell         |
| `-budget sec`      | 30                   | time budget per collide cell           |
| `-threads N`       | all cores            | thread count                           |
| `-seed N`          | 20260906             | PRNG seed (reproducibility)            |
| `-perf-k list`     | 100,1000,10000,65535 | K values in perf                       |
| `-perf-sec sec`    | 2.5                  | duration of a perf cell                |
| `-fa N`            | 200,000,000          | A attempts in full                     |
| `-fb N`            | 2,000,000            | B attempts in full                     |
| `-full-k N`        | 1024                 | K in full                              |
| `-full-budget sec` | 240                  | time budget of full                    |

Lists are comma-separated: `-k 64,1024`. Approximate duration on 24 cores: selftest — seconds; collide — ~6 minutes (B cells each hit the 30 s budget); full — ~2–4 minutes; perf — ~2 minutes; `all` — ~10 minutes. Examples: `-k 512 -events 400` (a single K point with doubled statistics), `-t 2 -k 64` (deeper truncation at a small K), `-perf-k 512 -threads 8` (measuring the production range).

## 10. Limits of the guarantees

- **Confirmed by measurement**: the rate model at reduced truncations (15 cells, observed |z| ≤ 2.1 against the threshold of 4); zero misses and spectra down to log2(targets) depth at full truncations; reception cost at four K values and two thread levels, including hash counters.
- **Not measured**: t > 1 for scheme B in collide (an event costs `2^(8(t+2))` — the tail is covered by the full mode's spectrum); the behavior of the production `VirtualIndexHasher` itself (different input order, cursor-based parallel search — its correctness is backed by the production solution's unit tests, not by this bench).
- The bench is about hashes and sector reception cost; RS recovery, file assembly, and multi-file streams are not involved here.
