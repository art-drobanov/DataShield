# DataShield Alpha-XVI — Assembly Guide

Deep-dive reference for the receiver-side assembly engine: reception slots, sector versions, collision search, subset planning, arbitration and tuning.

| | |
|---|---|
| Project | DataShield Alpha-XVI |
| Document | `docs/AssemblyGuide.en.md` (English); Russian mirror: `docs/AssemblyGuide.ru.md` |
| Audience | Engineers implementing, reviewing or porting the decoder; advanced operators tuning recovery limits for hostile channels |
| Scope | The `ReceptionSlot` model, version counting and snapshots, choice points, exhaustive and rotation searches, the volume-subset planner, the final verification gate, options and their calibration, cost model, worked examples |
| Out of scope | Wire format and hashing fundamentals ([AlgorithmGuide](AlgorithmGuide.en.md)), text filtering and scanning ([DecoderGuide](DecoderGuide.en.md)), encoding ([EncoderGuide](EncoderGuide.en.md)), CLI and GUI usage ([UserGuide](UserGuide.en.md)), project layout ([DeveloperGuide](DeveloperGuide.en.md)) |

Reference implementation: `DataShield.Codec.StreamProcessor` (`ReceptionSlot.cs`, `Versions/`, `Subsets/`, `StreamProcessor.cs`) with arithmetic isolated in `SectorCombinationMath.cs`.

## 1. Conventions used in this document

- `N` — data volume count, `M` — ECC volume count, `T = N + M` — total volumes of the file. "Volume index" and "sector number" are the same thing (see the [AlgorithmGuide](AlgorithmGuide.en.md) glossary).
- `E` — the number of **erased data volumes** in one RS attempt: volumes missing from the stream plus volumes deliberately excluded by a mask.
- A **version** is one candidate 64-byte payload stored for one sector number; a **collision slot** is a sector number with two or more distinct versions.
- An **attempt** is one full assembly trial: build a buffer, trim it, hash it, compare. Every attempt is arbitrated by the same final gate (§13).
- `H1..H5`, `D1..D3`, `S1..S2` are packet fields as defined in [AlgorithmGuide §5](AlgorithmGuide.en.md); `H5` is the 24-byte header hash that seeds all sector checks.
- Sizes: `B` = bytes; a payload is always 64 B.
- `2⁻ⁿ` is the probability that a random value passes an n-bit check.
- References of the form `ReceptionSlot.cs` point at the reference implementation; method and property names are given exactly as in code.

## 2. Where assembly sits

Decoding is a pipeline (full picture in [AlgorithmGuide §11](AlgorithmGuide.en.md)): a text filter extracts Base64 material from arbitrary surrounding text, a scanner finds intact 75-byte packets, and the `StreamProcessor` classifies each verified packet and accumulates it into **reception slots** — one slot per distinct header observed in the stream.

Assembly is the terminal operation of a slot:

```csharp
public byte[]? TryAssemble(
    RsCodecAdapter? rs,
    IProgress<CodecProgress>? progress,
    CancellationToken ct,
    IReadOnlyList<byte[]>? acceptedSignatures = null)
```

- `rs` — the Reed–Solomon adapter used for erasure recovery; may be `null` (then only direct assembly is possible).
- `progress` — a receiver of `CodecProgress` on a global 0..100 scale.
- `ct` — cancellation token, checked at stage entries and between attempts.
- `acceptedSignatures` — the unique `S1` values (64-byte SHA-512 signatures) collected from the stream. Not `null` ⇒ the extended check is active: after the base SHA-256 gate, the assembled content must be confirmed by at least one signature (§13). `null` ⇒ the check is off (`SignatureMode.Base`, or `Auto` with no signature packet received).

Return contract:

| Outcome | Meaning |
|---|---|
| non-null `byte[]` | The file was reconstructed and passed **Exact(SHA-256)** — and, when active, **Any(SHA-512)**. |
| `null` | The limits were exhausted or the data is unrecoverable. |
| `SignatureVerificationException` | Some attempt produced content that matches the header's SHA-256 **exactly**, but no collected signature confirms it — an integrity alarm, not a search failure (§13.3). |

## 3. The reception slot: identity and counters

A `ReceptionSlot` is created by the `StreamProcessor` when a header packet with unseen content arrives:

```csharp
public ReceptionSlot(
    byte[] headerBytes,        // serialized 51-byte header content
    HeaderContent header,      // parsed view of the same bytes
    byte[] headerHash,         // H5: 24 bytes
    SectorVersionSearchOptions searchOptions)
```

State carried by the slot:

| Member | Meaning |
|---|---|
| `Header` / `HeaderHash` | Parsed header and its `H5`; `H5` seeds every sector check for this file. |
| `HeaderReceptionCount` | How many copies of this header were accepted; saturates at `int.MaxValue`. |
| `HeaderMatches(ReadOnlySpan<byte>)` | Byte-wise comparison of a candidate packet's first 51 bytes against the stored header. Equal bytes ⇒ same file; the packet's trailing 24 bytes are the re-derivable `H5`. |
| `_sectors` | `SortedDictionary<int, List<SectorVariant>>` — sector number → ordered version list (§4). |
| `VirtualIndexCursor` | Expected next sector index under the VirtualIndex scheme (§5). |
| `_signatureRejected` | Sticky flag set by the final gate; drives the exception of §13.3. |

