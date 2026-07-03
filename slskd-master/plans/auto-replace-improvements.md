# Auto-Replace & Auto-Retry: Comprehensive Improvement Plan

## Executive Summary

The auto-replace system works — the logs show it searching, ranking candidates, and selecting the best match. But in practice, most replacement attempts **fail at the enqueue stage** because the selected remote user is offline, has a full queue, or times out. The system gives up after a single attempt per cycle, wasting the work of scoring and ranking the entire candidate list. Additionally, the strict size matching (`SizeToleranceBytes = 10240` with `RequireExactSize = true`) rejects the vast majority of candidates because different rips of the same track are rarely byte-identical, even when the percentage difference is negligible.

This plan addresses the **three most impactful bottlenecks** visible in production logs, plus two high-value enhancements:

| # | Improvement | Impact | Effort | Risk |
|---|-------------|--------|--------|------|
| 1 | **Fallback Candidate Queue** — try N candidates sequentially per cycle | Very High | Medium | Low |
| 2 | **Percentage-Based Size Tolerance** — unblock size-rejected candidates | High | Low | Low |
| 3 | **User Failure Tracking** — stop wasting time on unresponsive users | High | Low | Low |
| 4 | **Completion Cascade Retry** — react to replacement failure immediately | High | Medium | Low |
| 5 | **Extensible Extension Matching** — cross-format candidate matching | Medium | Low | Low |

---

## Improvement 1: Fallback Candidate Queue

### Problem

`AutoReplaceMatcher.SelectBest()` returns a single candidate. If `EnqueueAsync` fails for any reason (remote user offline, queue full, timeout, file rejected), the replacement fails permanently until the next `ScanAsync` tick (up to 5 seconds later) — and on that tick, the cooldown (`SearchCooldown = 60s`) likely prevents retry anyway.

**Log evidence:**
```
Auto-replace searching for ... (attempt 1/5)
  Rank #1: user1 - song.flac (meta=0.000, token=1.000, ...)
  Rank #2: user2 - song.flac (meta=0.000, token=1.000, ...)
  Rank #3: user3 - song.flac (meta=0.000, token=0.800, ...)
  Selected: user1 - song.flac
Auto-replace failed to enqueue song.flac from user1: The wait timed out
```

Ranks #2 and #3 are wasted because SelectBest returned only the winner. On the next scan tick, the same search runs again (assuming cooldown has elapsed), and user1 may still be offline.

### Solution

Change `SelectBest` to return **all ranked candidates above the similarity threshold**, and have `TryReplaceAsync` try them sequentially until one succeeds.

#### Data Flow

```
TryReplaceAsync:
  1. SearchAsync → responses
  2. SelectBest → returns IReadOnlyList<AutoReplaceCandidate> (ranked)
  3. If empty → return false
  4. For each candidate in ranked order:
     a. EnqueueAsync(candidate)
     b. If success → return true
     c. If failure → add candidate.Username to attemptedUsernames, continue
  5. All failed → return false
```

#### SelectBest Changes

- **Return type:** `AutoReplaceCandidate` → `IReadOnlyList<AutoReplaceCandidate>` (top N)
- **New threshold parameter** (optional): a minimum score floor to keep poor candidates out
- **Default behavior preserved:** callers passing `?maxResults` = 1 get exactly one result (backward compat)
- Actually, simpler: just return all scored candidates sorted. Let the caller decide.

**New signature:**

```csharp
public static IReadOnlyList<AutoReplaceCandidate> SelectBest(
    string originalFilename,
    long originalSize,
    int? originalBitRate,
    int? originalBitDepth,
    int? originalLength,
    int? originalSampleRate,
    IEnumerable<Response> responses,
    IEnumerable<string> excludedUsernames,
    Options.TransfersOptions.GlobalDownloadOptions.AutoReplaceOptions.MatchOptions options)
```

Returns the full ranked list (not just the top 1). Empty list when no candidates found.

#### AutoReplaceService Changes

