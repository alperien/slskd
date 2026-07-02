# Auto-Replace Improvements

## Overview

This document describes five targeted fixes to slskd's download auto-replace system, making downloads more reliable for headless/automated operation. Each fix closes a specific gap where the original implementation would give up or behave suboptimally.

The changes span four files and add roughly 150 lines of production code plus 250 lines of tests.

---

## Gap 1: No Inline Auto-Replace

### Problem

When a download exhausted same-source retries, the error was propagated up and the auto-replace system would only pick it up on the next background poll cycle (every 5 seconds). This meant a 0–5 second delay between a download failing and the search for an alternate source beginning.

### Solution

Add inline auto-replace: immediately attempt to find an alternate source in the same catch blocks that mark the transfer as failed.

#### Circular Dependency

`DownloadService` needs `IAutoReplaceService`, but `AutoReplaceService` already takes `IDownloadService`. Injecting `IAutoReplaceService` directly creates a circular dependency at construction time.

**Fix:** Inject `IServiceProvider` instead, and resolve `IAutoReplaceService` at runtime only when needed:

```csharp
// DownloadService constructor (new parameter)
IServiceProvider serviceProvider

// Usage inside TryAutoReplaceOnFailureAsync:
var autoReplaceService = ServiceProvider.GetRequiredService<IAutoReplaceService>();
await autoReplaceService.TryReplaceAsync(transfer, AutoReplaceReason.Failure);
```

#### The TryAutoReplaceOnFailureAsync Method

A new `protected virtual` method (following the existing `EmitMetrics()` pattern for testability):

```csharp
protected virtual async Task TryAutoReplaceOnFailureAsync(Transfer transfer, Exception exception)
{
    // 1. Skip if user cancelled — no point replacing a cancelled transfer
    if (exception is OperationCanceledException) return;

    // 2. Skip if auto-replace is disabled in config
    if (!OptionsMonitor.CurrentValue.Transfers.Download.AutoReplace.Enabled) return;

    // 3. Resolve service, attempt replacement, swallow errors
    try
    {
        var svc = ServiceProvider.GetRequiredService<IAutoReplaceService>();
        await svc.TryReplaceAsync(transfer, AutoReplaceReason.Failure);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Auto-replace failed for {Filename}", transfer.Filename);
    }
}
```

Called from both `catch` blocks in `DownloadAsync()` immediately after `TryFail()`:

```csharp
catch (Exception ex) when (ex is OperationCanceledException || ex is TimeoutException)
{
    TryFail(transfer.Id, exception: ex);
    await TryAutoReplaceOnFailureAsync(transfer, ex);  // <-- new
    throw;
}
catch (Exception ex)
{
    TryFail(transfer.Id, exception: ex);
    await TryAutoReplaceOnFailureAsync(transfer, ex);  // <-- new
    throw;
}
```

The original transfer remains failed (the new replacement gets its own transfer record if a candidate is found), and the exception is still re-thrown for upstream handling.

#### Files Changed

- `src/slskd/Transfers/Downloads/DownloadService.cs`: constructor, property, new method, both catch blocks

#### Tests

`tests/slskd.Tests.Unit/Transfers/Downloads/DownloadServiceTests.cs` (new file):

| Test | Scenario |
|---|---|
| `TryAutoReplaceOnFailure_Returns_Without_Replacement_When_Cancelled` | `OperationCanceledException` → `TryReplaceAsync` never called |
| `TryAutoReplaceOnFailure_Returns_Without_Replacement_When_Disabled` | Auto-replace disabled in options → `TryReplaceAsync` never called |
| `TryAutoReplaceOnFailure_Calls_TryReplace_When_Enabled` | Normal flow → `TryReplaceAsync` called once with correct args |
| `TryAutoReplaceOnFailure_Does_Not_Throw_When_TryReplace_Throws` | `TryReplaceAsync` throws → exception swallowed |
| `TryAutoReplaceOnFailure_Does_Not_Throw_When_Service_Not_Registered` | `GetRequiredService` throws → exception swallowed |

Uses a `TestableDownloadService` subclass to expose the `protected virtual` method (same pattern as existing `EmitMetrics` tests).

---

## Gap 2: Single Search Strategy — No Fallbacks

### Problem

When a download failed and auto-replace attempted to find an alternate source, it used exactly one strategy: build a query from the filename, search the network, and match results against strict criteria (same extension, exact size, free upload slot required). If this produced no candidate, the system gave up immediately.

### Solution

