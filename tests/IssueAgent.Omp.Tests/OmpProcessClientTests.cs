
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
    public async Task CreateSessionAsyncPreservesSemanticRoleWithoutModelSelectionRpc()
    {
        await using var client = StartClient();

        var session = await client.CreateSessionAsync("repository-planner", CancellationToken.None);

        Assert.Equal("repository-planner", session.Role);
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
    public async Task FactoryAppliesResolvedRoleModelAfterSwitchingDurableSession()
    {
        var argumentLog = Path.Combine(Path.GetTempPath(), $"issue-agent-omp-args-{Guid.NewGuid():N}.log");
        var commandLog = Path.Combine(Path.GetTempPath(), $"issue-agent-omp-commands-{Guid.NewGuid():N}.log");
        try
        {
            var environment = new Dictionary<string, string>
            {
                ["OMP_ARGUMENT_LOG"] = argumentLog,
                ["OMP_COMMAND_LOG"] = commandLog,
            };
            await using var client = OmpProcessClientFactory.Start(
                "python3",
                [ScriptPath, "--mode", "rpc", "--session-dir", Path.GetTempPath()],
                AppContext.BaseDirectory,
                environment);

            var session = await client.CreateSessionAsync("plan", CancellationToken.None);
            await client.SelectRoleAsync("task", CancellationToken.None);

            var startups = await File.ReadAllLinesAsync(argumentLog, TestContext.Current.CancellationToken);
            var commands = await File.ReadAllLinesAsync(commandLog, TestContext.Current.CancellationToken);
            Assert.Contains(startups, startup => startup.Contains("--model task", StringComparison.Ordinal));
            Assert.Contains(commands, command =>
                command.StartsWith("set_model ", StringComparison.Ordinal) &&
                command.Contains("\"provider\": \"configured\"", StringComparison.Ordinal) &&
                command.Contains("\"modelId\": \"task\"", StringComparison.Ordinal));
            Assert.Equal("fake-session-1", session.SessionId);
        }
        finally
        {
            File.Delete(argumentLog);
            File.Delete(commandLog);
        }
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
    public async Task RunAsyncConvertsMalformedFramesIntoTerminalFailure()
    {
        await using var client = StartClient();
        var session = await client.CreateSessionAsync("anthropic/claude-sonnet-5", CancellationToken.None);

        var events = await CollectAsync(client.RunAsync(
            new OmpRunRequest(session.SessionId, "/tmp", "malformed frame", new Dictionary<string, string>()),
            CancellationToken.None));

        var error = Assert.IsType<OmpErrorEvent>(Assert.Single(events));
        Assert.Contains("malformed NDJSON", error.Message, StringComparison.Ordinal);
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
    public async Task ConfiguredTimeoutBoundsSessionCreationAndStateLookup()
    {
        await using var client = StartClient(
            configuredTimeout: TimeSpan.FromMilliseconds(50),
            hangCommands: "new_session");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.CreateSessionAsync("plan", CancellationToken.None).AsTask());

        await using var stateClient = StartClient(
            configuredTimeout: TimeSpan.FromMilliseconds(50),
            hangCommands: "get_state");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stateClient.CreateSessionAsync("plan", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ConfiguredTimeoutBoundsSessionResumeControl()
    {
        var sessionFile = Path.Combine(Path.GetTempPath(), "persisted", "session.jsonl");
        await using var client = StartClient(
            configuredTimeout: TimeSpan.FromMilliseconds(50),
            hangCommands: "switch_session");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ResumeSessionAsync("existing-session", sessionFile, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task CancelAsyncUsesAbortGraceInsteadOfLongConfiguredTimeout()
    {
        await using var client = StartClient(
            abortGracePeriod: TimeSpan.FromMilliseconds(50),
            configuredTimeout: TimeSpan.FromSeconds(1),
            hangCommands: "abort");
        var session = await client.CreateSessionAsync("plan", CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.CancelAsync(session.SessionId, CancellationToken.None).AsTask()
                .WaitAsync(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancelAsyncHonorsCallerCancellationBeforeAbortGrace()
    {
        await using var client = StartClient(
            abortGracePeriod: TimeSpan.FromSeconds(1),
            configuredTimeout: TimeSpan.FromSeconds(2),
            hangCommands: "abort");
        var session = await client.CreateSessionAsync("plan", CancellationToken.None);
        using var callerCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.CancelAsync(session.SessionId, callerCancellation.Token).AsTask()
                .WaitAsync(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConfiguredTimeoutBoundsStartupAndRoleModelSelection()
    {
        var environment = new Dictionary<string, string>
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
            ["OMP_READY_DELAY_MS"] = "200",
        };
        await using var startupClient = OmpProcessClientFactory.Start(
            "python3",
            [ScriptPath, "--mode", "rpc", "--session-dir", Path.GetTempPath()],
            AppContext.BaseDirectory,
            environment,
            timeout: TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => startupClient.CreateSessionAsync("plan", CancellationToken.None).AsTask());

        environment["OMP_READY_DELAY_MS"] = "0";
        environment["OMP_HANG_COMMANDS"] = "set_model";
        await using var roleClient = OmpProcessClientFactory.Start(
            "python3",
            [ScriptPath, "--mode", "rpc", "--session-dir", Path.GetTempPath()],
            AppContext.BaseDirectory,
            environment,
            timeout: TimeSpan.FromMilliseconds(50));
        await roleClient.CreateSessionAsync("plan", CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => roleClient.SelectRoleAsync("task", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task StandardErrorIsDrainedWithoutExposingItsContentsInProcessDiagnostics()
    {
        var transport = NdjsonRpcTransport.Start(
            "python3",
            [ScriptPath],
            AppContext.BaseDirectory,
            new Dictionary<string, string>
            {
                ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
                ["OMP_STDERR_BYTES"] = "1048576",
                ["OMP_EXIT_AFTER_STDERR"] = "1",
            });
        await using var client = new OmpProcessClient(
            transport,
            TimeSpan.FromSeconds(5),
            Path.GetTempPath());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CreateSessionAsync("anthropic/claude-sonnet-5", CancellationToken.None)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        Assert.Contains("standard error", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stderr-secret-value", exception.Message, StringComparison.Ordinal);
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
    public async Task CancelAsyncInterruptsPromptAwaitingAcknowledgement()
    {
        await using var client = StartClient();
        var session = await client.CreateSessionAsync("anthropic/claude-sonnet-5", CancellationToken.None);
        using var runCts = new CancellationTokenSource();
        var run = CollectAsync(client.RunAsync(
            new OmpRunRequest(session.SessionId, "/tmp", "prompt dispatch hang", new Dictionary<string, string>()),
            runCts.Token));
        await Task.Delay(20, TestContext.Current.CancellationToken);

        await client.CancelAsync(session.SessionId, CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

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
        var session = await client.CreateSessionAsync("anthropic/claude-sonnet-5", CancellationToken.None);
        var run = CollectAsync(client.RunAsync(
            new OmpRunRequest(session.SessionId, "/tmp", "abort timeout hang", new Dictionary<string, string>()),
            CancellationToken.None));
        await Task.Delay(20, TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.CancelAsync(session.SessionId, CancellationToken.None).AsTask());

        await Assert.ThrowsAnyAsync<Exception>(
            () => run.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
    }

    private static OmpProcessClient StartClient(
        TimeSpan? abortGracePeriod = null,
        TimeSpan? configuredTimeout = null,
        string? hangCommands = null)
    {
        var environment = new Dictionary<string, string>
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
        };
        if (hangCommands is not null)
        {
            environment["OMP_HANG_COMMANDS"] = hangCommands;
        }

        var transport = NdjsonRpcTransport.Start(
            "python3",
            [ScriptPath],
            AppContext.BaseDirectory,
            environment);
        return new OmpProcessClient(
            transport,
            TimeSpan.FromSeconds(5),
            Path.GetTempPath(),
            abortGracePeriod,
            configuredTimeout: configuredTimeout);
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