**`TryReplaceAsync`** becomes a loop:

```csharp
public async Task<bool> TryReplaceAsync(Transfer transfer, AutoReplaceReason reason, CancellationToken cancellationToken = default)
{
    // ... existing guards (enabled, cancelled, budget, cooldown) ...

    var candidates = AutoReplaceMatcher.SelectBest(...);

    if (candidates.Count == 0)
    {
        Log.Information("Auto-replace found no suitable alternate source for {Filename}", transfer.Filename);
        return false;
    }

    var attemptedUsernames = ParseUsernames(transfer.AttemptedUsernames);

    foreach (var candidate in candidates)
    {
        if (attemptedUsernames.Contains(candidate.Username))
            continue;

        attemptedUsernames.Add(candidate.Username);

        var lineage = new TransferLineage
        {
            ReplacesId = transfer.Id,
            ReplacementAttempts = transfer.ReplacementAttempts + 1,
            AttemptedUsernames = FormatUsernames(attemptedUsernames),
        };

        var candidateMetadata = BuildMetadata(candidate);

        try
        {
            var (enqueued, failed) = await Downloads.EnqueueAsync(
                username: candidate.Username,
                files: [(candidate.Filename, candidate.Size)],
                batchId: transfer.BatchId,
                lineage: lineage,
                metadata: candidateMetadata,
                cancellationToken: cancellationToken);

            if (enqueued.Count > 0)
            {
                Log.Information(
                    "Auto-replace enqueued {Filename} from alternate source {Username} (replacing {OriginalId}, attempt {Attempt}/{Max})",
                    candidate.Filename, candidate.Username, transfer.Id, transfer.ReplacementAttempts + 1, options.MaxCandidates);
                return true;
            }

            Log.Information(
                "Auto-replace candidate {User} - {File} failed: {Message}",
                candidate.Username, candidate.Filename, failed.FirstOrDefault().Message ?? "unknown");
        }
        catch (Exception ex)
        {
            Log.Warning(
                "Auto-replace candidate {User} - {File} failed with exception: {Message}",
                candidate.Username, candidate.Filename, ex.Message);
        }
    }

    Log.Information(
        "Auto-replace exhausted all {Count} candidates for {Filename}",
        candidates.Count, transfer.Filename);
    return false;
}
```

#### Key Design Decisions