Introduce two additional fallback strategies, tried in order after the initial exact match fails.

#### Flow

```
TryReplaceAsync(transfer)
  │
  ├── Strategy 1: Exact search (existing)
  │     BuildQuery → Search → SelectBest(original match options)
  │
  ├── if null → Strategy 2: Relaxed match
  │     Same search results + relaxed MatchOptions:
  │       • RequireFreeUploadSlot = false
  │       • RequireSameExtension = false
  │       • RequireExactSize = false
  │
  ├── if null → Strategy 3: Browse fallback
  │     Client.BrowseAsync(originalUsername) → SelectBest(match options)
  │     (Sets HasFreeUploadSlot = true so user's own files aren't filtered out)
  │
  └── if still null → give up
```

#### Why These Fallbacks?

- **Relaxed match**: The original user may have the file with a different extension (`.ogg` vs `.mp3`), a slightly different size (different rip/encode), or may not have a free upload slot at that moment. Dropping these strict filters lets candidates through that would otherwise be rejected.

- **Browse fallback**: If nobody on the network is sharing the file, the original user might still have it (they were sharing it when the download started). Browsing the original user's share directory finds files that search can't. Also catches cases where the search query was too aggressive in stripping characters.

#### BrowseAsync Implementation

```csharp
private async Task<IEnumerable<Response>> BrowseAsync(string username, CancellationToken cancellationToken)
{
    try
    {
        var browseResponse = await Client.BrowseAsync(username, cancellationToken: cancellationToken);
        var allFiles = browseResponse.Directories
            .SelectMany(d => d.Files)
            .Select(f => new slskd.Search.File { ... })
            .ToList();

        if (allFiles.Count == 0) return Enumerable.Empty<Response>();

        return new[]
        {
            new Response
            {
                Username = username,
                FileCount = allFiles.Count,
                Files = allFiles,
                HasFreeUploadSlot = true,  // user is online and sharing
                UploadSpeed = 0,
                QueueLength = 0,
            },
        };
    }
    catch (Exception ex)
    {
        Log.Debug(ex, "Auto-replace browse of {Username} failed", username);
        return Enumerable.Empty<Response>();
    }
}
```

Each strategy logs whether it found a candidate so operators can see which path succeeded.

#### Files Changed

- `src/slskd/Transfers/Downloads/AutoReplace/AutoReplaceService.cs`: `TryReplaceAsync` (strategy chaining), new `BrowseAsync` method

#### Tests

Added to `tests/slskd.Tests.Unit/Transfers/Downloads/AutoReplace/AutoReplaceServiceTests.cs`:

| Test | Scenario |
|---|---|
| `Falls_Back_To_Relaxed_Match_When_Exact_Match_Fails` | Same basename/size, different extension → relaxed match picks it |
| `Falls_Back_To_Relaxed_Match_When_No_Free_Slot` | Matching file, no free slot → relaxed match picks it |
| `Falls_Back_To_Browse_When_Search_Returns_Nothing` | Empty search, browse finds file on original user |
| `Does_Not_Attempt_Browse_When_Search_Succeeds` | Exact match succeeds → browse never called |
| `Handles_Browse_Error_Gracefully` | Browse throws → returns false |

