using System.Text.Json.Nodes;

namespace IssueAgent.Omp.Tests;

public sealed class OmpProcessClientTests
{
    private static readonly string ScriptPath = Path.Combine(AppContext.BaseDirectory, "fake_omp_server.py");

    [Fact]
    public async Task CreateSessionAsyncReturnsIdAndPersistedSessionFileFromServer()
    {
        await using var client = StartClient();

        var session = await client.CreateSessionAsync("plan", CancellationToken.None);

        Assert.Equal("fake-session-1", session.SessionId);
        Assert.Equal("/tmp/fake-session-1.jsonl", session.SessionFile);
        Assert.Equal("plan", session.Role);
    }

    [Fact]
    public async Task ResumeSessionAsyncSwitchesToPersistedSession()
    {
        await using var client = StartClient();

        var session = await client.ResumeSessionAsync("existing-session", "/persisted/session.jsonl", CancellationToken.None);

        Assert.Equal("existing-session", session.SessionId);
        Assert.Equal("/persisted/session.jsonl", session.SessionFile);
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
    public async Task RunAsyncTimeoutRequestsAbortAndReturnsCancellationError()
    {
        await using var client = StartClient();
        var session = await client.CreateSessionAsync("plan", CancellationToken.None);

        var events = await CollectAsync(client.RunAsync(
            new OmpRunRequest(session.SessionId, "/tmp", "hang", new Dictionary<string, string>(), TimeSpan.FromMilliseconds(50)),
            CancellationToken.None));

        var error = Assert.IsType<OmpErrorEvent>(Assert.Single(events));
        Assert.True(error.WasCancelled);
        Assert.Contains("timed out", error.Message, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task CancellationBeforePromptDispatchPreventsPrompt()
    {
        await using var client = StartClient();
        var session = await client.CreateSessionAsync("plan", CancellationToken.None);
        await client.CancelAsync(session.SessionId, CancellationToken.None);

        var events = await CollectAsync(client.RunAsync(
            new OmpRunRequest(session.SessionId, "/tmp", "plan this issue", new Dictionary<string, string>()),
            CancellationToken.None));

        var error = Assert.IsType<OmpErrorEvent>(Assert.Single(events));
        Assert.True(error.WasCancelled);
        Assert.Contains("before prompt", error.Message, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task RealPinnedBinarySupportsTypedStartupAndSessionCommandsWhenConfigured()
    {
        var executable = Environment.GetEnvironmentVariable("OMP_TEST_BINARY");
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            Assert.Skip("Set OMP_TEST_BINARY to run the pinned-binary protocol smoke test.");
        }

        var sessionDirectory = Path.Combine(Path.GetTempPath(), "issue-agent-omp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionDirectory);
        await using var transport = NdjsonRpcTransport.Start(
            executable,
            ["--mode", "rpc", "--session-dir", sessionDirectory, "--no-tools"],
            AppContext.BaseDirectory,
            new Dictionary<string, string>
            {
                ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
                ["HOME"] = Environment.GetEnvironmentVariable("HOME") ?? sessionDirectory,
            });

        var created = await transport.SendCommandAsync("new_session", null, CancellationToken.None);
        Assert.True(created["success"]?.GetValue<bool>());
        var stateResponse = await transport.SendCommandAsync("get_state", null, CancellationToken.None);
        Assert.True(stateResponse["data"]?["sessionId"]?.GetValue<string>() is { Length: > 0 });
        var sessionFile = stateResponse["data"]?["sessionFile"]?.GetValue<string>()
            ?? stateResponse["data"]?["sessionPath"]?.GetValue<string>();
        Assert.True(sessionFile is { Length: > 0 });
        var resumed = await transport.SendCommandAsync(
            "switch_session",
            new JsonObject { ["sessionPath"] = sessionFile },
            CancellationToken.None);
        Assert.True(resumed["success"]?.GetValue<bool>());
        var aborted = await transport.SendCommandAsync("abort", null, CancellationToken.None);
        Assert.True(aborted["success"]?.GetValue<bool>());
    }
}
