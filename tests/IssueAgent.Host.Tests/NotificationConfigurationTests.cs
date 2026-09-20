using Xunit;

namespace IssueAgent.Host.Tests;

public sealed class NotificationConfigurationTests
{
    [Fact]
    public void ParseSlackWebhookUrlAcceptsHttpsUrlWithoutUserInformation()
    {
        var webhook = Program.ParseSlackWebhookUrl("https://hooks.slack.com/services/T000/B000/XXXX");

        Assert.Equal(Uri.UriSchemeHttps, webhook.Scheme);
    }

    [Theory]
    [InlineData("http://hooks.slack.com/services/T000/B000/XXXX")]
    [InlineData("https://token:secret@hooks.slack.com/services/T000/B000/XXXX")]
    public void ParseSlackWebhookUrlRejectsNonHttpsUrlsAndUserInformation(string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Program.ParseSlackWebhookUrl(value));

        Assert.Contains("must be an HTTPS URL without user information", exception.Message, StringComparison.Ordinal);
    }
}
