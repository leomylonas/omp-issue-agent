using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace IssueAgent.Observability;

/// <summary>Copies bounded correlation tags from the current activity into structured log events.</summary>
public sealed class ActivityLogEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        // Child spans often only carry operation-specific tags. Walk their parent chain so a log
        // emitted from a Git/OMP child retains dispatch correlation fields from its ancestor.
        for (var activity = Activity.Current; activity is not null; activity = activity.Parent)
        {
            foreach (var tag in activity.TagObjects)
            {
                if (tag.Key is LogContextFields.Provider or LogContextFields.Repository
                    or LogContextFields.IssueNumber or LogContextFields.PullOrMergeRequestNumber
                    or LogContextFields.WorkflowId or LogContextFields.CorrelationId
                    or LogContextFields.OmpSessionId or LogContextFields.Operation
                    or LogContextFields.Component or LogContextFields.OmpEventType)
                {
                    logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(tag.Key, tag.Value));
                }
            }
        }
    }
}
