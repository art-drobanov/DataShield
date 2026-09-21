# DataShield Alpha-XVI — Collision Bench Guide

Reference for the collision-resistance benchmark: methodology, modes, statistics and the production conclusions drawn from it.

| | |
|---|---|
| Project | DataShield Alpha-XVI |
| Document | `docs/CollisionBench.en.md` (English); Russian mirror: `docs/CollisionBench.ru.md` |
| Audience | Engineers validating or re-running the security measurements; reviewers of the sector-scheme design; anyone auditing the "why is VirtualIndex worth its cost" argument |
| Scope | The `DataShield.CollisionBench` console application: the two schemes under test, the false-accept model, the four run modes (`selftest`, `collide`, `full`, `perf`), the statistics used for verdicts, command-line reference, reproducibility |
| Out of scope | The wire format itself ([AlgorithmGuide](AlgorithmGuide.en.md)), assembly stages ([AssemblyGuide](AssemblyGuide.en.md)), everyday encoding/decoding ([EncoderGuide](EncoderGuide.en.md), [DecoderGuide](DecoderGuide.en.md)), project layout ([DeveloperGuide](DeveloperGuide.en.md)) |

Reference implementation: the `DataShield.CollisionBench` project (`Program.cs`, `BenchOptions.cs`, `ClassicScheme.cs`, `ExperimentalScheme.cs`, `SectorSeedCache.cs`, `ScenarioRunners.cs`, `Spectrum.cs`, `PerfBench.cs`, `SelfTest.cs`, `CollisionMath.cs`), targeting `net10.0`, referencing `DataShield.Codec.Packets` and `DataShield.Interfaces`.

## 1. Conventions used in this document

- Scheme **A** is the Classic sector scheme; scheme **B** is the VirtualIndex scheme (its in-repo codename during the design study — the code still says "experimental").
- `t` — the truncated hash length of scheme A in **bytes**, parameterizable in the bench (1..7); scheme B always uses `t + 2` bytes, so both sectors occupy the same `66 + t` bytes. Production values: `t = 9` for A (`Trunc9`), 11 bytes for B (`Trunc11`).
- `K` — the size of the full index set (data + ECC volumes), 1..65,535.
- A **trial** is one offered "bad" packet; an **accept** (false accept, event) is a bad packet that passes verification.
- `P` — modeled false-accept probability per trial; `2⁻ⁿ` — the chance of a random value passing an n-bit check.
- Statistics: `z` — the standardized deviation of an observed rate from the model; CI — confidence interval.
- Scenario names appear in the console in Russian: «шум» (noise), «чужой файл» (foreign file), «порча» (corruption).

## 2. What the bench is for

The packet format gives every sector packet a truncated, H5-seeded hash check: Classic stores a 2-byte index plus `Trunc9` (72 bits) and verifies with **one** hash; VirtualIndex drops the stored index, spends the freed 2 bytes on the hash (`Trunc11`, 88 bits) and recovers the index by **trying up to K indexes**. The bench exists to answer three questions with measurements instead of hand-waving:

1. **Does the analytic false-accept model hold?** Model: A accepts noise with `P = 2^-8t`; B accepts with `P = 1 − (1 − 2^-8h)^K ≈ K·2^-8h`, `h = t + 2`. The `collide` mode measures observed rates against the model across a grid of truncations and set sizes.
2. **Is the far tail (production truncations 72/88 bits) behaving as extrapolated?** Direct measurement of `2^-72` events is infeasible; the `full` mode instead verifies that the *reachable* part of the leading-bit match spectrum agrees with the uniform-hash model and that zero full matches occur — which validates the extrapolation.
3. **What does B cost at reception?** A always costs 1 hash; B costs up to K hashes per packet. The `perf` mode measures packets/second and hashes/second for the valid and worst cases.

The bench is a standalone console application (`DataShield.CollisionBench`), versioned with the product (v3.0 at the time of writing). It shares the production hashing primitive (`Sha256Compact` via `DataShield.Codec.Packets`), and its scheme-A replica is proven bit-identical to the production `PacketHasher` by the selftest — so its measurements are measurements of the shipped design, not of a sketch.

