// <copyright file="Z2026_06_29_TransferAutoReplaceMigration.cs" company="JP Dillingham">
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
///     Updates the Transfers table to add the auto-replace lineage columns (ReplacesId, ReplacementAttempts,
///     AttemptedUsernames).
/// </summary>
public class Z2026_06_29_TransferAutoReplaceMigration : IMigration
{
    public Z2026_06_29_TransferAutoReplaceMigration(ConnectionStringDictionary connectionStrings)
    {
        ConnectionString = connectionStrings[Database.Transfers];
    }

    private ILogger Log { get; } = Serilog.Log.ForContext<Z2026_06_29_TransferAutoReplaceMigration>();
    private string ConnectionString { get; }

    public bool NeedsToBeApplied()
    {
        var schema = SchemaInspector.GetDatabaseSchema(ConnectionString);
        var columns = schema["Transfers"];

        // check to see if the auto-replace columns exist
        if (columns.Any(c => c.Name == nameof(Transfer.ReplacesId))
            && columns.Any(c => c.Name == nameof(Transfer.ReplacementAttempts))
            && columns.Any(c => c.Name == nameof(Transfer.AttemptedUsernames)))
        {
            return false;
        }

        return true;
    }

    public void Apply()
    {
        if (!NeedsToBeApplied())
        {
            Log.Information("> Migration {Name} is not necessary or has already been applied", nameof(Z2026_06_29_TransferAutoReplaceMigration));
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

            Log.Information("> Adding ReplacesId, ReplacementAttempts, and AttemptedUsernames columns to the Transfers table...");

            if (!columns.Any(c => c.Name == nameof(Transfer.ReplacesId)))
            {
                Exec("ALTER TABLE Transfers ADD COLUMN ReplacesId TEXT NULL;");
            }

            if (!columns.Any(c => c.Name == nameof(Transfer.ReplacementAttempts)))
            {
                Exec("ALTER TABLE Transfers ADD COLUMN ReplacementAttempts INTEGER NOT NULL DEFAULT 0;");
            }

            if (!columns.Any(c => c.Name == nameof(Transfer.AttemptedUsernames)))
            {
                Exec("ALTER TABLE Transfers ADD COLUMN AttemptedUsernames TEXT NULL;");
            }

            Log.Information("> New columns added");

            Log.Information("> Adding missing index(es) on the Transfers table...");

            Exec("CREATE INDEX IF NOT EXISTS IDX_Transfers_ReplacesId ON Transfers (ReplacesId)");

            Log.Information("> Index(es) created");
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