1. **ReplacementAttempts tracking:** The lineage's `ReplacementAttempts` is incremented once per call to `TryReplaceAsync`, not once per candidate tried. This means `MaxCandidates = 5` means "5 search rounds", not "5 individual attempts". Rationale: the cooldown and search are the expensive operations; trying N candidates from the same search result is cheap. However, we should reconsider: if we fail 3 candidates in one round, does that count as 1 attempt or 3?

   **Decision:** Count each individual enqueue attempt against the budget. Set `lineage.ReplacementAttempts = transfer.ReplacementAttempts + 1` for the *first* candidate tried. If the first fails, the next round's `ReplacementAttempts` will be `transfer.ReplacementAttempts + 2` (because the replacement transfer from the first attempt carries `ReplacementAttempts + 1`). This naturally increments across rounds.

   Wait — this is tricky. The `TransferLineage.ReplacementAttempts` is set on the *new* transfer and compared against `MaxCandidates` on the *next* scan. Let me trace through:

   - **Round 1:** `original.ReplacementAttempts = 0`. First candidate tried → new transfer gets `ReplacementAttempts = 1`. First candidate fails → this new transfer is in Failed state with `ReplacementAttempts = 1`.
   - **Round 2 (next scan tick):** `failedTransfer.ReplacementAttempts = 1`. Check `1 >= MaxCandidates(5)` → no, continue. Try 2nd candidate → new transfer gets `ReplacementAttempts = 2`... but wait, the `attemptedUsernames` on the *new* transfer includes the first candidate's username.

   Actually, the problem is cleaner than I thought. The *original* transfer never gets updated — the failed replacement creates a *new* Transfer record with the lineage. The scan picks up the most recent failed transfer in the chain. Each link in the chain has `ReplacementAttempts` incremented by 1.

   So if we try 3 candidates in round 1 and they all fail, we create 3 new Transfer records, each with `ReplacementAttempts = 1`. The scan picks up all 3 of them, and tries to replace each — resulting in duplicate work.

   **Better approach:** Only create ONE replacement transfer per `TryReplaceAsync` call (the one that succeeds). If none succeed, don't create any — the original transfer remains as-is and will be retried on the next scan tick. But we need to avoid re-trying the same candidates.

   **Revised decision:** Count each `TryReplaceAsync` call as one attempt. Keep `ReplacementAttempts` incrementing by 1 per call. The `attemptedUsernames` set includes all users tried across all previous rounds, so we don't repeat them. This means:

   - Round 1: original has `ReplacementAttempts = 0`. Try candidates [A, B, C]. All fail. Original still has `ReplacementAttempts = 0` and `AttemptedUsernames` = "A\nB\nC". 
   - Wait, `AttemptedUsernames` is on the Transfer record. If no new transfer is created, we'd need to update the original transfer's `AttemptedUsernames` and `ReplacementAttempts`.

   This is getting complex. Let me simplify.

   **Simplest correct approach:**
   1. `TryReplaceAsync` gets the full candidate list
   2. For each candidate, try `EnqueueAsync`
   3. If one succeeds: create the replacement Transfer (with incremented `ReplacementAttempts` + `AttemptedUsernames`)
   4. If none succeed: increment `ReplacementAttempts` on the *original* transfer (update DB) and update `AttemptedUsernames` on it too

   But modifying the original transfer requires a DB update. That's a bigger change.

   **Even simpler approach (KISS):**
   - Keep the existing single-candidate-return flow
   - Add a `CandidateQueue` field to the Transfer model: `string CandidateQueue` (JSON array or newline-delimited)
   - When `SelectBest` returns candidates, store the full ranked list in `CandidateQueue` on the *original* transfer (DB update)
   - On the next scan, before searching, check `CandidateQueue`. If non-empty, pop the next candidate and try it
   - Only increment `ReplacementAttempts` when a search is performed (the queue is exhausted)

   Actually this is also complex. Let me think about what's simplest.

   **KISS approach:** Just change `SelectBest` to return top N candidates, and have `TryReplaceAsync` try them in a loop. Only the **first** enqueue success creates a replacement transfer. If all fail, we don't create a replacement transfer, and the original stays in the failed state for the next scan tick. To avoid repeating the same candidates, append them all to `attemptedUsernames` in the `lastAttempt` entry... but `lastAttempt` is just a timestamp.

   **Revised KISS:** Use the `AttemptedUsernames` field on the Transfer. In the original transfer, `AttemptedUsernames` is `null`. When we try candidates, we want to record all tried usernames. But we can't modify the original transfer without a DB update.

   **Cleanest solution:** Store the candidate queue right on the Transfer model:

   ```csharp
   // New property on Transfer
   public string PendingCandidatesJson { get; set; }
   ```

   And add a method on `AutoReplaceService` that:
   1. If `PendingCandidatesJson` is null/empty → do a search, rank candidates, store the full queue JSON on the transfer (DB update), then try the first
   2. If `PendingCandidatesJson` is non-empty → deserialize, pop the next, try it, update the queue (DB update) with the remaining

   This is the cleanest approach but involves a DB write per candidate attempt.

   **Final decision — simplest possible:**
   - Change `SelectBest` to return `IReadOnlyList<AutoReplaceCandidate>`
   - In `TryReplaceAsync`, try candidates sequentially
   - If one succeeds → enqueue replacement transfer (with `ReplacementAttempts + 1` and updated `AttemptedUsernames`)
   - If none succeed → **do not create any new transfers**. The original transfer stays in failed state.
   - To prevent re-trying the same candidates: the `cooldown` mechanism prevents re-searching, but we need to remember which users we tried. Use a **per-file in-memory set** of tried users (a `ConcurrentDictionary<Guid, HashSet<string>>`).

   This avoids DB changes, avoids touching the Transfer model, and is simple:

   ```csharp
   private ConcurrentDictionary<Guid, HashSet<string>> TriedUsernames { get; } = new();
   ```

   When `TryReplaceAsync` fails on all candidates, store the tried usernames: `TriedUsernames[transfer.Id] = attemptedUsernames`. On the next call, merge `TriedUsernames[transfer.Id]` into the excluded set.

   When a replacement *succeeds*, the new transfer gets a new `Id`, so the old entry in `TriedUsernames` is irrelevant. Clean up stale entries periodically or on success.

   This is the KISS winner. Let me finalize this approach.

