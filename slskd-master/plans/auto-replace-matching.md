# Auto-Replace Matching Improvement Plan

## Problem

Auto-replace fails to find alternate sources for common tracks because the file matcher
(`AutoReplaceMatcher.SelectBest`) requires an **exact, case-insensitive basename match**
(line 122 of `AutoReplaceMatcher.cs`):

```csharp
string.Equals(Basename(file.Filename), targetBasename, StringComparison.OrdinalIgnoreCase)
```

This means `Known for It (Freak Grips).flac` never matches
`Death Grips - Death Grips - 04 - Known for It (Freak Grips).flac`, even though they are
the same song from different users with different naming conventions.

## Available Signals in Soulseek Search Results

Each search result `File` object (`src/slskd/Search/Types/File.cs`) provides:

| Field | Type | Always present? | Matching value |
|-------|------|-----------------|----------------|
| `Filename` | string | Yes | Full remote path |
| `Size` | long | Yes | Strong signal |
| `Extension` | string | Derived | Format check |
| `Length` | int? | Sometimes | Track duration in seconds — strong signal |
| `BitRate` | int? | Sometimes | Bitrate in kbps |
| `BitDepth` | int? | Sometimes | 16/24 for lossless |
| `SampleRate` | int? | Sometimes | 44100, 48000, etc. |
| `IsVariableBitRate` | bool? | Sometimes | VBR vs CBR |

**Constraint (now resolved):** The original `Transfer` object previously stored only `Filename`
and `Size` — no audio metadata. This plan adds nullable metadata columns (`BitRate`, `BitDepth`,
`Length`, `SampleRate`, `IsVariableBitRate`) to the `Transfer` model and plumbs them through the
enqueue path so they are available for comparison during auto-replace.

## Proposed Approach: Token Similarity + Metadata Scoring

Replace the hard exact-basename filter with a **composite scoring system**:

### Signal 1 — Token Overlap (Jaccard Similarity)

Tokenize both basenames:
1. Strip directory path → basename
2. Strip extension
3. Replace non-alphanumeric with spaces
4. Split into words, discard single-character tokens
5. Compute Jaccard similarity: `|intersection| / |union|`

| Original | Candidate | Jaccard | Verdict |
|----------|-----------|---------|---------|
| `Death Grips - Death Grips - 04 - Known for It (Freak Grips)` | `04 - Known for It` | 0.50 | Match |
| `Death Grips - Death Grips - 04 - Known for It (Freak Grips)` | `Known for It (Freak Grips)` | 0.63 | Match |
| `Death Grips - Death Grips - 04 - Known for It (Freak Grips)` | `Some Other Song` | 0.00 | Reject |
| `Artist - 05 - Another Song` | `Artist - 04 - Known for It` | 0.25 | Reject |

### Signal 2 — Metadata Scoring (NEW)

When the original `Transfer` has stored audio metadata (populated at enqueue time if
available from the search/browse response), compare it against each candidate's metadata:

1. **Length (duration)**: If both sides have a value and they match within ±2s → +0.3 bonus.
   This is a very strong signal — same-length tracks are likely the same recording.
2. **BitDepth**: If both sides have the same value (e.g. 16, 24) → +0.1 bonus. Useful for lossless.
3. **SampleRate**: If both sides have the same value (e.g. 44100, 48000) → +0.1 bonus.
4. **BitRate**: If both sides have values within 32 kbps → +0.1 bonus. Weaker signal
   (same track can be encoded at different bitrates).

Metadata scoring is purely additive — it never rejects a candidate, only boosts better matches.
When metadata is null on both sides (no data available), the score is 0 and existing behavior
is preserved exactly.

### Signal 3 — Size Tolerance (existing)

Keep the existing `RequireExactSize` + `SizeToleranceBytes`. Validity check, not primary signal.

### Signal 4 — Extension Match (existing)

Keep `RequireSameExtension` as-is.

## Final Algorithm

