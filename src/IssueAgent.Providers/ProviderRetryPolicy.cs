using System.Globalization;
using System.Net;

namespace IssueAgent.Providers;

public static class ProviderRetryPolicy
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    /// <param name="send">Issues one HTTP attempt.</param>
    /// <param name="cancellationToken">Cancels the whole retry loop, including any delay.</param>
    /// <param name="isIdempotent">When <see langword="false"/> (a non-idempotent write such as a
    /// resource-creating POST), an ambiguous <c>5xx</c> is never retried — the request may have
    /// already been applied server-side, and retrying could duplicate the effect (specification
    /// §17, §25, §27). A definitive rate-limit rejection (<c>429</c>, or <c>403</c> with exhausted
    /// quota or <c>Retry-After</c>) is always safe to retry regardless of verb: the provider rejected
    /// the request before processing it.</param>
    public static async Task<HttpResponseMessage> SendAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken,
        bool isIdempotent = true)
    {
        ArgumentNullException.ThrowIfNull(send);

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await send(cancellationToken).ConfigureAwait(false);
                if (!IsRetryable(response, isIdempotent) || attempt == MaxAttempts)
                {
                    return response;
                }

                var delay = GetRetryDelay(response, attempt);
                response.Dispose();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (isIdempotent && attempt < MaxAttempts)
            {
                await Task.Delay(GetTransientRetryDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (isIdempotent && !cancellationToken.IsCancellationRequested && attempt < MaxAttempts)
            {
                await Task.Delay(GetTransientRetryDelay(attempt), cancellationToken).ConfigureAwait(false);
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

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var now = DateTimeOffset.UtcNow;
        TimeSpan? instructedDelay = response.Headers.RetryAfter?.Delta ??
            (response.Headers.RetryAfter?.Date is { } date ? (TimeSpan?)(date - now) : null) ??
            GetResetDelay(response, "X-RateLimit-Reset", now) ??
            GetResetDelay(response, "RateLimit-Reset", now);

        if (instructedDelay is { } delay)
        {
            if (delay <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            return delay > MaxRetryDelay ? MaxRetryDelay : delay;
        }
        var exponential = IsDefinitiveRateLimitRejection(response)
            ? TimeSpan.FromMinutes(Math.Min(5, Math.Pow(2, attempt - 1)))
            : TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt - 1));
        var jitter = IsDefinitiveRateLimitRejection(response)
            ? TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000))
            : TimeSpan.FromMilliseconds(Random.Shared.Next(0, 100));
        var fallback = exponential + jitter;
        return fallback > MaxRetryDelay ? MaxRetryDelay : fallback;
    }

    private static TimeSpan GetTransientRetryDelay(int attempt) =>
        TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 100));

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