### Configuration Changes

None. Fallback queue is always active (tries all candidates from `SelectBest`). The existing `MaxCandidates` already limits total rounds.

### Code Changes

| File | Change |
|------|--------|
| `AutoReplaceMatcher.cs` | `SelectBest` returns `IReadOnlyList<AutoReplaceCandidate>` instead of `AutoReplaceCandidate` |
| `AutoReplaceService.cs` | `TryReplaceAsync` loops over candidates; adds `TriedUsernames` dictionary; returns first success |
| `AutoReplaceService.cs` | Add `ConcurrentDictionary<Guid, HashSet<string>> TriedUsernames` field |
| `AutoReplaceService.cs` | On failure of all candidates, store tried usernames per transfer ID |
| Test files | Update `SelectBest` mock expectations; add tests for candidate loop behavior |

### Test Changes

| Test | Purpose |
|------|---------|
| `TryReplace_TriesNextCandidate_WhenFirstFails` | Verify loop continues after EnqueueAsync failure |
| `TryReplace_ReturnsTrue_WhenAnyCandidateSucceeds` | Verify loop stops on first success |
| `TryReplace_ExhaustsAllCandidates_ReturnsFalse` | Verify all candidates tried, returns false |
| `TryReplace_DoesNotRepeat_AlreadyTriedUsernames` | Verify in-memory blacklist prevents duplicates |

---

## Improvement 2: Percentage-Based Size Tolerance

### Problem

`RequireExactSize` with `SizeToleranceBytes = 10240` (10 KB) is too tight for large files. A 100 MB FLAC rip may differ from another user's rip by 500 KB due to different encoder versions or padding — a 0.5% difference that's musically identical. The logs show most candidates rejected by size.

**Log evidence:**
```
Skipping file .../song.flac from user2: size mismatch (expected=27891234, got=27945821, tolerance=10240)
```

Difference: 54,587 bytes (0.2%) — rejected. Yet the file is the same song, same format.

### Solution

Add a **percentage-based tolerance** beside the fixed byte tolerance. Both tolerances are evaluated independently; a candidate passes if it's within **either** tolerance.

**New config option:**

```yaml
size_tolerance_percent: 1.0   # default: 1.0 = 1% of file size
                              # set to 0 to disable percentage tolerance
```

**Updated matching logic:**

```csharp
bool sizeOk = !options.RequireExactSize;

if (options.RequireExactSize)
{
    var byteDiff = Math.Abs(file.Size - originalSize);
    var percentDiff = (double)byteDiff / originalSize * 100;

    // pass if within byte tolerance OR within percent tolerance
    sizeOk = byteDiff <= options.SizeToleranceBytes
          || (options.SizeTolerancePercent > 0 && percentDiff <= options.SizeTolerancePercent);
}
```

Default `SizeTolerancePercent = 1.0` means a 100 MB file can differ by up to 1 MB (1,048,576 bytes) — far more permissive than the 10 KB default. A 5 MB MP3 can differ by up to 50 KB.

### Backward Compatibility

