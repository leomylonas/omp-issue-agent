namespace IssueAgent.Domain;

/// <summary>Resolved bounded retry behavior shared by provider, notification, and OMP boundaries.</summary>
public sealed record RetryPolicy
{
    public int MaxAttempts { get; init; } = 3;

    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    public double BackoffMultiplier { get; init; } = 2.0;

    public TimeSpan MaxJitter { get; init; } = TimeSpan.FromMilliseconds(250);

    public static RetryPolicy Default { get; } = new();

    /// <summary>Returns the exponential delay and uniformly distributed bounded jitter after a failed attempt.</summary>
    public TimeSpan GetDelay(int failedAttempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempt, 1);
        var milliseconds = InitialDelay.TotalMilliseconds * Math.Pow(BackoffMultiplier, failedAttempt - 1);
        var exponential = TimeSpan.FromMilliseconds(Math.Min(milliseconds, TimeSpan.MaxValue.TotalMilliseconds));
        var jitterMilliseconds = MaxJitter.TotalMilliseconds <= 0
            ? 0
            : Random.Shared.NextDouble() * MaxJitter.TotalMilliseconds;
        return exponential + TimeSpan.FromMilliseconds(jitterMilliseconds);
    }

    /// <summary>Runs an operation until it succeeds or exhausts this policy's attempts.</summary>
    public async Task<Exception?> ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        Exception? lastException = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await action(cancellationToken).ConfigureAwait(false);
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastException = exception;
                if (attempt < MaxAttempts)
                {
                    await Task.Delay(GetDelay(attempt), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        return lastException;
    }

    /// <summary>Runs a value-producing operation until it succeeds or exhausts this policy's attempts.</summary>
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        Exception? lastException = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                return await action(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastException = exception;
                if (attempt < MaxAttempts)
                {
                    await Task.Delay(GetDelay(attempt), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw lastException!;
    }
}
