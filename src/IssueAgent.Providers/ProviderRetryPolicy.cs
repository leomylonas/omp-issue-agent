using System.Globalization;
using System.Net;
using IssueAgent.Domain;


namespace IssueAgent.Providers;

public static class ProviderRetryPolicy
{
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    /// <param name="send">Issues one HTTP attempt.</param>
    /// <param name="cancellationToken">Cancels the whole retry loop, including any delay.</param>
    /// <param name="isIdempotent">When <see langword="false"/> (a non-idempotent write such as a
    /// resource-creating POST), an ambiguous <c>5xx</c> is never retried — the request may have
    /// already been applied server-side, and retrying could duplicate the effect (specification
    /// §17, §25, §27). A definitive rate-limit rejection (<c>429</c>, or <c>403</c> with exhausted
    /// quota or <c>Retry-After</c>) is always safe to retry regardless of verb: the provider rejected
    /// the request before processing it.</param>
    /// <param name="retryPolicy">Resolved global retry configuration. Unit callers may omit it to use defaults.</param>
    public static async Task<HttpResponseMessage> SendAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken,
        bool isIdempotent = true,
        RetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(send);
        retryPolicy ??= RetryPolicy.Default;

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await send(cancellationToken).ConfigureAwait(false);
                if (!IsRetryable(response, isIdempotent) || attempt == retryPolicy.MaxAttempts)
                {
                    return response;
                }

                var delay = GetRetryDelay(response, attempt, retryPolicy);
                response.Dispose();
                await DelayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (isIdempotent && attempt < retryPolicy.MaxAttempts)
            {
                await DelayAsync(retryPolicy.GetDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (isIdempotent && !cancellationToken.IsCancellationRequested && attempt < retryPolicy.MaxAttempts)
            {
                await DelayAsync(retryPolicy.GetDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }
    /// <summary>
    /// Sends an idempotent request and materializes its successful response within the same retry
    /// attempt. A connection that fails while streaming a response body therefore retries the whole
    /// download rather than treating headers as a completed request.
    /// </summary>
    public static async Task<T> SendAndMaterializeAsync<T>(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        Func<HttpResponseMessage, CancellationToken, Task<T>> materialize,
        CancellationToken cancellationToken,
        RetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(materialize);

        retryPolicy ??= RetryPolicy.Default;
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HttpResponseMessage response;
            try
            {
                response = await send(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < retryPolicy.MaxAttempts)
            {
                await DelayAsync(retryPolicy.GetDelay(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < retryPolicy.MaxAttempts)
            {
                await DelayAsync(retryPolicy.GetDelay(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                if (IsRetryable(response, isIdempotent: true) && attempt < retryPolicy.MaxAttempts)
                {
                    await DelayAsync(GetRetryDelay(response, attempt, retryPolicy), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                try
                {
                    return await materialize(response, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException) when (attempt < retryPolicy.MaxAttempts)
                {
                    await DelayAsync(retryPolicy.GetDelay(attempt), cancellationToken).ConfigureAwait(false);
                }
                catch (IOException) when (attempt < retryPolicy.MaxAttempts)
                {
                    await DelayAsync(retryPolicy.GetDelay(attempt), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < retryPolicy.MaxAttempts)
                {
                    await DelayAsync(retryPolicy.GetDelay(attempt), cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static bool IsRetryable(HttpResponseMessage response, bool isIdempotent)
    {
        if (IsDefinitiveRateLimitRejection(response))
        {
            return true;
        }

        return isIdempotent && (response.StatusCode == HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500);
    }

    private static bool IsDefinitiveRateLimitRejection(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.TooManyRequests ||
        (response.StatusCode == HttpStatusCode.Forbidden &&
         ((response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Any(value => value == "0")) ||
          response.Headers.RetryAfter is not null));

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt, RetryPolicy retryPolicy)
    {
        var now = DateTimeOffset.UtcNow;
        TimeSpan? instructedDelay = response.Headers.RetryAfter?.Delta ??
            (response.Headers.RetryAfter?.Date is { } date ? (TimeSpan?)(date - now) : null) ??
            GetResetDelay(response, "X-RateLimit-Reset", now) ??
            GetResetDelay(response, "RateLimit-Reset", now);

        return instructedDelay is { } instructed
            ? (instructed <= TimeSpan.Zero ? TimeSpan.Zero : instructed)
            : retryPolicy.GetDelay(attempt);
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        // Task.Delay rejects intervals above its timer limit. Preserve a provider's explicit
        // retry window by waiting in cancellable chunks rather than reducing it to our fallback cap.
        while (delay > MaxRetryDelay)
        {
            await Task.Delay(MaxRetryDelay, cancellationToken).ConfigureAwait(false);
            delay -= MaxRetryDelay;
        }

        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private static TimeSpan? GetResetDelay(HttpResponseMessage response, string header, DateTimeOffset now)
    {
        if (!response.Headers.TryGetValues(header, out var values))
        {
            return null;
        }

        var value = values.FirstOrDefault();
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        // DateTimeOffset.FromUnixTimeSeconds throws outside the representable range. Treat
        // malformed/server-controlled values as unusable rather than letting rate-limit handling
        // fail before the policy's delay cap can be applied.
        if (seconds < DateTimeOffset.MinValue.ToUnixTimeSeconds() || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return null;
        }

        var resetAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return resetAt - now;
    }
}