- `SizeTolerancePercent = 0` → disabled (only byte tolerance applies)
- `SizeTolerancePercent = 1.0` (default) → new, more permissive behavior
- Users who want the old strict behavior set `size_tolerance_percent: 0`

### Configuration Changes

| File | Change |
|------|--------|
| `Options.cs` `MatchOptions` | Add `SizeTolerancePercent` property (double, default 1.0, range 0.0–100.0) |
| `config/slskd.example.yml` (3 copies) | Add `size_tolerance_percent: 1.0` |
| `docs/config.md` | Document new option |

### Code Changes

| File | Change |
|------|--------|
| `AutoReplaceMatcher.cs` | Modify size check to also consider `SizeTolerancePercent` |
| `Options.cs` | Add property + validation attribute |

### Test Changes

| Test | Purpose |
|------|---------|
| `Accepts_Size_Within_Percent_Tolerance` | 2% diff on a file, 1% tolerance → reject |
| `Accepts_Size_Within_Percent_When_Byte_Exceeded` | 50KB diff on 100MB file, 1% tolerance → accept |
| `Size_Percent_Tolerance_Zero_Disables_Percent` | Percent = 0, byte = 10KB, diff = 20KB on 1MB file → reject |
| Update existing `Accepts_Size_Within_Tolerance` | Ensure byte tolerance still works independently |

---

## Improvement 3: User Failure Tracking (Session-Level Blacklist)

### Problem

When `EnqueueAsync` fails for a remote user (timeout, rejection), the next scan tick may try the same user again — because `AttemptedUsernames` only tracks users tried within the *current lineage chain*, and if no replacement transfer was created, the original transfer's `AttemptedUsernames` is never updated. The `TriedUsernames` in-memory dictionary from Improvement 1 handles this intra-session, but across restart it's lost.

More fundamentally: some users are just unreliable (always full queue, always offline). We should track per-user failure counts and deprioritize them.

### Solution

Add an in-memory **per-session user failure tracker** with decay:

```csharp
public class UserFailureTracker
{
    private readonly ConcurrentDictionary<string, UserFailureRecord> failures = new();

    public record UserFailureRecord(int Count, DateTime LastFailure);

    public void RecordFailure(string username)
    {
        failures.AddOrUpdate(username,
            _ => new UserFailureRecord(1, DateTime.UtcNow),
            (_, existing) => new UserFailureRecord(existing.Count + 1, DateTime.UtcNow));
    }

    public bool IsUnreliable(string username, int threshold = 3, TimeSpan? window = null)
    {
        window ??= TimeSpan.FromMinutes(30);

        if (!failures.TryGetValue(username, out var record))
            return false;

        if (DateTime.UtcNow - record.LastFailure > window)
        {
            // decay: reset after window expires
            failures.TryRemove(username, out _);
            return false;
        }

        return record.Count >= threshold;
    }

    public void RecordSuccess(string username)
    {
        failures.TryRemove(username, out _); // clean slate on success
    }
}
```

#### Integration with AutoReplaceService

- **On enqueue failure** (catch block): `UserFailures.RecordFailure(candidate.Username)`
- **On enqueue success**: `UserFailures.RecordSuccess(candidate.Username)`
- **Before candidate loop**: filter out unreliable users from candidates
- **Threshold**: configurable via `max_user_failures` (default 3) over `user_failure_window_minutes` (default 30)

This is orthogonal to the fallback queue — it's a pre-filter that skips known-bad users before trying them.

### Configuration Changes

| File | Change |
|------|--------|
| `Options.cs` `AutoReplaceOptions` | Add `MaxUserFailures` (int, default 3) and `UserFailureWindowMinutes` (int, default 30) |

### Code Changes

| File | Change |
|------|--------|
| **New:** `UserFailureTracker.cs` | Per-session failure tracking with decay |
| `AutoReplaceService.cs` | Inject/factory-create `UserFailureTracker`; call it on enqueue success/failure; filter candidates |
| `AutoReplaceService.cs` | Wire into `TryReplaceAsync` |

