# File Matching Improvement Plan

## 1. Problem Summary

The application has five distinct file/string matching subsystems, all varying degrees of "naive":

| Subsystem | Core Weakness |
|-----------|---------------|
| **Share Filters** (`ShareScanner.cs:158-167`, `:217`, `:292`) | Pure regex against full file path strings; no metadata awareness, no structured rules, no negative/positive filtering distinction |
| **Search Request Filters** (`Application.cs:161-171`, `:1684`) | Regex applied to raw query text, not parsed tokens; no context-awareness (user, group, rate-limit) |
| **FTS5 File Search** (`SqliteShareRepository.cs:537-593`) | Single-column maskedFilename index; no metadata in index; substring-exclusion post-filter has O(n*m) perf (line 568) |
| **AutoReplace Matching** (`AutoReplaceMatcher.cs:77-161`) | Exact basename match only (`:122`); ignores token similarity, metadata signals, directory context |
| **Blacklist Username Patterns** (`UserService.cs:494-505`) | Regex against usernames — acceptable use, but the compilation is duplicated from the pattern used in ShareScanner/Application |

Cross-cutting issues:
- Regex compilation logic is duplicated across 3 files (`ShareScanner.cs`, `Application.cs`, `UserService.cs`)
- No shared matching abstraction
- No way to combine signals (size + name + extension + metadata)
- No negative/positive pattern distinction
- No benchmarking or performance telemetry for match latency

---

## 2. Proposed Architecture: `FileMatcher` Abstraction

### 2.1 Core Types

Add a new namespace `slskd.Common.Matching` with the following types:

```csharp
// A single matching rule — replaces raw regex strings in config
public sealed record MatchRule
{
    public MatchAction Action { get; init; } = MatchAction.Exclude;
    public MatchField Field { get; init; } = MatchField.Filename;
    public MatchOperator Operator { get; init; } = MatchOperator.Regex;
    public string Value { get; init; }
    public double? MinSimilarity { get; init; }  // for Similarity operator
}

public enum MatchAction { Include, Exclude }
public enum MatchField { Filename, FullPath, Extension, Size, BitRate, BitDepth, SampleRate, Length, Directory }
public enum MatchOperator { Regex, Equals, Contains, GreaterThan, LessThan, SimilarTo, Glob }

// Pre-compiled, efficient matcher
public sealed class CompiledMatcher
{
    private readonly Regex Regex;       // for regex rules
    private readonly string Literal;    // for literal comparison
    private readonly long NumericValue; // for numeric comparisons
    // ...
    public bool IsMatch(FileInfo file) { ... }
}
```

### 2.2 Central `FileMatcher` Service

```csharp
public interface IFileMatcher
{
    bool IsExcluded(string filePath);
    bool IsExcluded(FileInfo file);
    IEnumerable<MatchResult> EvaluateAll(string filePath);
}

public class FileMatcher : IFileMatcher
{
    private readonly IReadOnlyList<CompiledMatcher> Rules;

    public FileMatcher(IEnumerable<MatchRule> rules, bool caseSensitive)
    {
        // Compile all rules once at construction
        Rules = rules.Select(r => new CompiledMatcher(r, caseSensitive)).ToList();
    }

    public bool IsExcluded(string filePath)
    {
        // Short-circuit: if any exclude rule matches, return true
        return Rules.Any(r => r.Action == MatchAction.Exclude && r.IsMatch(filePath));
    }
}
```

### 2.3 Integration Points

Replace all ad-hoc regex arrays with `IFileMatcher`:

| Current Location | New Integration |
|---|---|
| `ShareScanner.cs:165-167` List of Regex filters | Inject `IFileMatcher` via DI; call `matcher.IsExcluded(directory)` and `matcher.IsExcluded(originalFilename)` |
| `Application.cs:168-171` CompiledSearchRequestFilters | Inject `IFileMatcher` for search request filtering |
| `UserService.cs:502-505` CompiledBlacklistPatterns | Keep separate (username patterns are different concern) but reuse the compilation helper |

