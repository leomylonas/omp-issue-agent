using IssueAgent.Omp;

namespace IssueAgent.Omp.Tests;

public sealed class OmpProcessClientTests
{
    private static readonly string ScriptPath = Path.Combine(AppContext.BaseDirectory, "fake_omp_server.py");

    [Fact]
    public async Task CreateSessionAsyncReturnsSessionIdFromServer()
    {
        await using var client = StartClient();

        var session = await client.CreateSessionAsync("plan", CancellationToken.None);

        Assert.Equal("fake-session-1", session.SessionId);
        Assert.Equal("plan", session.Role);
    }

    [Fact]
    public async Task ResumeSessionAsyncReturnsRoleFromServer()
    {
        await using var client = StartClient();

        var session = await client.ResumeSessionAsync("existing-session", CancellationToken.None);

        Assert.Equal("existing-session", session.SessionId);
        Assert.Equal("task", session.Role);
    }

    [Fact]
    public async Task RunAsyncStreamsEventsInOrderAndEndsWithCompleted()
    {
        await using var client = StartClient();
        var session = await client.CreateSessionAsync("plan", CancellationToken.None);

        var events = await CollectAsync(client.RunAsync(
            new OmpRunRequest(session.SessionId, "/tmp", "plan this issue", new Dictionary<string, string>()),
            CancellationToken.None));

        Assert.Collection(
            events,
            e => Assert.IsType<OmpMessageEvent>(e),
            e => Assert.IsType<OmpToolCallEvent>(e),
            e => Assert.IsType<OmpToolResultEvent>(e),
            e => Assert.IsType<OmpCompletedEvent>(e));

        var completed = Assert.IsType<OmpCompletedEvent>(events[^1]);
        Assert.Contains("\"summary\":\"done\"", completed.ResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsyncToolCallAndResultShareCorrelationId()
    {
        await using var client = StartClient();
        var session = await client.CreateSessionAsync("plan", CancellationToken.None);

        var events = await CollectAsync(client.RunAsync(
            new OmpRunRequest(session.SessionId, "/tmp", "plan this issue", new Dictionary<string, string>()),
            CancellationToken.None));

        var toolCall = events.OfType<OmpToolCallEvent>().Single();
        var toolResult = events.OfType<OmpToolResultEvent>().Single();
        Assert.Equal(toolCall.ToolCallId, toolResult.ToolCallId);
        Assert.False(toolResult.IsError);
    }

    [Fact]
    public async Task CancelAsyncSendsRequestAndReceivesAcknowledgement()
    {
        await using var client = StartClient();
        var session = await client.CreateSessionAsync("plan", CancellationToken.None);

        await client.CancelAsync(session.SessionId, CancellationToken.None);
    }

    private static OmpProcessClient StartClient()
    {
        var transport = NdjsonRpcTransport.Start(
            "python3",
            [ScriptPath],
            AppContext.BaseDirectory,
            new Dictionary<string, string> { ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? string.Empty });
        return new OmpProcessClient(transport, TimeSpan.FromSeconds(5));
    }

    private static async Task<List<OmpEvent>> CollectAsync(IAsyncEnumerable<OmpEvent> source)
    {
        var results = new List<OmpEvent>();
        await foreach (var item in source)
        {
            results.Add(item);
        }

        return results;
    }
}