### Test Changes

| Test | Purpose |
|------|---------|
| `UserFailureTracker_Records_Failure` | Basic record/get |
| `UserFailureTracker_Decays_After_Window` | Reset after expiry |
| `UserFailureTracker_Success_Resets` | Success clears failures |
| `TryReplace_Skips_Unreliable_User` | Verify unreliable user is filtered out |
| `TryReplace_Records_Failure_On_Enqueue_Error` | Verify failure recorded on exception |

---

## Improvement 4: Completion Cascade Retry

### Problem

After `EnqueueAsync` succeeds, the auto-replace system does not monitor the *replacement* transfer's outcome. If the replacement itself fails mid-transfer (rejected, timed out, stalled), no new replacement fires until the next `ScanAsync` tick. This creates a delay of up to 5 seconds (the monitor interval), and if the cooldown hasn't expired, the replacement is skipped entirely.

### Solution

Subscribe to the replacement transfer's completion event and trigger an immediate cascade retry if it fails.

#### Data Flow

```
AutoReplaceService.TryReplaceAsync succeeds
  → Enqueues candidate as new Transfer (the "replacement")
  → Subscribe to Transfer's state changes
  → On Completion/Error/Timeout:
    → Call TryReplaceAsync(replacement, AutoReplaceReason.Failure)
      → Search for another candidate (new search)
      → Try next candidate in queue
```

#### Implementation

The `DownloadService` already has state management. We need a way to observe transfer completion. Options:

1. **Polling in AutoReplaceMonitor:** Add a separate check for "recently-enqueued replacements that failed" — but this is what the existing scan does (minus the cooldown bypass).

2. **Event hook:** Subscribe to an event or callback from `DownloadService` when a transfer transitions to a terminal state.

3. **Override in EnqueueAsync:** Return the enqueued transfer(s) and have the caller track them.

**KISS approach:** Extend `AutoReplaceService` to maintain a set of "watched" transfer IDs. On each `ScanAsync` tick (which runs every 5 seconds), check if any watched transfers have reached a terminal failed state. If so, trigger an immediate cascade retry — bypassing the cooldown.

```csharp
private ConcurrentDictionary<Guid, DateTime> WatchList { get; } = new();

// In TryReplaceAsync, after successful enqueue:
WatchList.TryAdd(enqueuedTransfer.Id, DateTime.UtcNow);

// In ScanAsync:
foreach (var (id, enqueuedAt) in WatchList)
{
    var transfer = Downloads.Find(t => t.Id == id);
    if (transfer is null || IsTerminalFailure(transfer.State))
    {
        WatchList.TryRemove(id, out _);
        if (transfer is not null)
        {
            await TryReplaceAsync(transfer, AutoReplaceReason.Failure, cancellationToken);
        }
    }
    else if (transfer.State.HasFlag(TransferStates.Completed | TransferStates.Succeeded))
    {
        WatchList.TryRemove(id, out _); // success, stop watching
    }
}
```

This is simple, uses existing scan infrastructure, and bypasses the cooldown for cascade retries (since we check `WatchList` first and call `TryReplaceAsync` directly).

#### Bypassing the Cooldown

Add a `force` parameter to `TryReplaceAsync`:

```csharp
public async Task<bool> TryReplaceAsync(Transfer transfer, AutoReplaceReason reason, 
    bool bypassCooldown = false, CancellationToken cancellationToken = default)
```

When `bypassCooldown = true`, skip the `LastAttempt` cooldown check:

```csharp
if (!bypassCooldown 
    && LastAttempt.TryGetValue(transfer.Id, out var last) 
    && (DateTime.UtcNow - last).TotalMilliseconds < options.SearchCooldown)
{
    return false;
}
```

### Configuration Changes

None. Cascade retry is always active with the existing cooldown bypass.

### Code Changes

