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
    [Fact]
    public async Task SendAsyncClampsAnExcessiveRetryAfterDelayInsteadOfThrowing()
    {
        var attempt = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var exception = await Record.ExceptionAsync(() => ProviderRetryPolicy.SendAsync(
            _ =>
            {
                attempt++;
                var rejected = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
                // A hostile/misconfigured header instructing a ~3170-year delay must never be
                // honored verbatim. Before the fix, Task.Delay's own parameter validation throws
                // ArgumentOutOfRangeException immediately for a delay this large. After the fix,
                // the delay is clamped to the policy's one-minute cap, so the loop instead waits
                // normally and is cut short by OperationCanceledException when our 2-second token
                // fires, never touching the honored-verbatim path.
                rejected.Headers.Add("X-RateLimit-Reset", "99999999999");
                return Task.FromResult(rejected);
            },
            cts.Token,
            isIdempotent: true));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(1, attempt);
    }
}
