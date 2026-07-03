// <copyright file="TransfersDbContextAttemptsTests.cs" company="JP Dillingham">
//     Copyright (c) JP Dillingham. All rights reserved.
//
//     This program is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published
//     by the Free Software Foundation, version 3.
//
//     This program is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//
//     You should have received a copy of the GNU Affero General Public License
//     along with this program.  If not, see https://www.gnu.org/licenses/.
// </copyright>

namespace slskd.Tests.Unit.Transfers
{
    using System;
    using Microsoft.Data.Sqlite;
    using Microsoft.EntityFrameworkCore;
    using slskd.Transfers;
    using Soulseek;
    using Xunit;

    public class TransfersDbContextAttemptsTests
    {
        /// <summary>
        ///     Regression test for the "NOT NULL constraint failed: Transfers.Attempts" error that
        ///     occurred when enqueueing a download against a pre-existing database whose Transfers.Attempts
        ///     column was created without a "DEFAULT 0" clause.
        ///
        ///     EF must always write the Attempts value on insert so the NOT NULL constraint is satisfied
        ///     regardless of whether the column has a store default.
        /// </summary>
        [Fact]
        public void Enqueue_Succeeds_On_Legacy_Schema_Where_Attempts_Has_No_Default()
        {
            // a shared in-memory database that lives for the duration of the open connection
            using var connection = new SqliteConnection("Data Source=attempts-regression;Mode=Memory;Cache=Shared");
            connection.Open();

            // simulate a legacy Transfers table whose Attempts column is NOT NULL but has NO default value
            using (var create = connection.CreateCommand())
            {
                create.CommandText = @"
                    CREATE TABLE ""Transfers"" (
                        ""Id"" TEXT NOT NULL CONSTRAINT ""PK_Transfers"" PRIMARY KEY,
                        ""BatchId"" TEXT NULL,
                        ""Username"" TEXT NULL,
                        ""Direction"" TEXT NOT NULL,
                        ""Filename"" TEXT NULL,
                        ""Size"" INTEGER NOT NULL,
                        ""State"" INTEGER NOT NULL,
                        ""StateDescription"" TEXT NULL,
                        ""RequestedAt"" TEXT NOT NULL,
                        ""EnqueuedAt"" TEXT NULL,
                        ""StartedAt"" TEXT NULL,
                        ""EndedAt"" TEXT NULL,
                        ""BytesTransferred"" INTEGER NOT NULL,
                        ""AverageSpeed"" REAL NOT NULL,
                        ""PlaceInQueue"" INTEGER NULL,
                        ""Exception"" TEXT NULL,
                        ""Attempts"" INTEGER NOT NULL,
                        ""NextAttemptAt"" TEXT NULL,
                        ""ReplacesId"" TEXT NULL,
                        ""ReplacementAttempts"" INTEGER NOT NULL DEFAULT 0,
                        ""AttemptedUsernames"" TEXT NULL,
                        ""Removed"" INTEGER NOT NULL,
                        ""BitRate"" INTEGER NULL,
                        ""BitDepth"" INTEGER NULL,
                        ""Length"" INTEGER NULL,
                        ""SampleRate"" INTEGER NULL,
                        ""IsVariableBitRate"" INTEGER NULL
                    );";
                create.ExecuteNonQuery();
            }

            var options = new DbContextOptionsBuilder<TransfersDbContext>()
                .UseSqlite(connection)
                .Options;

            using var context = new TransfersDbContext(options);

            // mirrors DownloadService enqueue: Attempts is not set explicitly and relies on its default (0)
            var transfer = new slskd.Transfers.Transfer
            {
                Id = Guid.NewGuid(),
                Username = "someone",
                Direction = TransferDirection.Download,
                Filename = @"path\to\song.flac",
                Size = 12345,
                RequestedAt = DateTime.UtcNow,
                State = TransferStates.Queued | TransferStates.Locally,
            };

            context.Transfers.Add(transfer);

            // before the fix this threw DbUpdateException -> SqliteException "NOT NULL constraint failed: Transfers.Attempts"
            var exception = Record.Exception(() => context.SaveChanges());

            Assert.Null(exception);
            Assert.Equal(0, transfer.Attempts);
        }
    }
}