Derived observables:

| Property | Meaning |
|---|---|
| `DataVolumeCount` / `EccCount` / `TotalVolumeCount` | `N`, `M`, `T` from the header. |
| `ReceivedSectorCount` | How many distinct sector numbers have at least one version. |
| `ReceivedSectorCopyCount` | Total accepted sector copies — the sum of all version confirmation counters (`long`). |
| `CollisionSectorCount` | How many sector numbers have more than one distinct version. |
| `Coverage` | `ReceivedSectorCount × 100 / T`, a percentage. |

Duplicate header copies never create a second slot: `StreamProcessor.AcceptHeader` byte-compares the 51-byte content against every existing slot; a match only increments `HeaderReceptionCount`. Two different files in one stream therefore yield two slots, and each sector packet is offered to both — it sticks only to the slot whose `H5` verifies it (72-bit binding for Classic, §5).

## 4. Sector versions and confirmation counting

### 4.1 The version object

`SectorVariant` (internal) is the smallest unit of evidence:

| Field | Meaning |
|---|---|
| `Payload` | 64 bytes — one candidate content of the sector. |
| `ConfirmationCount` | How many accepted copies carried exactly these bytes; starts at 1 on first sight, saturates at `int.MaxValue`. |

The list for each sector number is kept **sorted by descending `ConfirmationCount`** at all times. The head of the list — the most confirmed version — is the decoder's default belief about that sector.

### 4.2 `AddSector`: the acceptance algorithm

```csharp
public bool AddSector(int sectorNum, byte[] payload)
```

1. Range check: `sectorNum` must be within `0..T−1` and `payload` exactly 64 B; otherwise `false` (defensive; verified packets normally satisfy both).
2. Scan the version list for a byte-identical `Payload`.
3. On a match, increment that version's counter (with saturation), then **promote** it: bubble it upward past versions with strictly smaller counters. The promotion stops at a version with an equal counter — equal-counter neighbours are never swapped. This keeps the sort stable and preserves whatever order equal versions happen to be in (important for the rotation search, §12).
4. On no match, append a **new version** with counter 1 at the end of the list. Since every existing version has counter ≥ 1, the descending-sort invariant survives the append without re-sorting.

Cost: one pass over the versions of that sector (64-byte compare each) plus a bounded shift. For realistic collision counts (a handful of versions) this is effectively constant time.

Example — sector 7 receives payloads in the order `P1, P2, P1, P1, P2`:

| After copy | Version list (counter) |
|---|---|
| `P1` | P1 (1) |
| `P2` | P1 (1), P2 (1) |
| `P1` | P1 (2), P2 (1) |
| `P1` | P1 (3), P2 (1) |
| `P2` | P1 (3), P2 (2) |

### 4.3 Snapshots

`GetSectorVersions(int sectorNum)` returns a defensive snapshot: an array of `SectorVersionInfo` records (`ReadOnlyMemory<byte> Payload`, `int ConfirmationCount`) in the current preference order. Snapshots are the public observation API — callers cannot mutate live slot state through them. The internal `ChoicePoint` (§9) is the counterpart that *does* hold a live list reference, on purpose: the rotation search reorders that list.

## 5. How packets reach the slot (recap)

Classification happens in `StreamProcessor`; details belong to the [DecoderGuide](DecoderGuide.en.md). One verified 75-byte packet is offered to the slot machinery as exactly one of:

| Packet kind | Autonomous check | Effect |
|---|---|---|
| Header | `H5 = Trunc24(SHA-256(H1..H4))` over the 51-byte content | Existing slot: `HeaderReceptionCount++`. New content: new slot. |
| Signature | `S2 = Trunc11(SHA-512(S1))` over the first 64 bytes | Copy counter always; unique `S1` stored when the mode is `Auto`/`Force`. |
| Sector, Classic | `D3 = Trunc9(SHA-256(H5 ‖ D1 ‖ D2))` under *that slot's* `H5` | `AddSector(D1, payload)`. |
| Sector, VirtualIndex | 11-byte tag `Trunc11(SHA-256(H5 ‖ payload ‖ idx))`, index recovered by the receiver | `AddSector(idx, payload)`; cursor advance on success. |

Two reception details matter for assembly:

- **Hybrid Classic-first.** `AcceptSectorForSlot` tries the Classic interpretation first, VirtualIndex second. A Classic-encoded stream is therefore still readable by a decoder configured for VirtualIndex.
- **The VirtualIndex cursor.** `VirtualIndexCursor` is the expected next index. `AdvanceVirtualIndexCursor(acceptedIndex)` sets it cyclically to `(acceptedIndex + 1) % T`. For a sequential stream the tag verification starts at the cursor and succeeds immediately — O(1) per packet; for a shuffled stream the scan falls back to a full index sweep. The cursor is a performance hint only; it never rejects a packet that verifies at another index.

