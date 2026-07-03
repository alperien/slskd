namespace slskd.Tests.Unit.Transfers.Downloads.AutoReplace;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using slskd;
using slskd.Search;
using slskd.Transfers.Downloads;
using Xunit;

using MatchOptions = slskd.Options.TransfersOptions.GlobalDownloadOptions.AutoReplaceOptions.MatchOptions;
using SearchFile = slskd.Search.File;

public class AutoReplaceMatcherTests
{
    private static SearchFile File(string filename, long size, bool isLocked = false, int? length = null, int? bitRate = null, int? bitDepth = null, int? sampleRate = null)
        => new SearchFile
        {
            Filename = filename,
            Size = size,
            IsLocked = isLocked,
            Length = length,
            BitRate = bitRate,
            BitDepth = bitDepth,
            SampleRate = sampleRate,
        };

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
        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100, null, null, null, null, [], null, new MatchOptions { MinTokenSimilarity = 1.0 });

        Assert.Null(result);
    }

    [Fact]
    public void Returns_Null_When_Filename_Is_Null()
    {
        var responses = new[] { Response("bob", [File("a\\Song.mp3", 100)]) };

        var result = AutoReplaceMatcher.SelectBest(null, 100, null, null, null, null, responses, null, new MatchOptions { MinTokenSimilarity = 1.0 });

        Assert.Null(result);
    }

    [Fact]
    public void Selects_Candidate_Matching_Basename_And_Size()
    {
        var responses = new[] { Response("bob", [File("other\\dir\\Song.mp3", 100)]) };

        var result = AutoReplaceMatcher.SelectBest("user\\path\\Song.mp3", 100, null, null, null, null, responses, null, new MatchOptions { MinTokenSimilarity = 1.0 });

        Assert.NotNull(result);
        Assert.Equal("bob", result.Username);
        Assert.Equal("other\\dir\\Song.mp3", result.Filename);
        Assert.Equal(100, result.Size);
    }

    [Fact]
    public void Rejects_Size_Mismatch_When_Exact_Match_Required()
    {
        var options = new MatchOptions { MinTokenSimilarity = 1.0, SizeToleranceBytes = 0, SizeTolerancePercent = 0 };
        var responses = new[] { Response("bob", [File("a\\Song.mp3", 101)]) };

        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100, null, null, null, null, responses, null, options);

        Assert.Null(result);
    }

    [Fact]
    public void Accepts_Size_Within_Tolerance()
    {
        var options = new MatchOptions { MinTokenSimilarity = 1.0, SizeToleranceBytes = 5 };
        var responses = new[] { Response("bob", [File("a\\Song.mp3", 103)]) };

        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100, null, null, null, null, responses, null, options);

        Assert.NotNull(result);
        Assert.Equal("bob", result.Username);
    }

    [Fact]
    public void Rejects_Size_Mismatch_When_Both_Byte_And_Percent_Tolerance_Exceeded()
    {
        var options = new MatchOptions { MinTokenSimilarity = 1.0, SizeToleranceBytes = 10, SizeTolerancePercent = 1.0 };
        var responses = new[] { Response("bob", [File("a\\Song.mp3", 200)]) };

        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100, null, null, null, null, responses, null, options);

        Assert.Null(result);
    }

    [Fact]
    public void Accepts_Size_Within_Percent_Tolerance_When_Byte_Tolerance_Exceeded()
    {
        var options = new MatchOptions { MinTokenSimilarity = 1.0, SizeToleranceBytes = 0, SizeTolerancePercent = 5.0 };
        var responses = new[] { Response("bob", [File("a\\Song.mp3", 104)]) };

        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100, null, null, null, null, responses, null, options);

        Assert.NotNull(result);
        Assert.Equal("bob", result.Username);
    }

    [Fact]
    public void Rejects_When_Basename_Differs_With_Exact_Similarity()
    {
        var responses = new[] { Response("bob", [File("a\\Different.mp3", 100)]) };

        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100, null, null, null, null, responses, null, new MatchOptions { MinTokenSimilarity = 1.0 });

        Assert.Null(result);
    }

    [Fact]
    public void Accepts_When_Basename_Differs_With_Token_Similarity()
    {
        var responses = new[] { Response("bob", [File("a\\Song Song.mp3", 100)]) };

        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100, null, null, null, null, responses, null, new MatchOptions { MinTokenSimilarity = 0.3 });

        Assert.NotNull(result);
        Assert.Equal("bob", result.Username);
    }

    [Fact]
    public void Excludes_Excluded_Usernames()
    {
        var responses = new[]
        {
            Response("bob", [File("a\\Song.mp3", 100)], hasFreeUploadSlot: true, uploadSpeed: 1000),
            Response("alice", [File("b\\Song.mp3", 100)], hasFreeUploadSlot: true, uploadSpeed: 10),
        };

        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100, null, null, null, null, responses, ["bob"], new MatchOptions { MinTokenSimilarity = 1.0 });

        Assert.NotNull(result);
        Assert.Equal("alice", result.Username);
    }

    [Fact]
    public void Skips_Locked_Files()
    {
        var responses = new[] { Response("bob", [File("a\\Song.mp3", 100, isLocked: true)]) };

        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100, null, null, null, null, responses, null, new MatchOptions { MinTokenSimilarity = 1.0 });

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

        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100, null, null, null, null, responses, null, new MatchOptions { MinTokenSimilarity = 1.0 });

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

        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100, null, null, null, null, responses, null, new MatchOptions { MinTokenSimilarity = 1.0 });

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

        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100, null, null, null, null, responses, null, new MatchOptions { MinTokenSimilarity = 1.0 });

        Assert.Equal("idle", result.Username);
    }

    [Fact]
    public void Filters_When_Free_Upload_Slot_Required()
    {
        var options = new MatchOptions { MinTokenSimilarity = 1.0, RequireFreeUploadSlot = true };
        var responses = new[] { Response("bob", [File("a\\Song.mp3", 100)], hasFreeUploadSlot: false) };

        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100, null, null, null, null, responses, null, options);

        Assert.Null(result);
    }

    [Fact]
    public void Filters_Below_Minimum_Upload_Speed()
    {
        var options = new MatchOptions { MinTokenSimilarity = 1.0, MinimumUploadSpeed = 500 };
        var responses = new[] { Response("bob", [File("a\\Song.mp3", 100)], uploadSpeed: 100) };

        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100, null, null, null, null, responses, null, options);

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

        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100, null, null, null, null, responses, null, new MatchOptions { MinTokenSimilarity = 1.0 });

        Assert.Equal("alice", result.Username);
    }

    [Fact]
    public void Rejects_Different_Extension_When_No_Groups_Configured()
    {
        var options = new MatchOptions { MinTokenSimilarity = 0.3, RequireSameExtension = true, ExtensionGroups = [] };
        var responses = new[] { Response("bob", [File("a\\Song.wav", 100)]) };

        var result = AutoReplaceMatcher.SelectBest("user\\Song.flac", 100, null, null, null, null, responses, null, options);

        Assert.Null(result);
    }

    [Fact]
    public void Rejects_Different_Extension_When_Group_Mismatch()
    {
        var options = new MatchOptions { MinTokenSimilarity = 0.3, RequireSameExtension = true, ExtensionGroups = [["flac", "wav"], ["mp3"]] };
        var responses = new[] { Response("bob", [File("a\\Song.mp3", 100)]) };

        var result = AutoReplaceMatcher.SelectBest("user\\Song.flac", 100, null, null, null, null, responses, null, options);

        Assert.Null(result);
    }

    [Fact]
    public void Accepts_Equivalent_Extension_By_Group()
    {
        var options = new MatchOptions { MinTokenSimilarity = 0.3, RequireSameExtension = true, ExtensionGroups = [["flac", "wav"], ["mp3"]] };
        var responses = new[] { Response("bob", [File("a\\Song.wav", 100)]) };

        var result = AutoReplaceMatcher.SelectBest("user\\Song.flac", 100, null, null, null, null, responses, null, options);

        Assert.NotNull(result);
        Assert.Equal("bob", result.Username);
    }

    [Fact]
    public void Accepts_Equivalent_Extension_By_Default_Group()
    {
        var responses = new[] { Response("bob", [File("a\\Song.wav", 100)]) };

        var result = AutoReplaceMatcher.SelectBest("user\\Song.flac", 100, null, null, null, null, responses, null, new MatchOptions { MinTokenSimilarity = 0.3 });

        Assert.NotNull(result);
        Assert.Equal("bob", result.Username);
    }

    // --- SelectAll Tests ---

    [Fact]
    public void SelectAll_Returns_Empty_When_No_Responses()
    {
        var result = AutoReplaceMatcher.SelectAll("user\\Song.mp3", 100, null, null, null, null, [], null, new MatchOptions { MinTokenSimilarity = 1.0 });

        Assert.Empty(result);
    }

    [Fact]
    public void SelectAll_Returns_Empty_When_Filename_Null()
    {
        var responses = new[] { Response("bob", [File("a\\Song.mp3", 100)]) };

        var result = AutoReplaceMatcher.SelectAll(null, 100, null, null, null, null, responses, null, new MatchOptions { MinTokenSimilarity = 1.0 });

        Assert.Empty(result);
    }

    [Fact]
    public void SelectAll_Returns_Single_Candidate()
    {
        var responses = new[] { Response("bob", [File("a\\Song.mp3", 100)]) };

        var result = AutoReplaceMatcher.SelectAll("user\\Song.mp3", 100, null, null, null, null, responses, null, new MatchOptions { MinTokenSimilarity = 1.0 });

        Assert.Single(result);
        Assert.Equal("bob", result[0].Username);
    }

    [Fact]
    public void SelectAll_Returns_Multiple_Candidates_In_Order()
    {
        var responses = new[]
        {
            Response("slow", [File("a\\Song.mp3", 100)], hasFreeUploadSlot: true, uploadSpeed: 100),
            Response("fast", [File("b\\Song.mp3", 100)], hasFreeUploadSlot: true, uploadSpeed: 9000),
        };

        var result = AutoReplaceMatcher.SelectAll("user\\Song.mp3", 100, null, null, null, null, responses, null, new MatchOptions { MinTokenSimilarity = 1.0 });

        Assert.Equal(2, result.Count);
        Assert.Equal("fast", result[0].Username);
        Assert.Equal("slow", result[1].Username);
    }

    [Fact]
    public void SelectAll_Excludes_Excluded_Usernames()
    {
        var responses = new[]
        {
            Response("bob", [File("a\\Song.mp3", 100)]),
            Response("alice", [File("b\\Song.mp3", 100)]),
        };

        var result = AutoReplaceMatcher.SelectAll("user\\Song.mp3", 100, null, null, null, null, responses, ["bob"], new MatchOptions { MinTokenSimilarity = 1.0 });

        Assert.Single(result);
        Assert.Equal("alice", result[0].Username);
    }

    [Fact]
    public void SelectAll_Filters_By_Extension_Groups()
    {
        var options = new MatchOptions { MinTokenSimilarity = 0.3, RequireSameExtension = true, ExtensionGroups = [["flac", "wav"]] };
        var responses = new[] { Response("bob", [File("a\\Song.wav", 100)]) };

        var result = AutoReplaceMatcher.SelectAll("user\\Song.flac", 100, null, null, null, null, responses, null, options);

        Assert.Single(result);
        Assert.Equal("bob", result[0].Username);
    }

    [Fact]
    public void SelectAll_Rejects_When_No_Match()
    {
        var options = new MatchOptions { MinTokenSimilarity = 1.0 };
        var responses = new[] { Response("bob", [File("a\\Different.mp3", 100)]) };

        var result = AutoReplaceMatcher.SelectAll("user\\Song.mp3", 100, null, null, null, null, responses, null, options);

        Assert.Empty(result);
    }

    [Fact]
    public void SelectAll_Deduplicates_By_Username()
    {
        var responses = new[]
        {
            Response("bob", [File("a\\Song.mp3", 100), File("b\\Song.mp3", 100)]),
        };

        var result = AutoReplaceMatcher.SelectAll("user\\Song.mp3", 100, null, null, null, null, responses, null, new MatchOptions { MinTokenSimilarity = 1.0 });

        // same user, two matching files — both should be returned
        Assert.Equal(2, result.Count);
        Assert.All(result, c => Assert.Equal("bob", c.Username));
    }

    // --- UserFailureTracker Tests ---

    [Fact]
    public void UserFailureTracker_Records_Failure()
    {
        var tracker = new UserFailureTracker(maxFailures: 2, window: TimeSpan.FromMinutes(30));

        tracker.RecordFailure("bob");

        Assert.False(tracker.IsUnreliable("bob")); // only 1 failure, need 2
    }

    [Fact]
    public void UserFailureTracker_Marks_Unreliable_After_Threshold()
    {
        var tracker = new UserFailureTracker(maxFailures: 2, window: TimeSpan.FromMinutes(30));

        tracker.RecordFailure("bob");
        tracker.RecordFailure("bob");

        Assert.True(tracker.IsUnreliable("bob"));
    }

    [Fact]
    public void UserFailureTracker_Success_Resets_Failures()
    {
        var tracker = new UserFailureTracker(maxFailures: 2, window: TimeSpan.FromMinutes(30));

        tracker.RecordFailure("bob");
        tracker.RecordFailure("bob");
        tracker.RecordSuccess("bob");

        Assert.False(tracker.IsUnreliable("bob"));
    }

    [Fact]
    public void UserFailureTracker_Decays_After_Window()
    {
        var tracker = new UserFailureTracker(maxFailures: 2, window: TimeSpan.FromMilliseconds(50));

        tracker.RecordFailure("bob");
        tracker.RecordFailure("bob");

        Assert.True(tracker.IsUnreliable("bob"));

        Thread.Sleep(100);

        Assert.False(tracker.IsUnreliable("bob"));
    }

    [Fact]
    public void UserFailureTracker_IsUnreliable_Returns_False_For_Unknown_User()
    {
        var tracker = new UserFailureTracker(maxFailures: 2, window: TimeSpan.FromMinutes(30));

        Assert.False(tracker.IsUnreliable("unknown"));
    }

    [Fact]
    public void UserFailureTracker_IsUnreliable_Returns_False_For_Null_Username()
    {
        var tracker = new UserFailureTracker(maxFailures: 2, window: TimeSpan.FromMinutes(30));

        Assert.False(tracker.IsUnreliable(null));
    }

    [Fact]
    public void UserFailureTracker_Records_Multiple_Users_Independently()
    {
        var tracker = new UserFailureTracker(maxFailures: 2, window: TimeSpan.FromMinutes(30));

        tracker.RecordFailure("bob");
        tracker.RecordFailure("alice");
        tracker.RecordFailure("bob");

        Assert.True(tracker.IsUnreliable("bob"));
        Assert.False(tracker.IsUnreliable("alice")); // only 1 failure
    }

    // --- Token Similarity Tests ---

    [Fact]
    public void Tokenize_Returns_Empty_For_Null()
    {
        var result = AutoReplaceMatcher.Tokenize(null);
        Assert.Empty(result);
    }

    [Fact]
    public void Tokenize_Returns_Empty_For_Empty()
    {
        var result = AutoReplaceMatcher.Tokenize("");
        Assert.Empty(result);
    }

    [Fact]
    public void Tokenize_Drops_Single_Character_Tokens()
    {
        var result = AutoReplaceMatcher.Tokenize("a b c");
        Assert.Empty(result);
    }

    [Fact]
    public void Tokenize_Returns_Distinct_Tokens()
    {
        var result = AutoReplaceMatcher.Tokenize("foo foo bar");
        Assert.Equal(2, result.Length);
    }

    [Fact]
    public void JaccardSimilarity_Returns_1_For_Identical_Tokens()
    {
        var result = AutoReplaceMatcher.JaccardSimilarity(
            ["foo", "bar"],
            ["foo", "bar"]);
        Assert.Equal(1.0, result);
    }

    [Fact]
    public void JaccardSimilarity_Returns_0_For_Disjoint_Tokens()
    {
        var result = AutoReplaceMatcher.JaccardSimilarity(
            ["foo", "bar"],
            ["baz", "qux"]);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void JaccardSimilarity_Returns_Expected_For_Partial_Overlap()
    {
        var result = AutoReplaceMatcher.JaccardSimilarity(
            ["Known", "for", "It", "Freak", "Grips"],
            ["Known", "for", "It"]);
        Assert.Equal(0.6, result, 2);
    }

    // --- Metadata Scoring Tests ---

    [Fact]
    public void ComputeMetadataScore_Returns_Zero_When_No_Metadata()
    {
        var result = AutoReplaceMatcher.ComputeMetadataScore(null, null, null, null, null, null, null, null);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void ComputeMetadataScore_Bonus_For_Length_Match()
    {
        var result = AutoReplaceMatcher.ComputeMetadataScore(245, null, null, null, 245, null, null, null);
        Assert.Equal(0.3, result);
    }

    [Fact]
    public void ComputeMetadataScore_Bonus_For_Length_Within_Tolerance()
    {
        var result = AutoReplaceMatcher.ComputeMetadataScore(245, null, null, null, 246, null, null, null);
        Assert.Equal(0.3, result);
    }

    [Fact]
    public void ComputeMetadataScore_No_Bonus_For_Length_Outside_Tolerance()
    {
        var result = AutoReplaceMatcher.ComputeMetadataScore(245, null, null, null, 250, null, null, null);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void ComputeMetadataScore_Bonus_For_All_Signals()
    {
        var result = AutoReplaceMatcher.ComputeMetadataScore(
            originalLength: 245, originalBitRate: 320, originalBitDepth: 16, originalSampleRate: 44100,
            candidateLength: 245, candidateBitRate: 320, candidateBitDepth: 16, candidateSampleRate: 44100);
        Assert.Equal(0.6, result);
    }

    [Fact]
    public void Metadata_Scoring_Boosts_Better_Match()
    {
        // both candidates have the same basename match
        var responses = new[]
        {
            Response("no-meta", [File("a\\Song.mp3", 100)]),
            Response("with-meta", [File("b\\Song.mp3", 100, length: 245, bitRate: 320, bitDepth: 16, sampleRate: 44100)]),
        };

        // original has metadata, so with-meta should be preferred
        var result = AutoReplaceMatcher.SelectBest("user\\Song.mp3", 100,
            originalBitRate: 320, originalBitDepth: 16, originalLength: 245, originalSampleRate: 44100,
            responses, null, new MatchOptions { MinTokenSimilarity = 1.0 });

        Assert.NotNull(result);
        Assert.Equal("with-meta", result.Username);
    }
}
