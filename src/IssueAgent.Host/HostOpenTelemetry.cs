using IssueAgent.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace IssueAgent.Host;

internal static class HostOpenTelemetry
{
    internal static void Configure(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("IssueAgent"))
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(IssueAgentMetrics.MeterName);
                metrics.AddPrometheusExporter();

                var endpoint = configuration["OpenTelemetry:OtlpEndpoint"];
                if (!string.IsNullOrWhiteSpace(endpoint))
                {
                    metrics.AddOtlpExporter(options => options.Endpoint = new Uri(endpoint));
                }
            })
            .WithTracing(tracing =>
            {
                var samplingRatio = configuration.GetValue<double?>("OpenTelemetry:TraceSamplingRatio") ?? 1.0;
                if (samplingRatio is < 0 or > 1)
                {
                    throw new InvalidOperationException("OpenTelemetry:TraceSamplingRatio must be between 0 and 1.");
                }

                tracing.SetSampler(new TraceIdRatioBasedSampler(samplingRatio));
                tracing.AddSource(IssueAgentActivitySource.Name);
                tracing.AddHttpClientInstrumentation(options => options.EnrichWithHttpRequestMessage = (activity, request) =>
                {
                    if (request.RequestUri is { IsAbsoluteUri: true } uri)
                    {
                        activity.SetTag("url.full", HttpSpanRedactor.RedactUrl(uri));
                    }
                });

                var endpoint = configuration["OpenTelemetry:OtlpEndpoint"];
                if (!string.IsNullOrWhiteSpace(endpoint))
                {
                    tracing.AddOtlpExporter(options => options.Endpoint = new Uri(endpoint));
                }
            });
    }
}