A packet binds to at most the slots whose `H5` verify it, which is what keeps fragments of two files from mixing silently.

## 6. Observability: maps and counters

The slot exposes three map builders used by reports, logs and tests:

| Method | Result |
|---|---|
| `BuildValidityMap()` | `bool[T]`: `true` where at least one version exists. |
| `FormatValidityMap()` | One character per volume: `█` present, single version; `▓` present, collision; `░` missing. Empty file field yields the placeholder string `(пусто)`. |
| `BuildCollisionMap()` | Dictionary: sector number → number of distinct versions, collision slots only. |
| `BuildCollisionFlags()` | `bool[T]` with `true` at collision positions, or `null` when there are no collisions. |

Example, `T = 11`, sector 4 missing and sectors 2, 8 in collision:

```text
index : 0 1 2 3 4 5 6 7 8 9 10
map   : █ █ ▓ █ ░ █ █ █ ▓ █ █
```

## 7. The assembly pipeline at a glance

`TryAssemble` runs a fixed sequence of stages. Every attempt inside every stage goes through the same arbitration (§13); the first attempt that passes it wins.

| Stage | Runs when | What it tries |
|---|---|---|
| 0. Preferred selection | always | Take the head version of every sector. |
| 1. Single attempt | always, first | Direct assembly of the selection; on failure, RS recovery over the full validity map. |
| 2. Version search | stage 1 failed **and** choice points exist (§9) | Other combinations of the tied head versions: exhaustive enumeration (§11) or heuristic rotation (§12), chosen by the decision rule of §10. |
| 3. Volume subset search | stages 1–2 failed, `rs` present, `M ≥ 1` | Deliberately exclude suspicious volumes, let RS restore them (§14). |

Termination outcomes are those of §2. On success the slot reports progress 100 ("assembly finished"). Progress during searches is throttled to whole percentages and never reaches 100 prematurely (§15).

## 8. Stage 1 — the preferred selection and the single attempt

Two pure builders prepare the attempt:

- `BuildPreferredSelection()` — `byte[T][]`, element `i` being the head version payload of sector `i` (or `null` if absent).
- `BuildChoicePoints()` — the branch points for stage 2 (§9).

`TryAssembleSelected(selected, rs, ...)` is one attempt:

1. **Direct assembly** (`TryAssembleDirect`): requires all `N` data volumes present; concatenates the payloads into an `N·64` buffer; hands it to the final gate (§13).
2. **RS recovery** (`TryAssembleWithEcc`, only if direct failed and `rs` is set): builds the validity `map[T]` and the `received[T]` payload array from the selection; requires `present ≥ N`; calls `rs.Decode(received, map, N, progress, ct)`; validates that exactly `N` 64-byte chunks came back; concatenates and gates them.

When no choice points exist, this single attempt *is* the whole pipeline apart from stage 3 — the common case: an intact or merely truncated stream assembles here in one pass.

`TryAssembleDirect()` and `TryAssembleWithEcc(rs)` also exist as internal parameterless wrappers over the preferred selection; tests use them to probe slot state directly.

## 9. Choice points: where versions actually branch

A **choice point** is a collision slot whose top counters are tied. For each sector with ≥ 2 versions, `BuildChoicePoints` counts how many leading versions share the maximum `ConfirmationCount` — that is `TiedVariantCount`. A slot becomes a choice point only when `TiedVariantCount ≥ 2`.

| Version counters in one slot | Choice point? | Branches |
|---|---|---|
| 5 | no | — |
| 5, 3 | no | the head is strictly best; no ambiguity |
| 5, 5, 3 | yes | 2 |
| 5, 5, 5, 2 | yes | 3 |
| 4, 4, 4, 4 | yes | 4 |

The design rationale: a strictly dominant version is the decoder's unambiguous belief — trying its runners-up would burn the budget on the least likely hypotheses. The search space is defined *only by actual ambiguity*: ties among the most confirmed candidates. Versions below the tie group are never branched on by stage 2; if they are right and the head is wrong, stage 3 can still rescue the file by excluding the whole slot (§14).

`ChoicePoint` (internal) holds the sector number, a **live** reference to the version list, and `TiedVariantCount`. The live reference is what allows the rotation search to reorder the list in place.

The joint search space of stage 2 is the product of all `TiedVariantCount` values — `SectorCombinationMath.CountCombinations` (§15).

## 10. Strategy decision after the first attempt

When choice points exist, the initial state (all heads) is attempted first, as attempt #1, *with progress suppressed* — the measurement doubles as calibration:

- `firstAttemptTicks` — wall-clock cost of one full attempt (direct + full-map RS + hashing) on this machine, on this data.
- `combinationCount = CountCombinations(tiedCounts)` — the size of the joint space.
- `estimatedSeconds = firstAttemptTicks / Stopwatch.Frequency × combinationCount` — the linear cost model of exhausting the space.

