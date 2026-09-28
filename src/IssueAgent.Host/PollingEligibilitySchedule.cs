namespace IssueAgent.Host;

/// <summary>Keeps repositories out of discovery until a provider-directed rate-limit window ends.
/// This moves a long wait out of the bounded polling work rather than holding a polling slot.</summary>
internal sealed class PollingEligibilitySchedule(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan MaximumProviderRetryWindow = TimeSpan.FromMinutes(1);
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, DateTimeOffset> nextEligible = new(StringComparer.Ordinal);
    private readonly Lock gate = new();

    public bool IsEligible(string repositoryKey)
    {
        lock (gate)
        {
            return !nextEligible.TryGetValue(repositoryKey, out var eligibleAt) || eligibleAt <= timeProvider.GetUtcNow();
        }
    }

    public void Defer(string repositoryKey, TimeSpan retryAfter)
    {
        var retryWindow = retryAfter <= TimeSpan.Zero
            ? TimeSpan.Zero
            : retryAfter > MaximumProviderRetryWindow ? MaximumProviderRetryWindow : retryAfter;
        var eligibleAt = timeProvider.GetUtcNow() + retryWindow;
        lock (gate)
        {
            if (!nextEligible.TryGetValue(repositoryKey, out var current) || eligibleAt > current)
            {
                nextEligible[repositoryKey] = eligibleAt;
            }
        }
    }
}