| File | Change |
|------|--------|
| `AutoReplaceService.cs` | Add `WatchList` dictionary; add `bypassCooldown` param; update `ScanAsync` to check watch list |
| `AutoReplaceService.cs` | After successful enqueue, add transfer to watch list |

### Test Changes

| Test | Purpose |
|------|---------|
| `TryReplace_Forces_Cascade_When_BypassCooldown` | Verify cooldown is skipped |
| `Scan_Watches_Replacement_And_Cascades_On_Failure` | Full integration: enqueue → failure → cascade |

---

## Improvement 5: Extensible Extension Matching

### Problem

`RequireSameExtension` rejects candidates with different file extensions, even when the audio content is identical. A user who downloaded a FLAC rip may find only MP3 versions from other users. In the current strict mode, these are all rejected.

**Log evidence:**
```
Skipping file .../song.mp3 from user2: extension mismatch (expected=flac, got=mp3)
```

### Solution

Allow the user to define **extension equivalence groups**. Candidates are accepted if their extension is in the same group as the original's extension.

**New config option:**

```yaml
extension_groups:
  # Default groups (hardcoded fallback if config not present):
  lossless: [flac, wav, aiff, alac, ape, wv]
  lossy: [mp3, m4a, ogg, opus, aac, wma]
```

**Matching logic:**

```csharp
if (options.RequireSameExtension)
{
    var candidateExt = Extension(file.Filename);
    var targetExt = Extension(originalFilename);

    bool sameExt = string.Equals(candidateExt, targetExt, StringComparison.OrdinalIgnoreCase);

    // If not same extension, check if they're in the same equivalence group
    if (!sameExt && options.ExtensionGroups is not null)
    {
        sameExt = options.ExtensionGroups.Any(group =>
            group.Contains(targetExt, StringComparer.OrdinalIgnoreCase)
            && group.Contains(candidateExt, StringComparer.OrdinalIgnoreCase));
    }

    if (!sameExt)
    {
        // reject
        continue;
    }
}
```

**Hardcoded defaults:** If `ExtensionGroups` is not configured (null), fall back to the two built-in groups above. Users can override by specifying `extension_groups` in config, replacing the defaults entirely.

### Configuration Changes

| File | Change |
|------|--------|
| `Options.cs` `MatchOptions` | Add `ExtensionGroups` property — `List<List<string>>` or `List<string[]>` |
| `config/slskd.example.yml` (3 copies) | Add `extension_groups:` with default groups |
| `docs/config.md` | Document new option |

### Code Changes

| File | Change |
|------|--------|
| `AutoReplaceMatcher.cs` | Extend extension check to also test against equivalence groups |

### Test Changes

| Test | Purpose |
|------|---------|
| `Accepts_Equivalent_Extension_By_Group` | `.flac` original, `.wav` candidate in same group → accepted |
| `Rejects_Different_Group_Extension` | `.flac` original, `.mp3` candidate in different group → rejected |
| `Accepts_Same_Extension_When_No_Groups_Defined` | Backward compat: no groups, same ext → accepted |
| `Rejects_Different_Extension_When_No_Groups_Defined` | Backward compat: no groups, different ext → rejected |

---

## Implementation Order

| Step | Improvement | Files | Depends On |
|------|-------------|-------|------------|
| 1 | **Percentage Size Tolerance** | Options.cs, AutoReplaceMatcher.cs, config, docs | Nothing |
| 2 | **Extensible Extension Matching** | Options.cs, AutoReplaceMatcher.cs, config, docs | Nothing |
| 3 | **User Failure Tracker** | New file, AutoReplaceService.cs, Options.cs | Nothing |
| 4 | **Fallback Candidate Queue** | AutoReplaceMatcher.cs, AutoReplaceService.cs | Step 3 (uses tracker) |
| 5 | **Completion Cascade Retry** | AutoReplaceService.cs | Step 4 |

Steps 1–2 are independent of each other and of 3–5. Steps 3–5 build on each other.

### Recommended Order

