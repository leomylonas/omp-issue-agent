using IssueAgent.Omp;

namespace IssueAgent.Omp.Tests;

public sealed class FakeOmpClientTests
{
    [Fact]
    public async Task CreateSessionAsyncReturnsEnqueuedIdAndRecordsRole()
    {
        var client = new FakeOmpClient().EnqueueSessionId("session-42");

        var session = await client.CreateSessionAsync("plan", CancellationToken.None);

        Assert.Equal("session-42", session.SessionId);
        Assert.Equal("/data/omp/session-42.jsonl", session.SessionFile);
        Assert.Equal(["plan"], client.CreatedRoles);
    }

    [Fact]
    public async Task CreateSessionAsyncGeneratesIdWhenNoneQueued()
    {
        var client = new FakeOmpClient();

        var session = await client.CreateSessionAsync("plan", CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(session.SessionId));
    }

    [Fact]
    public async Task RunAsyncYieldsScriptedEventsAndRecordsRequest()
    {
        var completed = new OmpCompletedEvent("session-1", DateTimeOffset.UtcNow, """{"summary":"done"}""");
        var client = new FakeOmpClient().EnqueueRun(
            new OmpMessageEvent("session-1", DateTimeOffset.UtcNow, "working"),
            completed);
        var request = new OmpRunRequest("session-1", "/tmp", "do the plan", new Dictionary<string, string>());

        var events = new List<OmpEvent>();
        await foreach (var e in client.RunAsync(request, CancellationToken.None))
        {
            events.Add(e);
        }

        Assert.Equal(2, events.Count);
        Assert.Same(completed, events[^1]);
        Assert.Same(request, Assert.Single(client.RunRequests));
    }

    [Fact]
    public void EnqueueRunRejectsSequenceNotEndingInTerminalEvent()
    {
        var client = new FakeOmpClient();

        Assert.Throws<ArgumentException>(() =>
            client.EnqueueRun(new OmpMessageEvent("session-1", DateTimeOffset.UtcNow, "working")));
    }

    [Fact]
    public async Task RunAsyncThrowsWhenNoRunIsScripted()
    {
        var client = new FakeOmpClient();
        var request = new OmpRunRequest("session-1", "/tmp", "do the plan", new Dictionary<string, string>());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in client.RunAsync(request, CancellationToken.None))
            {
            }
        });
    }

    [Fact]
    public async Task CancelAsyncRecordsSessionId()
    {
        var client = new FakeOmpClient();

        await client.CancelAsync("session-1", CancellationToken.None);

        Assert.Equal(["session-1"], client.CancelledSessionIds);
    }

    [Fact]
    public async Task DisposeAsyncMarksClientDisposed()
    {
        var client = new FakeOmpClient();

        await client.DisposeAsync();

        Assert.True(client.Disposed);
    }
}
