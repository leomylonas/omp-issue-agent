
namespace IssueAgent.Omp.Tests;

public sealed class OmpProcessClientTests
{
    private static readonly string ScriptPath = Path.Combine(AppContext.BaseDirectory, "fake_omp_server.py");

    [Fact]
    public async Task CreateSessionAsyncReturnsIdAndPersistedSessionFileFromServer()
    {
        await using var client = StartClient();

        var session = await client.CreateSessionAsync("openrouter/anthropic/claude-sonnet-5", CancellationToken.None);

        Assert.Equal("fake-session-1", session.SessionId);
        Assert.Equal("/tmp/fake-session-1.jsonl", session.SessionFile);
        Assert.Equal("openrouter/anthropic/claude-sonnet-5", session.Role);
    }

    [Fact]
    public async Task ResumeSessionAsyncSwitchesToPersistedSession()
    {
        await using var client = StartClient();

        var sessionFile = Path.Combine(Path.GetTempPath(), "persisted", "session.jsonl");
        var session = await client.ResumeSessionAsync("existing-session", sessionFile, CancellationToken.None);

        Assert.Equal("existing-session", session.SessionId);
        Assert.Equal(sessionFile, session.SessionFile);
    }

    [Fact]
    public async Task ResumeSessionAsyncRejectsAnUnexpectedActiveSessionIdentity()
    {
        await using var client = StartClient();

        var exception = await Assert.ThrowsAsync<OmpRpcException>(
            () => client.ResumeSessionAsync(
                "existing-session",
                Path.Combine(Path.GetTempPath(), "mismatch.jsonl"),
                CancellationToken.None).AsTask());

        Assert.Contains("durable state requires", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResumeSessionAsyncRejectsSessionFilesOutsideConfiguredDirectory()
    {
        await using var client = StartClient();

        var exception = await Assert.ThrowsAsync<OmpRpcException>(
            () => client.ResumeSessionAsync("existing-session", "/untrusted/session.jsonl", CancellationToken.None).AsTask());

        Assert.Contains("outside the configured session directory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelSelectorAllowsSlashesInTheModelId()
    {
        Assert.True(OmpModel.TryParse("openrouter/anthropic/claude-sonnet-5", out var model));
        Assert.Equal("openrouter", model.Provider);
        Assert.Equal("anthropic/claude-sonnet-5", model.ModelId);
    }

    [Fact]
    public async Task RunAsyncStreamsEventsInOrderAndEndsWithCompleted()
    {
        await using var client = StartClient();
        var session = await client.CreateSessionAsync("anthropic/claude-sonnet-5", CancellationToken.None);

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
        var session = await client.CreateSessionAsync("anthropic/claude-sonnet-5", CancellationToken.None);

        var events = await CollectAsync(client.RunAsync(
            new OmpRunRequest(session.SessionId, "/tmp", "plan this issue", new Dictionary<string, string>()),
            CancellationToken.None));

        var toolCall = events.OfType<OmpToolCallEvent>().Single();
        var toolResult = events.OfType<OmpToolResultEvent>().Single();
        Assert.Equal(toolCall.ToolCallId, toolResult.ToolCallId);
        Assert.False(toolResult.IsError);
    }

    [Fact]
    public async Task RunAsyncTimeoutRequestsAbortAndReturnsFailure()
    {
        await using var client = StartClient();
        var session = await client.CreateSessionAsync("anthropic/claude-sonnet-5", CancellationToken.None);

        var events = await CollectAsync(client.RunAsync(
            new OmpRunRequest(session.SessionId, "/tmp", "hang", new Dictionary<string, string>(), TimeSpan.FromMilliseconds(50)),
            CancellationToken.None));

        var error = Assert.IsType<OmpErrorEvent>(Assert.Single(events));
        Assert.False(error.WasCancelled);
        Assert.Contains("timed out", error.Message, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task CancellationBeforePromptDispatchPreventsPrompt()
    {
        await using var client = StartClient();
        var session = await client.CreateSessionAsync("anthropic/claude-sonnet-5", CancellationToken.None);
        await client.CancelAsync(session.SessionId, CancellationToken.None);

        var events = await CollectAsync(client.RunAsync(
            new OmpRunRequest(session.SessionId, "/tmp", "plan this issue", new Dictionary<string, string>()),
            CancellationToken.None));

        var error = Assert.IsType<OmpErrorEvent>(Assert.Single(events));
        Assert.True(error.WasCancelled);
        Assert.Contains("before prompt", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelAsyncHonorsCancellationWhilePromptDispatchOwnsGate()
    {
        await using var client = StartClient();
        var session = await client.CreateSessionAsync("plan", CancellationToken.None);
        using var runCts = new CancellationTokenSource();
        var run = CollectAsync(client.RunAsync(
            new OmpRunRequest(session.SessionId, "/tmp", "prompt dispatch hang", new Dictionary<string, string>()),
            runCts.Token));
        await Task.Delay(20, TestContext.Current.CancellationToken);
        using var cancelCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var cancel = client.CancelAsync(session.SessionId, cancelCts.Token).AsTask();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => cancel.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        }
        finally
        {
            runCts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task CancelAsyncBoundsGateAcquisitionByAbortGracePeriod()
    {
        await using var client = StartClient(TimeSpan.FromMilliseconds(50));
        var session = await client.CreateSessionAsync("plan", CancellationToken.None);
        using var runCts = new CancellationTokenSource();
        var run = CollectAsync(client.RunAsync(
            new OmpRunRequest(session.SessionId, "/tmp", "prompt dispatch hang", new Dictionary<string, string>()),
            runCts.Token));
        await Task.Delay(20, TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.CancelAsync(session.SessionId, CancellationToken.None).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));

        runCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancelAsyncSendsRequestAndReceivesAcknowledgement()
    {
        await using var client = StartClient();
        var session = await client.CreateSessionAsync("anthropic/claude-sonnet-5", CancellationToken.None);

        await client.CancelAsync(session.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task CancelAsyncThrowsWhenAbortIsNotAcknowledgedBeforeItsDeadline()
    {
        await using var client = StartClient(TimeSpan.FromMilliseconds(50));
        var session = await client.CreateSessionAsync("plan", CancellationToken.None);
        var run = CollectAsync(client.RunAsync(
            new OmpRunRequest(session.SessionId, "/tmp", "abort timeout hang", new Dictionary<string, string>()),
            CancellationToken.None));
        await Task.Delay(20, TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.CancelAsync(session.SessionId, CancellationToken.None).AsTask());

        await Assert.ThrowsAnyAsync<Exception>(
            () => run.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
    }

    private static OmpProcessClient StartClient(TimeSpan? abortGracePeriod = null)
    {
        var transport = NdjsonRpcTransport.Start(
            "python3",
            [ScriptPath],
            AppContext.BaseDirectory,
            new Dictionary<string, string> { ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? string.Empty });
        return new OmpProcessClient(transport, TimeSpan.FromSeconds(5), Path.GetTempPath(), abortGracePeriod);
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
        var allowedEnvironment = new Dictionary<string, string>
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
            ["HOME"] = Environment.GetEnvironmentVariable("HOME") ?? sessionDirectory,
            ["OPENAI_API_KEY"] = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty,
        };
        var transport = NdjsonRpcTransport.Start(
            executable,
            ["--mode", "rpc", "--session-dir", sessionDirectory, "--no-tools"],
            AppContext.BaseDirectory,
            allowedEnvironment);
        await using var client = new OmpProcessClient(transport, TimeSpan.FromSeconds(5), sessionDirectory);

        var created = await client.CreateSessionAsync("openai/gpt-4.1", CancellationToken.None);
        Assert.NotEmpty(created.SessionId);
        Assert.NotNull(created.SessionFile);
        var resumed = await client.ResumeSessionAsync(
            created.SessionId,
            created.SessionFile!,
            CancellationToken.None);
        Assert.Equal(created.SessionId, resumed.SessionId);
        await client.CancelAsync(resumed.SessionId, CancellationToken.None);
    }
}