## 3. The two schemes under test

| Property | A — Classic (replica) | B — VirtualIndex (replica) |
|---|---|---|
| Sector layout | `idxLE(2) ‖ payload(64) ‖ Trunc_t` | `payload(64) ‖ Trunc_(t+2)` |
| Size at truncation `t` | `66 + t` bytes | `64 + (t + 2) = 66 + t` bytes |
| Hash input | `SHA-256(H5 ‖ idxLE ‖ payload)` — 90 bytes | identical 90 bytes (the index is *virtual*: it lives only in the hash input) |
| Production truncation | 9 B = 72 bits | 11 B = 88 bits |
| Reception cost | 1 hash (index is stored) | up to K hashes (index recovered by sweep) |
| False-accept model | `2^-8t` | `1 − (1 − 2^-8(t+2))^K` |

The equal byte budget is the whole point of the comparison: B converts A's stored-index bytes into hash bits, then pays for the index recovery with a sweep over K candidates. The bench's replicas (`ClassicScheme`, `ExperimentalScheme`) parameterize the truncation so that events become measurable (small `t`), while their hash inputs and, at `t = 9`, their exact wire bytes coincide with production.

`SectorSeedCache` makes the B sweep affordable: since `H5` is constant for the whole file, the 26-byte hash-input prefixes `H5(24) ‖ idxLE(2)` are precomputed once for all K indexes (`K × 26` bytes; ≈ 1.6 MB at `K = 65,535`); verifying one sector then copies a prefix, appends the payload and hashes. The cache is bounds-checked (`1..65,535`, `H5` exactly 24 bytes).

## 4. The mathematical model and its statistics

`CollisionMath` implements the model and the report statistics:

| Function | Role |
|---|---|
| `FalseAcceptClassic(t)` | `2^-8t` — one hash against one stored value. |
| `FalseAcceptExperimental(h, K)` | `1 − (1 − 2^-8h)^K`; for `K·p < 10⁻⁹` returns the product `K·p` directly, because `double` rounds `(1 − p)` to 1 and the closed form would underflow to 0. |
| `EffectiveBits(P)` | `-log2(P)` — effective strength in bits. |
| `WilsonInterval(accepts, trials)` | 95 % Wilson score interval for the observed proportion (`z = 1.959963984540054`). |
| `FormatProb` / `FormatBits` / `FormatCount` | Report formatting (`3.91E-03`, `2^-72.0`, `1 234 567`). |

Effective strength: A = `8t` bits; B = `8h − log2(K)` bits. The crossover is at `K = 65,536`:

| K | B strength (h = 11) | B gain over A (72 bits) |
|---|---|---|
| 64 | 82 bits | +10.0 |
| 512 | 79 bits | +7.0 |
| 4,096 | 76 bits | +4.0 |
| 32,768 | 73 bits | +1.0 |
| 65,535 | ≈ 72 bits | ≈ 0 (parity) |

(The `collide` verdict prints exactly this table for the full K grid 64..65,535.)

Verdict statistics for a measured cell:

- `z = (observed − p) / sqrt(p(1−p)/n)` — the normal-approximation deviation from the model.
- A cell is **confirmed** when it accumulated ≥ 10 events and `|z| ≤ 4`. The threshold 4 (not the usual 1.96) accounts for multiple comparisons: with dozens of cells, a 95 % threshold would flag 1–2 innocent cells per campaign.
- Cells with fewer than 10 events are reported as "too few events" and do not fail the run.

## 5. Modes and exit codes

| Mode | Purpose | Failure condition |
|---|---|---|
| `selftest` | Correctness of the replicas, cache and scenario engine (§6) | any failed check throws (runtime code 2) |
| `collide` | False-accept campaign over `scheme × scenario × t × K` (§7) | any confirmed cell with `|z| > 4` |
| `full` | Production truncations: zero full matches + leading-bit spectrum (§8) | any full match observed |
| `perf` | Reception cost measurements (§9) | none (always exits 0) |
| `all` | All of the above in sequence | the worst of the individual codes |

