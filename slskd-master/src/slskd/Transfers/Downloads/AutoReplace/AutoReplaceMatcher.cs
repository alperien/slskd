// <copyright file="AutoReplaceMatcher.cs" company="JP Dillingham">
//           ▄▄▄▄     ▄▄▄▄     ▄▄▄▄
//     ▄▄▄▄▄▄█  █▄▄▄▄▄█  █▄▄▄▄▄█  █
//     █__ --█  █__ --█    ◄█  -  █
//     █▄▄▄▄▄█▄▄█▄▄▄▄▄█▄▄█▄▄█▄▄▄▄▄█
//   ┍━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ ━━━━ ━  ━┉   ┉     ┉
//   │ Copyright (c) JP Dillingham.
//   │
//   │ This program is free software: you can redistribute it and/or modify
//   │ it under the terms of the GNU Affero General Public License as published
//   │ by the Free Software Foundation, version 3.
//   │
//   │ This program is distributed in the hope that it will be useful,
//   │ but WITHOUT ANY WARRANTY; without even the implied warranty of
//   │ MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//   │ GNU Affero General Public License for more details.
//   │
//   │ You should have received a copy of the GNU Affero General Public License
//   │ along with this program.  If not, see https://www.gnu.org/licenses/.
//   │
//   │ This program is distributed with Additional Terms pursuant to Section 7
//   │ of the AGPLv3.  See the LICENSE file in the root directory of this
//   │ project for the complete terms and conditions.
//   │
//   │ https://slskd.org
//   │
//   ├╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌ ╌ ╌╌╌╌ ╌
//   │ SPDX-FileCopyrightText: JP Dillingham
//   │ SPDX-License-Identifier: AGPL-3.0-only
//   ╰───────────────────────────────────────────╶──── ─ ─── ─  ── ──┈  ┈
// </copyright>

namespace slskd.Transfers.Downloads;

using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;
using slskd.Search;

/// <summary>
///     A candidate alternate source for an auto-replaced download.
/// </summary>
public record AutoReplaceCandidate
{
    /// <summary>
    ///     Gets the username of the alternate source.
    /// </summary>
    public string Username { get; init; }

    /// <summary>
    ///     Gets the remote filename to download from the alternate source.
    /// </summary>
    public string Filename { get; init; }

    /// <summary>
    ///     Gets the size of the file, in bytes.
    /// </summary>
    public long Size { get; init; }

    /// <summary>
    ///     Gets the audio bitrate, if known.
    /// </summary>
    public int? BitRate { get; init; }

    /// <summary>
    ///     Gets the audio bit depth, if known.
    /// </summary>
    public int? BitDepth { get; init; }

    /// <summary>
    ///     Gets the track length in seconds, if known.
    /// </summary>
    public int? Length { get; init; }

    /// <summary>
    ///     Gets the audio sample rate, if known.
    /// </summary>
    public int? SampleRate { get; init; }
}

