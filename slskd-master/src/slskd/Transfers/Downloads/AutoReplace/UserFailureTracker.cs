// <copyright file="UserFailureTracker.cs" company="JP Dillingham">
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

/// <summary>
///     Tracks per-user enqueue failures across the current session to avoid repeatedly selecting
///     unreliable sources.  Decays after a configurable window; records are in-memory only.
/// </summary>
public class UserFailureTracker
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="UserFailureTracker"/> class.
    /// </summary>
    /// <param name="maxFailures">The number of failures before a user is considered unreliable.</param>
    /// <param name="window">The time window after which failure records decay.</param>
    public UserFailureTracker(int maxFailures = 3, TimeSpan? window = null)
    {
        MaxFailures = maxFailures;
        Window = window ?? TimeSpan.FromMinutes(30);
    }

    private ConcurrentDictionary<string, UserFailureRecord> Failures { get; } = new();
    private int MaxFailures { get; }
    private TimeSpan Window { get; }

    /// <summary>
    ///     Records a failed enqueue attempt for the specified <paramref name="username"/>.
    /// </summary>
    /// <param name="username">The username to record a failure for.</param>
    public void RecordFailure(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        Failures.AddOrUpdate(username,
            _ => new UserFailureRecord(1, DateTime.UtcNow),
            (_, existing) => new UserFailureRecord(existing.Count + 1, DateTime.UtcNow));
    }

    /// <summary>
    ///     Records a successful enqueue for the specified <paramref name="username"/>, clearing any prior failures.
    /// </summary>
    /// <param name="username">The username to record a success for.</param>
    public void RecordSuccess(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        Failures.TryRemove(username, out _);
    }

    /// <summary>
    ///     Returns a value indicating whether the specified <paramref name="username"/> is considered unreliable
    ///     (has exceeded the failure threshold within the decay window).
    /// </summary>
    /// <param name="username">The username to check.</param>
    /// <returns><c>true</c> if the user is unreliable; otherwise <c>false</c>.</returns>
    public bool IsUnreliable(string username)
    {
        if (string.IsNullOrWhiteSpace(username) || !Failures.TryGetValue(username, out var record))
        {
            return false;
        }

        if (DateTime.UtcNow - record.LastFailure > Window)
        {
            Failures.TryRemove(username, out _);
            return false;
        }

        return record.Count >= MaxFailures;
    }

    private record UserFailureRecord(int Count, DateTime LastFailure);
}