`SectorCombinationMath.ShouldUseExhaustiveSearch(combinationCount, estimatedSeconds, options)` selects the strategy:

| Condition | Consequence |
|---|---|
| `combinationCount ≤ MaxExhaustiveCombinations` **and** `estimatedSeconds ≤ TimeBudget` | exhaustive search (§11) — completeness guaranteed. |
| otherwise | heuristic rotation (§12) — bounded coverage, cheap states. |

With defaults (100,000 combinations, 30 s) the overwhelming majority of realistic cases go exhaustive; rotation is the safety valve for pathological tie structures (for example, a hundred slots with 64 tied versions each).

> [!NOTE]
> The time estimate is a *linear extrapolation of one measurement*. It is deliberately conservative: one real attempt is measured rather than modelled, so machine speed, file size and RS state are all baked in.

## 11. Stage 2a — exhaustive search

`TryExhaustiveSearch` enumerates the full Cartesian product of the tie groups:

- `indexes[k]` — the current pick in each of the `k` choice points; `moduli[k] = TiedVariantCount_k`.
- `SectorCombinationMath.AdvanceIndexes(indexes, moduli)` increments the mixed-radix counter (odometer: last wheel first, carry on wrap) and returns `false` when the counter wraps past the last combination — the natural stop.
- Combination 0 (all heads) was already tried as attempt #1; the loop starts at 1.
- `ApplyIndexes` writes the picked versions into the `selected` array; `TryAssembleSelected` runs the attempt.
- Between attempts: `ct.ThrowIfCancellationRequested()` and the wall-clock check `timer.Elapsed ≥ TimeBudget` — the budget is **soft**, enforced at attempt boundaries, never mid-attempt.
- Progress is reported through the throttled channel (§15) as `completed / combinationCount`.

Exhaustive search is *complete within the tie groups*: if any combination of tied head versions assembles a file that passes the gate, this stage finds it — provided the limits hold. Complexity is exactly one attempt per combination.

## 12. Stage 2b — heuristic rotation

`TryRotationSearch` is the fallback when the joint space is too large to enumerate.

**Mechanics.** After each failed attempt, every choice point's tie group is rotated by one: the head version moves to the end of the tied block (`RotateTiedVariants` — remove index 0, insert at `TiedVariantCount − 1`). Versions below the tie are untouched, so the descending-sort invariant of §4 survives. The next attempt then re-runs `BuildPreferredSelection` — which now picks the new heads.

**Worked micro-example** (from the reference implementation): two slots, `A/B/C` and `X/Y` (three and two tied versions). Synchronous stepping yields:

```text
attempt 1: A + X      attempt 4: A + Y
attempt 2: B + Y      attempt 5: B + X
attempt 3: C + X      attempt 6: C + Y
```

**State count.** The joint state of synchronously stepping wheels of sizes `t1..tk` repeats with period `LCM(t1..tk)`. `SectorCombinationMath.CountRotationStates(tiedCounts, MaxHeuristicAttempts)` computes exactly that LCM, with two safeguards: the intermediate LCM is overflow-limited, and the result is capped at `MaxHeuristicAttempts` (the cap includes the already-tried initial state; the loop runs `stateCount − 1` further attempts).

**Coverage.** Rotation visits `LCM` distinct combinations out of the `Π ti` in the space. When the tie counts are pairwise coprime — as 3 and 2 above — `LCM = Π` and rotation is *complete*. When counts share factors (say 2 and 4: `LCM = 4 < 8`), some combinations are never visited; that is the accepted price of the fallback. Which combinations are visited is fully determined by the current version order, and the order-stability rule of §4.2 guarantees the walk is reproducible.

| Property | Exhaustive (§11) | Rotation (§12) |
|---|---|---|
| Trigger | space within limits | space too large |
| States visited | all `Π ti` | `min(LCM(ti), MaxHeuristicAttempts)` |
| Completeness | yes, within tie groups | only if tie counts pairwise coprime |
| Per-state cost | one attempt | one attempt |
| Order of hypotheses | odometer (last slot varies fastest) | round-robin fairness across slots |

## 13. The final gate: trim, verify, wipe

Every attempt ends in `TrimAndVerify(buffer, acceptedSignatures)`:

1. **Trim.** The buffer is `N·64` bytes; the file is `FileSize` bytes. If the lengths differ, the first `FileSize` bytes are copied out as the candidate result.
2. **Base gate — Exact(SHA-256).** `Sha256Secure.HashData(result)` must equal the header's `H3` byte-for-byte. On mismatch the attempt's buffers are wiped (§13.4) and the gate returns `null` — the attempt failed, the search continues.
3. **Extended gate — Any(SHA-512).** When `acceptedSignatures` is not `null`, `Sha512Secure.HashData(result)` (64 bytes, computed once into a stack buffer) must equal at least one collected `S1`. On failure: `_signatureRejected = true`, buffers wiped, `null` returned — and the search continues, because a different branch might reconstruct content that *is* confirmed.
4. **Wipe.** `Wipe(buffer, result)` zeroes both the working buffer and the trimmed copy before any failed return. Nothing unverifiable survives an attempt in memory.

