// <copyright file="StallDetector.cs" company="JP Dillingham">
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
using Soulseek;
using Transfer = slskd.Transfers.Transfer;

/// <summary>
///     Tracks the progress of active downloads over time and identifies those that have stalled.
/// </summary>
/// <remarks>
///     A download is considered stalled if it is in progress but has transferred no additional bytes within the
///     configured stall timeout, or if it has been remotely queued without its place in queue advancing within the
///     configured queue stall timeout.  State is held in-memory and reset across restarts.
/// </remarks>
public class StallDetector
{
    private readonly Dictionary<Guid, Progress> tracked = new();
    private readonly object syncRoot = new();

    /// <summary>
    ///     Evaluates the supplied <paramref name="downloads"/> and returns the identifiers of those that have stalled.
    /// </summary>
    /// <param name="downloads">The current set of downloads to evaluate.</param>
    /// <param name="nowUtc">The current time, in UTC.</param>
    /// <param name="stallTimeout">The duration after which an in-progress download with no byte movement is stalled.</param>
    /// <param name="queueStallTimeout">The duration after which a remotely-queued download that is not advancing is stalled.</param>
    /// <returns>The identifiers of the stalled downloads.</returns>
    public IReadOnlyCollection<Guid> Evaluate(
        IEnumerable<Transfer> downloads,
        DateTime nowUtc,
        TimeSpan stallTimeout,
        TimeSpan queueStallTimeout)
    {
        var stalled = new List<Guid>();
        var list = downloads?.ToList() ?? [];

        lock (syncRoot)
        {
            var activeIds = new HashSet<Guid>();

            foreach (var transfer in list)
            {
                var isInProgress = transfer.State.HasFlag(TransferStates.InProgress) || transfer.State.HasFlag(TransferStates.Initializing);
                var isRemotelyQueued = transfer.State.HasFlag(TransferStates.Queued) && transfer.State.HasFlag(TransferStates.Remotely);

                // only track transfers that are actively expected to make progress
                if (!isInProgress && !isRemotelyQueued)
                {
                    continue;
                }

                activeIds.Add(transfer.Id);

                var current = new Progress(transfer.BytesTransferred, transfer.PlaceInQueue, nowUtc);

                if (!tracked.TryGetValue(transfer.Id, out var previous))
                {
                    // first observation; establish the baseline and don't flag yet
                    tracked[transfer.Id] = current;
                    continue;
                }

                // determine whether meaningful progress has been made since the last observation
                var madeProgress = isInProgress
                    ? current.Bytes != previous.Bytes
                    : current.PlaceInQueue != previous.PlaceInQueue;

                if (madeProgress)
                {
                    tracked[transfer.Id] = current;
                    continue;
                }

                // no progress; carry forward the time of the last observed change
                tracked[transfer.Id] = previous with { Bytes = current.Bytes, PlaceInQueue = current.PlaceInQueue };

                var timeout = isInProgress ? stallTimeout : queueStallTimeout;

                if (nowUtc - previous.LastChangedUtc >= timeout)
                {
                    stalled.Add(transfer.Id);
                }
            }

            // prune entries for transfers that are no longer active
            foreach (var staleId in tracked.Keys.Where(id => !activeIds.Contains(id)).ToList())
            {
                tracked.Remove(staleId);
            }
        }

        return stalled;
    }

    /// <summary>
    ///     Removes any tracking state for the specified <paramref name="id"/>.
    /// </summary>
    /// <param name="id">The identifier of the transfer to forget.</param>
    public void Forget(Guid id)
    {
        lock (syncRoot)
        {
            tracked.Remove(id);
        }
    }

    /// <summary>
    ///     Returns a snapshot of currently tracked transfers and their progress.
    /// </summary>
    /// <returns>A dictionary of transfer IDs and their tracked progress.</returns>
    public IReadOnlyDictionary<Guid, (long Bytes, int? PlaceInQueue, DateTime LastChangedUtc)> GetSnapshot()
    {
        lock (syncRoot)
        {
            return tracked.ToDictionary(
                kvp => kvp.Key,
                kvp => (kvp.Value.Bytes, kvp.Value.PlaceInQueue, kvp.Value.LastChangedUtc));
        }
    }

    private sealed record Progress(long Bytes, int? PlaceInQueue, DateTime LastChangedUtc);
}
