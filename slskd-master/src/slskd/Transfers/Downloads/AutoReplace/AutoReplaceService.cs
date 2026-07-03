// <copyright file="AutoReplaceService.cs" company="JP Dillingham">
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Serilog;
using slskd.Search;
using slskd.Transfers;
using Soulseek;
using Transfer = slskd.Transfers.Transfer;

/// <summary>
///     The reason a download is being replaced with an alternate source.
/// </summary>
public enum AutoReplaceReason
{
    /// <summary>
    ///     The download failed after exhausting same-source retries.
    /// </summary>
    Failure,

    /// <summary>
    ///     The download stalled, making no progress for an extended period.
    /// </summary>
    Stall,
}

/// <summary>
///     Finds alternate sources for downloads that have failed or stalled and transparently re-enqueues them.
/// </summary>
public interface IAutoReplaceService
{
    /// <summary>
    ///     Scans active and recently-failed downloads and replaces any that have stalled or failed.
    /// </summary>
    /// <param name="cancellationToken">The token to monitor for cancellation.</param>
    /// <returns>The operation context.</returns>
    Task ScanAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Attempts to replace the specified <paramref name="transfer"/> with an alternate source.
    /// </summary>
    /// <param name="transfer">The failed or stalled transfer to replace.</param>
    /// <param name="reason">The reason for the replacement.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation.</param>
    /// <returns>A value indicating whether a replacement was enqueued.</returns>
    Task<bool> TryReplaceAsync(Transfer transfer, AutoReplaceReason reason, CancellationToken cancellationToken = default);
}

/// <summary>
///     Finds alternate sources for downloads that have failed or stalled and transparently re-enqueues them.
/// </summary>
public class AutoReplaceService : IAutoReplaceService
{
    private static readonly char[] UsernameDelimiter = ['\n'];

    // exact transfer states that represent a download actively expected to make progress.  matched with an IN
    // expression (rather than a HasFlag/bitwise expression) so SQLite can use the State index.
    private static readonly HashSet<TransferStates> ActiveStates = [
        TransferStates.Initializing,
        TransferStates.InProgress,
        TransferStates.Queued | TransferStates.Remotely,
    ];

    // exact terminal states that represent a failed download eligible for replacement.  excludes user-initiated
    // cancellation and aborted transfers, which should not be automatically replaced.
    private static readonly HashSet<TransferStates> ReplaceableFailureStates = [
        TransferStates.Completed | TransferStates.TimedOut,
        TransferStates.Completed | TransferStates.Errored,
        TransferStates.Completed | TransferStates.Rejected,
    ];

    /// <summary>
    ///     Initializes a new instance of the <see cref="AutoReplaceService"/> class.
    /// </summary>
    /// <param name="downloadService">The download service.</param>
    /// <param name="soulseekClient">The Soulseek client.</param>
    /// <param name="optionsMonitor">The options monitor.</param>
    /// <param name="stallDetector">The stall detector.</param>
    public AutoReplaceService(
        IDownloadService downloadService,
        ISoulseekClient soulseekClient,
        IOptionsMonitor<slskd.Options> optionsMonitor,
        StallDetector stallDetector = null)
    {
        Downloads = downloadService;
        Client = soulseekClient;
        OptionsMonitor = optionsMonitor;
        Detector = stallDetector ?? new StallDetector();
    }

    private IDownloadService Downloads { get; }
    private ISoulseekClient Client { get; }
    private IOptionsMonitor<slskd.Options> OptionsMonitor { get; }
    private StallDetector Detector { get; }
    private ConcurrentDictionary<Guid, DateTime> LastAttempt { get; } = new();
    private ILogger Log { get; } = Serilog.Log.ForContext<AutoReplaceService>();

    private slskd.Options.TransfersOptions.GlobalDownloadOptions.AutoReplaceOptions OptionsValue
        => OptionsMonitor.CurrentValue.Transfers.Download.AutoReplace;

    /// <summary>
    ///     Builds a network search query from the supplied remote <paramref name="filename"/> by isolating the file
    ///     name, dropping the extension, and reducing the remainder to alphanumeric search terms.
    /// </summary>
    /// <param name="filename">The remote filename to derive a query from.</param>
    /// <returns>The search query text.</returns>
    public static string BuildQuery(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            return string.Empty;
        }

