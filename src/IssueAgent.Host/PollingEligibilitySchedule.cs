namespace IssueAgent.Host;

/// <summary>Keeps repositories out of discovery until a provider-directed rate-limit window ends.
/// This moves a long wait out of the bounded polling work rather than holding a polling slot.</summary>
public sealed class PollingEligibilitySchedule(TimeProvider? timeProvider = null)
{
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

    public TimeSpan GetRetryAfter(string providerName)
    {
        lock (gate)
        {
            if (!nextEligible.TryGetValue(providerName, out var eligibleAt))
            {
                return TimeSpan.Zero;
            }

            var remaining = eligibleAt - timeProvider.GetUtcNow();
            return remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }
    }

    /// <summary>Acquires a polling slot only when the provider remains eligible at acquisition time.</summary>
    public async Task<bool> AcquirePollingSlotIfEligibleAsync(
        SemaphoreSlim pollingLimiter,
        string providerName,
        CancellationToken cancellationToken)
    {
        await pollingLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (IsEligible(providerName))
        {
            return true;
        }

        pollingLimiter.Release();
        return false;
    }

    public void Defer(string repositoryKey, TimeSpan retryAfter)
    {
        var now = timeProvider.GetUtcNow();
        var eligibleAt = retryAfter <= TimeSpan.Zero
            ? now
            : retryAfter >= DateTimeOffset.MaxValue - now
                ? DateTimeOffset.MaxValue
                : now + retryAfter;
        lock (gate)
        {
            if (!nextEligible.TryGetValue(repositoryKey, out var current) || eligibleAt > current)
            {
                nextEligible[repositoryKey] = eligibleAt;
            }
        }
    }
}
