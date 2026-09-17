using IssueAgent.Configuration;
using IssueAgent.Host;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddIssueAgentOptions(builder.Configuration);
builder.Services.AddSingleton<ReadinessState>();
builder.Services.AddHostedService<Worker>();

var application = builder.Build();

application.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
application.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
application.MapGet("/health/ready", (ReadinessState readiness) =>
    readiness.IsInitialized
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

await application.RunAsync();

public partial class Program;
