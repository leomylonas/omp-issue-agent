using System.Diagnostics.Metrics;
using System.Net;
using IssueAgent.Host;
using IssueAgent.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using Xunit;

namespace IssueAgent.Host.Tests;

public sealed class HostOpenTelemetryTests
{
    [Fact]
    public async Task ExportsMetricsToConfiguredOtlpEndpointAlongsidePrometheus()
    {
        var exported = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));

        var collector = builder.Build();
        collector.MapPost("/opentelemetry.proto.collector.metrics.v1.MetricsService/Export", async context =>
        {
            await using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            exported.TrySetResult(body.ToArray());
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/grpc";
            context.Features.Get<IHttpResponseTrailersFeature>()?.Trailers["grpc-status"] = "0";
        });
        await collector.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var endpoint = collector.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.Single();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenTelemetry:OtlpEndpoint"] = endpoint,
                })
                .Build();
            var services = new ServiceCollection();
            HostOpenTelemetry.Configure(services, configuration);

            await using var provider = services.BuildServiceProvider();
            var meterProvider = provider.GetRequiredService<MeterProvider>();
            using var meter = new Meter(IssueAgentMetrics.MeterName);
            var counter = meter.CreateCounter<long>("host.observability.test");
            counter.Add(1);
            meterProvider.ForceFlush(10_000);
            var payload = await exported.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.NotEmpty(payload);
        }
        finally
        {
            await collector.StopAsync(TestContext.Current.CancellationToken);
            await collector.DisposeAsync();
        }
    }
}
