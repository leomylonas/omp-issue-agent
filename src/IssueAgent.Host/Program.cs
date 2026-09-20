using IssueAgent.Configuration;
using IssueAgent.Host;
using IssueAgent.Observability;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.ConfigureServices((context, services) =>
{
    var shutdownGracePeriod = context.Configuration.GetValue<TimeSpan?>("IssueAgent:ShutdownGracePeriod")
        ?? TimeSpan.FromSeconds(15);
    services.Configure<HostOptions>(options => options.ShutdownTimeout = shutdownGracePeriod + Worker.ShutdownCancellationHeadroom);
});

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.With<ActivityLogEnricher>());

builder.Services.AddIssueAgentOptions(builder.Configuration);
builder.Services.AddSingleton(services => EffectiveConfigurationResolver.Resolve(
    services.GetRequiredService<Microsoft.Extensions.Options.IOptions<IssueAgentOptions>>().Value,
    Environment.GetEnvironmentVariable,
    File.ReadAllText));
builder.Services.AddSingleton(services =>
    services.GetRequiredService<Microsoft.Extensions.Options.IOptions<IssueAgentOptions>>().Value.Retry.ToPolicy());
builder.Services.AddSingleton<ProviderRegistry>();
builder.Services.AddSingleton<IReadOnlyCollection<IssueAgent.Providers.IGitProvider>>(services =>
    services.GetRequiredService<ProviderRegistry>().All);
builder.Services.AddSingleton<IssueAgent.Git.IGitRepositoryManager>(services =>
{
    var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<IssueAgentOptions>>().Value;
    var inner = new IssueAgent.Git.RetryingGitRepositoryManager(
        new IssueAgent.Git.LibGit2SharpRepositoryManager(
            Path.Combine(options.Workspace.RootPath, "repos")),
        services.GetRequiredService<IssueAgent.Domain.RetryPolicy>());
    return new ObservableGitRepositoryManager(
        inner,
        services.GetRequiredService<IssueAgentMetrics>(),
        services.GetRequiredService<ILogger<ObservableGitRepositoryManager>>());
});
builder.Services.AddSingleton<IssueAgent.Workflow.IClock, IssueAgent.Workflow.SystemClock>();
builder.Services.AddSingleton<IssueAgent.Workflow.IWorkflowNotifier>(services =>
{
    var configuration = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<IssueAgent.Configuration.IssueAgentOptions>>().Value;
    var effectiveConfiguration = services.GetRequiredService<EffectiveIssueAgentConfiguration>();
    var notificationsTlsTrust = new IssueAgent.Git.TlsTrust
    {
        Mode = effectiveConfiguration.Notifications.TlsMode switch
        {
            IssueAgent.Configuration.ConfiguredTlsTrustMode.System => IssueAgent.Git.TlsTrustMode.System,
            IssueAgent.Configuration.ConfiguredTlsTrustMode.SystemPlusAdditionalCa => IssueAgent.Git.TlsTrustMode.SystemPlusAdditionalCa,
            IssueAgent.Configuration.ConfiguredTlsTrustMode.Pinned => IssueAgent.Git.TlsTrustMode.Pinned,
            IssueAgent.Configuration.ConfiguredTlsTrustMode.None => IssueAgent.Git.TlsTrustMode.None,
            _ => throw new InvalidOperationException($"Unsupported notifications TLS trust mode '{effectiveConfiguration.Notifications.TlsMode}'."),
        },
        AdditionalCaCertificatePaths = effectiveConfiguration.Notifications.AdditionalCaCertificatePaths,
        Fingerprints = effectiveConfiguration.Notifications.TlsFingerprints,
    };
    var metrics = services.GetRequiredService<IssueAgentMetrics>();
    var logger = services.GetRequiredService<ILogger<Program>>();
    var sinks = new List<IssueAgent.Notifications.INotificationSink>();
    if (configuration.Notifications.Telegram is { } telegram)
    {
        var httpClient = new HttpClient(IssueAgent.Git.TlsHttpHandlerFactory.Create(notificationsTlsTrust))
        {
            BaseAddress = new Uri("https://api.telegram.org/"),
        };
        sinks.Add(new IssueAgent.Notifications.TelegramNotificationSink(httpClient, telegram.BotToken.Resolve(Environment.GetEnvironmentVariable, File.ReadAllText), telegram.ChatId));
    }
    if (configuration.Notifications.Slack is { } slack)
    {
        var webhook = slack.WebhookUrl.Resolve(Environment.GetEnvironmentVariable, File.ReadAllText);
        sinks.Add(new IssueAgent.Notifications.SlackNotificationSink(
            new HttpClient(IssueAgent.Git.TlsHttpHandlerFactory.Create(notificationsTlsTrust)),
            new Uri(webhook, UriKind.Absolute)));
    }
    var routing = new Dictionary<IssueAgent.Workflow.WorkflowNotificationKind, IReadOnlySet<string>>();
    foreach (var (eventName, sinkNames) in configuration.Notifications.Routing)
    {
        if (Enum.TryParse<IssueAgent.Workflow.WorkflowNotificationKind>(eventName, ignoreCase: true, out var eventKind))
        {
            routing[eventKind] = sinkNames;
        }
    }
    var inner = new IssueAgent.Notifications.FanOutNotifier(
        sinks,
        services.GetRequiredService<IssueAgent.Domain.RetryPolicy>(),
        routing,
        (sinkName, notification, exception) =>
        {
            metrics.NotificationFailures.Add(1, new KeyValuePair<string, object?>(LogContextFields.Operation, notification.Kind.ToString()));
            LogNotificationSinkFailure(logger, sinkName, exception.GetType().Name);
        });
    return new ObservableWorkflowNotifier(
        inner,
        metrics,
        services.GetRequiredService<ILogger<ObservableWorkflowNotifier>>());
});
builder.Services.AddSingleton<StartupValidator>();
builder.Services.AddSingleton<DefaultBranchResolver>();
builder.Services.AddSingleton<PollingScheduler>();
builder.Services.AddSingleton<ActiveOmpSessionRegistry>();
builder.Services.AddSingleton<WorkflowWorkerPool>();
builder.Services.AddSingleton<WorkflowShutdownCoordinator>();
builder.Services.AddSingleton<OmpRuntimeEnvironmentFactory>();
builder.Services.AddSingleton<WorkflowDispatcher>();
builder.Services.AddSingleton<IssueAgentMetrics>();
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("IssueAgent"))
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(IssueAgentMetrics.MeterName);
        metrics.AddPrometheusExporter();
    })
    .WithTracing(tracing =>
    {
        var samplingRatio = builder.Configuration.GetValue<double?>("OpenTelemetry:TraceSamplingRatio") ?? 1.0;
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
        var endpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            tracing.AddOtlpExporter(options => options.Endpoint = new Uri(endpoint));
        }
    });

builder.Services.AddSingleton<ReadinessState>();
builder.Services.AddHostedService<Worker>();

var application = builder.Build();

application.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
application.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
application.MapGet("/health/ready", (ReadinessState readiness) =>
    readiness.IsInitialized
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
application.MapPrometheusScrapingEndpoint("/metrics");

await application.RunAsync();

public partial class Program
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Notification sink {SinkName} exhausted retries with {ExceptionType}")]
    private static partial void LogNotificationSinkFailure(Microsoft.Extensions.Logging.ILogger logger, string sinkName, string exceptionType);
}
