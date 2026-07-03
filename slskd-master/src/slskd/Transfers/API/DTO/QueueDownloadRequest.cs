// <copyright file="QueueDownloadRequest.cs" company="JP Dillingham">
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

using System.ComponentModel.DataAnnotations;

namespace slskd.Transfers.API
{
    public class QueueDownloadRequest
    {
        /// <summary>
        ///     Gets or sets the filename to download.
        /// </summary>
        [Required]
        public string Filename { get; set; }

        /// <summary>
        ///     Gets or sets the size of the file.
        /// </summary>
        public long Size { get; set; }

        /// <summary>
        ///     Gets or sets the optional audio bitrate, in kilobits per second.
        /// </summary>
        public int? BitRate { get; set; }

        /// <summary>
        ///     Gets or sets the optional audio bit depth.
        /// </summary>
        public int? BitDepth { get; set; }

        /// <summary>
        ///     Gets or sets the optional audio length, in seconds.
        /// </summary>
        public int? Length { get; set; }

        /// <summary>
        ///     Gets or sets the optional audio sample rate, in hertz.
        /// </summary>
        public int? SampleRate { get; set; }

        /// <summary>
        ///     Gets or sets a value indicating whether the audio bitrate is variable.
        /// </summary>
        public bool? IsVariableBitRate { get; set; }
    }
}
