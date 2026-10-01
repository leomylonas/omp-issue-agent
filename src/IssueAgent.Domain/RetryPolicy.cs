namespace IssueAgent.Domain;

/// <summary>Resolved bounded retry behavior shared by provider, Git, notification, and OMP boundaries.</summary>
public sealed record RetryPolicy
{
    public int MaxAttempts { get; init; } = 3;

    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    public double BackoffMultiplier { get; init; } = 2.0;

    public TimeSpan MaxJitter { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>Conservative base delay when a provider rejects a request for rate limiting without a usable retry window.</summary>
    public TimeSpan RateLimitFallbackDelay { get; init; } = TimeSpan.FromMinutes(1);


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

    /// <summary>Returns the bounded exponential fallback for a rate-limit rejection that supplied no usable retry instruction.</summary>
    public TimeSpan GetRateLimitFallbackDelay(int failedAttempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempt, 1);
        var milliseconds = RateLimitFallbackDelay.TotalMilliseconds * Math.Pow(BackoffMultiplier, failedAttempt - 1);
        var exponential = TimeSpan.FromMilliseconds(Math.Min(milliseconds, TimeSpan.FromMinutes(5).TotalMilliseconds));
        var jitterMilliseconds = MaxJitter.TotalMilliseconds <= 0
            ? 0
            : Random.Shared.NextDouble() * MaxJitter.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(Math.Min(
            exponential.TotalMilliseconds + jitterMilliseconds,
            TimeSpan.FromMinutes(5).TotalMilliseconds));

    }

    /// <summary>Runs an operation until it succeeds or exhausts this policy's attempts.</summary>
    public async Task<Exception?> ExecuteAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken,
        Func<Exception, bool>? shouldRetry = null)
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
                if (attempt < MaxAttempts && (shouldRetry?.Invoke(exception) ?? true))
                {
                    RetryTelemetry.RecordAttempt();
                    await Task.Delay(GetDelay(attempt), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    RetryTelemetry.RecordExhausted();
                    return exception;
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
                    RetryTelemetry.RecordAttempt();
                    await Task.Delay(GetDelay(attempt), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    RetryTelemetry.RecordExhausted();
                }
            }
        }

        throw lastException!;
    }
}