        var index = filename.LastIndexOfAny(['\\', '/']);
        var basename = index >= 0 ? filename[(index + 1)..] : filename;

        var dot = basename.LastIndexOf('.');

        if (dot > 0)
        {
            basename = basename[..dot];
        }

        // replace any non-alphanumeric character with a space, then collapse runs of whitespace
        var cleaned = Regex.Replace(basename, "[^a-zA-Z0-9]+", " ");
        var terms = cleaned
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2);

        return string.Join(' ', terms).Trim();
    }

    /// <inheritdoc/>
    public async Task ScanAsync(CancellationToken cancellationToken = default)
    {
        var options = OptionsValue;

        if (!options.Enabled)
        {
            return;
        }

        // only attempt replacements while connected and logged in; searches require the network
        if (!Client.State.HasFlag(SoulseekClientStates.Connected) || !Client.State.HasFlag(SoulseekClientStates.LoggedIn))
        {
            return;
        }

        try
        {
            if (options.OnStall)
            {
                await ScanForStallsAsync(options, cancellationToken);
            }

            if (options.OnFailure)
            {
                await ScanForFailuresAsync(options, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Auto-replace scan encountered an error: {Message}", ex.Message);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> TryReplaceAsync(Transfer transfer, AutoReplaceReason reason, CancellationToken cancellationToken = default)
    {
        if (transfer is null)
        {
            return false;
        }

        var options = OptionsValue;

        if (!options.Enabled
            || (reason == AutoReplaceReason.Failure && !options.OnFailure)
            || (reason == AutoReplaceReason.Stall && !options.OnStall))
        {
            return false;
        }

        // never replace a transfer the user cancelled themselves
        if (transfer.State.HasFlag(TransferStates.Cancelled))
        {
            return false;
        }

        // enforce the per-file candidate budget across the lineage
        if (transfer.ReplacementAttempts >= options.MaxCandidates)
        {
            Log.Information(
                "Auto-replace giving up on {Filename}: exhausted {Max} candidate(s)",
                transfer.Filename,
                options.MaxCandidates);
            return false;
        }

        // respect the cooldown so we don't hammer the network for a file with no available source
        if (LastAttempt.TryGetValue(transfer.Id, out var last) && (DateTime.UtcNow - last).TotalMilliseconds < options.SearchCooldown)
        {
            return false;
        }

        LastAttempt[transfer.Id] = DateTime.UtcNow;

        var attempted = ParseUsernames(transfer.AttemptedUsernames);

        if (!string.IsNullOrWhiteSpace(transfer.Username))
        {
            attempted.Add(transfer.Username);
        }

        // for a stall, stop the stuck transfer before searching for a replacement
        if (reason == AutoReplaceReason.Stall)
        {
            Downloads.TryCancel(transfer.Id);
            Detector.Forget(transfer.Id);
        }

        Log.Information(
            "Auto-replace searching for an alternate source for {Filename} (reason: {Reason}, attempt {Attempt}/{Max})",
            transfer.Filename,
            reason,
            transfer.ReplacementAttempts + 1,
            options.MaxCandidates);

        var responses = await SearchAsync(transfer.Filename, options, cancellationToken);

        var candidate = AutoReplaceMatcher.SelectBest(
            originalFilename: transfer.Filename,
            originalSize: transfer.Size,
            originalBitRate: transfer.BitRate,
            originalBitDepth: transfer.BitDepth,
            originalLength: transfer.Length,
            originalSampleRate: transfer.SampleRate,
            responses: responses,
            excludedUsernames: attempted,
            options: options.Match);

        if (candidate is null)
        {
            Log.Information("Auto-replace found no suitable alternate source for {Filename}", transfer.Filename);
            return false;
        }

        var lineage = new TransferLineage
        {
            ReplacesId = transfer.Id,
            ReplacementAttempts = transfer.ReplacementAttempts + 1,
            AttemptedUsernames = FormatUsernames(attempted),
        };

        var candidateMetadata = new Dictionary<string, TransferFileMetadata>
        {
            [candidate.Filename] = new TransferFileMetadata
            {
                BitRate = candidate.BitRate,
                BitDepth = candidate.BitDepth,
                Length = candidate.Length,
                SampleRate = candidate.SampleRate,
            },
        };

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
                    "Auto-replace enqueued {Filename} from alternate source {Username} (replacing {OriginalId})",
                    candidate.Filename,
                    candidate.Username,
                    transfer.Id);
                return true;
            }

            Log.Information(
                "Auto-replace failed to enqueue {Filename} from {Username}: {Message}",
                candidate.Filename,
                candidate.Username,
                failed.FirstOrDefault().Message ?? "unknown");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Auto-replace failed to enqueue {Filename} from {Username}: {Message}", candidate.Filename, candidate.Username, ex.Message);
            return false;
        }
    }

    private async Task ScanForStallsAsync(slskd.Options.TransfersOptions.GlobalDownloadOptions.AutoReplaceOptions options, CancellationToken cancellationToken)
    {
        var active = Downloads.List(t =>
            t.Direction == TransferDirection.Download && ActiveStates.Contains(t.State));

        var stalled = Detector.Evaluate(
            active,
            DateTime.UtcNow,
            TimeSpan.FromMilliseconds(options.StallTimeout),
            TimeSpan.FromMilliseconds(options.QueueStallTimeout));

        foreach (var id in stalled)
        {
            var transfer = Downloads.Find(t => t.Id == id);

            if (transfer is not null)
            {
                Log.Information("Auto-replace detected a stalled download: {Filename} from {Username}", transfer.Filename, transfer.Username);
                await TryReplaceAsync(transfer, AutoReplaceReason.Stall, cancellationToken);
            }
        }
    }

    private async Task ScanForFailuresAsync(slskd.Options.TransfersOptions.GlobalDownloadOptions.AutoReplaceOptions options, CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow.AddMilliseconds(-options.MaxAge);

        // failed (but not user-cancelled) downloads that ended recently
        var failed = Downloads.List(t =>
            t.Direction == TransferDirection.Download
            && ReplaceableFailureStates.Contains(t.State)
            && t.EndedAt != null
            && t.EndedAt >= cutoff);

        if (failed.Count == 0)
        {
            return;
        }

        // the set of transfers that are already the target of a replacement; used to avoid replacing the same
        // failure more than once
        var alreadyReplaced = Downloads.List(t => t.ReplacesId != null)
            .Select(t => t.ReplacesId.Value)
            .ToHashSet();

        foreach (var transfer in failed)
        {
            if (alreadyReplaced.Contains(transfer.Id))
            {
                continue;
            }

            await TryReplaceAsync(transfer, AutoReplaceReason.Failure, cancellationToken);
        }
    }

    private async Task<IEnumerable<Response>> SearchAsync(string filename, slskd.Options.TransfersOptions.GlobalDownloadOptions.AutoReplaceOptions options, CancellationToken cancellationToken)
    {
        var queryText = BuildQuery(filename);

        if (string.IsNullOrWhiteSpace(queryText))
        {
            return Enumerable.Empty<Response>();
        }

        try
        {
            var searchOptions = new SearchOptions(
                searchTimeout: options.Search.Timeout,
                responseLimit: options.Search.ResponseLimit,
                filterResponses: false);

            var (_, responses) = await Client.SearchAsync(
                SearchQuery.FromText(queryText),
                scope: SearchScope.Network,
                token: null,
                options: searchOptions,
                cancellationToken: cancellationToken);

            return responses.Select(Response.FromSoulseekSearchResponse).ToList();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Auto-replace search for '{Query}' failed: {Message}", queryText, ex.Message);
            return Enumerable.Empty<Response>();
        }
    }

    private HashSet<string> ParseUsernames(string delimited)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(delimited))
        {
            return set;
        }

        foreach (var username in delimited.Split(UsernameDelimiter, StringSplitOptions.RemoveEmptyEntries))
        {
            set.Add(username);
        }

        return set;
    }

    private string FormatUsernames(IEnumerable<string> usernames)
        => string.Join('\n', usernames.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.OrdinalIgnoreCase));
}
