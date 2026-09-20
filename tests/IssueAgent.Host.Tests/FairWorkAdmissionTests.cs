using IssueAgent.Domain;
using IssueAgent.Host;
using Xunit;

namespace IssueAgent.Host.Tests;

public sealed class FairWorkAdmissionTests
{
    [Fact]
    public void HumanCommandPrecedesNewPlanning()
    {
        var admission = new FairWorkAdmission();
        Assert.True(admission.TryEnqueue(Item("repo-a", WorkflowWorkPriority.NewPlanning, null, 1)));
        Assert.True(admission.TryEnqueue(Item("repo-b", WorkflowWorkPriority.NewPlanning, null, 2)));
        Assert.True(admission.TryEnqueue(Item("repo-a", WorkflowWorkPriority.HumanCommand, WorkflowCommand.Implement, 3)));

        Assert.Equal(3, admission.TryStart(out var first) ? first!.Key.IssueNumber : -1);
        Assert.Equal(1, admission.TryStart(out var second) ? second!.Key.IssueNumber : -1);
        Assert.Equal(2, admission.TryStart(out var third) ? third!.Key.IssueNumber : -1);
    }

    [Fact]
    public void RoundRobinRepositoriesPreservesPerRepositoryFifo()
    {
        var admission = new FairWorkAdmission();
        admission.TryEnqueue(Item("repo-a", WorkflowWorkPriority.NewPlanning, null, 1));
        admission.TryEnqueue(Item("repo-a", WorkflowWorkPriority.NewPlanning, null, 2));
        admission.TryEnqueue(Item("repo-b", WorkflowWorkPriority.NewPlanning, null, 3));
        admission.TryEnqueue(Item("repo-b", WorkflowWorkPriority.NewPlanning, null, 4));

        Assert.Equal(1, Start(admission));
        Assert.Equal(3, Start(admission));
        Assert.Equal(2, Start(admission));
        Assert.Equal(4, Start(admission));
    }

    [Fact]
    public void DuplicateIsRejectedWhileQueuedOrInFlightAndAllowedAfterCompletion()
    {
        var admission = new FairWorkAdmission();
        var item = Item("repo-a", WorkflowWorkPriority.NewPlanning, null, 1);

        Assert.True(admission.TryEnqueue(item));
        Assert.False(admission.TryEnqueue(item));
        Assert.Equal(1, admission.QueuedCount);
        Assert.True(admission.TryStart(out var started));
        Assert.False(admission.TryEnqueue(item));
        Assert.Equal(1, admission.InFlightCount);

        admission.Complete(started!.Key);
        Assert.Equal(0, admission.Count);
        Assert.True(admission.TryEnqueue(item));
    }

    [Fact]
    public void QueuedWorkIsAdmittedBeforeItStarts()
    {
        var admission = new FairWorkAdmission();
        var item = Item("repo-a", WorkflowWorkPriority.NewPlanning, null, 1);

        Assert.True(admission.TryEnqueue(item));
        Assert.True(admission.IsAdmitted(item.Key));

        Assert.True(admission.TryStart(out var started));
        Assert.True(admission.IsAdmitted(item.Key));

        admission.Complete(started!.Key);
        Assert.False(admission.IsAdmitted(item.Key));
    }

    [Fact]
    public void RegistersAttemptCancellationBeforeExposingCandidateAsInFlight()
    {
        var admission = new FairWorkAdmission();
        var item = Item("repo-a", WorkflowWorkPriority.HumanCommand, WorkflowCommand.Implement, 1);
        Assert.True(admission.TryEnqueue(item));
        var registered = false;

        Assert.True(admission.TryStart(
            out var started,
            key =>
            {
                registered = true;
                Assert.Equal(item.Key, key);
                Assert.False(admission.IsInFlight(key));
            }));

        Assert.True(registered);
        Assert.True(admission.IsInFlight(started!.Key));
    }

    [Fact]
    public void DiscardQueuedPreservesOnlyInFlightAttempt()
    {
        var admission = new FairWorkAdmission();
        var running = Item("repo-a", WorkflowWorkPriority.HumanCommand, WorkflowCommand.Implement, 1);
        var queued = Item("repo-b", WorkflowWorkPriority.NewPlanning, null, 2);
        admission.TryEnqueue(running);
        admission.TryEnqueue(queued);
        Assert.True(admission.TryStart(out var started));

        Assert.Equal(1, admission.DiscardQueued());
        Assert.Equal(1, admission.Count);
        Assert.Equal(1, admission.InFlightCount);
        Assert.False(admission.TryStart(out _));

        admission.Complete(started!.Key);
        Assert.Equal(0, admission.Count);
    }

    private static long Start(FairWorkAdmission admission)
    {
        Assert.True(admission.TryStart(out var candidate));
        return candidate!.Key.IssueNumber;
    }

    private static WorkflowCandidate Item(
        string repository,
        WorkflowWorkPriority priority,
        WorkflowCommand? command,
        long issue) =>
        new(
            new WorkflowWorkKey("provider", repository, issue),
            command is null ? WorkflowCandidateKind.NewPlanning : WorkflowCandidateKind.ExistingWorkflow,
            priority,
            command,
            issue,
            _ => Task.CompletedTask);
}