Exit codes: `0` — success; `64` — argument error (help is printed); `2` — runtime failure; `1` — model discrepancy (a red-flagged cell or an observed full match). The progress line is disabled when output is redirected, so pipelines receive clean tables.

## 6. Mode `selftest`: correctness before measurement

`SelfTest.Run` executes four groups of checks (any failure aborts with a descriptive exception):

1. **Scheme A ≡ production.** For 1,000 random sectors at `t = 9`: the replica builds 75-byte sectors; the production `PacketHasher.VerifySectorPacket` accepts them; the replica verifies them and reads the index back; a hand-assembled reference (index ‖ payload ‖ `PacketHasher.ComputeSectorHash`) is byte-identical to the replica's output; single-bit corruptions produce identical verdicts in replica and production (both reject); a foreign `H5` is rejected by both.
2. **Scheme B semantics.** For `K ∈ {1, 2, 7, 300, 4096}` and 200 sectors each: `VerifyAll` (full sweep) finds exactly the true index with exactly one match; `TryVerify` (early-exit sweep) finds the same index; a foreign seed cache yields zero matches on all K positions; corruption of the payload or of the stored hash rejects the sector. Additionally, `HashInto` of A and B produce identical digests for 200 random inputs — the schemes differ only in what they *store*, not in what they *hash*.
3. **Seed cache.** Prefixes equal `H5 ‖ idxLE`; bounds enforced: `K = 0`, `K = 65,536` and a short `H5` all throw.
4. **Scenario engine sanity.** Mini-campaigns verify the measurement loop itself observes model-consistent rates: A at `t = 1` (`P = 2^-8`, 50 target events) must land within a factor ×4 of the model in all three scenarios; B at `h = 3, K = 4096` (`P = 2^-12`, 12 target events) within ×6.

## 7. Mode `collide`: the false-accept campaign

### 7.1 Scenarios — how "bad" packets are born

All scenarios share one real `H5` per cell (`Trunc24(SHA-256(random header))`, exactly as production builds it) and, for B, one seed cache per (H5, K):

| Scenario | Construction | Hashes per trial |
|---|---|---|
| «шум» (noise) | fully random bytes | A: 1; B: K |
| «чужой файл» (foreign file) | a *valid* sector built under a different file's `H5` (random index, random payload) | A: 2 (build + verify); B: K + 1 |
| «порча» (corruption) | a valid sector of *this* file, then 1..4 unique byte positions flipped with non-zero XOR masks — anywhere in the packet, including the (stored or hashed) index field | A: 2; B: K + 1 |

### 7.2 The grid and the engine

For every truncation `t` (default `[1]`, range 1..7) and every `K` (default `[64, 128, 256, 512]`), the campaign runs six cells: A×{noise, foreign, corrupt} and B×{noise, foreign, corrupt}. Each cell is a `Campaign.Run` job:

- worker threads (default: all cores) with per-worker seeded `Random`s (`MixSeed` mixes the base seed with the worker id);
- batch sizes: A — 65,536 trials; B — `clamp(2,000,000 / K, 1, 65,536)` so a batch costs roughly constant time across K;
- stop conditions: target events reached (default 200), time budget exhausted (default 30 s per cell) or trial limit;
- counters accumulate atomically; progress is reported every 0.25 s on one console line.

Cell seeds derive from the base seed and the cell parameters (`seed ^ (hashBytes << 24) ^ K·0x9E37`), making every cell independently reproducible.

### 7.3 B-specific diagnostics

B sweeps K indexes, so the bench distinguishes *where* a bad packet landed:

- `Misplaced` — the first matching index is not the true one (measured in the corruption scenario, where a true index exists; for foreign-file cells every accept is by definition misplaced);
- `Ambiguous` — the packet matched several indexes at once.