---

## 3. Share Filters — Structured Rules

### 3.1 Current
```yaml
shares:
  filters:
    - \.ini$
    - Thumbs.db$
    - \.DS_Store$
```

### 3.2 Proposed
```yaml
shares:
  match:
    rules:
      # Exclude by extension (structured, no regex needed for common cases)
      - action: exclude
        field: extension
        operator: equals
        value: .ini
      - action: exclude
        field: extension
        operator: equals
        value: .ds_store
      # Exclude by filename pattern (glob style)
      - action: exclude
        field: filename
        operator: glob
        value: Thumbs.db
      # Exclude by size (tiny files)
      - action: exclude
        field: size
        operator: less_than
        value: 1024
      # Complex regex still supported when needed
      - action: exclude
        field: full_path
        operator: regex
        value: \\Temp\\
```

### 3.3 Backward Compatibility
Deprecate `shares.filters` string array but keep it working. If both `filters` and `match.rules` are present, merge them (convert legacy strings to `{ action: exclude, field: filename, operator: regex, value: <string> }`).

---

## 4. AutoReplace Matching — Multi-Signal Similarity

### 4.1 Current (from `AutoReplaceMatcher.cs:122`)
```csharp
string.Equals(Basename(file.Filename), targetBasename, StringComparison.OrdinalIgnoreCase)
```

### 4.2 Proposed Algorithm

Replace the exact basename match with a pipeline of scored signals:

```
For each candidate file from search results:
  1. Skip if null/locked/empty username/already-tried
  2. Skip if requireFreeUploadSlot and no free slot
  3. Skip if upload speed too low

  4. Compute score components:
     a. Token Jaccard similarity (basename → token set → |intersect|/|union|)
        weight: 0-1 (configurable threshold)
     b. Size proximity: 1 - |size_diff| / max(size, original)
        weight: 0-1 (with hard reject if requireExactSize)
     c. Extension match: 1 if same, 0 if different
        weight: hard reject if requireSameExtension
     d. Metadata match (when available on both sides):
        - Length (duration) within ±2s: +0.3 bonus
        - BitDepth match: +0.1
        - SampleRate match: +0.1
        - BitRate within tolerance: +0.1

  5. Composite score = weighted sum of available signals
  6. If composite < minimum threshold → skip
  
  7. Final sort: (composite desc, hasFreeSlot desc, uploadSpeed desc, -queueLength desc)
```

### 4.3 Add `AutoReplaceMatcher.Tokenize()` helper

```csharp
public static string[] Tokenize(string basename)
{
    // 1. Strip extension
    // 2. Split on non-alphanumeric characters
    // 3. ToLower/ToUpper (per case sensitivity flag)
    // 4. Discard single-character tokens
    // 5. Return distinct tokens
}
```

### 4.4 Token Jaccard similarity

```csharp
public static double JaccardSimilarity(string[] a, string[] b)
{
    if (a.Length == 0 || b.Length == 0) return 0;
    var intersection = a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count();
    var union = a.Union(b, StringComparer.OrdinalIgnoreCase).Count();
    return (double)intersection / union;
}
```

---

## 5. FTS5 Search — Tokenizer and Index Improvements

### 5.1 Current Index
```sql
CREATE VIRTUAL TABLE filenames USING fts5(maskedFilename);
```
FTS5 default tokenizer splits on whitespace and punctuation. The `maskedFilename` is a Soulseek path like `Music\Artist\Album\Track.flac`.

### 5.2 Proposed Changes

#### 5.2.1 Add a content table with metadata columns
```sql
CREATE VIRTUAL TABLE filenames USING fts5(
    maskedFilename,
    directory,
    extension,
    content='files',
    content_rowid='rowid',
    tokenize='unicode61'
);

-- Also store metadata for post-filtering
ALTER TABLE files ADD COLUMN length INTEGER;
ALTER TABLE files ADD COLUMN bitrate INTEGER;
ALTER TABLE files ADD COLUMN bitdepth INTEGER;
ALTER TABLE files ADD COLUMN samplerate INTEGER;
```