1. **Step 1 (Percent Tolerance)** — single config option, single method change, immediate impact
2. **Step 2 (Extension Groups)** — single config option, single method change, immediate impact
3. **Step 3 (User Failure Tracker)** — new class, wire into service
4. **Step 4 (Fallback Queue)** — restructure `SelectBest` return, loop in `TryReplaceAsync`
5. **Step 5 (Cascade Retry)** — add watch list, bypass cooldown

---

## Verification Strategy

### Unit Tests

Each step adds or modifies tests as described above. Run `dotnet test tests/slskd.Tests.Unit` after each step.

### Integration Verification

After all steps are implemented:
1. Config validation: start with each new option at default, at boundary values, disabled
2. Log inspection: verify log messages show candidate fallback, size percent, extension groups
3. End-to-end: manually trigger a failed download and verify auto-replace cycles through candidates

### Regression Tests

All existing 1286 tests must continue to pass after each step:
- `MatchOptions` with defaults → legacy behavior preserved (percent=0, no extension groups)
- `SelectBest` with single candidate → existing ranking tests (free slot, speed, queue, tie-break)
- `TryReplaceAsync` → existing tests (disabled, cancelled, budget, no candidate, lineage)

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Fallback queue creates too many enqueue attempts (network spam) | Medium | Medium | `MaxCandidates` already limits rounds; candidate count per round is bounded by search results |
| User failure tracker decays too slowly, blacklisting users permanently | Low | Low | Decay window is configurable; in-memory only (reset on restart) |
| Extension groups are too permissive, matching wrong formats | Low | Medium | Only applies when `RequireSameExtension = true`; groups are user-configurable |
| Cascade retry causes infinite loop (failed replacement → cascade → same replacement fails → infinite) | Low | High | `ReplacementAttempts` budget applies; each cascade is a new `TryReplaceAsync` call that checks budget |
| DB writes for `PendingCandidatesJson` on every candidate try | Not used | N/A | Current design avoids DB writes for candidate tracking (in-memory only) |

---

## Appendix: Transfer Model Changes Needed

If we decide to persist the candidate queue or user failures across restarts, we'd add to `Transfer.cs`:

```csharp
// Optional: store the serialized candidate queue on the failed transfer
// so it survives restarts
public string PendingCandidatesJson { get; set; }
```

This is NOT needed for the initial implementation (in-memory only). Adding it later is straightforward.

---

## Appendix: `MatchOptions` — Final Property Table

```csharp
public class MatchOptions
{
    // Existing:
    public bool RequireExactSize { get; init; } = true;
    public int SizeToleranceBytes { get; init; } = 10240;
    public bool RequireSameExtension { get; init; } = true;
    public bool RequireFreeUploadSlot { get; init; } = false;
    public int MinimumUploadSpeed { get; init; } = 0;
    public double MinTokenSimilarity { get; init; } = 0.3;

    // New (Step 1):
    [Range(0.0, 100.0)]
    public double SizeTolerancePercent { get; init; } = 1.0;

    // New (Step 2):
    public List<List<string>> ExtensionGroups { get; init; } = null; // null = use hardcoded defaults
}
```

And for `AutoReplaceOptions`:

```csharp
public class AutoReplaceOptions
{
    // Existing:
    public bool Enabled { get; init; } = true;
    public bool OnFailure { get; init; } = true;
    public bool OnStall { get; init; } = true;
    public int MaxCandidates { get; init; } = 5;
    public int StallTimeout { get; init; } = 60_000;
    public int QueueStallTimeout { get; init; } = 1_800_000;
    public int MaxAge { get; init; } = 3_600_000;
    public int SearchCooldown { get; init; } = 60_000;
    public MatchOptions Match { get; init; } = new();
    public SearchOptions Search { get; init; } = new();

    // New (Step 3):
    [Range(1, 100)]
    public int MaxUserFailures { get; init; } = 3;

    [Range(1, 1440)]
    public int UserFailureWindowMinutes { get; init; } = 30;
}
```