The `Mocks` class was updated to default `BrowseAsync` to an empty `BrowseResponse` (so existing tests don't break when browse is unexpectedly called) and a `SetBrowseResults(SoulseekFile[])` helper was added.

---

## Gap 3: Startup Recovery Leaves Stale Errored Records

### Problem

When slskd starts after a crash or shutdown:

1. All in-progress transfers are marked `Completed | Errored` with message "Application shut down"
2. Their IDs are saved
3. After login, new transfers are enqueued (same file, same user, new transfer records)
4. The old errored records remain in the database with `Removed = false`

The auto-replace background scan (`ScanForFailuresAsync`) queries for transfers with `Completed | Errored` state within `MaxAge` (default 1 hour). It finds the old startup-marked records and tries to replace them — creating a third copy of the download.

### Solution

After re-enqueueing on login, remove (soft-delete) the old errored records:

```csharp
// In Client_LoggedIn handler, after re-enqueue loop:
foreach (var download in resumableDownloads)
{
    Transfers.Downloads.Remove(download.Id);
}
```

The `Remove` method sets `Removed = true`, and `List()` filters `!Removed` by default, so `ScanForFailuresAsync` never sees these records.

#### Files Changed

- `src/slskd/Application.cs`: `Client_LoggedIn` handler, before clearing `ActiveDownloadIdsAtPreviousShutdown`

---

## Gap 4: Retry Treats All Errors the Same

### Problem

The `isRetryable` predicate inside `Retry.Do` excluded only four exception types from retry:

```
NOT retryable: OperationCanceledException
NOT retryable: TransferRejectedException
NOT retryable: DuplicateTransferException
NOT retryable: TransferSizeMismatchException
```

`TimeoutException` was conspicuously absent. A download that times out would be retried up to 3 times with exponential backoff (~0s, ~1s, ~3s) before finally failing and triggering auto-replace. This added ~4–7 seconds of unnecessary latency since timeouts are unlikely to resolve across rapid retries.

### Solution

Add `TimeoutException` to the non-retryable list:

```csharp
isRetryable: (attempts, ex) =>
    ex is not OperationCanceledException
    && ex is not TimeoutException                   // <-- new
    && ex is not TransferRejectedException
    && ex is not DuplicateTransferException
    && ex is not TransferSizeMismatchException,
```

Now timeouts fail-fast — the first timeout immediately exits the retry loop, falls into the catch block, and triggers inline auto-replace.

#### Note on Catch Block Routing

`Retry.Do` always throws `AggregateException` when the retry loop exits (whether via exhaustion or `isRetryable` returning false). This means the first catch block (`ex is OperationCanceledException || ex is TimeoutException`) is unreachable for retried exceptions. Both catch blocks correctly call `TryAutoReplaceOnFailureAsync`, so the behavioral difference is minimal — the win is purely from avoiding wasted retry time.

#### Files Changed

- `src/slskd/Transfers/Downloads/DownloadService.cs`: `isRetryable` predicate in `DownloadAsync`

---

## Gap 5: Sequential Scanning Blocks All Replacements

### Problem

The auto-replace background scan runs every 5 seconds and processes transfers in sequence:

```
ScanAsync
  ├── ScanForStallsAsync
  │     └── foreach stalled → TryReplaceAsync → SearchAsync (network I/O)
  └── ScanForFailuresAsync
        └── foreach failed → TryReplaceAsync → SearchAsync (network I/O)
```

If one search takes 5+ seconds (e.g., timeout waiting for responses), all subsequent transfers are delayed. With multiple stalled or failed transfers, the last one could wait minutes before being processed.

### Solution

Process transfers concurrently within each scan method using `Task.WhenAll`.

#### Before (stalls):

```csharp
foreach (var id in stalled)
{
    var transfer = Downloads.Find(t => t.Id == id);
    if (transfer is not null)
    {
        await TryReplaceAsync(transfer, AutoReplaceReason.Stall, cancellationToken);
    }
}
```

#### After:

```csharp
var tasks = stalled
    .Select(id => Downloads.Find(t => t.Id == id))
    .Where(t => t is not null)
    .Select(transfer =>
    {
        Log.Information("Auto-replace detected a stalled download: {Filename} from {Username}", ...);
        return TryReplaceAsync(transfer, AutoReplaceReason.Stall, cancellationToken);
    });

await Task.WhenAll(tasks);
```

Same pattern applied to `ScanForFailuresAsync`.

#### Safety

- `TryReplaceAsync` catches all exceptions internally, so one failing task can't crash the whole batch
- Each task operates on a different transfer with independent state
- `LastAttempt` uses `ConcurrentDictionary<Guid, DateTime>` and is the only shared mutable state
- Network-level rate limiting is handled by Soulseek's internal search throttling

#### Files Changed

- `src/slskd/Transfers/Downloads/AutoReplace/AutoReplaceService.cs`: `ScanForStallsAsync`, `ScanForFailuresAsync`

---

## Summary of All Files Changed

| File | Change |
|---|---|
| `src/slskd/Transfers/Downloads/DownloadService.cs` | Constructor + IServiceProvider, TryAutoReplaceOnFailureAsync method, modified catch blocks, TimeoutException excluded from retry |
| `src/slskd/Transfers/Downloads/AutoReplace/AutoReplaceService.cs` | Strategy chaining in TryReplaceAsync, BrowseAsync method, parallel scans |
| `src/slskd/Application.cs` | Remove old errored transfers after re-enqueue on login |
| `tests/slskd.Tests.Unit/Transfers/Downloads/DownloadServiceTests.cs` | 5 tests for inline auto-replace (new file) |
| `tests/slskd.Tests.Unit/Transfers/Downloads/AutoReplace/AutoReplaceServiceTests.cs` | 5 tests for multi-strategy search, updated Mocks class |