### 13.3 Why a signature failure throws instead of returning `null`

`_signatureRejected` is reset at `TryAssemble` entry and can be set by any attempt. After all stages:

- some attempt passed Exact(SHA-256) — the header's own content hash — but no signature confirmed it, and no fully-confirmed assembly was found either ⇒ `TryAssemble` **throws** `SignatureVerificationException`;
- otherwise failure is reported as plain `null`.

The distinction matters operationally. `null` means "not enough verified data". The exception means "content that satisfies the file's own 256-bit description was reconstructed, yet the independent 512-bit signatures disagree with it" — the signature set and the header do not describe the same bytes. Typical causes: signature packets and header packets came from transmissions of *different* files (mixed fragments), or a header was tampered with while signature packets survived. The decoder refuses to pass such bytes off as a success and refuses to bury the signal inside a `null`.

The exact interplay of the modes (`Base` always `null`, `Auto` non-null iff ≥ 1 signature received, `Force` always non-null) is specified in [AlgorithmGuide §11.5](AlgorithmGuide.en.md) — from the slot's point of view, the mode has already been reduced to the single question "is `acceptedSignatures` null?".

## 14. Stage 3 — volume subset search

### 14.1 Why it exists

Stages 1–2 have a blind spot: a volume (data *or* ECC) whose packet is internally consistent but wrong. The sector's index is intact and its 72-bit `D3` check passed — by accident with probability about 2⁻⁷² per packet, or because an adversary spent the work to forge one. The slot then holds a *single* version: no branch point exists, stage 2 has nothing to search, and stage 1's RS pass treats the forged volume as truth — the syndrome check inside RS will simply fail, or worse, silently confirm wrong data when the parity happens to match.

Stage 3 covers exactly this case: **suspect the data itself**. A set of volumes is declared erased; RS recomputes them from the survivors; the final SHA-256 gate arbitrates. Forging a packet that survives stage 3 requires forging the file's full SHA-256 — 2⁻²⁵⁶ — or defeating the signature gate on top.

### 14.2 Preconditions and snapshot

`TryVolumeSubsetSearch` runs only when `rs` is available and `M ≥ 1` (without parity there is nothing to restore from). It builds a `VolumeReception[T]` snapshot (`BuildVolumeReceptions`):

| Field | Meaning |
|---|---|
| `Present` | At least one version received. |
| `VariantCount` | Distinct versions. |
| `HeadConfirmationCount` | Counter of the head version — the slot's confidence. |
| `HasCollision` | `Present && VariantCount > 1`. |

Missing volumes are the struct default (`Present = false`). The seed for all pseudo-randomness below is the first 4 bytes of `H5` read little-endian (`BinaryPrimitives.ReadUInt32LittleEndian(HeaderHash)`) — fixed per file, never transmitted separately.

### 14.3 The planner: base, candidates, levels

`SubsetMaskPlanner.Plan(volumes, N, M, options, seed)` is a pure function (the style of `SectorCombinationMath`): snapshot in, plan out, no slot access. The plan (`SubsetMaskPlan`) is an upper-bound attempt count plus a **lazy** enumeration of exclusion masks — each mask a sorted array of volume numbers to treat as erased.

The mask space is structured in levels:

| Level | Content | Order |
|---|---|---|
| Base | **All** collision slots at once | single mask (if any collisions exist) |
| 1 | Base + one extra excluded volume, per candidate | by ascending suspicion |
| 2..`MaxExtraExclusionLevel` | Base + combinations of `t` candidates from the shortlist | lexicographic |

Decisions embedded in the planner:

- **Base = every collision slot.** Their versions are *not* enumerated here (stage 2 already did that within tie groups); instead the whole slot is cut out and restored by RS. If the base alone does not fit the ECC budget or the `E` cap, the plan is empty: partially-excluded collisions are not attempted (§14.4 explains why the budget is binary, not incremental).
- **Candidates** are present, non-collision volumes. The candidate order is built deterministically: Fisher–Yates shuffle over the candidate list keyed by the `H5`-derived seed (vendored `Mt19937`, [DeveloperGuide](DeveloperGuide.en.md)), then a stable sort by ascending `HeadConfirmationCount`. Least-confirmed volumes come first — they are the most suspicious; ties keep the shuffled order. Same snapshot + same header ⇒ same candidate order, on every run, on every machine.
- **Level 1 sweeps all candidates**; levels ≥ 2 draw from the `ShortlistSize` most suspicious ones only — a bounded frontier against combinatorial growth (`C(64, 2) + C(64, 3) = 2 016 + 41 664` masks by default).

### 14.4 Feasibility of a mask

