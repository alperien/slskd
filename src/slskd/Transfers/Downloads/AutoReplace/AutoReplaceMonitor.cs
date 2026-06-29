// <copyright file="AutoReplaceMonitor.cs" company="JP Dillingham">
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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Serilog;

/// <summary>
///     Periodically scans downloads for failures and stalls and routes them to the auto-replace service.
/// </summary>
public class AutoReplaceMonitor : IHostedService
{
    private int scanning;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AutoReplaceMonitor"/> class.
    /// </summary>
    /// <param name="autoReplaceService">The auto-replace service.</param>
    public AutoReplaceMonitor(IAutoReplaceService autoReplaceService)
    {
        AutoReplace = autoReplaceService;
    }

    private IAutoReplaceService AutoReplace { get; }
    private ILogger Log { get; } = Serilog.Log.ForContext<AutoReplaceMonitor>();

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Clock.EveryFiveSeconds += HandleTick;
        Log.Debug("Auto-replace monitor started");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Clock.EveryFiveSeconds -= HandleTick;
        Log.Debug("Auto-replace monitor stopped");
        return Task.CompletedTask;
    }

    private void HandleTick(object sender, ClockEventArgs args)
    {
        // ensure only one scan runs at a time; a scan may take several seconds while searches complete
        if (Interlocked.CompareExchange(ref scanning, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await AutoReplace.ScanAsync();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Auto-replace scan failed: {Message}", ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref scanning, 0);
            }
        });
    }
}
