namespace slskd.Tests.Unit.Transfers.Downloads.AutoReplace;

using System.Collections.Generic;
using System.Linq;
using slskd;
using slskd.Search;
using slskd.Transfers.Downloads;
using Xunit;

using MatchOptions = slskd.Options.TransfersOptions.GlobalDownloadOptions.AutoReplaceOptions.MatchOptions;
using SearchFile = slskd.Search.File;

public class AutoReplaceMatcherTests
{
    private static SearchFile File(string filename, long size, bool isLocked = false)
        => new SearchFile { Filename = filename, Size = size, IsLocked = isLocked };

    private static Response Response(
        string username,
        IEnumerable<SearchFile> files,
        bool hasFreeUploadSlot = true,
        int uploadSpeed = 100,
        long queueLength = 0)
        => new Response
        {
            Username = username,
            Files = files.ToList(),
            HasFreeUploadSlot = hasFreeUploadSlot,
            UploadSpeed = uploadSpeed,
            QueueLength = queueLength,
        };

    [Fact]
    public void Returns_Null_When_No_Responses()
    {
        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100, [], null, new MatchOptions());

        Assert.Null(result);
    }

    [Fact]
    public void Returns_Null_When_Filename_Is_Null()
    {
        var responses = new[] { Response("bob", [File("a\\Song.mp3", 100)]) };

        var result = AutoReplaceMatcher.SelectBest(null, 100, responses, null, new MatchOptions());

        Assert.Null(result);
    }

    [Fact]
    public void Selects_Candidate_Matching_Basename_And_Size()
    {
        var responses = new[] { Response("bob", [File("other\\dir\\Song.mp3", 100)]) };

        var result = AutoReplaceMatcher.SelectBest("user\\path\\Song.mp3", 100, responses, null, new MatchOptions());

        Assert.NotNull(result);
        Assert.Equal("bob", result.Username);
        Assert.Equal("other\\dir\\Song.mp3", result.Filename);
        Assert.Equal(100, result.Size);
    }

    [Fact]
    public void Rejects_Size_Mismatch_When_Exact_Match_Required()
    {
        var responses = new[] { Response("bob", [File("a\\Song.mp3", 101)]) };

        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100, responses, null, new MatchOptions());

        Assert.Null(result);
    }

    [Fact]
    public void Accepts_Size_Within_Tolerance()
    {
        var options = new MatchOptions { SizeToleranceBytes = 5 };
        var responses = new[] { Response("bob", [File("a\\Song.mp3", 103)]) };

        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100, responses, null, options);

        Assert.NotNull(result);
        Assert.Equal("bob", result.Username);
    }

    [Fact]
    public void Rejects_When_Basename_Differs()
    {
        var responses = new[] { Response("bob", [File("a\\Different.mp3", 100)]) };

        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100, responses, null, new MatchOptions());

        Assert.Null(result);
    }

    [Fact]
    public void Excludes_Excluded_Usernames()
    {
        var responses = new[]
        {
            Response("bob", [File("a\\Song.mp3", 100)], hasFreeUploadSlot: true, uploadSpeed: 1000),
            Response("alice", [File("b\\Song.mp3", 100)], hasFreeUploadSlot: true, uploadSpeed: 10),
        };

        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100, responses, ["bob"], new MatchOptions());

        Assert.NotNull(result);
        Assert.Equal("alice", result.Username);
    }

    [Fact]
    public void Skips_Locked_Files()
    {
        var responses = new[] { Response("bob", [File("a\\Song.mp3", 100, isLocked: true)]) };

        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100, responses, null, new MatchOptions());

        Assert.Null(result);
    }

    [Fact]
    public void Prefers_Free_Upload_Slot_Over_Speed()
    {
        var responses = new[]
        {
            Response("fast-no-slot", [File("a\\Song.mp3", 100)], hasFreeUploadSlot: false, uploadSpeed: 5000),
            Response("slow-with-slot", [File("b\\Song.mp3", 100)], hasFreeUploadSlot: true, uploadSpeed: 10),
        };

        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100, responses, null, new MatchOptions());

        Assert.Equal("slow-with-slot", result.Username);
    }

    [Fact]
    public void Prefers_Higher_Speed_When_Slots_Equal()
    {
        var responses = new[]
        {
            Response("slow", [File("a\\Song.mp3", 100)], hasFreeUploadSlot: true, uploadSpeed: 100),
            Response("fast", [File("b\\Song.mp3", 100)], hasFreeUploadSlot: true, uploadSpeed: 9000),
        };

        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100, responses, null, new MatchOptions());

        Assert.Equal("fast", result.Username);
    }

    [Fact]
    public void Prefers_Shorter_Queue_When_Speed_Equal()
    {
        var responses = new[]
        {
            Response("busy", [File("a\\Song.mp3", 100)], hasFreeUploadSlot: true, uploadSpeed: 100, queueLength: 50),
            Response("idle", [File("b\\Song.mp3", 100)], hasFreeUploadSlot: true, uploadSpeed: 100, queueLength: 1),
        };

        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100, responses, null, new MatchOptions());

        Assert.Equal("idle", result.Username);
    }

    [Fact]
    public void Filters_When_Free_Upload_Slot_Required()
    {
        var options = new MatchOptions { RequireFreeUploadSlot = true };
        var responses = new[] { Response("bob", [File("a\\Song.mp3", 100)], hasFreeUploadSlot: false) };

        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100, responses, null, options);

        Assert.Null(result);
    }

    [Fact]
    public void Filters_Below_Minimum_Upload_Speed()
    {
        var options = new MatchOptions { MinimumUploadSpeed = 500 };
        var responses = new[] { Response("bob", [File("a\\Song.mp3", 100)], uploadSpeed: 100) };

        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100, responses, null, options);

        Assert.Null(result);
    }

    [Fact]
    public void Tie_Break_Is_Deterministic_By_Username()
    {
        var responses = new[]
        {
            Response("bob", [File("a\\Song.mp3", 100)], hasFreeUploadSlot: true, uploadSpeed: 100, queueLength: 0),
            Response("alice", [File("b\\Song.mp3", 100)], hasFreeUploadSlot: true, uploadSpeed: 100, queueLength: 0),
        };

        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100, responses, null, new MatchOptions());

        Assert.Equal("alice", result.Username);
    }
}
