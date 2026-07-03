// <copyright file="Z2026_07_03_TransferMetadataMigration.cs" company="JP Dillingham">
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

namespace slskd.Migrations;

using System;
using System.Linq;
using Microsoft.Data.Sqlite;
using Serilog;
using slskd.Transfers;

/// <summary>
///     Updates the Transfers table to add audio metadata columns (BitRate, BitDepth, Length, SampleRate,
///     IsVariableBitRate) used by the auto-replace system for improved candidate matching.
/// </summary>
public class Z2026_07_03_TransferMetadataMigration : IMigration
{
    public Z2026_07_03_TransferMetadataMigration(ConnectionStringDictionary connectionStrings)
    {
        ConnectionString = connectionStrings[Database.Transfers];
    }

    private ILogger Log { get; } = Serilog.Log.ForContext<Z2026_07_03_TransferMetadataMigration>();
    private string ConnectionString { get; }

    public bool NeedsToBeApplied()
    {
        var schema = SchemaInspector.GetDatabaseSchema(ConnectionString);
        var columns = schema["Transfers"];

        var expected = new[] { nameof(Transfer.BitRate), nameof(Transfer.BitDepth), nameof(Transfer.Length), nameof(Transfer.SampleRate), nameof(Transfer.IsVariableBitRate) };
        return expected.Any(col => !columns.Any(c => c.Name == col));
    }

    public void Apply()
    {
        if (!NeedsToBeApplied())
        {
            Log.Information("> Migration {Name} is not necessary or has already been applied", nameof(Z2026_07_03_TransferMetadataMigration));
            return;
        }

        var columns = SchemaInspector.GetDatabaseSchema(ConnectionString)["Transfers"];

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var transaction = connection.BeginTransaction();

        try
        {
            void Exec(string sql)
            {
                using var command = new SqliteCommand(sql, connection, transaction);
                command.ExecuteNonQuery();
            }

            Log.Information("> Adding audio metadata columns to the Transfers table...");

            if (!columns.Any(c => c.Name == nameof(Transfer.BitRate)))
            {
                Exec("ALTER TABLE Transfers ADD COLUMN BitRate INTEGER NULL;");
            }

            if (!columns.Any(c => c.Name == nameof(Transfer.BitDepth)))
            {
                Exec("ALTER TABLE Transfers ADD COLUMN BitDepth INTEGER NULL;");
            }

            if (!columns.Any(c => c.Name == nameof(Transfer.Length)))
            {
                Exec("ALTER TABLE Transfers ADD COLUMN Length INTEGER NULL;");
            }

            if (!columns.Any(c => c.Name == nameof(Transfer.SampleRate)))
            {
                Exec("ALTER TABLE Transfers ADD COLUMN SampleRate INTEGER NULL;");
            }

            if (!columns.Any(c => c.Name == nameof(Transfer.IsVariableBitRate)))
            {
                Exec("ALTER TABLE Transfers ADD COLUMN IsVariableBitRate INTEGER NULL;");
            }

            Log.Information("> New columns added");
            transaction.Commit();
            Log.Information("> Done!");
        }
        catch (Exception)
        {
            transaction.Rollback();
            throw;
        }
    }
}
