namespace slskd.Tests.Unit.Transfers.Downloads.AutoReplace;

using System;
using System.Collections.Generic;
using System.Linq;
using Soulseek;
using slskd.Transfers.Downloads;
using Xunit;

using Transfer = slskd.Transfers.Transfer;

public class StallDetectorTests
{
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan QueueStallTimeout = TimeSpan.FromSeconds(120);

    private static Transfer InProgress(Guid id, long bytesTransferred)
        => new Transfer { Id = id, Direction = TransferDirection.Download, State = TransferStates.InProgress, BytesTransferred = bytesTransferred };

    private static Transfer RemotelyQueued(Guid id, int placeInQueue)
        => new Transfer { Id = id, Direction = TransferDirection.Download, State = TransferStates.Queued | TransferStates.Remotely, PlaceInQueue = placeInQueue };

    [Fact]
    public void First_Observation_Is_Not_Flagged()
    {
        var detector = new StallDetector();
        var id = Guid.NewGuid();
        var t0 = DateTime.UtcNow;

        var stalled = detector.Evaluate([InProgress(id, 0)], t0, StallTimeout, QueueStallTimeout);

        Assert.Empty(stalled);
    }

    [Fact]
    public void In_Progress_Without_Byte_Movement_Is_Flagged_After_Timeout()
    {
        var detector = new StallDetector();
        var id = Guid.NewGuid();
        var t0 = DateTime.UtcNow;

        detector.Evaluate([InProgress(id, 0)], t0, StallTimeout, QueueStallTimeout);

        var beforeTimeout = detector.Evaluate([InProgress(id, 0)], t0.AddSeconds(30), StallTimeout, QueueStallTimeout);
        Assert.Empty(beforeTimeout);

        var afterTimeout = detector.Evaluate([InProgress(id, 0)], t0.AddSeconds(61), StallTimeout, QueueStallTimeout);
        Assert.Contains(id, afterTimeout);
    }

    [Fact]
    public void In_Progress_With_Byte_Movement_Is_Not_Flagged()
    {
        var detector = new StallDetector();
        var id = Guid.NewGuid();
        var t0 = DateTime.UtcNow;

        detector.Evaluate([InProgress(id, 0)], t0, StallTimeout, QueueStallTimeout);
        detector.Evaluate([InProgress(id, 1_000)], t0.AddSeconds(30), StallTimeout, QueueStallTimeout);

        // progress was observed at t+30, so at t+61 only 31s have elapsed since the last change
        var result = detector.Evaluate([InProgress(id, 1_000)], t0.AddSeconds(61), StallTimeout, QueueStallTimeout);

        Assert.Empty(result);
    }

    [Fact]
    public void Remotely_Queued_Without_Advancement_Is_Flagged_After_Queue_Timeout()
    {
        var detector = new StallDetector();
        var id = Guid.NewGuid();
        var t0 = DateTime.UtcNow;

        detector.Evaluate([RemotelyQueued(id, 10)], t0, StallTimeout, QueueStallTimeout);

        // the in-progress timeout (60s) must not apply to a queued transfer
        var beforeQueueTimeout = detector.Evaluate([RemotelyQueued(id, 10)], t0.AddSeconds(90), StallTimeout, QueueStallTimeout);
        Assert.Empty(beforeQueueTimeout);

        var afterQueueTimeout = detector.Evaluate([RemotelyQueued(id, 10)], t0.AddSeconds(121), StallTimeout, QueueStallTimeout);
        Assert.Contains(id, afterQueueTimeout);
    }

    [Fact]
    public void Remotely_Queued_That_Advances_Is_Not_Flagged()
    {
        var detector = new StallDetector();
        var id = Guid.NewGuid();
        var t0 = DateTime.UtcNow;

        detector.Evaluate([RemotelyQueued(id, 10)], t0, StallTimeout, QueueStallTimeout);
        detector.Evaluate([RemotelyQueued(id, 5)], t0.AddSeconds(90), StallTimeout, QueueStallTimeout);

        var result = detector.Evaluate([RemotelyQueued(id, 5)], t0.AddSeconds(121), StallTimeout, QueueStallTimeout);

        Assert.Empty(result);
    }

    [Fact]
    public void Inactive_Transfers_Are_Ignored()
    {
        var detector = new StallDetector();
        var id = Guid.NewGuid();
        var t0 = DateTime.UtcNow;
        var completed = new Transfer { Id = id, Direction = TransferDirection.Download, State = TransferStates.Completed | TransferStates.Succeeded };

        detector.Evaluate([completed], t0, StallTimeout, QueueStallTimeout);
        var result = detector.Evaluate([completed], t0.AddSeconds(300), StallTimeout, QueueStallTimeout);

        Assert.Empty(result);
    }

    [Fact]
    public void Forget_Resets_Tracking()
    {
        var detector = new StallDetector();
        var id = Guid.NewGuid();
        var t0 = DateTime.UtcNow;

        detector.Evaluate([InProgress(id, 0)], t0, StallTimeout, QueueStallTimeout);
        detector.Forget(id);

        // after forgetting, the next observation re-establishes a baseline and must not be flagged
        var result = detector.Evaluate([InProgress(id, 0)], t0.AddSeconds(61), StallTimeout, QueueStallTimeout);

        Assert.Empty(result);
    }

    [Fact]
    public void Stops_Tracking_Transfers_No_Longer_Active()
    {
        var detector = new StallDetector();
        var id = Guid.NewGuid();
        var t0 = DateTime.UtcNow;

        detector.Evaluate([InProgress(id, 0)], t0, StallTimeout, QueueStallTimeout);

        // transfer disappears from the active set (e.g. completed/removed)
        detector.Evaluate([], t0.AddSeconds(30), StallTimeout, QueueStallTimeout);

        // reappears; treated as a fresh baseline rather than immediately stalled
        var result = detector.Evaluate([InProgress(id, 0)], t0.AddSeconds(61), StallTimeout, QueueStallTimeout);

        Assert.Empty(result);
    }
}
