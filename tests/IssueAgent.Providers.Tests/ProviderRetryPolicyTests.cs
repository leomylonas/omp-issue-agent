using System.Diagnostics.Metrics;
using IssueAgent.Domain;

namespace IssueAgent.Providers.Tests;

public sealed class ProviderRetryPolicyTests
{
    [Fact]
    public async Task SendAsyncDoesNotRetryAnAmbiguous500ForANonIdempotentPost()
    {
        var attempts = 0;
        var response = await ProviderRetryPolicy.SendAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
            },
            CancellationToken.None,
            isIdempotent: false);

        Assert.Equal(1, attempts);
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task SendAsyncRetriesAnAmbiguous500ForAnIdempotentRequest()
    {
        var attempts = 0;
        var response = await ProviderRetryPolicy.SendAsync(
            _ =>
            {
                attempts++;
                var status = attempts < 3 ? System.Net.HttpStatusCode.InternalServerError : System.Net.HttpStatusCode.OK;
                return Task.FromResult(new HttpResponseMessage(status));
            },
            CancellationToken.None,
            isIdempotent: true);

        Assert.Equal(3, attempts);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SendAsyncEmitsRetryAttemptMetric()
    {
        long retries = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "IssueAgent" && instrument.Name == "issueagent.retry.attempts")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "issueagent.retry.attempts")
            {
                Interlocked.Add(ref retries, measurement);
            }
        });
        listener.Start();

        var attempts = 0;
        using var response = await ProviderRetryPolicy.SendAsync(
            _ => Task.FromResult(new HttpResponseMessage(
                ++attempts == 1 ? System.Net.HttpStatusCode.ServiceUnavailable : System.Net.HttpStatusCode.OK)),
            CancellationToken.None,
            retryPolicy: new RetryPolicy
            {
                MaxAttempts = 2,
                InitialDelay = TimeSpan.Zero,
                MaxJitter = TimeSpan.Zero,
            });

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.True(Interlocked.Read(ref retries) >= 1);
    }

    [Fact]
    public async Task SendAsyncEmitsExhaustionMetricForTerminalTransportFailure()
    {
        long exhausted = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "IssueAgent" && instrument.Name == "issueagent.retry.exhausted")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "issueagent.retry.exhausted")
            {
                Interlocked.Add(ref exhausted, measurement);
            }
        });
        listener.Start();

        await Assert.ThrowsAsync<HttpRequestException>(() => ProviderRetryPolicy.SendAsync(
            _ => Task.FromException<HttpResponseMessage>(new HttpRequestException("connection reset")),
            CancellationToken.None,
            retryPolicy: new RetryPolicy
            {
                MaxAttempts = 1,
                InitialDelay = TimeSpan.Zero,
                MaxJitter = TimeSpan.Zero,
            }));

        Assert.True(Interlocked.Read(ref exhausted) >= 1);
    }


    [Fact]
    public async Task SendAsyncEmitsExhaustionMetricForAFilteredTransportFailure()
    {
        long exhausted = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "IssueAgent" && instrument.Name == "issueagent.retry.exhausted")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "issueagent.retry.exhausted")
            {
                Interlocked.Add(ref exhausted, measurement);
            }
        });
        listener.Start();

        var attempts = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => ProviderRetryPolicy.SendAsync(
            _ =>
            {
                attempts++;
                return Task.FromException<HttpResponseMessage>(new HttpRequestException("connection reset"));
            },
            CancellationToken.None,
            isIdempotent: false,
            retryPolicy: new RetryPolicy
            {
                MaxAttempts = 2,
                InitialDelay = TimeSpan.Zero,
                MaxJitter = TimeSpan.Zero,
            }));

        Assert.Equal(1, attempts);
        Assert.True(Interlocked.Read(ref exhausted) >= 1);
    }
    [Fact]
    public async Task SendAsyncRetriesA429EvenForANonIdempotentPost()
    {
        var attempts = 0;
        var response = await ProviderRetryPolicy.SendAsync(
            _ =>
            {
                attempts++;
                var status = attempts < 2 ? System.Net.HttpStatusCode.TooManyRequests : System.Net.HttpStatusCode.Created;
                return Task.FromResult(new HttpResponseMessage(status));
            },
            CancellationToken.None,
            isIdempotent: false);

        Assert.Equal(2, attempts);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.TooManyRequests)]
    [InlineData(System.Net.HttpStatusCode.Forbidden)]
    public async Task SendAsyncUsesConservativeFallbackForRateLimitsWithoutAUsableRetryWindow(System.Net.HttpStatusCode statusCode)
    {
        var attempts = 0;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProviderRetryPolicy.SendAsync(
            _ =>
            {
                attempts++;
                var response = new HttpResponseMessage(statusCode);
                if (statusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    response.Headers.Add("X-RateLimit-Remaining", "0");
                }

                return Task.FromResult(response);
            },
            cancellation.Token,
            retryPolicy: new IssueAgent.Domain.RetryPolicy
            {
                InitialDelay = TimeSpan.Zero,
                MaxJitter = TimeSpan.Zero,
                RateLimitFallbackDelay = TimeSpan.FromMinutes(1),
            }));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task SendAsyncUsesTheGlobalTransientBackoffInsteadOfTheRateLimitFallback()
    {
        var attempts = 0;

        using var response = await ProviderRetryPolicy.SendAsync(
            _ => Task.FromResult(new HttpResponseMessage(++attempts == 1
                ? System.Net.HttpStatusCode.ServiceUnavailable
                : System.Net.HttpStatusCode.OK)),
            CancellationToken.None,
            retryPolicy: new IssueAgent.Domain.RetryPolicy
            {
                InitialDelay = TimeSpan.Zero,
                MaxJitter = TimeSpan.Zero,
                RateLimitFallbackDelay = TimeSpan.FromMinutes(1),
            });

        Assert.Equal(2, attempts);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SendAsyncRetriesTransientTransportFailuresForIdempotentRequests()
    {
        var attempts = 0;
        var response = await ProviderRetryPolicy.SendAsync(
            _ => ++attempts == 1
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("connection reset"))
                : Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)),
            CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SendAsyncDoesNotRetryCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProviderRetryPolicy.SendAsync(
            _ =>
            {
                attempts++;
                throw new OperationCanceledException();
            },
            cancellation.Token));

        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task SendAsyncRetriesRequestTimeoutForIdempotentRequests()
    {
        var attempts = 0;
        var response = await ProviderRetryPolicy.SendAsync(
            _ => Task.FromResult(new HttpResponseMessage(++attempts == 1
                ? System.Net.HttpStatusCode.RequestTimeout
                : System.Net.HttpStatusCode.OK)),
            CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SendAsyncDoesNotRetryForbiddenOnlyBecauseItIncludesAResetHeader()
    {
        var attempts = 0;
        var response = await ProviderRetryPolicy.SendAsync(
            _ =>
            {
                attempts++;
                var forbidden = new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden);
                forbidden.Headers.Add("X-RateLimit-Reset", "99999999999");
                return Task.FromResult(forbidden);
            },
            CancellationToken.None);

        Assert.Equal(1, attempts);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }
    [Fact]
    public async Task SendAsyncHonorsAnExplicitRetryWindowBeyondTheFallbackCap()
    {
        var attempt = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var exception = await Record.ExceptionAsync(() => ProviderRetryPolicy.SendAsync(
            _ =>
            {
                attempt++;
                var rejected = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
                rejected.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(6));
                return Task.FromResult(rejected);
            },
            cts.Token,
            isIdempotent: true));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(1, attempt);
    }

    [Fact]
    public async Task SendAndMaterializeAsyncRetriesWhenTheResponseBodyFailsAfterHeaders()
    {
        var sends = 0;
        var materializations = 0;

        var result = await ProviderRetryPolicy.SendAndMaterializeAsync(
            _ =>
            {
                sends++;
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
            },
            (_, _) =>
            {
                materializations++;
                return materializations == 1
                    ? Task.FromException<string>(new HttpRequestException("body connection reset"))
                    : Task.FromResult("downloaded");
            },
            CancellationToken.None);

        Assert.Equal("downloaded", result);
        Assert.Equal(2, materializations);
        Assert.Equal(2, sends);
    }
    [Fact]
    public async Task SendAsyncUsesTheInjectedAttemptLimit()
    {
        var attempts = 0;

        using var response = await ProviderRetryPolicy.SendAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
            },
            CancellationToken.None,
            retryPolicy: new IssueAgent.Domain.RetryPolicy { MaxAttempts = 1, MaxJitter = TimeSpan.Zero });

        Assert.Equal(1, attempts);
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task SendAsyncDefersATerminalRateLimitWhenPollingSchedulingIsEnabled()
    {
        using var scope = PollingRateLimitScheduling.Enter();

        var exception = await Assert.ThrowsAsync<PollingRateLimitedException>(() => ProviderRetryPolicy.SendAsync(
            _ =>
            {
                var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
                return Task.FromResult(response);
            },
            CancellationToken.None,
            retryPolicy: new IssueAgent.Domain.RetryPolicy { MaxAttempts = 1 }));

        Assert.Equal(TimeSpan.FromMinutes(2), exception.RetryAfter);
    }

    [Fact]
    public async Task SendAndMaterializeAsyncDefersATerminalRateLimitWhenPollingSchedulingIsEnabled()
    {
        using var scope = PollingRateLimitScheduling.Enter();

        var exception = await Assert.ThrowsAsync<PollingRateLimitedException>(() => ProviderRetryPolicy.SendAndMaterializeAsync(
            _ =>
            {
                var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
                return Task.FromResult(response);
            },
            (_, _) => Task.FromResult("not reached"),
            CancellationToken.None,
            retryPolicy: new IssueAgent.Domain.RetryPolicy { MaxAttempts = 1 }));

        Assert.Equal(TimeSpan.FromMinutes(2), exception.RetryAfter);
    }

}
