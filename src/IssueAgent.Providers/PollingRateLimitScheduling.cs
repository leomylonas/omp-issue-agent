namespace IssueAgent.Providers;

/// <summary>Allows discovery callers to defer a provider-directed rate-limit retry rather than
/// occupying bounded polling capacity while the provider's retry window elapses.</summary>
public static class PollingRateLimitScheduling
{
    private static readonly AsyncLocal<int> Enabled = new();

    public static IDisposable Enter()
    {
        Enabled.Value++;
        return new Scope();
    }

    public static void ThrowIfEnabled(TimeSpan retryAfter)
    {
        if (Enabled.Value > 0)
        {
            throw new PollingRateLimitedException(retryAfter <= TimeSpan.Zero ? TimeSpan.Zero : retryAfter);
        }
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => Enabled.Value--;
    }
}

/// <summary>Signals that polling should resume after a provider-directed retry window.</summary>
public sealed class PollingRateLimitedException(TimeSpan retryAfter) : Exception
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}
