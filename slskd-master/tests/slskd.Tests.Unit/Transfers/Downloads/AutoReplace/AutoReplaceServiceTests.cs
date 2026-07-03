namespace slskd.Tests.Unit.Transfers.Downloads.AutoReplace;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Soulseek;
using slskd;
using slskd.Transfers;
using slskd.Transfers.Downloads;
using Xunit;

using AutoReplaceOptions = slskd.Options.TransfersOptions.GlobalDownloadOptions.AutoReplaceOptions;
using SearchFile = slskd.Search.File;
using SoulseekFile = Soulseek.File;
using Transfer = slskd.Transfers.Transfer;

public class AutoReplaceServiceTests
{
    [Theory]
    [InlineData("user\\folder\\Some Song.mp3", "Some Song")]
    [InlineData("user/folder/Another_Track.flac", "Another Track")]
    [InlineData("Track-01 (Remastered).mp3", "Track 01 Remastered")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Build_Query_Strips_Path_Extension_And_Punctuation(string filename, string expected)
    {
        Assert.Equal(expected, AutoReplaceService.BuildQuery(filename));
    }

    [Fact]
    public async Task Try_Replace_Returns_False_When_Disabled()
    {
        var mocks = new Mocks(enabled: false);
        var service = mocks.Build();

        var result = await service.TryReplaceAsync(Dead("deadguy", "user\\Song.mp3", 100), AutoReplaceReason.Failure);

        Assert.False(result);
        mocks.Downloads.Verify(
            d => d.EnqueueAsync(It.IsAny<string>(), It.IsAny<IEnumerable<(string, long)>>(), It.IsAny<Guid?>(), It.IsAny<TransferLineage>(), It.IsAny<IReadOnlyDictionary<string, TransferFileMetadata>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Try_Replace_Returns_False_When_User_Cancelled()
    {
        var mocks = new Mocks();
        var service = mocks.Build();
        var cancelled = Dead("deadguy", "user\\Song.mp3", 100);
        cancelled.State = TransferStates.Completed | TransferStates.Cancelled;

        var result = await service.TryReplaceAsync(cancelled, AutoReplaceReason.Failure);

        Assert.False(result);
    }

    [Fact]
    public async Task Try_Replace_Returns_False_When_Candidate_Budget_Exhausted()
    {
        var mocks = new Mocks();
        var service = mocks.Build();
        var dead = Dead("deadguy", "user\\Song.mp3", 100);
        dead.ReplacementAttempts = 5; // equals default MaxCandidates

        var result = await service.TryReplaceAsync(dead, AutoReplaceReason.Failure);

        Assert.False(result);
        mocks.Client.Verify(
            c => c.SearchAsync(It.IsAny<SearchQuery>(), It.IsAny<SearchScope>(), It.IsAny<int?>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken?>()),
            Times.Never);
    }

    [Fact]
    public async Task Try_Replace_Returns_False_When_No_Candidate_Found()
    {
        var mocks = new Mocks();
        mocks.SetSearchResults();
        var service = mocks.Build();

        var result = await service.TryReplaceAsync(Dead("deadguy", "user\\Song.mp3", 100), AutoReplaceReason.Failure);

        Assert.False(result);
        mocks.Downloads.Verify(
            d => d.EnqueueAsync(It.IsAny<string>(), It.IsAny<IEnumerable<(string, long)>>(), It.IsAny<Guid?>(), It.IsAny<TransferLineage>(), It.IsAny<IReadOnlyDictionary<string, TransferFileMetadata>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Try_Replace_Enqueues_Best_Candidate_With_Lineage()
    {
        var mocks = new Mocks();
        mocks.SetSearchResults(
            new SoulseekSearchResponseSpec("alice", HasFreeUploadSlot: true, UploadSpeed: 500, Files: [new SoulseekFile(1, "alice\\Song.mp3", 100, "mp3")]));

        TransferLineage captured = null;
        string enqueuedUsername = null;
        mocks.Downloads
            .Setup(d => d.EnqueueAsync(It.IsAny<string>(), It.IsAny<IEnumerable<(string, long)>>(), It.IsAny<Guid?>(), It.IsAny<TransferLineage>(), It.IsAny<IReadOnlyDictionary<string, TransferFileMetadata>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<(string, long)>, Guid?, TransferLineage, IReadOnlyDictionary<string, TransferFileMetadata>, CancellationToken>((u, f, b, l, m, c) =>
            {
                enqueuedUsername = u;
                captured = l;
            })
            .ReturnsAsync((new List<Transfer> { new Transfer { Id = Guid.NewGuid() } }, new List<(string, string)>()));

        var service = mocks.Build();
        var dead = Dead("deadguy", "user\\Song.mp3", 100);

        var result = await service.TryReplaceAsync(dead, AutoReplaceReason.Failure);

        Assert.True(result);
        Assert.Equal("alice", enqueuedUsername);
        Assert.NotNull(captured);
        Assert.Equal(dead.Id, captured.ReplacesId);
        Assert.Equal(1, captured.ReplacementAttempts);
        Assert.Contains("deadguy", captured.AttemptedUsernames);
    }

    [Fact]
    public async Task Try_Replace_For_Stall_Cancels_Original_First()
    {
        var mocks = new Mocks();
        mocks.SetSearchResults(
            new SoulseekSearchResponseSpec("alice", HasFreeUploadSlot: true, UploadSpeed: 500, Files: [new SoulseekFile(1, "alice\\Song.mp3", 100, "mp3")]));
        mocks.Downloads
            .Setup(d => d.EnqueueAsync(It.IsAny<string>(), It.IsAny<IEnumerable<(string, long)>>(), It.IsAny<Guid?>(), It.IsAny<TransferLineage>(), It.IsAny<IReadOnlyDictionary<string, TransferFileMetadata>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<Transfer> { new Transfer { Id = Guid.NewGuid() } }, new List<(string, string)>()));

        var service = mocks.Build();
        var dead = Dead("deadguy", "user\\Song.mp3", 100);
        dead.State = TransferStates.InProgress;

        var result = await service.TryReplaceAsync(dead, AutoReplaceReason.Stall);

        Assert.True(result);
        mocks.Downloads.Verify(d => d.TryCancel(dead.Id), Times.Once);
    }

    [Fact]
    public async Task Try_Replace_Fires_Cascade_To_Batch_Siblings()
    {
        var batchId = Guid.NewGuid();
        var siblingId = Guid.NewGuid();
        var dead1 = new Transfer
        {
            Id = Guid.NewGuid(),
            Username = "deadguy",
            Filename = "user\\Song.flac",
            Size = 100,
            Direction = TransferDirection.Download,
            State = TransferStates.Completed | TransferStates.Errored,
            EndedAt = DateTime.UtcNow,
            BatchId = batchId,
        };
        var dead2 = new Transfer
        {
            Id = siblingId,
            Username = "deadguy",
            Filename = "user\\OtherSong.flac",
            Size = 200,
            Direction = TransferDirection.Download,
            State = TransferStates.Completed | TransferStates.Errored,
            EndedAt = DateTime.UtcNow,
            BatchId = batchId,
        };

        var mocks = new Mocks();
        mocks.SetSearchResults(
            new SoulseekSearchResponseSpec("alice", HasFreeUploadSlot: true, UploadSpeed: 500, Files: [new SoulseekFile(1, "alice\\Song.flac", 100, "flac")]));

        mocks.Downloads
            .Setup(d => d.EnqueueAsync(It.IsAny<string>(), It.IsAny<IEnumerable<(string, long)>>(), It.IsAny<Guid?>(), It.IsAny<TransferLineage>(), It.IsAny<IReadOnlyDictionary<string, TransferFileMetadata>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<Transfer> { new Transfer { Id = Guid.NewGuid() } }, new List<(string, string)>()));

        // List() returns the sibling for batch queries
        var allTransfers = new List<Transfer> { dead1, dead2 };
        mocks.Downloads
            .Setup(d => d.List(It.IsAny<Expression<Func<Transfer, bool>>>(), It.IsAny<bool>()))
            .Returns((Expression<Func<Transfer, bool>> expr, bool includeRemoved) =>
            {
                var compiled = expr.Compile();
                return allTransfers.Where(t => compiled(t)).ToList();
            });

        // second search (for the sibling) also returns a result
        mocks.Client
            .Setup(c => c.SearchAsync(It.IsAny<SearchQuery>(), It.IsAny<SearchScope>(), It.IsAny<int?>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken?>()))
            .ReturnsAsync(((Search)null, (IReadOnlyCollection<SearchResponse>)new List<SearchResponse>
            {
                new SearchResponse("bob", token: 0, true, 500, queueLength: 0, fileList: [new SoulseekFile(1, "bob\\OtherSong.flac", 200, "flac")]),
            }));

        var service = mocks.Build();
        var result = await service.TryReplaceAsync(dead1, AutoReplaceReason.Failure);

        Assert.True(result);

        // two searches: one for the original replacement, one for the cascade sibling
        mocks.Client.Verify(
            c => c.SearchAsync(It.IsAny<SearchQuery>(), It.IsAny<SearchScope>(), It.IsAny<int?>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken?>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task Cascade_Does_Not_Fire_When_BatchId_Is_Null()
    {
        var mocks = new Mocks();
        mocks.SetSearchResults(
            new SoulseekSearchResponseSpec("alice", HasFreeUploadSlot: true, UploadSpeed: 500, Files: [new SoulseekFile(1, "alice\\Song.mp3", 100, "mp3")]));
        mocks.Downloads
            .Setup(d => d.EnqueueAsync(It.IsAny<string>(), It.IsAny<IEnumerable<(string, long)>>(), It.IsAny<Guid?>(), It.IsAny<TransferLineage>(), It.IsAny<IReadOnlyDictionary<string, TransferFileMetadata>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<Transfer> { new Transfer { Id = Guid.NewGuid() } }, new List<(string, string)>()));

        var service = mocks.Build();
        var dead = Dead("deadguy", "user\\Song.mp3", 100); // no BatchId

        var result = await service.TryReplaceAsync(dead, AutoReplaceReason.Failure);

        Assert.True(result);
        mocks.Downloads.Verify(
            d => d.EnqueueAsync(It.IsAny<string>(), It.IsAny<IEnumerable<(string, long)>>(), It.IsAny<Guid?>(), It.IsAny<TransferLineage>(), It.IsAny<IReadOnlyDictionary<string, TransferFileMetadata>>(), It.IsAny<CancellationToken>()),
            Times.Once); // only the original replacement, no cascade
    }

    private static Transfer Dead(string username, string filename, long size)
        => new Transfer
        {
            Id = Guid.NewGuid(),
            Username = username,
            Filename = filename,
            Size = size,
            Direction = TransferDirection.Download,
            State = TransferStates.Completed | TransferStates.Errored,
            EndedAt = DateTime.UtcNow,
        };

    private sealed record SoulseekSearchResponseSpec(string Username, bool HasFreeUploadSlot, int UploadSpeed, IEnumerable<SoulseekFile> Files);

    private sealed class Mocks
    {
        public Mocks(bool enabled = true)
        {
            OptionsMonitor = new TestOptionsMonitor<Options>(new Options
            {
                Transfers = new Options.TransfersOptions
                {
                    Download = new Options.TransfersOptions.GlobalDownloadOptions
                    {
                        AutoReplace = new AutoReplaceOptions { Enabled = enabled },
                    },
                },
            });
        }

        public Mock<IDownloadService> Downloads { get; } = new();

        public Mock<ISoulseekClient> Client { get; } = new();

        public TestOptionsMonitor<Options> OptionsMonitor { get; }

        public void SetSearchResults(params SoulseekSearchResponseSpec[] specs)
        {
            var responses = specs
                .Select(s => new SearchResponse(s.Username, token: 0, s.HasFreeUploadSlot, s.UploadSpeed, queueLength: 0, fileList: s.Files))
                .ToList();

            Client
                .Setup(c => c.SearchAsync(It.IsAny<SearchQuery>(), It.IsAny<SearchScope>(), It.IsAny<int?>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken?>()))
                .ReturnsAsync(((Search)null, (IReadOnlyCollection<SearchResponse>)responses));
        }

        public AutoReplaceService Build()
            => new AutoReplaceService(Downloads.Object, Client.Object, OptionsMonitor);
    }
}