These counters do not change the accept rate model; they show how a false accept *manifests* (wrong slot vs. multi-claim), which matters for the assembly-side collision machinery described in the [AssemblyGuide](AssemblyGuide.en.md).

### 7.4 Output and verdict

The results table prints one row per cell: scheme, scenario, hash bytes, K, trials, accepts, observed rate, expected rate, ratio, `z`, the 95 % Wilson CI and a verdict — "MODEL OK" (green), "TOO FEW EVENTS" (amber) or "DISCREPANCY" (red). The run then prints the production-truncation gain table (§4) and exits 0 if all confirmed cells have `|z| ≤ 4`, else 1 with a per-cell discrepancy list.

> [!NOTE]
> Cost awareness when choosing `t`: one B event at hash size `h` costs about `2^(8h)` hashes; the default `t = 1` (h = 3) is calibrated for quick runs. Each +1 on `t` multiplies the time to first event by 256 for A and by 2,096 for B — the deep tail is the job of the `full` mode, not of bigger `t`.

## 8. Mode `full`: validating the unreachable tail

At production truncations (A: 72 bits, B: 88 bits) false accepts cannot be awaited — `2^-72` of a chance per hash is beyond any budget. The `full` mode validates the tail differently:

- Run A with `FullClassicTrials` trials (default 200,000,000; one target per trial — the index is read from the packet itself) and B with `FullExperimentalTrials × FullSectors` (default 2,000,000 × 1,024 ≈ 2·10⁹ hash comparisons), under a total budget (default 240 s).
- For every noise trial, record `M` — the maximum number of **leading bits** that agree between the stored truncated hash and the computed one (over all K targets for B, with an early break on a full match).
- The spectrum `P(M ≥ m)` is compared with the uniform-hash model (`2^-m` per trial for A; `1 − (1 − 2^-m)^K` for B) using Poisson z-scores `(count − λ)/√λ`, `λ = expected × trials`, over the reachable depth `m ≈ log2(targets)` and about 11 bits below it.

The logic: if the truncated hash behaves like a uniform value down to the depth where events are observable, and zero *full* matches occurred, then the extrapolation to `2^-72` / `K·2^-88` rests on measured ground, not on hope. The verdict prints both schemes' spectra plus the "max bits observed vs `log2(targets)`" sanity line; any full match fails the run.

## 9. Mode `perf`: the price of the sweep

`PerfBench.Run` walks a grid of K (default `{100, 1000, 10000, 65535}`) and measures, per K, in one thread and on all threads:

| Case | Meaning | Hashes per packet |
|---|---|---|
| "A валид" (A valid) | a valid Classic sector | exactly 1 |
| "B валид" (B valid) | a valid VirtualIndex sector, early-exit sweep | index position + 1 on average ≈ K/2 |
| "B чужой" (B foreign) | random noise — nothing matches | full sweep: exactly K |

Methodology: 4,096 prebuilt sample packets per cell; 0.3 s JIT warmup; timed loop over the samples with a time check every 16 packets (B) or 4,096 (A); multi-threaded runs start each worker at a distinct sample offset and stop on a volatile flag; counters merge atomically. The report lists packets/s, hashes/s, µs/packet and the slowdown ratio against the "A valid" baseline of the same K and thread count, and summarizes the worst case (largest K).

Production context for the numbers: in the real decoder the sweep is not always K hashes — the [VirtualIndex reception cursor](AlgorithmGuide.en.md) makes *sequential* streams O(1) per packet, and the scheme is only enabled for files with `T ≤ 512`, where the worst case is 512 hashes per foreign packet (see §11).

## 10. Command-line reference

```
DataShield.CollisionBench [mode] [keys]
```

The first bare argument selects the mode (`selftest | collide | full | perf | all`); without it the mode defaults to `collide`.