```
For each candidate file from search results:
  1. Skip if null/locked/empty username/already-tried
  2. Skip if requireFreeUploadSlot and no free slot
  3. Skip if upload speed too low
  4. Compute token Jaccard similarity between original and candidate basenames
  5. If similarity < threshold → skip
  6. If requireSameExtension and extension mismatch → skip
  7. If requireExactSize and |size - original| > tolerance → skip
  8. Compute metadata similarity bonus (0.0–0.6) from Length/BitDepth/SampleRate/BitRate
  9. Score: (metadataScore, tokenSimilarity, hasFreeSlot, uploadSpeed, -queueLength)
  10. Pick highest-scoring candidate
```

When a replacement is enqueued, the candidate's metadata is stored on the new Transfer record,
so the lineage carries metadata forward through successive replacements.

## Data Flow

```
User initiates download             Auto-replace runs
       |                                   |
       v                                   v
  EnqueueAsync(Filename, Size)        Transfer has metadata
       |                                   |
       v                                   v
  Transfer stored (no metadata)      SelectBest(originalMeta, candidates)
                                           |
                                           v
                                    Candidate scored with metadata bonus
                                           |
                                           v
                                    EnqueueAsync(Candidate, Meta)
                                           |
                                           v
                                    Replacement Transfer stored WITH metadata
                                           |
                                           v
                                    (Next failure) metadata flows forward
```

## Knobs (configurable)

Add to `MatchOptions` in `Options.cs`:

```
min_token_similarity: 0.3    # default, range 0.0–1.0
                              # 0.0 = any token overlap passes
                              # 1.0 = exact match only (current behavior)
```

Metadata scoring has no config knob — it always runs when data is available. Zero config.

## Files to Modify

| File | Change |
|------|--------|
| **New:** `src/slskd/Transfers/Types/TransferFileMetadata.cs` | Simple record with metadata fields |
| `src/slskd/Transfers/Types/Transfer.cs` | Add 5 nullable metadata properties |
| `src/slskd/Transfers/Downloads/IDownloadService.cs` | Add `fileMetadata` optional param to `EnqueueAsync` |
| `src/slskd/Transfers/Downloads/DownloadService.cs` | Accept and store metadata on Transfer |
| `src/slskd/Transfers/Downloads/AutoReplace/AutoReplaceMatcher.cs` | Accept original metadata; add scoring; add metadata to candidate |
| `src/slskd/Transfers/Downloads/AutoReplace/AutoReplaceService.cs` | Pass transfer metadata to SelectBest; pass candidate metadata to EnqueueAsync |
| `src/slskd/Core/Options.cs` | Add `MinTokenSimilarity` to `MatchOptions` (default 0.3) |
| **New:** `src/slskd/Core/Data/Migrations/Z2026_07_03_TransferMetadataMigration.cs` | Add metadata columns to Transfers table |
| `src/slskd/Core/Data/Migrator.cs` | Register new migration |
| `tests/slskd.Tests.Unit/Transfers/Downloads/AutoReplace/AutoReplaceMatcherTests.cs` | Update for new signature; add metadata scoring tests |
| `tests/slskd.Tests.Unit/Transfers/Downloads/AutoReplace/AutoReplaceServiceTests.cs` | Update mocks for new EnqueueAsync signature |
| `config/slskd.example.yml` | Add `min_token_similarity` example |
| `docs/config.md` | Document new option |

## Files NOT Modified

- `AutoReplaceMonitor.cs` — monitoring unchanged
- `Program.cs` — no configuration changes needed
- `TransfersDbContext.cs` — no special EF config needed for nullable value type columns
- `TransfersController.cs` — existing callers pass default null for new optional param
- `Application.cs` — existing caller passes default null

## Verification

1. Exact filename matches continue to work (Jaccard = 1.0, passes trivially)
2. Differently-named same-track files matched (test with known failure cases)
3. Different tracks with incidental word overlap are still rejected (Jaccard < 0.3)
4. Metadata scoring boosts candidates with matching Length/BitDepth/SampleRate/BitRate
5. No regression when metadata is null (existing exact-match tests pass unchanged)
6. Metadata propagates through auto-replace lineage to replacement transfers
7. Migration is idempotent (columns added once, skipped on subsequent runs)