A mask is generated only if `IsFeasible` holds, with (recall `E` from §1):

- `erasedData = missingData + (excluded data volumes)`;
- `excludedEcc` = excluded ECC volumes;
- `availableEcc = M − missingEcc − excludedEcc`.

Rules:

1. `erasedData ≥ 1` — a mask that erases no data volume is an RS passthrough and would repeat stage 1; skipped. (Pure-ECC exclusions are skipped *unless* data volumes are already missing — and they are genuinely useful then: a corrupt parity volume can be cut out so RS decodes from the remaining parity.)
2. `erasedData ≤ MaxErasedDataVolumes` — the `E` cap; `E` is the dominant cost driver of the Gaussian inversion (~`E²·N`), so it is limited independently of attempt counts.
3. `erasedData ≤ availableEcc` — RS can restore at most as many erased data volumes as surviving parity allows. This is also why base feasibility is all-or-nothing: the budget `availableEcc` shrinks with every exclusion, so "exclude half the collisions now, the rest later" is not a valid schedule — each mask must be self-consistent.

### 14.5 Determinism

The plan is a pure function of `(volumes, N, M, options, seed)`. The seed is fixed by the file's `H5`; the shuffle and stable sort are deterministic; `Combinations` enumerates lexicographically. Consequences: reproducible recovery runs (essential for debugging and for the [CollisionBench](CollisionBench.en.md) methodology), and no reliance on ambient randomness that could differ between the GUI, the console app and the test suite.

### 14.6 Execution loop

For each mask from the lazy enumeration, in order:

1. check `attempted ≥ MaxAttempts` and the wall clock against `TimeBudget` — break on either (both **soft**, checked between attempts);
2. run `TryAssembleWithEcc(rs, selected, ..., forcedErasure: mask, ...)` — the attempt of §8 with the mask's volumes removed from the RS input map;
3. report throttled progress as `attempted / AttemptUpperBound`;
4. first gate pass wins and returns.

`AttemptUpperBound` is the planner's a-priori count: `1` (base, if feasible) `+ candidates.Length + Σ C(shortlist, t)` over levels 2..`MaxExtraExclusionLevel`, saturated at `long.MaxValue`. It is an *upper* bound — infeasible masks are skipped without being attempted, so the loop can finish early; progress simply reaches 100 % at completion of the enumeration.

### 14.7 The repair narrative

Putting §14.1–§14.6 together, the stage tells this story: "Something in what I received is lying, and I cannot see it directly. I will cut out what I trust least — first the ambiguous slots, then the least-confirmed volumes, then small groups of them — and each time let the mathematics rebuild the hole and the file's own SHA-512-bound signature decide." Each step of that story costs one bounded, deterministic attempt.

## 15. Progress reporting, cancellation, memory

- **Scale.** All progress is on one global 0..100 scale. Stage messages carry the stage name and counters (`SectorVersionSearch`, `HeuristicRotation`, `ExhaustiveSearch`, `VolumeSubsetSearch`, `RsRecovery`...); the subset stage reports `attempted / AttemptUpperBound`.
- **Throttling.** `ReportSearchProgress` recomputes the integer percentage, clamps it to ≤ 99, and reports only when it changes — at most one hundred messages per stage regardless of attempt count, and never a premature 100.
- **Suppression inside attempts.** Searches run attempts with `progress: null`; only stage-level milestones surface. The single-attempt path (§8) forwards a scaled sub-progress to the RS decoder.
- **Cancellation.** `ct.ThrowIfCancellationRequested()` at `TryAssemble` entry and before every attempt in every stage; RS decode receives the token as well.
- **Counters that saturate.** Version counters, header counters, combination counts and attempt bounds all saturate (`int.MaxValue` / `long.MaxValue`); the formatter replaces `long.MaxValue` with an overflow placeholder instead of printing nonsense.
- **Memory hygiene.** Failed attempts wipe their buffers (§13.4); snapshots are defensive copies; the lazy mask enumeration materializes one mask at a time.

## 16. SectorCombinationMath: the arithmetic toolbox

All stage-2 arithmetic lives in one internal static class of pure functions — no state, fully unit-tested in isolation:

| Function | Role |
|---|---|
| `Gcd(a, b)` | Euclid's algorithm; building block of the LCM. |
| `LeastCommonMultipleLimited(a, b, limit)` | LCM with overflow protection — returns `limit` rather than wrapping. |
| `CountCombinations(tiedCounts)` | Product of tie sizes — the exhaustive space; saturates at `long.MaxValue`. |
| `CountRotationStates(tiedCounts, limit)` | `LCM` of tie sizes, capped at `limit` — the rotation period (§12). |
| `AdvanceIndexes(indexes, moduli)` | Mixed-radix odometer step; `false` on wrap-around — the enumeration stop condition. |
| `ShouldUseExhaustiveSearch(count, estimatedSeconds, options)` | The two-condition strategy rule of §10. |

