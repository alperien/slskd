// <copyright file="DiagnosticsController.cs" company="JP Dillingham">
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

using Microsoft.Extensions.Options;

namespace slskd.Core.API
{
    using System;
    using System.Linq;
    using Asp.Versioning;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Mvc;
    using slskd.Transfers;
    using slskd.Transfers.Downloads;
    using Soulseek;

    /// <summary>
    ///     Diagnostics.
    /// </summary>
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiVersion("0")]
    [ApiController]
    [Produces("application/json")]
    [Consumes("application/json")]
    public class DiagnosticsController : ControllerBase
    {
        public DiagnosticsController(
            IDownloadService downloadService,
            IOptionsMonitor<Options> optionsMonitor,
            StallDetector stallDetector)
        {
            Downloads = downloadService;
            OptionsMonitor = optionsMonitor;
            StallDetector = stallDetector;
        }

        private IDownloadService Downloads { get; }
        private IOptionsMonitor<Options> OptionsMonitor { get; }
        private StallDetector StallDetector { get; }

        /// <summary>
        ///     Gets diagnostic information about download retry and auto-replace state.
        /// </summary>
        /// <returns>A diagnostic summary of retry and auto-replace state.</returns>
        [HttpGet("transfers")]
        [Authorize(Policy = AuthPolicy.Any)]
        public IActionResult GetTransferDiagnostics()
        {
            var options = OptionsMonitor.CurrentValue.Transfers.Download;
            var allDownloads = Downloads.List(includeRemoved: false);

            var now = DateTime.UtcNow;

            // retry info
            var retryConfig = new
            {
                options.Retry.Attempts,
                options.Retry.Delay,
                options.Retry.MaxDelay,
                options.Retry.Partial,
            };

            var retrying = allDownloads
                .Where(t => t.Direction == TransferDirection.Download && t.Attempts > 0 && t.Attempts < options.Retry.Attempts)
                .Select(t => new
                {
                    t.Id,
                    t.Username,
                    Filename = t.Filename.Length > 80 ? t.Filename[..80] + "..." : t.Filename,
                    t.Attempts,
                    MaxAttempts = options.Retry.Attempts,
                    t.State,
                    Started = t.StartedAt,
                    Ended = t.EndedAt,
                })
                .ToList();

            // auto-replace config
            var autoReplaceConfig = new
            {
                options.AutoReplace.Enabled,
                options.AutoReplace.OnFailure,
                options.AutoReplace.OnStall,
                options.AutoReplace.MaxCandidates,
                options.AutoReplace.StallTimeout,
                options.AutoReplace.QueueStallTimeout,
                options.AutoReplace.MaxAge,
                options.AutoReplace.SearchCooldown,
                Match = new
                {
                    options.AutoReplace.Match.RequireExactSize,
                    options.AutoReplace.Match.SizeToleranceBytes,
                    options.AutoReplace.Match.RequireSameExtension,
                    options.AutoReplace.Match.RequireFreeUploadSlot,
                    options.AutoReplace.Match.MinimumUploadSpeed,
                },
                Search = new
                {
                    options.AutoReplace.Search.Timeout,
                    options.AutoReplace.Search.ResponseLimit,
                },
            };

            // transfers that are part of an auto-replace lineage
            var replaced = allDownloads
                .Where(t => t.Direction == TransferDirection.Download && t.ReplacesId != null)
                .Select(t => new
                {
                    t.Id,
                    t.Username,
                    Filename = t.Filename.Length > 80 ? t.Filename[..80] + "..." : t.Filename,
                    t.ReplacesId,
                    t.ReplacementAttempts,
                    t.AttemptedUsernames,
                    t.State,
                    Started = t.StartedAt,
                    Ended = t.EndedAt,
                })
                .ToList();

            // stall detector state
            var stallSnapshot = StallDetector.GetSnapshot();
            var stalledTracked = stallSnapshot.Select(kvp => new
            {
                TransferId = kvp.Key,
                kvp.Value.Bytes,
                kvp.Value.PlaceInQueue,
                LastChangedUtc = kvp.Value.LastChangedUtc,
                StalledFor = now - kvp.Value.LastChangedUtc,
            }).ToList();

            // transfers that have exhausted retries and are eligible for auto-replace
            var exhausted = allDownloads
                .Where(t =>
                    t.Direction == TransferDirection.Download
                    && t.Attempts >= options.Retry.Attempts
                    && t.Attempts > 0
                    && (t.State == (TransferStates.Completed | TransferStates.TimedOut)
                        || t.State == (TransferStates.Completed | TransferStates.Errored)
                        || t.State == (TransferStates.Completed | TransferStates.Rejected)))
                .Select(t => new
                {
                    t.Id,
                    t.Username,
                    Filename = t.Filename.Length > 80 ? t.Filename[..80] + "..." : t.Filename,
                    t.Attempts,
                    MaxAttempts = options.Retry.Attempts,
                    t.State,
                    Ended = t.EndedAt,
                })
                .ToList();

            return Ok(new
            {
                RetryConfiguration = retryConfig,
                AutoReplaceConfiguration = autoReplaceConfig,
                CurrentlyRetrying = retrying,
                RetriesExhausted = exhausted,
                AutoReplaced = replaced,
                StallDetectorState = new
                {
                    TrackedCount = stalledTracked.Count,
                    Tracked = stalledTracked,
                },
            });
        }
    }
}