#### 5.2.2 External content FTS5
Use FTS5 with an external content table to avoid duplicating the maskedFilename data:
```sql
-- Populate from files table
INSERT INTO filenames(rowid, maskedFilename, directory, extension)
SELECT rowid, maskedFilename, 
       substr(maskedFilename, 1, length(maskedFilename) - instr(maskedFilename, '\')),
       extension
FROM files;
```

#### 5.2.3 Search query translation improvements

Replace the naive `string.Join(" AND ", ...)` approach with smarter query construction:
- Use prefix queries for partial matches: `"foo"*` instead of `"foo"`
- Support phrase searches (quoted terms)
- Handle exclusions within FTS5 properly (avoid the O(n*m) post-filter)

Current problematic code (`SqliteShareRepository.cs:568`):
```csharp
// **CAUTION** performance of this will degrade exponentially
if (query.Exclusions.Any(x => filename.Contains(x, StringComparison.OrdinalIgnoreCase)))
```

Replace with FTS5-native exclusion:
```sql
WHERE filenames MATCH '("term1" "term2") NOT ("exclusion1" OR "exclusion2")'
```

The substring issue (where `bar` in `foo xbarx baz` isn't caught) should be handled by tokenizing exclusions the same way as query terms — FTS5 tokenizes both sides identically, so if the exclusion tokenizer produces `bar` and the document tokenizer produces `xbarx`, they genuinely don't match. This is *correct* behavior; the current code's post-filter is actually introducing false positives by doing naive substring matching.

#### 5.2.4 Rebuild index on extension change

When `RebuildFilenameIndex()` runs (`SqliteShareRepository.cs:517`), also rebuild the FTS5 index with the new schema.

---

## 6. Search Request Filters — Token-Aware Matching

### 6.1 Current
```csharp
CompiledSearchRequestFilters.Any(filter => filter.IsMatch(query.SearchText))
```

### 6.2 Proposed
Introduce a parse step that tokenizes the search query before matching:

```csharp
// Tokenize the raw search text into terms and exclusions
var parsed = SearchQuery.Parse(query.SearchText);

// Apply filters against parsed structure
if (CompiledSearchRequestFilters.Any(f => 
    f.IsMatch(parsed.SearchText) ||      // legacy: match raw
    f.IsMatch(string.Join(" ", parsed.Terms)) ||  // match on terms only
    parsed.Terms.Count < MinTermLength))  // new: reject by term count
{
    return null;
}
```

---

## 7. Performance and Telemetry

### 7.1 Regex Compilation Caching

All regex compilation should go through a single factory:

```csharp
public static class RegexCache
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new();

    public static Regex GetOrCompile(string pattern, RegexOptions options)
    {
        var key = $"{pattern}:{options}";
        return Cache.GetOrAdd(key, _ => new Regex(pattern, options | RegexOptions.Compiled));
    }
}
```

### 7.2 Match Latency Metrics

Add a histogram metric for match evaluation latency (matching `Metrics.Search.Incoming.Filter` at `Metrics.cs:147`):

```
slskd_matching_evaluation_duration_seconds{rule="<rule_id>", action="exclude|include"}
```

---

## 8. Configuration Schema Changes

### 8.1 New `match` section under `shares`

```yaml
shares:
  match:
    min_token_similarity: 0.3      # similarity threshold for token matching
    rules:
      - action: exclude
        field: extension
        operator: equals
        value: .ini
      - action: exclude
        field: filename
        operator: equals
        value: Thumbs.db
```

### 8.2 Addition to `auto_replace.match`

```yaml
transfers:
  download:
    auto_replace:
      match:
        require_exact_size: true
        size_tolerance_bytes: 10240
        require_same_extension: true
        require_free_upload_slot: false
        minimum_upload_speed: 0
        min_token_similarity: 0.3   # NEW: replaces exact basename match
```

### 8.3 Legacy Support

Old `shares.filters` string array continues to work. Internally converted:
```csharp
var legacyRules = legacyFilters.Select(f => new MatchRule
{
    Action = MatchAction.Exclude,
    Field = MatchField.Filename,
    Operator = MatchOperator.Regex,
    Value = f
});
```

---

## 9. Files to Modify

| File | Changes |
|------|---------|
| **New:** `src/slskd/Common/Matching/MatchRule.cs` | Core types: `MatchRule`, `MatchAction`, `MatchField`, `MatchOperator` |
| **New:** `src/slskd/Common/Matching/CompiledMatcher.cs` | Pre-compiled matcher from a `MatchRule` |
| **New:** `src/slskd/Common/Matching/FileMatcher.cs` | `IFileMatcher` + `FileMatcher` implementation |
| **New:** `src/slskd/Common/Matching/RegexCache.cs` | Shared regex compilation cache |
| **New:** `src/slskd/Common/Matching/TokenSimilarity.cs` | `Tokenize()` and `JaccardSimilarity()` helpers |
| `src/slskd/Core/Options.cs` | Add `SharesOptions.Match` section; add `MinTokenSimilarity` to `MatchOptions`; deprecate `SharesOptions.Filters` |
| `src/slskd/Shares/ShareScanner.cs` | Replace regex list with `IFileMatcher` |
| `src/slskd/Application.cs` | Replace `CompiledSearchRequestFilters` with search-specific matcher |
| `src/slskd/Transfers/Downloads/AutoReplace/AutoReplaceMatcher.cs` | Replace exact basename match with Jaccard + composite scoring |
| `src/slskd/Shares/SqliteShareRepository.cs` | Expand FTS5 schema; remove O(n*m) post-filter |
| `src/slskd/Telemetry/Metrics.cs` | Add match latency histogram |
| `config/slskd.example.yml` | Add new config examples |
| `docs/config.md` | Document new options and migration |
| `tests/slskd.Tests.Unit/...` | Test `TokenSimilarity`, `CompiledMatcher`, new `AutoReplaceMatcher` scoring |

---

## 10. Implementation Order

| Phase | Work | Depends On |
|-------|------|------------|
| **1** | Create `MatchRule`, `CompiledMatcher`, `RegexCache` types | Nothing |
| **2** | Create `IFileMatcher` + `FileMatcher`; replace `ShareScanner` filters | Phase 1 |
| **3** | Replace `CompiledSearchRequestFilters` with search matcher | Phase 1 |
| **4** | Add `TokenSimilarity` helpers; rewrite `AutoReplaceMatcher.SelectBest()` | Nothing (independent of 1-3) |
| **5** | FTS5 schema expansion + index rebuild | Nothing (independent) |
| **6** | Metrics and telemetry | Phase 2 |
| **7** | Config schema + docs + legacy support | Phases 1-4 |
| **8** | Tests for all new code | Phases 1-7 |
| **9** | Deprecation warnings for `shares.filters` in logs | Phase 2 |

---

## 11. Verification

1. Share scan with empty rules includes all files (same as today with no filters)
2. Share scan with legacy `filters` array matches existing behavior exactly
3. Share scan with new `match.rules` correctly excludes by extension, size, and glob
4. `AutoReplaceMatcher` with `min_token_similarity: 1.0` matches existing behavior exactly
5. `AutoReplaceMatcher` with `min_token_similarity: 0.3` matches differently-named same-track files
6. FTS5 search with exclusions no longer does O(n*m) post-filter
7. All existing unit tests pass without modification (or are updated to reflect new behavior)
8. Performance: share scan time is not worse than before (same number of evaluations, just structured)