| Key | Default | Meaning |
|---|---|---|
| `-t list` | `1` | Truncation(s) of scheme A, 1..7; B uses `t + 2`. Comma-separated: `-t 1,2`. |
| `-k list` | `64,128,256,512` | K values for `collide`, 1..65,535. |
| `-events N` | `200` | Target events per collide cell (early stop). |
| `-budget sec` | `30` | Time budget per collide cell, 1..3600. |
| `-threads N` | all cores | Worker threads, 1..128. |
| `-seed N` | `20260906` | Base PRNG seed (reproducibility). |
| `-perf-k list` | `100,1000,10000,65535` | K grid for `perf`. |
| `-perf-sec sec` | `2.5` | Duration of one perf cell, 0.2..600. |
| `-fa N` | `200,000,000` | Trials of scheme A in `full`. |
| `-fb N` | `2,000,000` | Trials of scheme B in `full`. |
| `-full-k N` | `1024` | Targets K per B trial in `full`. |
| `-full-budget sec` | `240` | Total `full` budget, 1..7200. |
| `-h` / `-help` / `--help` | | Built-in help. |

Examples:

```text
dotnet run --project DataShield.CollisionBench -- selftest
dotnet run --project DataShield.CollisionBench -- collide -t 1,2 -k 64,512 -events 300
dotnet run --project DataShield.CollisionBench -- full -fa 500000000 -full-budget 600
dotnet run --project DataShield.CollisionBench -- perf -perf-k 512,65535 -perf-sec 5
dotnet run --project DataShield.CollisionBench -- all
```

## 11. What the measurements justify in production

The design decisions anchored by this bench:

1. **VirtualIndex pays its hash bits honestly.** At equal byte budget B is stronger than A by `log2(65,536 / K)` bits — measured across scenarios and truncations that agree with the closed-form model.
2. **The 512-volume limit.** Production enables VirtualIndex only for `T ≤ 512` (`VirtualIndexHasher.DefaultSectorLimit`), i.e. ≥ 79 effective bits and a worst-case sweep of 512 hashes — the gain column shrinks as K grows, so the scheme is capped where it is still clearly profitable.
3. **The tail is extrapolated on evidence.** The `full` spectrum check is what allows the [AlgorithmGuide](AlgorithmGuide.en.md) to state 72-bit / 88-bit bound strengths as properties of a measured uniform hash, not as an assumption.
4. **The reception cost is bounded and known.** The perf table quantifies the K-hash worst case that motivated the cursor optimization and the hybrid Classic-first reception order in the decoder.
5. **False accepts degrade into assembly collisions, not silent corruption.** The misplaced/ambiguous diagnostics feed the same mental model that the version-search and subset-search stages of the [AssemblyGuide](AssemblyGuide.en.md) are built to handle — with the file's full SHA-256 as the final arbiter in any case.

## 12. Reproducibility notes

- Every random decision chains from the single base seed (default `20260906`): cell seeds, worker seeds (`MixSeed`), sample generation. Same arguments ⇒ same tables, on any machine (modulo timing-dependent early stops, which only shorten runs).
- All rates, expectations and intervals are computed with invariant culture formatting; large counts are printed with thin separators.
- Console colors mark verdicts; redirected output drops progress spinners but keeps every table row, so CI logs are parseable.
- The bench is not part of the unit-test suite: it is a measurement instrument, run on demand (`all` for a full campaign; `selftest` as a smoke check).

## 13. Document map

| Document | Content |
|---|---|
| [AlgorithmGuide](AlgorithmGuide.en.md) | Wire format and algorithms; the production Classic and VirtualIndex schemes whose model this bench validates. |
| [EncoderGuide](EncoderGuide.en.md) | Using the encoder. |
| [DecoderGuide](DecoderGuide.en.md) | Using the decoder. |
| [AssemblyGuide](AssemblyGuide.en.md) | What happens downstream when a false accept does occur: version search and subset planning. |
| [CollisionBench](CollisionBench.en.md) (this file) | Benchmark methodology and measurements. |
| [DeveloperGuide](DeveloperGuide.en.md) | Project layout, build, tests. |
| [UserGuide](UserGuide.en.md) | End-to-end usage from the console application and the GUI. |

Russian mirrors of every document carry the `ru` suffix (`CollisionBench.ru.md`, …).
