namespace IssueAgent.Notifications;

/// <summary>One global configurable retry policy (specification §27): a bounded number of
/// attempts with exponential backoff and jitter.</summary>
public sealed record RetryPolicy
{
    public int MaxAttempts { get; init; } = 3;

    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    public double BackoffMultiplier { get; init; } = 2.0;

    public static RetryPolicy Default { get; } = new();

    /// <summary>Runs <paramref name="action"/> with retry. Returns <see langword="null"/> on
    /// success, or the last observed exception if every attempt failed.</summary>
    public async Task<Exception?> ExecuteAsync(Func<CancellationToken, Task> action, Random random, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(random);

        var delay = InitialDelay;
        Exception? lastException = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await action(cancellationToken).ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (attempt >= MaxAttempts)
                {
                    break;
                }

                var jitter = TimeSpan.FromMilliseconds(random.Next(0, 250));
                await Task.Delay(delay + jitter, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * BackoffMultiplier);
            }
        }

        return lastException;
    }
}
