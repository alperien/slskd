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
}

/// <summary>
///     Pure logic for selecting the best alternate source for a download from a set of search responses.
/// </summary>
public static class AutoReplaceMatcher
{
    /// <summary>
    ///     Selects the best alternate source for the file identified by <paramref name="originalFilename"/> from the
    ///     supplied <paramref name="responses"/>, excluding any source whose username appears in
    ///     <paramref name="excludedUsernames"/>.
    /// </summary>
    /// <param name="originalFilename">The original remote filename (full remote path) being replaced.</param>
    /// <param name="originalSize">The size of the original file, in bytes.</param>
    /// <param name="responses">The search responses to consider.</param>
    /// <param name="excludedUsernames">Usernames that must not be selected (already-tried or known-bad sources).</param>
    /// <param name="options">The candidate matching options.</param>
    /// <returns>The best matching candidate, or null if none is suitable.</returns>
    public static AutoReplaceCandidate SelectBest(
        string originalFilename,
        long originalSize,
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
        var targetStem = Stem(targetBasename);
        var excluded = new HashSet<string>(excludedUsernames ?? [], StringComparer.OrdinalIgnoreCase);

        var scored = new List<(AutoReplaceCandidate Candidate, (int Slot, int Speed, long Queue) Score)>();

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

            // never select locked files; only consider the freely available ones
            foreach (var file in response.Files ?? Enumerable.Empty<File>())
            {
                if (file is null || file.IsLocked || string.IsNullOrWhiteSpace(file.Filename))
                {
                    continue;
                }

                var fileBasename = Basename(file.Filename);

                if (options.RequireSameExtension)
                {
                    // strict: compare full basename (name + extension)
                    if (!string.Equals(fileBasename, targetBasename, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }
                else
                {
                    // relaxed: compare only the stem (name without extension)
                    if (!string.Equals(Stem(fileBasename), targetStem, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                if (options.RequireExactSize && Math.Abs(file.Size - originalSize) > options.SizeToleranceBytes)
                {
                    continue;
                }

                var candidate = new AutoReplaceCandidate
                {
                    Username = response.Username,
                    Filename = file.Filename,
                    Size = file.Size,
                };

                // higher is better: prefer a free upload slot, then higher speed, then a shorter queue
                var score = (
                    Slot: response.HasFreeUploadSlot ? 1 : 0,
                    Speed: response.UploadSpeed,
                    Queue: -response.QueueLength);

                scored.Add((candidate, score));
            }
        }

        return scored
            .OrderByDescending(x => x.Score.Slot)
            .ThenByDescending(x => x.Score.Speed)
            .ThenByDescending(x => x.Score.Queue)
            .ThenBy(x => x.Candidate.Username, StringComparer.OrdinalIgnoreCase) // deterministic tie-break
            .Select(x => x.Candidate)
            .FirstOrDefault();
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

    private static string Stem(string basename)
    {
        var dot = basename.LastIndexOf('.');
        return dot >= 0 ? basename[..dot] : basename;
    }
}