The companion of the planner side is `SubsetMaskPlanner` with its own combinatorics (saturating binomials, lexicographic `Combinations`) — same discipline: pure, deterministic, testable without a slot.

## 17. Options reference and tuning

### 17.1 `SectorVersionSearchOptions` (stage 2)

| Option | Default | Meaning |
|---|---|---|
| `MaxExhaustiveCombinations` | 100,000 | Hard cap on the enumerated joint space of tie groups. |
| `MaxHeuristicAttempts` | 100,000 | Cap on rotation states; **includes** the initial attempt. |
| `TimeBudget` | 30 s | Soft wall-clock budget, checked between attempts. |
| `SubsetSearch` | `VolumeSubsetSearchOptions.Default` | Nested options of stage 3. |

`Validate()` enforces positive values. `Default` is the calibration below.

### 17.2 `VolumeSubsetSearchOptions` (stage 3)

| Option | Default | Meaning |
|---|---|---|
| `MaxAttempts` | 100,000 | Ceiling on mask attempts; the earlier of it and the budget applies. |
| `TimeBudget` | 30 s | Soft budget, checked between attempts. |
| `MaxErasedDataVolumes` | 32 | Cap on `E` per attempt — the separate knob because `E` drives ~`E²·N` inversion cost. |
| `ShortlistSize` | 64 | Frontier for levels ≥ 2: `C(S, t)` masks per level. |
| `MaxExtraExclusionLevel` | 3 | Deepest level of extra exclusions (3 ⇒ pairs and triples on top of singles). |

### 17.3 Calibration background

Defaults were measured with the RS benchmark (`RsRaid16Demo`, mode "bench"; methodology in [CollisionBench](CollisionBench.en.md)). One subset attempt costs `Init` (~`O(E²·N)`) plus 32 × `Process` (~`O(E·N)`) plus a final SHA-256 over `N·64` bytes. At the cap `E ≤ 32`, even the largest legal file (`T = 65,535`) completes ≥ 40 attempts inside the 30-second budget — hence 32 and 30 s fit together by construction, not by coincidence.

### 17.4 Tuning recipes

| Scenario | Change | Rationale |
|---|---|---|
| Interactive decoding (UI thread responsiveness) | both `TimeBudget` ≈ 5 s | soft budgets abort gracefully; user can retry with more |
| Hostile channel, adversarial mixing | keep defaults; ensure signatures are sent (`signaturePackets512`) | stage 3 + Any(SHA-512) are the anti-forgery pair |
| Overnight batch recovery | `TimeBudget` minutes; `MaxAttempts` 10⁶ | wall-clock is the only real constraint |
| Deep suspicion of clustered corruption | `MaxExtraExclusionLevel` 4, `ShortlistSize` 96 | quadruples+ of suspects — see the caution below |
| Constrained hardware | `MaxErasedDataVolumes` 16; budgets 10 s | inversion cost scales with `E²` |

> [!CAUTION]
> `C(96, 4) ≈ 3.3 × 10⁶` masks on level 4 alone. Raising `MaxExtraExclusionLevel` multiplies the plan quadratically-to-factorially; always re-estimate with `AttemptUpperBound` logic in mind before shipping a configuration.

## 18. Cost model

| Operation | Cost |
|---|---|
| `AddSector` | O(versions in slot) byte compares + bounded shift; ~constant in practice |
| `GetSectorVersions` / snapshots | O(versions) with defensive copies |
| `BuildPreferredSelection` / `BuildChoicePoints` / `BuildVolumeReceptions` | O(T + total versions) |
| `CountCombinations` / `CountRotationStates` | O(choice points) |
| Planner (`Plan`) | O(T) shuffle + O(T log T) sort; enumeration O(1) amortized per mask |
| One attempt, direct | copy `N·64` + SHA-256 over `FileSize` |
| One attempt, with RS | + Init ~O(E²·N) + 32 × Process ~O(E·N) |
| Whole stage 1 | one attempt |
| Whole stage 2 | ≤ `min(Π ti, limits)` attempts |
| Whole stage 3 | ≤ `min(AttemptUpperBound, MaxAttempts, budget)` attempts |

## 19. Worked examples

### 19.1 Tied versions resolved by exhaustive search

File: 200 B, defaults ⇒ `N = ⌈200/64⌉ = 4`, `M = max(1, round(0.4)) = 1`, `T = 5`, three header copies. The mangled stream delivered sectors 0, 1, 3, 4 cleanly, and sector 2 **twice with two distinct payloads** `A` and `B` — each copy individually verified (a truncated-hash coincidence or a forged variant; the slot cannot tell and does not need to).

- Version lists: sector 2 → `A (2), B (2)`; others single-version.
- `BuildChoicePoints`: one point at sector 2, `TiedVariantCount = 2`. `CountCombinations = 2`.
- Attempt 1 (heads, i.e. `A`): direct assembly → SHA-256 mismatch → full-map RS → passthrough, mismatch again → `null`. Measured: say 0.4 ms.
- Decision: `2 ≤ 100,000` and `0.8 ms ≤ 30 s` ⇒ exhaustive.
- Exhaustive, combination 1 (`B`): direct assembly → SHA-256 matches; signatures (if active) confirm ⇒ **success**, progress 100.