/// <summary>
///     Pure logic for selecting the best alternate source for a download from a set of search responses.
/// </summary>
public static class AutoReplaceMatcher
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(AutoReplaceMatcher));

    /// <summary>
    ///     Built-in extension equivalence groups used when <see cref="Options.TransfersOptions.GlobalDownloadOptions.AutoReplaceOptions.MatchOptions.ExtensionGroups"/>
    ///     is not configured.  Lossless formats are interchangeable with other lossless formats, and lossy with lossy.
    /// </summary>
    private static readonly List<List<string>> DefaultExtensionGroups =
    [
        ["flac", "wav", "aiff", "alac", "ape", "wv"],
        ["mp3", "m4a", "ogg", "opus", "aac", "wma"],
    ];

    /// <summary>
    ///     Selects the best alternate source for the file identified by <paramref name="originalFilename"/> from the
    ///     supplied <paramref name="responses"/>, excluding any source whose username appears in
    ///     <paramref name="excludedUsernames"/>.
    /// </summary>
    /// <param name="originalFilename">The original remote filename (full remote path) being replaced.</param>
    /// <param name="originalSize">The size of the original file, in bytes.</param>
    /// <param name="originalBitRate">The original audio bitrate, if known.</param>
    /// <param name="originalBitDepth">The original audio bit depth, if known.</param>
    /// <param name="originalLength">The original track length in seconds, if known.</param>
    /// <param name="originalSampleRate">The original audio sample rate, if known.</param>
    /// <param name="responses">The search responses to consider.</param>
    /// <param name="excludedUsernames">Usernames that must not be selected (already-tried or known-bad sources).</param>
    /// <param name="options">The candidate matching options.</param>
    /// <returns>The best matching candidate, or null if none is suitable.</returns>
    public static AutoReplaceCandidate SelectBest(
        string originalFilename,
        long originalSize,
        int? originalBitRate,
        int? originalBitDepth,
        int? originalLength,
        int? originalSampleRate,
        IEnumerable<Response> responses,
        IEnumerable<string> excludedUsernames,
        Options.TransfersOptions.GlobalDownloadOptions.AutoReplaceOptions.MatchOptions options)
    {
        if (string.IsNullOrWhiteSpace(originalFilename) || responses is null)
        {
            return null;
        }

        options ??= new Options.TransfersOptions.GlobalDownloadOptions.AutoReplaceOptions.MatchOptions();

        var targetBasename = Basename(originalFilename);
        var targetExtension = Extension(originalFilename);
        var targetTokens = Tokenize(targetBasename);
        var excluded = new HashSet<string>(excludedUsernames ?? [], StringComparer.OrdinalIgnoreCase);

        Log.Debug(
            "Auto-replace matching: target={Filename} basename={Basename} tokens=[{Tokens}] ext={Ext} size={Size} " +
            "metadata=(bitrate={Br},bitdepth={Bd},length={Len},samplerate={Sr}) " +
            "excluded=[{Excluded}] minTokenSimilarity={Sim} requireExactSize={Res} sizeToleranceBytes={TolB} sizeTolerancePercent={TolP} requireSameExt={Rse}",
            originalFilename, targetBasename, string.Join(",", targetTokens), targetExtension, originalSize,
            originalBitRate, originalBitDepth, originalLength, originalSampleRate,
            string.Join(",", excluded), options.MinTokenSimilarity,
            options.RequireExactSize, options.SizeToleranceBytes, options.SizeTolerancePercent, options.RequireSameExtension);

        var scored = new List<(AutoReplaceCandidate Candidate, (double Meta, double Token, int Slot, int Speed, long Queue) Score)>();

        foreach (var response in responses)
        {
            if (response is null || string.IsNullOrWhiteSpace(response.Username) || excluded.Contains(response.Username))
            {
                continue;
            }

            if (options.RequireFreeUploadSlot && !response.HasFreeUploadSlot)
            {
                Log.Debug("  Skipping response from {User}: no free upload slot", response.Username);
                continue;
            }

            if (response.UploadSpeed < options.MinimumUploadSpeed)
            {
                Log.Debug("  Skipping response from {User}: upload speed {Speed} < min {Min}", response.Username, response.UploadSpeed, options.MinimumUploadSpeed);
                continue;
            }

            // never select locked files; only consider the freely available ones
            foreach (var file in response.Files ?? Enumerable.Empty<File>())
            {
                if (file is null || file.IsLocked || string.IsNullOrWhiteSpace(file.Filename))
                {
                    continue;
                }

                if (options.RequireSameExtension)
                {
                    var candidateExt = Extension(file.Filename);
                    var sameExt = string.Equals(candidateExt, targetExtension, StringComparison.OrdinalIgnoreCase);

                    if (!sameExt)
                    {
                        // check extension equivalence groups
                        var groups = options.ExtensionGroups ?? DefaultExtensionGroups;
                        sameExt = groups.Any(g => g.Contains(targetExtension, StringComparer.OrdinalIgnoreCase)
                                                   && g.Contains(candidateExt, StringComparer.OrdinalIgnoreCase));
                    }

                    if (!sameExt)
                    {
                        Log.Debug("  Skipping file {File} from {User}: extension mismatch (expected={Exp}, got={Got})", file.Filename, response.Username, targetExtension, candidateExt);
                        continue;
                    }
                }

                if (options.RequireExactSize)
                {
                    var byteDiff = Math.Abs(file.Size - originalSize);
                    var byteToleranceOk = byteDiff <= options.SizeToleranceBytes;
                    var percentToleranceOk = options.SizeTolerancePercent > 0
                        && originalSize > 0
                        && ((double)byteDiff / originalSize * 100) <= options.SizeTolerancePercent;

                    if (!byteToleranceOk && !percentToleranceOk)
                    {
                        Log.Debug("  Skipping file {File} from {User}: size mismatch (expected={Exp}, got={Got}, byteTol={Byt}, pctTol={Pct})",
                            file.Filename, response.Username, originalSize, file.Size, options.SizeToleranceBytes, options.SizeTolerancePercent);
                        continue;
                    }
                }

                // compute token similarity
                var candidateBasename = Basename(file.Filename);
                var candidateTokens = Tokenize(candidateBasename);
                var tokenSimilarity = JaccardSimilarity(targetTokens, candidateTokens);

                if (tokenSimilarity < options.MinTokenSimilarity)
                {
                    Log.Debug("  Skipping file {File} from {User}: token similarity too low ({Sim} < {Min}), target tokens=[{Target}], candidate tokens=[{Candidate}]",
                        file.Filename, response.Username, tokenSimilarity, options.MinTokenSimilarity, string.Join(",", targetTokens), string.Join(",", candidateTokens));
                    continue;
                }

                // compute metadata bonus when original metadata is available
                var metadataScore = ComputeMetadataScore(
                    originalLength, originalBitRate, originalBitDepth, originalSampleRate,
                    file.Length, file.BitRate, file.BitDepth, file.SampleRate);

                var candidate = new AutoReplaceCandidate
                {
                    Username = response.Username,
                    Filename = file.Filename,
                    Size = file.Size,
                    BitRate = file.BitRate,
                    BitDepth = file.BitDepth,
                    Length = file.Length,
                    SampleRate = file.SampleRate,
                };

                // higher is better: prefer metadata match, then token similarity, then free slot, then speed, then shorter queue
                var score = (
                    Meta: metadataScore,
                    Token: tokenSimilarity,
                    Slot: response.HasFreeUploadSlot ? 1 : 0,
                    Speed: response.UploadSpeed,
                    Queue: -response.QueueLength);

                Log.Debug("  Accepted file {File} from {User}: tokenSim={Sim:0.000} metaScore={Meta:0.000} slot={Slot} speed={Speed} queue={Queue}",
                    file.Filename, response.Username, tokenSimilarity, metadataScore, score.Slot, score.Speed, response.QueueLength);

                scored.Add((candidate, score));
            }
        }

        if (scored.Count > 0)
        {
            var ranked = scored
                .OrderByDescending(x => x.Score.Meta)
                .ThenByDescending(x => x.Score.Token)
                .ThenByDescending(x => x.Score.Slot)
                .ThenByDescending(x => x.Score.Speed)
                .ThenByDescending(x => x.Score.Queue)
                .ThenBy(x => x.Candidate.Username, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var i = 0; i < ranked.Count; i++)
            {
                var (candidate, s) = ranked[i];
                Log.Debug("  Rank #{Rank}: {User} - {File} (meta={Meta:0.000}, token={Token:0.000}, slot={Slot}, speed={Speed}, queue={Queue})",
                    i + 1, candidate.Username, candidate.Filename, s.Meta, s.Token, s.Slot, s.Speed, -s.Queue);
            }

            var winner = ranked[0].Candidate;
            Log.Debug("  Selected: {User} - {File}", winner.Username, winner.Filename);
            return winner;
        }
        else
        {
            Log.Debug("  No matching candidates found");
            return null;
        }
    }

    /// <summary>
    ///     Selects all suitable alternate sources for the file identified by <paramref name="originalFilename"/>,
    ///     ranked by score.  Used by the fallback queue to try multiple candidates in order.
    /// </summary>
    /// <param name="originalFilename">The original remote filename (full remote path) being replaced.</param>
    /// <param name="originalSize">The size of the original file, in bytes.</param>
    /// <param name="originalBitRate">The original audio bitrate, if known.</param>
    /// <param name="originalBitDepth">The original audio bit depth, if known.</param>
    /// <param name="originalLength">The original track length in seconds, if known.</param>
    /// <param name="originalSampleRate">The original audio sample rate, if known.</param>
    /// <param name="responses">The search responses to consider.</param>
    /// <param name="excludedUsernames">Usernames that must not be selected (already-tried or known-bad sources).</param>
    /// <param name="options">The candidate matching options.</param>
    /// <returns>A list of matching candidates sorted by score (best first).</returns>
    public static List<AutoReplaceCandidate> SelectAll(
        string originalFilename,
        long originalSize,
        int? originalBitRate,
        int? originalBitDepth,
        int? originalLength,
        int? originalSampleRate,
        IEnumerable<Response> responses,
        IEnumerable<string> excludedUsernames,
        Options.TransfersOptions.GlobalDownloadOptions.AutoReplaceOptions.MatchOptions options)
    {
        if (string.IsNullOrWhiteSpace(originalFilename) || responses is null)
        {
            return [];
        }

        options ??= new Options.TransfersOptions.GlobalDownloadOptions.AutoReplaceOptions.MatchOptions();

        var targetBasename = Basename(originalFilename);
        var targetExtension = Extension(originalFilename);
        var targetTokens = Tokenize(targetBasename);
        var excluded = new HashSet<string>(excludedUsernames ?? [], StringComparer.OrdinalIgnoreCase);

        var scored = new List<(AutoReplaceCandidate Candidate, (double Meta, double Token, int Slot, int Speed, long Queue) Score)>();

        foreach (var response in responses)
        {
            if (response is null || string.IsNullOrWhiteSpace(response.Username) || excluded.Contains(response.Username))
            {
                continue;
            }

            if (options.RequireFreeUploadSlot && !response.HasFreeUploadSlot)
            {
                continue;
            }

            if (response.UploadSpeed < options.MinimumUploadSpeed)
            {
                continue;
            }

            foreach (var file in response.Files ?? Enumerable.Empty<File>())
            {
                if (file is null || file.IsLocked || string.IsNullOrWhiteSpace(file.Filename))
                {
                    continue;
                }

                if (options.RequireSameExtension)
                {
                    var candidateExt = Extension(file.Filename);
                    var sameExt = string.Equals(candidateExt, targetExtension, StringComparison.OrdinalIgnoreCase);

                    if (!sameExt)
                    {
                        var groups = options.ExtensionGroups ?? DefaultExtensionGroups;
                        sameExt = groups.Any(g => g.Contains(targetExtension, StringComparer.OrdinalIgnoreCase)
                                                   && g.Contains(candidateExt, StringComparer.OrdinalIgnoreCase));
                    }

                    if (!sameExt)
                    {
                        continue;
                    }
                }

                if (options.RequireExactSize)
                {
                    var byteDiff = Math.Abs(file.Size - originalSize);
                    var byteToleranceOk = byteDiff <= options.SizeToleranceBytes;
                    var percentToleranceOk = options.SizeTolerancePercent > 0
                        && originalSize > 0
                        && ((double)byteDiff / originalSize * 100) <= options.SizeTolerancePercent;

                    if (!byteToleranceOk && !percentToleranceOk)
                    {
                        continue;
                    }
                }

                var candidateBasename = Basename(file.Filename);
                var candidateTokens = Tokenize(candidateBasename);
                var tokenSimilarity = JaccardSimilarity(targetTokens, candidateTokens);

                if (tokenSimilarity < options.MinTokenSimilarity)
                {
                    continue;
                }

                var metadataScore = ComputeMetadataScore(
                    originalLength, originalBitRate, originalBitDepth, originalSampleRate,
                    file.Length, file.BitRate, file.BitDepth, file.SampleRate);

                var candidate = new AutoReplaceCandidate
                {
                    Username = response.Username,
                    Filename = file.Filename,
                    Size = file.Size,
                    BitRate = file.BitRate,
                    BitDepth = file.BitDepth,
                    Length = file.Length,
                    SampleRate = file.SampleRate,
                };

                var score = (
                    Meta: metadataScore,
                    Token: tokenSimilarity,
                    Slot: response.HasFreeUploadSlot ? 1 : 0,
                    Speed: response.UploadSpeed,
                    Queue: -response.QueueLength);

                scored.Add((candidate, score));
            }
        }

        if (scored.Count > 0)
        {
            return scored
                .OrderByDescending(x => x.Score.Meta)
                .ThenByDescending(x => x.Score.Token)
                .ThenByDescending(x => x.Score.Slot)
                .ThenByDescending(x => x.Score.Speed)
                .ThenByDescending(x => x.Score.Queue)
                .ThenBy(x => x.Candidate.Username, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Candidate)
                .ToList();
        }

        return [];
    }

    /// <summary>
    ///     Tokenizes a basename into a set of alphanumeric tokens, dropping single-character tokens.
    /// </summary>
    /// <param name="basename">The basename to tokenize.</param>
    /// <returns>An array of distinct, lowercased tokens with length >= 2.</returns>
    public static string[] Tokenize(string basename)
    {
        if (string.IsNullOrEmpty(basename))
        {
            return [];
        }

        var cleaned = System.Text.RegularExpressions.Regex.Replace(basename, "[^a-zA-Z0-9]+", " ");
        return cleaned
            .Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2)
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    ///     Computes the Jaccard similarity between two token sets.
    /// </summary>
    /// <param name="a">The first set of tokens.</param>
    /// <param name="b">The second set of tokens.</param>
    /// <returns>A value in [0.0, 1.0] representing the size of the intersection divided by the size of the union.</returns>
    public static double JaccardSimilarity(string[] a, string[] b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        var intersection = a.Intersect(b, System.StringComparer.OrdinalIgnoreCase).Count();
        var union = a.Union(b, System.StringComparer.OrdinalIgnoreCase).Count();
        return (double)intersection / union;
    }

    /// <summary>
    ///     Computes a metadata similarity bonus in range [0.0, 0.6] by comparing original and candidate audio metadata.
    ///     Returns 0 when no metadata is available on either side.
    /// </summary>
    /// <param name="originalLength">The original track length in seconds.</param>
    /// <param name="originalBitRate">The original audio bitrate.</param>
    /// <param name="originalBitDepth">The original audio bit depth.</param>
    /// <param name="originalSampleRate">The original audio sample rate.</param>
    /// <param name="candidateLength">The candidate track length in seconds.</param>
    /// <param name="candidateBitRate">The candidate audio bitrate.</param>
    /// <param name="candidateBitDepth">The candidate audio bit depth.</param>
    /// <param name="candidateSampleRate">The candidate audio sample rate.</param>
    /// <returns>A bonus score in [0.0, 0.6].</returns>
    public static double ComputeMetadataScore(
        int? originalLength, int? originalBitRate, int? originalBitDepth, int? originalSampleRate,
        int? candidateLength, int? candidateBitRate, int? candidateBitDepth, int? candidateSampleRate)
    {
        double score = 0;

        if (originalLength.HasValue && candidateLength.HasValue)
        {
            var diff = System.Math.Abs(originalLength.Value - candidateLength.Value);

            if (diff <= 2)
            {
                score += 0.3;
            }
        }

        if (originalBitDepth.HasValue && candidateBitDepth.HasValue && originalBitDepth.Value == candidateBitDepth.Value)
        {
            score += 0.1;
        }

        if (originalSampleRate.HasValue && candidateSampleRate.HasValue && originalSampleRate.Value == candidateSampleRate.Value)
        {
            score += 0.1;
        }

        if (originalBitRate.HasValue && candidateBitRate.HasValue)
        {
            var diff = System.Math.Abs(originalBitRate.Value - candidateBitRate.Value);

            if (diff <= 32)
            {
                score += 0.1;
            }
        }

        return score;
    }

    private static string Basename(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        var index = path.LastIndexOfAny(['\\', '/']);
        return index >= 0 ? path[(index + 1)..] : path;
    }

    private static string Extension(string path)
    {
        var basename = Basename(path);
        var dot = basename.LastIndexOf('.');
        return dot >= 0 ? basename[(dot + 1)..] : string.Empty;
    }
}
