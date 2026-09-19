using IssueAgent.Providers;

namespace IssueAgent.Providers.Tests;

/// <summary>Provider-neutral behavioral contract. Concrete GitHub and GitLab test assemblies inherit
/// this suite so the same cancellation, failure, and attachment-host requirements run against both
/// transports.</summary>
public abstract class ProviderContractTests
{
    protected abstract IGitProvider Provider { get; }
    protected abstract RepositoryRef Repository { get; }
    protected abstract Uri TrustedAttachmentUri { get; }
    protected abstract Uri UntrustedAttachmentUri { get; }
    protected abstract void StubIssueResponse(int statusCode, string body, TimeSpan? delay = null);

    [Fact]
    public void AttachmentTrustNeverIncludesUnconfiguredHost()
    {
        Assert.True(Provider.IsTrustedAttachmentHost(TrustedAttachmentUri));
        Assert.False(Provider.IsTrustedAttachmentHost(UntrustedAttachmentUri));
    }

    [Fact]
    public async Task ProviderRequestHonorsCancellation()
    {
        StubIssueResponse(200, ValidIssuePayload, TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Provider.GetIssueAsync(Repository, 7, cancellation.Token));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task CommonHttpFailureDoesNotProduceDomainObject(int statusCode)
    {
        StubIssueResponse(statusCode, "{}");

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await Provider.GetIssueAsync(Repository, 7, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RetryableRateLimitFailureIsRetriedBeforeProducingDomainObject()
    {
        StubRetryableIssueResponse(ValidIssuePayload);

        var issue = await Provider.GetIssueAsync(Repository, 7, TestContext.Current.CancellationToken);

        Assert.Equal(7, issue.Number);
    }

    [Fact]
    public async Task MalformedPayloadDoesNotProducePartialDomainObject()
    {
        StubIssueResponse(200, "{not-json");

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await Provider.GetIssueAsync(Repository, 7, TestContext.Current.CancellationToken));
    }

    protected abstract string ValidIssuePayload { get; }
    protected abstract void StubRetryableIssueResponse(string successBody);
}