Total: two attempts. The tie was resolved by the file's own content hash, not by trust in counters.

### 19.2 Invisible corruption resolved by subset search

File: 640 B ⇒ `N = 10`, `M = 1`, `T = 11`; every volume present, **no collisions**, so no choice points. Data volume 4 is silently wrong: its packet passed the 72-bit `D3` check but its payload is forged.

- Stage 1: direct assembly → mismatch. Full-map RS: zero erasures ⇒ the corrupt volume is trusted ⇒ mismatch. `null`.
- Stage 2: no choice points ⇒ skipped entirely.
- Stage 3 (`M = 1 ≥ 1`, adapter present): snapshot — all `Present`, all `VariantCount = 1`, all `HeadConfirmationCount = 1`. Base = ∅ (no collisions ⇒ no base mask). Candidates: all 11 volumes, shuffled by the `H5` seed; all counts equal ⇒ the shuffle order *is* the candidate order. Say it comes out `7, 3, 4, 9, 0, …`.
  - Mask `{7}`: `erasedData = 1 ≤ availableEcc = 1` ⇒ feasible. RS restores volume 7 — to its original value, since 7 was fine — assembly still fails on volume 4. `null`.
  - Mask `{3}`: same story. `null`.
  - Mask `{4}`: volume 4 erased ⇒ RS recomputes it from the syndrome ⇒ original bytes restored ⇒ SHA-256 passes, signature confirms ⇒ **success**.
- The ECC volume masks (`{10}` and any other `i ≥ N`) fail rule 1 of §14.4 — no erased data ⇒ skipped before any work.

`AttemptUpperBound = 0 + 11 + C(11, 2) + C(11, 3) = 11 + 55 + 165 = 231`; the search stopped after 3 attempts. With `M = 1`, at most one data volume per mask can be excluded — which is exactly why level 1 sweeps *all* candidates: with a single parity volume, the only hypothesis space is "which one volume lies".

## 20. Glossary

| Term | Meaning |
|---|---|
| Reception slot | Per-header decoder state: versions, counters, cursor, assembly. |
| Version (`SectorVariant`) | One candidate 64-byte payload for one sector number, with a confirmation counter. |
| Confirmation count | How many verified copies carried exactly this payload. |
| Collision slot | Sector number with ≥ 2 distinct versions. |
| Choice point | Collision slot whose leading versions tie on the maximum confirmation count. |
| Tied count | How many versions share that maximum — the branching factor of the slot. |
| Combination count | Product of tied counts — the size of the stage-2 space. |
| Rotation state | One synchronized step of all tie groups; the sequence has LCM period. |
| Preferred selection | The head version of every sector — attempt #1 of every assembly. |
| Attempt | One assembly trial arbitrated by the final gate. |
| Final gate | Trim to `FileSize`, then Exact(SHA-256), then Any(SHA-512) when active. |
| Exclusion mask | Sorted set of volume numbers declared erased for one RS attempt. |
| Base | The all-collisions exclusion that every stage-3 mask carries. |
| Candidate | A present, non-collision volume eligible for extra exclusion. |
| Shortlist | The `ShortlistSize` most suspicious candidates; frontier for levels ≥ 2. |
| Level | Extra-exclusion depth: 1 = singles, 2 = pairs, …, `MaxExtraExclusionLevel`. |
| `E` / available ECC | Erased data volumes per attempt / parity volumes surviving (`M − missingEcc − excludedEcc`). |
| `AttemptUpperBound` | Planner's a-priori mask count for progress display. |
| Soft budget | Time limit enforced between attempts, never mid-attempt. |
| Mt19937 | Vendored, hash-pinned Mersenne Twister used for the deterministic candidate shuffle. |

## 21. Document map

| Document | Content |
|---|---|
| [AlgorithmGuide](AlgorithmGuide.en.md) | Wire format and algorithms; packet fields (`H1..H5`, `D1..D3`, `S1..S2`), hashing, RS layer, pipeline overview. |
| [EncoderGuide](EncoderGuide.en.md) | Using the encoder: input modes, output text formats, statistics, reports. |
| [DecoderGuide](DecoderGuide.en.md) | Using the decoder: text filtering, scanning, multi-file reception, reports. |
| [AssemblyGuide](AssemblyGuide.en.md) (this file) | Slot model, version search, subset planning, limits and tuning. |
| [CollisionBench](CollisionBench.en.md) | Benchmark methodology and measured resilience under damage patterns. |
| [DeveloperGuide](DeveloperGuide.en.md) | Project layout, build, test suite, vendored references (`Mt19937`). |
| [UserGuide](UserGuide.en.md) | End-to-end usage from the console application and the GUI. |

Russian mirrors of every document carry the `ru` suffix (`AssemblyGuide.ru.md`, …).
